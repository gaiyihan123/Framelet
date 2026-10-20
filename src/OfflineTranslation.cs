using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

// The packaged native libraries are loaded into QingCapture.exe; translation
// does not start a helper executable, service, localhost server, or API call.
internal sealed class OfflineTranslator : IDisposable
{
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory,"offline","qwen");
    private static bool EmbeddedAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory,"offline","embedded","python313.zip")) && File.Exists(Path.Combine(AppContext.BaseDirectory,"offline","inprocess_bridge.py"));
    public static bool IsInstalled => EmbeddedAvailable && File.Exists(Path.Combine(AppContext.BaseDirectory,"offline","native","python313.dll")) && File.Exists(Path.Combine(DefaultFolder,"model","manifest.json"));
    public static string ArgosFallbackFolder => Path.Combine(AppContext.BaseDirectory,"offline");
    public static bool IsArgosFallbackInstalled => EmbeddedAvailable && File.Exists(Path.Combine(ArgosFallbackFolder,"embedded","site","ctranslate2","ctranslate2.dll")) && File.Exists(Path.Combine(ArgosFallbackFolder,"native","python313.dll")) && File.Exists(Path.Combine(ArgosFallbackFolder,"model","manifest.json"));
    private readonly string folder;
    private readonly bool qwen;
    private readonly TimeSpan startupTimeout,requestTimeout;
    private bool disposed;
    private bool started;
    public OfflineTranslator(string? folder=null,TimeSpan? startupTimeout=null,TimeSpan? requestTimeout=null)
    {
        folder ??= DefaultFolder;
        this.folder=Path.GetFullPath(folder);
        qwen=string.Equals(this.folder,Path.GetFullPath(DefaultFolder),StringComparison.OrdinalIgnoreCase);
        this.startupTimeout=startupTimeout??TimeSpan.FromSeconds(60);
        this.requestTimeout=requestTimeout??TimeSpan.FromSeconds(90);
    }
    public async Task<string> Translate(string text,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!EmbeddedAvailable||!File.Exists(Path.Combine(folder,"model","manifest.json"))||(qwen&&!IsInstalled)||(!qwen&&!IsArgosFallbackInstalled))
            throw new InvalidOperationException("内置离线翻译组件或模型缺失，请重新安装完整安装包；不会转为在线翻译。");
        TimeSpan limit=started?requestTimeout:startupTimeout+requestTimeout;
        started=true;
        try { return await AwaitRequest(token=>EmbeddedPython.Translate(qwen,text,token),limit,ct).ConfigureAwait(false); }
        catch(TimeoutException) { throw new TimeoutException("内置离线翻译超时；本次结果已丢弃，可稍后重试或减少文字量。"); }
    }
    internal static async Task<string> AwaitRequest(Func<CancellationToken,Task<string>> start,TimeSpan limit,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var requestStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<string> request;
        try { request=start(requestStop.Token); }
        catch { requestStop.Dispose(); throw; }
        try { string result=await request.WaitAsync(limit,cancellation).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested(); return result; }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { cancellation.ThrowIfCancellationRequested(); throw; }
        catch(TimeoutException) when(cancellation.IsCancellationRequested) { cancellation.ThrowIfCancellationRequested(); throw; }
        finally {
            // Invalidate queued work before reporting timeout/cancellation. A native
            // call already running still owns the gate until its real completion.
            try { requestStop.Cancel(); }
            finally { _=ObserveAndDispose(request,requestStop); }
        }
    }
    private static async Task ObserveAndDispose(Task<string> request,CancellationTokenSource requestStop)
    {
        try { await request.ConfigureAwait(false); }
        catch { /* Late results and failures belong to the expired request. */ }
        finally { requestStop.Dispose(); }
    }
    public void Dispose() { disposed=true; }
}

