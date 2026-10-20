using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

internal static class OfflineTranslationAcceptance
{
    public static List<string> Run(string folder)
    {
        var lines=new List<string>();
        lines.AddRange(TranslationRegressionAcceptance.Run());
        void Check(bool ok,string text) { if(!ok) throw new Exception("FAIL: "+text); lines.Add("PASS: "+text); }
        bool Correct(string source,string expected)=>TranslationCorrections.TryTranslate(source,out var result)&&result==expected;
        Check(Correct("The meeting was moved from Monday to Friday at 3 PM.","会议原定星期一，现改至星期五下午3点。"),"moved meeting preserves source and new dates instead of a multi-day range");
        Check(Correct("The meeting has been moved from FRIDAY to Monday at 3:30 p.m.","会议原定星期五，现改至星期一下午3点30分。"),"meeting correction accepts case and minute variants");
        Check(Correct("Submit the report by Friday.","最迟应于星期五提交报告。"),"by-weekday deadline explicitly includes the named deadline day");
        Check(Correct("Changes made before 5 p.m. today will be reviewed tomorrow.","今天下午5点前作出的更改，将于明天审核。"),"before-time and review-day relation stays explicit");
        Check(Correct("Reduce the total by 15%, not to 15%.","将总额减少15%，而不是减少到15%。"),"percentage reduction and final amount stay distinct");
        Check(Correct("Reduce the error rate by 12.5%, not to 5%.","将错误率减少12.5%，而不是减少到5%。"),"decimal percentage correction preserves both quantities");
        Check(Correct("The refund will not be issued until 30 days after the returned item is received.","收到退回的商品30天后才会退款。"),"refund clock starts when the returned item is received");
        Check(Correct("The trial lasts for two weeks after installation.","软件安装后的试用期为2周。"),"installation trial uses the software-trial sense and normalizes spelled-out count");
        Check(Correct("The trial lasts for 8 days after installation.","软件安装后的试用期为8天。"),"installation-trial rule retains numeric day duration");
        Check(Correct("Only delete files newer than 30 days.","只删除最近30天内的文件。"),"newer-than duration explicitly states the recent-file range");
        Check(!TranslationCorrections.TryTranslate("Only delete files newer than 30 days unless archived.",out _),"files rule leaves extra retention conditions untouched");
        Check(Correct("Do not delete files older than 30 days.","不要删除超过30天的文件。"),"older-than negation preserves both the date threshold and delete prohibition");
        Check(Correct("Don't delete files older than 7 days!","不要删除超过7天的文件。"),"contracted negation and a different age threshold remain intact");
        Check(!TranslationCorrections.TryTranslate("Do not delete files newer than 30 days.",out _),"older-than correction cannot invert a newer-than condition");
        Check(Correct("Wait no more than 15 minutes before restarting the computer.","重启电脑前，等待时间不得超过15分钟。"),"maximum wait is placed before restart, not after it");
        Check(!TranslationCorrections.TryTranslate("Wait no more than 15 minutes after restarting the computer.",out _),"before/after reversal is not hidden by the restart rule");
        Check(!TranslationCorrections.TryTranslate("The trial lasted for two months after installation.",out _),"unsupported trial wording is left to the model");
        Check(TranslationCorrections.PreserveCurrencySymbols("The price increases from $120 to $150.","下月价格由120元上涨至150元。").Contains("$120")&&TranslationCorrections.PreserveCurrencySymbols("The price increases from $120 to $150.","下月价格由120元上涨至150元。").Contains("$150"),"currency symbols survive model localization for every amount");
        Check(!TranslationCorrections.TryTranslate("Prices rose by 15 percent and the deadline is Friday.",out _),"unmatched compound text remains model translated without partial rewriting");
        Check(!TranslationCorrections.TryTranslate("Reduce the total by 15%, not to 5% next month.",out _),"percentage repair rejects sentences with extra temporal context");
        Check(Correct("Decrease the total by 15 percent, not to 15 percent.","将总额减少15%，而不是减少到15%。"),"written percentage units preserve by-versus-to distinction");
        Check(Correct("Your software trial ends two weeks after installation.","软件试用期将在安装后2周结束。"),"software trial end date retains duration and event order");
        Check(Correct("The update is available through Friday, but expires at 12:01 a.m. Saturday.","更新会在整个星期五保持可用，并于星期六凌晨12点01分失效。"),"through-Friday availability remains inclusive and expiry stays Saturday");
        Check(Correct("Keep the old version until the new version has been installed and verified.","新版本安装并验证完成之前，请保留旧版本。"),"old version is retained until install and verification both finish");
        Check(Correct("Do not restart the computer until the file backup has completed.","文件备份完成之前，请勿重启电脑。"),"restart prohibition remains in force until backup completion");
        Check(Correct("Do not start the next step until the previous step has finished.","上一步完成前，请勿开始下一步。"),"negative until wording remains a prohibition instead of becoming an affirmative command");
        Check(Correct("Start the next step only after the previous step has finished.","只有在上一步完成后，才能开始下一步。"),"only-after wording stays conditional and reads naturally");
        Check(!TranslationCorrections.TryTranslate("Do not start the next step until the previous step has finished or the owner approves.",out _),"an added exception is not silently discarded by the exact step-order repair");
        Check(Correct("The error rate must stay below 5%, not at 5%.","错误率必须低于5%，不得达到该数值。"),"percentage boundary remains strictly below the limit");
        Check(Correct("The error rate must stay below 2.5%, not at 2.5%.","错误率必须低于2.5%，不得达到该数值。"),"decimal strict-bound rule retains the number");
        int qwenCalls=0,argosCalls=0;
        using var offlineFallback=new TranslationClient((text,ct)=>{qwenCalls++;return Task.FromResult(text);},(text,ct)=>{argosCalls++;return Task.FromResult("公司向日本出口货物。");});
        Check(Call(offlineFallback,"The company exports goods to Japan.")=="公司向日本出口货物。"&&qwenCalls==1&&argosCalls==1&&offlineFallback.LastProvider=="Argos CPU fallback","English-only Qwen result is retranslated with the bundled Argos model");
        int copiedPhraseFallbacks=0;
        using var residualFallback=new TranslationClient((text,ct)=>Task.FromResult("提示：Do not close this window until the download is complete."),(text,ct)=>{copiedPhraseFallbacks++;return Task.FromResult("下载完成前，请勿关闭此窗口。");});
        Check(Call(residualFallback,"Do not close this window until the download is complete.")=="下载完成前，请勿关闭此窗口。"&&copiedPhraseFallbacks==1&&residualFallback.LastProvider=="Argos CPU fallback","long copied English residue triggers the local fallback instead of a partial success");
        using var untranslatedFallback=new TranslationClient((text,ct)=>Task.FromResult("Translation is unavailable."),(text,ct)=>Task.FromResult("Translation is still unavailable."));
        bool untranslatedRejected=false;try { Call(untranslatedFallback,"The application failed to start."); } catch(InvalidOperationException e) { untranslatedRejected=e.Message.Contains("未能生成中文"); }
        Check(untranslatedRejected,"an English-only result from both local models is surfaced as incomplete, not success");
        string currencies=TranslationCorrections.PreserveCurrencySymbols("$1,250.50 then €200", "人民币1,250.50元，然后200欧元");
        Check(currencies.Contains("$1,250.50")&&currencies.Contains("€200"),"multiple currencies and grouped decimal amounts keep their original symbols");
        using var punctuationClient=new TranslationClient((text,ct)=>Task.FromResult("更新 。按 Enter 完成 。"));
        Check(Call(punctuationClient,"Update. Press Enter to finish.")=="更新。按 Enter 完成。","spacing before Chinese sentence punctuation is removed without changing English key labels");
        using var currencyClient=new TranslationClient((text,ct)=>Task.FromResult("下月价格从120元升至150元。"));
        Check(Call(currencyClient,"The price rises from $120 to $150.").Contains("$120")&&Call(currencyClient,"The price rises from $120 to $150.").Contains("$150"),"currency symbols survive the complete translation and cache path");
        string Call(TranslationClient client,string text,CancellationToken ct=default)=>client.Translate(text,ct).GetAwaiter().GetResult();
        string Engine(OfflineTranslator client,string text,CancellationToken ct=default)=>client.Translate(text,ct).GetAwaiter().GetResult();
        int fallbackCalls=0;
        using var fallback=new TranslationClient((text,ct)=> { fallbackCalls++;return Task.FromResult("通过后备模型"); });
        Check(Call(fallback,"The meeting was moved from Monday to Friday at 3 PM.")=="会议原定星期一，现改至星期五下午3点。"&&fallbackCalls==0,"approved high-risk phrase repair bypasses an ambiguous model output");
        Check(Call(fallback,"Changes made before 6 p.m. today will be reviewed tomorrow.")=="今天下午6点前作出的更改，将于明天审核。"&&fallbackCalls==0,"matched variable cutoff time stays explicit");
        Check(Call(fallback,"Submit the invoice by Tuesday.")=="最迟应于星期二提交发票。"&&fallbackCalls==0,"deadline repair covers explicitly supported invoice submissions");
        Check(Call(fallback,"The refund won't be issued until 14 days after the returned package is received.")=="收到退回的商品14天后才会退款。"&&fallbackCalls==0,"refund repair covers contracted negation and package wording");
        Check(Call(fallback,"The trial lasts for 30 days after installation.")=="软件安装后的试用期为30天。"&&fallbackCalls==0,"software trial repair covers day durations");
        Check(Call(fallback,"The trial lasted for two months after installation.")=="通过后备模型"&&fallbackCalls==1,"non-matching tense falls through rather than being over-corrected");
        int calls=0;
        using var cached=new TranslationClient((text,ct)=> { calls++; return Task.FromResult("已翻译"); });
        Check(Call(cached,"Cache me")=="已翻译"&&Call(cached,"Cache me")=="已翻译"&&calls==1,"offline result cache avoids repeated inference");
        Check(Call(cached,"  ")==""&&calls==1,"empty text does not start a model");
        using(var bounded=new TranslationClient((text,ct)=>Task.FromResult(new string('译',text.Length)))) {
            for(int i=0;i<80;i++){string input=i.ToString("000")+new string('x',15990);string result=Call(bounded,input);if(result.Length!=input.Length||bounded.CachedTextBytes>TranslationClient.MaxCachedTextBytes)throw new Exception("Large translation cache exceeded its budget or changed output");}
            Check(bounded.CachedTextBytes>0&&bounded.CachedTextBytes<=TranslationClient.MaxCachedTextBytes,"80 large translations preserve outputs while cached text remains within 4 MiB");
            bounded.Dispose();Check(bounded.CachedTextBytes==0,"disposing a translation client releases its bounded text cache");
        }
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        bool cancellation=false; try { Call(cached,"Cache me",cancelled.Token); } catch(OperationCanceledException) { cancellation=true; }
        Check(cancellation&&calls==1,"pre-cancelled cached translation does not succeed or start inference");
        bool tooLong=false; try { Call(cached,new string('x',16001)); } catch(InvalidOperationException e) { tooLong=e.Message.Contains("过长"); }
        Check(tooLong&&calls==1,"oversized block is rejected explicitly before inference");
        using var traditional=new TranslationClient((text,ct)=>Task.FromResult("繁體中文，慢慢滾動。 English 123"));
        Check(Call(traditional,"Test")=="繁体中文，慢慢滚动。 English 123","offline output normalizes traditional Chinese");
        string unicode="English 中文 😀 _ token\nsecond line"; string seen="";
        using var unicodeClient=new TranslationClient((text,ct)=> { seen=text; return Task.FromResult("中文 😀 _ token"); });
        Check(Call(unicodeClient,unicode)=="中文 😀 _ token"&&seen==unicode,"Unicode and line boundaries reach the offline adapter intact");
        using var echo=new TranslationClient((text,ct)=>Task.FromResult(text));
        bool echoed=false; try { Call(echo,"Hello world"); } catch(InvalidOperationException e) { echoed=e.Message.Contains("未能生成中文")||e.Message.Contains("返回原文"); }
        Check(echoed,"unchanged multiword English is not reported as successful translation");
        using var empty=new TranslationClient((text,ct)=>Task.FromResult(" "));
        bool emptyRejected=false; try { Call(empty,"Hello"); } catch(InvalidOperationException e) { emptyRejected=e.Message.Contains("空译文"); }
        Check(emptyRejected,"empty model output fails explicitly");
        using(var realFallback=new TranslationClient()) {
            string exported=Call(realFallback,"The company exports goods to Japan.");
            Check(exported.Contains("公司")&&exported.Contains("日本")&&(exported.Contains("货物")||exported.Contains("商品"))&&(realFallback.LastProvider is "Qwen2.5 1.5B CPU" or "Argos CPU fallback"),$"installed Qwen or local Argos fallback translates trade context ({realFallback.LastProvider}: {exported})");
        }
        var blocks=new List<OcrBlock>{new(){Original="Completed",Translation="已完成"},new(){Original="Pending"},new(){Original="Last"}};
        using var failure=new TranslationClient((text,ct)=>throw new InvalidOperationException("离线模型缺失"));
        failure.Fill(blocks,new Progress<string>(),default).GetAwaiter().GetResult();
        Check(blocks[0].Translation=="已完成"&&blocks[1].Error.Contains("模型缺失")&&blocks[2].Error=="尚未翻译","model failure preserves completed blocks and marks remaining blocks");
        cached.Fill(blocks,new Progress<string>(),default).GetAwaiter().GetResult();
        Check(blocks.All(b=>b.Translation.Length>0&&b.Error.Length==0),"retry fills pending blocks and clears their errors");
        using(var missing=new OfflineTranslator(Path.Combine(folder,"nonexistent-offline-runtime"))) {
            bool missingReported=false; try { Engine(missing,"Hello world"); } catch(InvalidOperationException e) { missingReported=e.Message.Contains("缺失"); }
            Check(missingReported,"missing embedded model reports repair guidance without network fallback");
        }
        Check(OfflineTranslator.IsInstalled&&OfflineTranslator.IsArgosFallbackInstalled,"both embedded model manifests and the in-process runtime are bundled");
        var realSamples=new List<object>();
        int helpersBefore=Process.GetProcessesByName("QingTranslate").Length;
        using(var real=new OfflineTranslator()) {
            var watch=Stopwatch.StartNew();
            string translated=Engine(real,"Do not close this window until the download is complete.");
            realSamples.Add(new { source="Do not close this window until the download is complete.",translated,seconds=watch.Elapsed.TotalSeconds });
            Check(translated.Contains("不要")&&translated.Contains("下载")&&!translated.Contains('\u2581'),"real Qwen primary preserves a tested negative instruction");
            Check(Process.GetProcessesByName("QingTranslate").Length==helpersBefore,"Qwen runs in QingCapture without starting a translation helper process");
            string longInput=string.Join(" ",Enumerable.Repeat("Click Save to export the screenshot as a PNG image.",35))+" The last word is pineapple.";
            string longOutput=Engine(real,longInput);
            realSamples.Add(new { source=longInput,translated=longOutput });
            Check((longOutput.Contains("菠萝")||longOutput.Contains("凤梨"))&&longOutput.Count(c=>c=='。')>=30,"long input reaches the final sentence without silent model truncation");
            using var stop=new CancellationTokenSource();
            var pending=real.Translate(longInput,stop.Token); stop.CancelAfter(30);
            cancellation=false;watch.Restart();try { pending.GetAwaiter().GetResult(); } catch(OperationCanceledException) { cancellation=true; }
            Check(cancellation&&watch.Elapsed<TimeSpan.FromSeconds(5),"in-flight cancellation returns promptly without a child process");
            translated=Engine(real,"Click Save to export the screenshot as a PNG image.");
            Check(translated.Contains("保存")&&translated.Contains("PNG"),"translation remains usable after in-process cancellation");
            using(var argos=new OfflineTranslator(OfflineTranslator.ArgosFallbackFolder)) {
                translated=Engine(argos,"Your files stay on this computer and are never uploaded.");
                Check(translated.Contains("上传")&&Process.GetProcessesByName("QingTranslate").Length==helpersBefore,"Argos fallback translates inside the same application process");
            }
            using var queuedStop=new CancellationTokenSource();using var activeStop=new CancellationTokenSource();
            var active=real.Translate(longInput,activeStop.Token);
            var queued=real.Translate("Queued",queuedStop.Token);queuedStop.Cancel();
            cancellation=false;try { queued.GetAwaiter().GetResult(); } catch(OperationCanceledException) { cancellation=true; }
            Check(cancellation,"cancelling a queued request leaves the active native inference independent");
            activeStop.Cancel();try { active.GetAwaiter().GetResult(); } catch(OperationCanceledException) { }
        }
        using(var timeout=new OfflineTranslator(startupTimeout:TimeSpan.FromMilliseconds(1),requestTimeout:TimeSpan.FromMilliseconds(1))) {
            bool timedOut=false;try { Engine(timeout,"Translate this sentence into Chinese."); } catch(TimeoutException) { timedOut=true; }
            Check(timedOut,"in-process inference timeout returns a bounded error to the caller");
        }
        var disposable=new OfflineTranslator();Engine(disposable,"Hello world");disposable.Dispose();
        Check(Process.GetProcessesByName("QingTranslate").Length==helpersBefore,"disposing the translator leaves no helper process running");
        File.WriteAllText(Path.Combine(folder,"offline-real-translations.json"),JsonSerializer.Serialize(realSamples,new JsonSerializerOptions{WriteIndented=true}));
        return lines;
    }
}
