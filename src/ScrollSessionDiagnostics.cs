using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QingCapture;

// Only bounded session metadata belongs here: no screenshots or source text.
internal static class ScrollSessionDiagnostics
{
    private const int MaximumBytes=32*1024;
    private static readonly JsonSerializerOptions options=new() {
        WriteIndented=true,
        NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    internal static void Save(object value)
    {
        if(Array.Exists(Environment.GetCommandLineArgs(),arg=>
            string.Equals(arg,"--ui-self-test",StringComparison.OrdinalIgnoreCase)||
            string.Equals(arg,"--self-test",StringComparison.OrdinalIgnoreCase)))return;
        string? temporary=null;
        try {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(value,options);
            if(bytes.Length>MaximumBytes)return; // Never leave truncated JSON.
            Directory.CreateDirectory(App.DataPath);
            string destination=Path.Combine(App.DataPath,"latest-scroll-session.json");
            temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllBytes(temporary,bytes);
            File.Move(temporary,destination,true);
        }catch {/* Diagnostic serialization or I/O must not affect capture. */}
        finally {
            if(temporary!=null)try{File.Delete(temporary);}catch { }
        }
    }
}
