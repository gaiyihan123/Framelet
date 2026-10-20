using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

internal static class TranslationRegressionAcceptance
{
    public static List<string> Run()=>RunAsync().GetAwaiter().GetResult();
    private static async Task<List<string>> RunAsync()
    {
        var lines=new List<string>();
        void Check(bool ok,string label) { if(!ok) throw new Exception("FAIL: "+label); lines.Add("PASS: "+label); }
        var cases=new (string Source,string Input,string Expected,string Label)[] {
            ("The price is $20.","价格为$20。","价格为$20。","existing currency symbol is not duplicated"),
            ("$20 and $20","20美元和20美元","$20和$20","repeated equal amounts are each repaired once"),
            ("$20 and €20","20美元和20欧元","$20和€20","equal amounts in named currencies keep distinct symbols"),
            ("$20 and €20","20元和20元","20元和20元","ambiguous equal amounts in different currencies are left intact"),
            ("$20","数量20个，金额20美元。","数量20个，金额$20。","ordinary quantities are not assigned a currency"),
            ("$20","200元、120元、20.5元、20,500元；20元。","200元、120元、20.5元、20,500元；$20。","currency matching cannot consume part of another number"),
            ("$1,250.50 then €200","人民币1,250.50元，然后200欧元","$1,250.50，然后€200","localized prefix and suffix are replaced together"),
            ("$1,250.50 then €200","1250.50元，然后200欧元","$1250.50，然后€200","thousands separators do not change currency identity"),
            ("$20","20美元与$20","20美元与$20","existing symbols reserve their source occurrence first"),
            ("$20","20元件和20元素；20美元。","20元件和20元素；$20。","component and element words are not currency labels"),
            ("$20","20元2件","20元2件","removing a unit cannot merge the amount with the next quantity"),
            ("In AD 20 the price was $20.","公元20年，价格20元。","公元20年，价格$20。","an era label cannot consume a currency occurrence"),
            ("The first price is $20; the other is 20 yuan.","第一项US$20，另一项20元。","第一项US$20，另一项20元。","country-prefixed existing symbols reserve their occurrence"),
            ("$20 and $20","20美元$20，20元","$20$20，20元","adjacent currency symbols stay accounted for on repeated passes"),
            ("$20.50","价格20.50元。","价格$20.50。","decimal amount followed by punctuation is fully preserved")
        };
        foreach(var c in cases) {
            string actual=TranslationCorrections.PreserveCurrencySymbols(c.Source,c.Input);
            Check(actual==c.Expected&&TranslationCorrections.PreserveCurrencySymbols(c.Source,actual)==actual,c.Label+"; second pass is unchanged");
        }
        using(var client=new TranslationClient((text,ct)=>Task.FromResult("价格为$20。")))
            Check(await client.Translate("The price is $20.",default)=="价格为$20。"&&await client.Translate("The price is $20.",default)=="价格为$20。","correct currency output survives complete client and cache path");
        var ocrLines=new List<OcrBlock> {
            new() { Number=1,Bounds=new Rectangle(10,20,300,25),Original="Do not close this window until" },
            new() { Number=2,Bounds=new Rectangle(10,200,300,25),Original="the download is complete.\r\nThen restart the application." }
        };
        var paragraph=TranslationText.MergeForTranslation(ocrLines);
        const string whole="Do not close this window until the download is complete. Then restart the application.";
        Check(paragraph.Count==1&&paragraph[0].Original==whole,"OCR line and paragraph breaks are joined without losing sentence context");
        int paragraphCalls=0;string received="";
        using(var client=new TranslationClient((text,ct)=> { paragraphCalls++;received=text;return Task.FromResult("下载完成前，请勿关闭此窗口。\r\n\r\n然后重启应用程序。"); })) {
            await client.Fill(paragraph,new Progress<string>(),default);
            Check(paragraphCalls==1&&received==whole&&TranslationText.Translated(paragraph)=="下载完成前，请勿关闭此窗口。 然后重启应用程序。","complete OCR text reaches one translation request and produces one continuous paragraph");
        }
        var failedParagraph=TranslationText.MergeForTranslation(ocrLines);
        using(var failure=new TranslationClient((text,ct)=>throw new InvalidOperationException("synthetic failure"))) {
            await failure.Fill(failedParagraph,new Progress<string>(),default);
            Check(failedParagraph.Count==1&&failedParagraph[0].Translation.Length==0&&failedParagraph[0].Error=="synthetic failure","whole-text failure does not present an incomplete sentence as success");
        }
        using(var retry=new TranslationClient((text,ct)=>Task.FromResult("下载完成前，请勿关闭此窗口，然后重启应用程序。"))) {
            await retry.Fill(failedParagraph,new Progress<string>(),default);
            Check(failedParagraph[0].Translation.Length>0&&failedParagraph[0].Error.Length==0,"whole-text retry clears the previous error");
        }
        string longOriginal=string.Join(" ",Enumerable.Repeat("content",3000))+" final sentence.";
        var longRequests=TranslationText.MergeForTranslation(new[]{new OcrBlock { Original=longOriginal }});
        Check(longRequests.Count>1&&longRequests.All(block=>block.Original.Length<=16000)&&TranslationText.Original(longRequests)==longOriginal,"long captures retain every source character within the existing request limit");
        foreach(bool timeout in new[]{false,true}) {
            using var gate=new SemaphoreSlim(1,1);
            using var release=new ManualResetEventSlim();
            using var activeStop=new CancellationTokenSource();
            var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rawTasks=new List<Task<string>>();
            int expiredCalls=0,survivorCalls=0;
            Task<string> Start(Func<string> execute,CancellationToken token) {
                var task=EmbeddedPython.RunSerialized(gate,execute,token);rawTasks.Add(task);return task;
            }
            try {
                Task<string>? activeRaw=null;
                var active=OfflineTranslator.AwaitRequest(token=>activeRaw=Start(()=> {
                    entered.TrySetResult(true);release.Wait();
                    if(timeout) throw new InvalidOperationException("Synthetic late inference failure");
                    return "late result";
                },token),timeout?TimeSpan.FromSeconds(2):TimeSpan.FromSeconds(10),activeStop.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if(!timeout) activeStop.Cancel();
                bool bounded=false;
                try { await active.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch(TimeoutException) when(timeout&&active.IsCompleted) { bounded=true; }
                catch(OperationCanceledException e) when(!timeout&&e.CancellationToken==activeStop.Token) { bounded=true; }
                Check(bounded&&!activeRaw!.IsCompleted&&gate.CurrentCount==0,(timeout?"timeout":"cancellation")+" returns while active native work retains the gate");
                Task<string>? expiredRaw=null;
                bool timedOut=false;
                try { await OfflineTranslator.AwaitRequest(token=>expiredRaw=Start(()=> {Interlocked.Increment(ref expiredCalls);return "expired";},token),TimeSpan.FromMilliseconds(50),default); }
                catch(TimeoutException) { timedOut=true; }
                bool removed=false;
                try { await expiredRaw!.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch(OperationCanceledException) { removed=true; }
                Check(timedOut&&removed&&expiredCalls==0&&gate.CurrentCount==0,"queued timeout is cancelled before entering inference");
                using var queuedStop=new CancellationTokenSource();
                var queued=OfflineTranslator.AwaitRequest(token=>Start(()=> {Interlocked.Increment(ref expiredCalls);return "cancelled";},token),TimeSpan.FromSeconds(10),queuedStop.Token);
                queuedStop.Cancel();bool cancelled=false;
                try { await queued; } catch(OperationCanceledException e) { cancelled=e.CancellationToken==queuedStop.Token; }
                Check(cancelled&&expiredCalls==0,"queued caller cancellation retains caller token and skips inference");
                var survivor=Start(()=> {Interlocked.Increment(ref survivorCalls);return "next";},default);
                Check(!survivor.IsCompleted&&gate.CurrentCount==0,"next request cannot enter while abandoned native execution is active");
                release.Set();
                Check(await survivor.WaitAsync(TimeSpan.FromSeconds(5))=="next"&&survivorCalls==1&&expiredCalls==0&&gate.CurrentCount==1,"queue recovers after late "+(timeout?"failure":"result")+" without executing expired requests");
            } finally {
                release.Set();
                try { await Task.WhenAll(rawTasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
        }
        return lines;
    }
}
