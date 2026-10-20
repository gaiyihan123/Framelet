using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

// Synthetic, headless same-process probe. It never creates an App, opens a
// screenshot, touches the clipboard or reads/writes the user's settings.
internal static class TranslationMemoryAcceptance
{
    private const string QwenSample="Do not close this window until the download is complete.";
    private const string ArgosSample="Your files stay on this computer and are never uploaded.";
    private static readonly JsonSerializerOptions JsonOptions=new() {
        WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Run(string[] args)=>RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        bool expectRelease=args.Contains("--expect-release");
        int outputIndex=Array.IndexOf(args,"--memory-report");
        if(outputIndex>=0&&outputIndex+1>=args.Length) {
            Console.Error.WriteLine("Missing --memory-report path.");return 1;
        }
        string output=Path.GetFullPath(outputIndex>=0?args[outputIndex+1]:
            "translation-memory-"+(expectRelease?"optimized":"baseline")+".json");
        var watch=Stopwatch.StartNew();
        var stages=new List<MemoryStage>();
        var checks=new List<string>();
        var translations=new List<TranslationSample>();
        var initialChildren=ChildProcesses(Environment.ProcessId).Select(p=>p.Id).ToHashSet();
        string? error=null;
        long? idlePrivateDrop=null,idleWorkingSetDrop=null;
        long? qwenOnlyIdlePrivateDrop=null,qwenOnlyIdleWorkingSetDrop=null;
        bool explicitlyReleased=false;

        void Save(string status)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output,JsonSerializer.Serialize(new {
                mode=expectRelease?"optimized":"baseline",status,
                process_id=Environment.ProcessId,process_path=Environment.ProcessPath,
                elapsed_seconds=watch.Elapsed.TotalSeconds,idle_seconds=35,
                measurement="Same-process private bytes, working set and OS peak working set; no forced GC or working-set trimming by the probe.",
                baseline_model_retention=expectRelease?null:"Original bridge keeps Qwen/Argos global engine references after requests; this baseline measures that unchanged behavior.",
                network_attempts=EmbeddedPython.NetworkAttempts,
                network_measurement="Embedded Python audit-hook attempts, not an OS-level traffic capture.",
                minimum_idle_private_drop_bytes=expectRelease?64L*1024*1024:(long?)null,
                idle_private_drop_bytes=idlePrivateDrop,idle_working_set_drop_bytes=idleWorkingSetDrop,
                qwen_only_idle_private_drop_bytes=qwenOnlyIdlePrivateDrop,
                qwen_only_idle_working_set_drop_bytes=qwenOnlyIdleWorkingSetDrop,
                stages,translations,checks,error
            },JsonOptions));
        }
        void Check(bool ok,string description)
        {
            if(!ok)throw new InvalidOperationException("FAIL: "+description);
            checks.Add("PASS: "+description);
        }
        async Task Record(string name)
        {
            JsonElement? state=null;
            if(expectRelease) {
                using var status=JsonDocument.Parse(await EmbeddedPython.ModelStateAsync().ConfigureAwait(false));
                state=status.RootElement.Clone();
            }
            using var process=Process.GetCurrentProcess();process.Refresh();
            var children=ChildProcesses(process.Id);
            stages.Add(new MemoryStage(name,watch.Elapsed.TotalSeconds,
                process.PrivateMemorySize64,process.WorkingSet64,process.PeakWorkingSet64,children,state));
            Save("running");
            Check(children.All(p=>initialChildren.Contains(p.Id)),name+" starts no child translation/helper process");
        }
        void CheckReleased(string name)
        {
            JsonElement state=stages.Last(s=>s.Stage==name).ModelState??throw new InvalidOperationException("Model state is missing.");
            Check(!state.GetProperty("qwen_loaded").GetBoolean()&&!state.GetProperty("argos_loaded").GetBoolean(),name+" has no loaded Qwen/Argos models");
            Check(state.GetProperty("network_attempts").GetInt32()==0,name+" reports no audited network attempts");
            string? releaseError=state.GetProperty("release_error").GetString();
            Check(string.IsNullOrEmpty(releaseError),name+" has no recorded release error");
        }
        async Task<string> Translate(OfflineTranslator translator,string provider,string source,string stage)
        {
            var timer=Stopwatch.StartNew();
            string result=await translator.Translate(source,CancellationToken.None).ConfigureAwait(false);
            translations.Add(new TranslationSample(stage,provider,source,result,timer.Elapsed.TotalSeconds));
            Check(!string.IsNullOrWhiteSpace(result),stage+" produces nonempty output");
            Check(EmbeddedPython.NetworkAttempts==0,stage+" makes no audited network attempts");
            await Record(stage).ConfigureAwait(false);return result;
        }

        try {
            // Constructing the production client must not itself load a model.
            using var client=new TranslationClient();
            await Record("new_client_before_translation").ConfigureAwait(false);
            if(expectRelease) {
                JsonElement state=stages[^1].ModelState!.Value;
                Check(!state.GetProperty("initialized").GetBoolean(),"constructing a client and reading model state do not initialize embedded Python");
                CheckReleased("new_client_before_translation");
            }
            using var qwen=new OfflineTranslator();
            using var argos=new OfflineTranslator(OfflineTranslator.ArgosFallbackFolder);
            string qwenFirst=await Translate(qwen,"Qwen",QwenSample,"qwen_first_translation");
            if(expectRelease) {
                MemoryStage loaded=stages[^1];
                JsonElement state=loaded.ModelState!.Value;
                Check(state.GetProperty("qwen_loaded").GetBoolean()&&!state.GetProperty("argos_loaded").GetBoolean(),"Qwen-only idle probe begins with only Qwen loaded");
                Save("waiting_for_qwen_only_35_second_idle");
                await Task.Delay(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
                await Record("qwen_only_after_idle_35_seconds").ConfigureAwait(false);
                CheckReleased("qwen_only_after_idle_35_seconds");
                MemoryStage idle=stages[^1];
                qwenOnlyIdlePrivateDrop=loaded.PrivateBytes-idle.PrivateBytes;
                qwenOnlyIdleWorkingSetDrop=loaded.WorkingSetBytes-idle.WorkingSetBytes;
                Check(qwenOnlyIdlePrivateDrop>=64L*1024*1024,"Qwen-only automatic idle unload releases at least 64 MiB of private memory");
                string qwenAfterOwnIdle=await Translate(qwen,"Qwen",QwenSample,"qwen_same_text_after_qwen_only_idle");
                Check(qwenAfterOwnIdle==qwenFirst,"Qwen reloads with the same output after its own automatic idle unload");
            }
            string argosFirst=await Translate(argos,"Argos",ArgosSample,"argos_first_translation");
            Check(qwenFirst.Contains("下载")&&(qwenFirst.Contains("不要")||qwenFirst.Contains("勿")),"Qwen preserves the tested download prohibition");
            Check(argosFirst.Contains("上传"),"Argos keeps the never-uploaded context");
            Save("waiting_for_35_second_idle");
            await Task.Delay(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
            await Record("after_idle_35_seconds").ConfigureAwait(false);
            if(expectRelease) {
                CheckReleased("after_idle_35_seconds");
                MemoryStage loaded=stages.Single(s=>s.Stage=="argos_first_translation"),idle=stages[^1];
                idlePrivateDrop=loaded.PrivateBytes-idle.PrivateBytes;
                idleWorkingSetDrop=loaded.WorkingSetBytes-idle.WorkingSetBytes;
                Check(idlePrivateDrop>=64L*1024*1024,"idle unload releases at least 64 MiB of process private memory, not merely the working set");
            }
            string qwenAgain=await Translate(qwen,"Qwen",QwenSample,"qwen_same_text_after_idle");
            string argosAgain=await Translate(argos,"Argos",ArgosSample,"argos_same_text_after_idle");
            Check(qwenAgain==qwenFirst,"Qwen same-text output remains stable after idle");
            Check(argosAgain==argosFirst,"Argos same-text output remains stable after idle");
            if(expectRelease) {
                await EmbeddedPython.ReleaseModelsAsync().ConfigureAwait(false);explicitlyReleased=true;
                await Record("after_explicit_release").ConfigureAwait(false);
                CheckReleased("after_explicit_release");
                explicitlyReleased=false;
                string qwenExplicit=await Translate(qwen,"Qwen",QwenSample,"qwen_same_text_after_explicit_release");
                string argosExplicit=await Translate(argos,"Argos",ArgosSample,"argos_same_text_after_explicit_release");
                Check(qwenExplicit==qwenFirst,"Qwen can reload after explicit release without changing the same-text output");
                Check(argosExplicit==argosFirst,"Argos can reload after explicit release without changing the same-text output");
                await EmbeddedPython.ReleaseModelsAsync().ConfigureAwait(false);explicitlyReleased=true;
                await Record("after_final_release").ConfigureAwait(false);
                CheckReleased("after_final_release");
            }
            await Record("before_process_exit").ConfigureAwait(false);
            Save("passed");return 0;
        } catch(Exception ex) {
            error=ex.ToString();Save("failed");Console.Error.WriteLine(error);return 1;
        } finally {
            // A failed optimized probe still releases only after real native
            // inference completes under the production gate. Baseline must
            // retain its original behavior for an honest before comparison.
            if(expectRelease&&!explicitlyReleased) {
                try { await EmbeddedPython.ReleaseModelsAsync().ConfigureAwait(false); }
                catch(Exception releaseFailure) {
                    error=(error??"")+"\nFinal model release: "+releaseFailure;
                    Save("failed");Console.Error.WriteLine(releaseFailure);
                }
            }
        }
    }

    private sealed record MemoryStage(string Stage,double ElapsedSeconds,long PrivateBytes,long WorkingSetBytes,long PeakWorkingSetBytes,List<ChildProcess> ChildProcesses,JsonElement? ModelState);
    private sealed record TranslationSample(string Stage,string Provider,string Source,string Output,double Seconds);
    private sealed record ChildProcess(int Id,string Name);

    private static List<ChildProcess> ChildProcesses(int parentId)
    {
        const uint snapshotProcesses=2;
        IntPtr snapshot=CreateToolhelp32Snapshot(snapshotProcesses,0);
        if(snapshot==new IntPtr(-1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            var entry=new ProcessEntry { Size=(uint)Marshal.SizeOf<ProcessEntry>(),ExeFile="" };
            var children=new List<ChildProcess>();
            if(!Process32First(snapshot,ref entry)) {
                int failure=Marshal.GetLastWin32Error();
                if(failure!=18)throw new System.ComponentModel.Win32Exception(failure);
                return children;
            }
            do {
                if(entry.ParentProcessId==(uint)parentId)children.Add(new ChildProcess((int)entry.ProcessId,entry.ExeFile));
            } while(Process32Next(snapshot,ref entry));
            int lastError=Marshal.GetLastWin32Error();
            if(lastError!=18)throw new System.ComponentModel.Win32Exception(lastError);
            return children;
        } finally { CloseHandle(snapshot); }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size,Usage,ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId,Threads,ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string ExeFile;
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint processId);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="Process32FirstW")]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="Process32NextW")]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseHandle(IntPtr handle);
}
