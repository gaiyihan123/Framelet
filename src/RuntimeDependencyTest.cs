using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace QingCapture;

/// <summary>Checks the actual native modules loaded by a completed OCR operation.</summary>
internal static class RuntimeDependencyTest
{
    private static readonly string[] CrtNames = { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" };
    private static readonly string[] OcrNames = { "tesseract50.dll", "leptonica-1.82.0.dll" };

    // Call only after real OCR: merely loading or finding DLL files is not proof that OCR works.
    public static IReadOnlyList<string> AssertAppLocalAfterOcr(string? reportPath = null)
    {
        var lines = new List<string>();
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("Native runtime acceptance requires an x64 process.");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "vc-runtime-manifest.json")));
            if (manifest.RootElement.GetProperty("architecture").GetString() != "x64")
                throw new InvalidOperationException("Native runtime manifest architecture is not x64.");
            var entries = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
            var names = entries.Select(x => x.GetProperty("name").GetString() ?? "").ToArray();
            if (names.Length != CrtNames.Length || !CrtNames.All(n => names.Count(x => string.Equals(n, x, StringComparison.OrdinalIgnoreCase)) == 1))
                throw new InvalidOperationException("Native runtime manifest must contain the three required CRT modules exactly once.");
            foreach (var entry in entries)
            {
                string name = entry.GetProperty("name").GetString()!;
                using var file = File.OpenRead(Path.Combine(root, name));
                string hash = Convert.ToHexString(SHA256.HashData(file));
                if (!string.Equals(hash, entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Native runtime hash mismatch: " + name);
                lines.Add("PASS: packaged Microsoft runtime SHA-256 " + name + " " + hash);
            }

            using var process = Process.GetCurrentProcess();
            var modules = process.Modules.Cast<ProcessModule>().ToArray();
            foreach (string name in CrtNames.Concat(OcrNames))
            {
                var found = modules.Where(m => string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Length == 0)
                    throw new InvalidOperationException("Native module was not loaded by OCR: " + name + ". Run this check immediately after successful real OCR.");
                string expectedDirectory = CrtNames.Contains(name) ? root : Path.Combine(root, "x64");
                foreach (var module in found)
                {
                    string path = Path.GetFullPath(module.FileName);
                    if (!string.Equals(Path.GetDirectoryName(path), expectedDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Native module was loaded outside the application package: " + path);
                    lines.Add("PASS: loaded application-local native module " + name + " => " + path);
                }
            }
            lines.Add("PASS: real OCR used the packaged x64 OCR and Microsoft VC runtime modules; no global VC runtime module was used for these dependencies.");
            return lines;
        }
        catch (Exception error)
        {
            lines.Add("FAIL: " + error.Message);
            throw;
        }
        finally
        {
            if (reportPath != null)
            {
                string fullPath = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllLines(fullPath, lines);
            }
        }
    }
}
