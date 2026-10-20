using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

// Both packaged CPU models run inside QingCapture.exe. Native inference cannot
// safely be force-killed; cancellation stops waiting while the current native
// call finishes under the global gate before another request may start.
internal static class EmbeddedPython
{
    private static readonly object initialization=new();
    private static readonly SemaphoreSlim inference=new(1,1);
    private static bool ready;
    private static Exception? initializationError;
    private static IntPtr module,qwenFunction,argosFunction,releaseFunction,stateFunction;
    private static int networkAttempts;
    private static string? releaseError;
    private static readonly ModelIdleLifetime lifetime=new(TimeSpan.FromSeconds(30),ReleaseIfIdle,ex=>Volatile.Write(ref releaseError,ex.Message));
    internal static int NetworkAttempts=>Volatile.Read(ref networkAttempts);

    public static async Task<string> Translate(bool qwen,string text,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var request=lifetime.BeginRequest();
        // The lease ends only when the actual native call completes, including
        // after its UI wait was cancelled or timed out.
        return await RunSerialized(inference,()=>Execute(qwen,text),cancellation).ConfigureAwait(false);
    }
    private static async Task ReleaseIfIdle(long expected)
    {
        await inference.WaitAsync().ConfigureAwait(false);
        try {if(lifetime.IsExpired(expected))ReleaseModels();}
        finally {inference.Release();}
    }
    internal static async Task ReleaseModelsAsync()
    {
        await inference.WaitAsync().ConfigureAwait(false);
        try {ReleaseModels();}finally {inference.Release();}
    }
    private static void ReleaseModels()
    {
        if(!ready)return;CallControl(releaseFunction);Volatile.Write(ref releaseError,null);
    }
    internal static async Task<string> ModelStateAsync()
    {
        await inference.WaitAsync().ConfigureAwait(false);
        try {
            // Observation and cleanup never initialize a model or CPython.
            if(!ready)return JsonSerializer.Serialize(new {initialized=false,qwen_loaded=false,argos_loaded=false,network_attempts=NetworkAttempts,release_error=Volatile.Read(ref releaseError)});
            using var state=JsonDocument.Parse(CallControl(stateFunction));
            return JsonSerializer.Serialize(new {initialized=true,qwen_loaded=state.RootElement.GetProperty("qwen_loaded").GetBoolean(),argos_loaded=state.RootElement.GetProperty("argos_loaded").GetBoolean(),network_attempts=NetworkAttempts,release_error=Volatile.Read(ref releaseError)});
        }finally {inference.Release();}
    }
    private static string CallControl(IntPtr function)
    {
        int gil=PyGILState_Ensure();IntPtr result=IntPtr.Zero;
        try {
            result=PyObject_CallNoArgs(function);
            if(result==IntPtr.Zero){PyErr_Clear();throw new InvalidOperationException("离线翻译模型资源释放或状态检查失败。");}
            IntPtr data=PyUnicode_AsUTF8AndSize(result,out nint length);
            if(data==IntPtr.Zero||length<0||length>262144){PyErr_Clear();throw new InvalidOperationException("离线模型状态响应无效。");}
            var bytes=new byte[(int)length];Marshal.Copy(data,bytes,0,bytes.Length);return Encoding.UTF8.GetString(bytes);
        }finally {if(result!=IntPtr.Zero)Py_DecRef(result);PyGILState_Release(gil);}
    }
    internal static Task<string> RunSerialized(SemaphoreSlim gate,Func<string> execute,CancellationToken cancellation)
    {
        return Task.Run(async()=> {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try { cancellation.ThrowIfCancellationRequested(); return execute(); }
            finally { gate.Release(); }
        },CancellationToken.None);
    }
    private static string Execute(bool qwen,string text)
    {
        EnsureInitialized();
        int gil=PyGILState_Ensure();
        IntPtr argument=IntPtr.Zero,result=IntPtr.Zero;
        try {
            byte[] utf8=Encoding.UTF8.GetBytes(text);
            argument=PyUnicode_FromStringAndSize(utf8,utf8.Length);
            if(argument==IntPtr.Zero) throw new InvalidOperationException("无法向内置翻译模型传入文本。");
            result=PyObject_CallOneArg(qwen?qwenFunction:argosFunction,argument);
            if(result==IntPtr.Zero) { PyErr_Clear(); throw new InvalidOperationException("内置离线模型未能完成翻译，请重试或手动修改译文。"); }
            IntPtr data=PyUnicode_AsUTF8AndSize(result,out nint length);
            if(data==IntPtr.Zero||length<0||length>262144) { PyErr_Clear(); throw new InvalidOperationException("内置离线模型响应无效。"); }
            var bytes=new byte[(int)length];Marshal.Copy(data,bytes,0,bytes.Length);
            using var response=JsonDocument.Parse(bytes);
            Interlocked.Exchange(ref networkAttempts,response.RootElement.GetProperty("network_attempts").GetInt32());
            if(NetworkAttempts!=0) throw new InvalidOperationException("离线模型检测到异常网络调用，已停止翻译。");
            return response.RootElement.GetProperty("text").GetString()??"";
        } finally {
            if(result!=IntPtr.Zero) Py_DecRef(result);
            if(argument!=IntPtr.Zero) Py_DecRef(argument);
            PyGILState_Release(gil);
        }
    }
    private static void EnsureInitialized()
    {
        lock(initialization) {
            if(ready) return;
            if(initializationError!=null) throw new InvalidOperationException("内置离线运行环境初始化失败，请重新安装完整安装包。",initializationError);
            try {
                string root=Path.Combine(AppContext.BaseDirectory,"offline");
                string embedded=Path.Combine(root,"embedded"),native=Path.Combine(root,"native");
                string python=Path.Combine(native,"python313.dll"),stdlib=Path.Combine(embedded,"python313.zip"),site=Path.Combine(embedded,"site");
                if(!File.Exists(python)||!File.Exists(stdlib)||!File.Exists(Path.Combine(root,"inprocess_bridge.py"))) throw new FileNotFoundException("内置翻译运行环境缺失");
                Environment.SetEnvironmentVariable("PYTHONNOUSERSITE","1");
                Environment.SetEnvironmentVariable("HF_HUB_OFFLINE","1");
                Environment.SetEnvironmentVariable("TRANSFORMERS_OFFLINE","1");
                var library=NativeLibrary.Load(python);
                Marshal.WriteInt32(NativeLibrary.GetExport(library,"Py_NoSiteFlag"),1);
                Marshal.WriteInt32(NativeLibrary.GetExport(library,"Py_DontWriteBytecodeFlag"),1);
                Py_SetPath(string.Join(Path.PathSeparator,stdlib,site,native,root));
                Py_InitializeEx(0);
                try {
                    if(PyRun_SimpleString("import inprocess_bridge")!=0) throw new InvalidOperationException("内置翻译模块无法导入");
                    module=PyImport_ImportModule("inprocess_bridge");
                    if(module==IntPtr.Zero) throw new InvalidOperationException("内置翻译模块不可用");
                    qwenFunction=PyObject_GetAttrString(module,"translate_qwen");
                    argosFunction=PyObject_GetAttrString(module,"translate_argos");
                    releaseFunction=PyObject_GetAttrString(module,"release_models");
                    stateFunction=PyObject_GetAttrString(module,"model_state");
                    if(qwenFunction==IntPtr.Zero||argosFunction==IntPtr.Zero||releaseFunction==IntPtr.Zero||stateFunction==IntPtr.Zero) throw new InvalidOperationException("内置翻译入口缺失");
                    ready=true;
                } finally { PyEval_SaveThread(); }
            } catch(Exception error) {
                initializationError=error;
                throw new InvalidOperationException("内置离线运行环境初始化失败，请重新安装完整安装包。",error);
            }
        }
    }
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] private static extern void Py_SetPath(string path);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern void Py_InitializeEx(int initsigs);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int PyRun_SimpleString([MarshalAs(UnmanagedType.LPUTF8Str)] string code);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr PyEval_SaveThread();
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int PyGILState_Ensure();
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern void PyGILState_Release(int state);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr PyImport_ImportModule([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr PyObject_GetAttrString(IntPtr item,[MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr PyUnicode_FromStringAndSize(byte[] value,nint size);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr PyObject_CallOneArg(IntPtr callable,IntPtr argument);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr PyObject_CallNoArgs(IntPtr callable);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr PyUnicode_AsUTF8AndSize(IntPtr value,out nint size);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern void Py_DecRef(IntPtr value);
    [DllImport("python313.dll",CallingConvention=CallingConvention.Cdecl)] private static extern void PyErr_Clear();
}