internal sealed class TranslationClient : IDisposable
{
    private static readonly Regex CopiedEnglishRun=new(@"\b(?:[A-Za-z]+(?:['-][A-Za-z]+)?\s+){4}[A-Za-z]+(?:['-][A-Za-z]+)?\b",RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(50));
    private readonly OfflineTranslator? worker,fallbackWorker;
    private readonly Func<string,CancellationToken,Task<string>> translate;
    private readonly Func<string,CancellationToken,Task<string>>? fallbackTranslate;
    private readonly ConcurrentDictionary<string,string> cache=new();
    private readonly object cacheSync=new();
    private long cachedTextBytes;
    internal const long MaxCachedTextBytes=4*1024*1024;
    internal long CachedTextBytes {get {lock(cacheSync)return cachedTextBytes;}}
    public string LastProvider { get; private set; }="";
    public TranslationClient() {
        worker=new OfflineTranslator(); translate=worker.Translate;
        if(OfflineTranslator.IsArgosFallbackInstalled){fallbackWorker=new OfflineTranslator(OfflineTranslator.ArgosFallbackFolder);fallbackTranslate=fallbackWorker.Translate;}
    }
    // Dependency injection is for deterministic failure/cancellation tests only.
    internal TranslationClient(Func<string,CancellationToken,Task<string>> translate) { this.translate=translate; }
    internal TranslationClient(Func<string,CancellationToken,Task<string>> translate,Func<string,CancellationToken,Task<string>>? fallbackTranslate) { this.translate=translate; this.fallbackTranslate=fallbackTranslate; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern int LCMapStringEx(string locale,uint flags,string source,int sourceLength,[Out] char[]? destination,int destinationLength,IntPtr version,IntPtr reserved,IntPtr sortHandle);
    private static string Simplified(string value)
    {
        const uint simplifiedChinese=0x02000000;
        int length=LCMapStringEx("zh-CN",simplifiedChinese,value,value.Length,null,0,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(length==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法转换简体中文");
        var result=new char[length];
        int written=LCMapStringEx("zh-CN",simplifiedChinese,value,value.Length,result,length,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(written==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法转换简体中文");
        return new string(result,0,written);
    }
    private static bool ContainsCopiedEnglishRun(string source,string translation)
    {
        foreach(Match match in CopiedEnglishRun.Matches(translation)) {
            string phrase=Regex.Replace(match.Value,@"\s+"," ").Trim();
            if(source.Contains(phrase,StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    public async Task<string> Translate(string text,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(text.Length>16000) throw new InvalidOperationException("单段文字过长，请缩小截图范围后重试。");
        if(string.IsNullOrWhiteSpace(text)) return "";
        if(cache.TryGetValue(text,out var old)) { LastProvider="translation cache"; return old; }
        string value;
        if(TranslationCorrections.TryTranslate(text,out var reviewed)) { value=reviewed; LastProvider="local phrase rule"; }
        else {
            value=await translate(text,ct).ConfigureAwait(false); LastProvider="Qwen2.5 1.5B CPU";
            // An all-English response or a copied five-word source phrase is a
            // likely omission. Try the second local model instead of silently
            // presenting a partial translation as complete.
            bool noChinese=!Regex.IsMatch(value,@"[\u4e00-\u9fff]");
            bool copiedSourcePhrase=ContainsCopiedEnglishRun(text,value);
            if((noChinese||copiedSourcePhrase)&&fallbackTranslate!=null) {
                value=await fallbackTranslate(text,ct).ConfigureAwait(false);
                LastProvider="Argos CPU fallback";
                noChinese=!Regex.IsMatch(value,@"[\u4e00-\u9fff]");
                copiedSourcePhrase=ContainsCopiedEnglishRun(text,value);
            }
            if(noChinese&&Regex.Matches(text,@"\b[A-Za-z]{2,}\b").Count>=2)
                throw new InvalidOperationException("离线模型未能生成中文译文，请核对原文或手动编辑；不会改用在线翻译。");
            if(copiedSourcePhrase)
                throw new InvalidOperationException("离线模型仍保留了较长的原文片段，未确认译文完整；请核对原文或手动编辑。");
        }
        ct.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("离线模型返回空译文，请重试或修改译文。");
        if(value.Trim().Equals(text.Trim(),StringComparison.OrdinalIgnoreCase)&&Regex.Matches(text,"[A-Za-z]+").Count>=2)
            throw new InvalidOperationException("离线模型返回原文，未确认翻译成功。请重试或修改译文。");
        string originalValue=value.Trim();
        value=Simplified(TranslationCorrections.PreserveCurrencySymbols(text,originalValue));
        if(value!=originalValue&&!LastProvider.Contains("currency safeguard",StringComparison.Ordinal)) LastProvider+=" + currency safeguard";
        value=Regex.Replace(value,@"\s+([，。！？；：、])","$1");
        value=Regex.Replace(value,@"([\p{IsCJKUnifiedIdeographs}0-9%])\.(?=$|\s)","$1。",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
        long bytes=((long)text.Length+value.Length)*sizeof(char);
        lock(cacheSync) {
            // Retain inexpensive repeated-line translations, not hundreds of
            // large documents. This bounds text payloads as well as entry count.
            if(bytes<=MaxCachedTextBytes) {
                if(cache.Count>=256||cachedTextBytes+bytes>MaxCachedTextBytes){cache.Clear();cachedTextBytes=0;}
                if(cache.TryAdd(text,value))cachedTextBytes+=bytes;
            }
        }
        return value;
    }
    public async Task Fill(List<OcrBlock> blocks,IProgress<string> progress,CancellationToken ct)
    {
        for(int i=0;i<blocks.Count;i++) {
            ct.ThrowIfCancellationRequested(); var block=blocks[i]; if(block.Translation.Length>0) continue;
            progress.Report($"本机离线翻译  {i+1} / {blocks.Count}（首次加载模型，可取消）");
            try { block.Translation=await Translate(block.Original,ct).ConfigureAwait(false); block.Error=""; }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex) {
                block.Error=ex.Message;
                for(int j=i+1;j<blocks.Count;j++) if(blocks[j].Translation.Length==0) blocks[j].Error="尚未翻译";
                return;
            }
        }
    }
    public void Dispose() { worker?.Dispose(); fallbackWorker?.Dispose();lock(cacheSync){cache.Clear();cachedTextBytes=0;} }
}
