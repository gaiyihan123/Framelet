using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace QingCapture;

// A repeatable, synthetic semantic probe for the exact offline model shipped.
// Raw inputs and original model outputs stay visible beside the post-fix result.
internal static class OfflineQualityAudit
{
    private static (string Name,string Hash) ModelMetadata(bool argos)
    {
        if(argos) {
            string root=Path.Combine(OfflineTranslator.ArgosFallbackFolder,"model");
            using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json")));
            using var data=File.OpenRead(Path.Combine(root,"model","model.bin"));
            string actual=Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if(actual!=metadata.RootElement.GetProperty("files").GetProperty("model/model.bin").GetString())throw new InvalidOperationException("Argos model report integrity mismatch");
            return ("Argos en-zh 1.9 CPU test primary",actual);
        }
        string folder=Path.Combine(OfflineTranslator.DefaultFolder,"model");
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"manifest.json")));
        string filename=manifest.RootElement.GetProperty("file").GetString()??"";
        if(Path.GetFileName(filename)!=filename||string.IsNullOrWhiteSpace(filename))throw new InvalidOperationException("Invalid model filename");
        using var model=File.OpenRead(Path.Combine(folder,filename));
        string hash=Convert.ToHexString(SHA256.HashData(model)).ToLowerInvariant();
        if(hash!=manifest.RootElement.GetProperty("sha256").GetString())throw new InvalidOperationException("Model report integrity mismatch");
        return (manifest.RootElement.GetProperty("model").GetString()??"Qwen",hash);
    }
    public static int Run(string[] args)
    {
        string Value(string option) { int i=Array.IndexOf(args,option); if(i<0||i+1==args.Length) throw new ArgumentException("Missing "+option); return args[i+1]; }
        string input=Path.GetFullPath(Value("--quality-input")),output=Path.GetFullPath(Value("--quality-report"));
        using var source=JsonDocument.Parse(File.ReadAllText(input));
        var results=new List<object>(); var timer=Stopwatch.StartNew();
        bool useArgos=args.Contains("--quality-use-argos");int errors=0;
        try {
            using var argos=useArgos?new OfflineTranslator(OfflineTranslator.ArgosFallbackFolder):null;
            using var translator=useArgos?new TranslationClient(argos!.Translate):new TranslationClient();
            foreach(var item in source.RootElement.GetProperty("results").EnumerateArray()) {
                string english=item.GetProperty("source").GetString()!;
                string baseline=item.GetProperty("translation").GetString()!;
                string translation="",treatment="",error="";
                try {translation=translator.Translate(english,CancellationToken.None).GetAwaiter().GetResult();treatment=translator.LastProvider;}
                catch(Exception ex){errors++;error=ex.Message;treatment="translation failed";}
                if(useArgos)treatment=treatment.Replace("Qwen2.5 1.5B CPU","Argos CPU test primary",StringComparison.Ordinal);
                results.Add(new {
                    id=item.GetProperty("id").GetInt32(),
                    category=item.GetProperty("category").GetString(),
                    source=english,
                    expected_meaning=item.GetProperty("expected_meaning").GetString(),
                    expected_output=item.TryGetProperty("expected_output",out var expected)?expected.GetString():null,
                    before_1_1_0=baseline,
                    before_1_1_1=item.TryGetProperty("after_1_1_1",out var patched)?patched.GetString():null,
                    after_1_3_0=translation,
                    treatment,error
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var metadata=ModelMetadata(useArgos);
            var report=new {
                version="1.3.0",model=metadata.Name+(useArgos?" / isolated primary-provider evaluation":" / CPU-only llama.cpp with Argos CPU fallback for English-only or copied-English output"),
                model_sha256=metadata.Hash,
                cases=results.Count,error_count=errors,elapsed_seconds=timer.Elapsed.TotalSeconds,
                network_calls=EmbeddedPython.NetworkAttempts,
                network_measurement="Embedded Python audit-hook attempts; not an OS-level traffic capture.",
                notes="Only explicitly matched English templates use deterministic rules. Other lines use Qwen; English-only or a long copied source phrase triggers Argos locally, and output still untranslated is surfaced as an error. Compare every output against expected_meaning; this targeted set is not an accuracy benchmark.",
                results
            };
            File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
            return errors==0?0:1;
        } catch { return 1; }
    }
}
