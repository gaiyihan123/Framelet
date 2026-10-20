using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

internal static class ClipboardAcceptance
{
    /// <summary>Default regression: checks real PNG, DIB, DIBV5 and native Bitmap pixels without opening the clipboard.</summary>
    public static List<string> Run()
    {
        var lines = new List<string>();
        foreach (var size in new[] { new Size(1, 1), new Size(3, 2), new Size(7, 5), new Size(445, 391) })
        {
            using var fixture = Fixture(size);
            var payload = ClipboardImages.CreatePayload(fixture);
            if (payload.Width != size.Width || payload.Height != size.Height) throw new Exception("FAIL: clipboard image dimensions changed");
            if (!payload.Png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new Exception("FAIL: clipboard PNG contains a serializer wrapper instead of actual PNG bytes");
            using (var stream = new MemoryStream(payload.Png, false))
            using (var png = new Bitmap(stream))
                Compare(fixture, png, false, "PNG");
            CheckDib(fixture, payload.Dib, false);
            CheckDib(fixture, payload.DibV5, true);
            using (var bitmap = ClipboardImages.DecodeBitmapFallback(payload)) Compare(fixture, bitmap, true, "native Bitmap");
            using (var duplicate = ClipboardImages.DecodeDuplicatedBitmapFallback(payload)) Compare(fixture, duplicate, true, "backup DDB Bitmap");
            lines.Add($"PASS: {size.Width}x{size.Height} clipboard PNG/DIBV5 preserve every RGBA pixel; DIB/native DDB Bitmap and its backup preserve opaque RGB and white-composite transparency; bottom-up rows and odd-width padding correct (clipboard untouched)");
        }
        using (var cancelled = Fixture(new Size(3, 2)))
        {
            try { ClipboardImages.CreatePayload(cancelled, new CancellationToken(true)); throw new Exception("FAIL: cancelled image copy was prepared"); }
            catch (OperationCanceledException) { lines.Add("PASS: cancelled clipboard preparation stops before any clipboard access"); }
        }
        try { ClipboardImages.CreatePayload(null!); throw new Exception("FAIL: null clipboard image was accepted"); }
        catch (ArgumentNullException) { lines.Add("PASS: null clipboard images fail clearly before clipboard access"); }
        return lines;
    }

    /// <summary>
    /// Optional explicit native read/write test. Original formats are saved/restored while one clipboard lock is held.
    /// Rejects uncloneable prior formats before altering contents. Keep separate from automatic UI/self-test entry points.
    /// </summary>
    public static async Task<List<string>> RunRoundTripAsync(CancellationToken cancellationToken = default)
    {
        var lines = Run();
        using var fixture = Fixture(new Size(7, 5));
        await ClipboardImages.VerifyNativeRoundTripAsync(ClipboardImages.CreatePayload(fixture, cancellationToken), cancellationToken).ConfigureAwait(false);
        lines.Add("PASS: explicit native clipboard round-trip stores real PNG, image/png, DIB, DIBV5 and Bitmap; all bytes/pixels read back and original clipboard restored before releasing the lock");
        return lines;
    }

    /// <summary>
    /// Explicit GUI-test setup: save the current clipboard, release its lock, run the editor and paste locally.
    /// In finally, await lease.RestoreAsync; false means a newer external clipboard was intentionally preserved.
    /// The lease and tested editor must share a process. Dispose only releases the backup; it does not restore implicitly.
    /// </summary>
    internal static Task<ClipboardImages.ClipboardSnapshotLease> PreserveForGuiTestAsync(CancellationToken cancellationToken = default)
        => ClipboardImages.PreserveForTestAsync(cancellationToken);

    /// <summary>Child-process endpoint. The host must hold its clipboard snapshot lease before launching this explicitly.</summary>
    internal static async Task<List<string>> WriteLifetimeFixtureAsync(CancellationToken cancellationToken = default)
    {
        var lines = Run();
        using var fixture = Fixture(new Size(7, 5));
        try { await ClipboardImages.CopyAsync(fixture, cancellationToken).ConfigureAwait(false); }
        finally
        {
            Console.WriteLine("CLIPBOARD_TEST_SEQUENCE=" + ClipboardImages.LastWrittenSequenceForTest);
            Console.Out.Flush();
        }
        lines.Add("PASS: explicit child writer published its native lifetime fixture; host must verify after this process exits");
        return lines;
    }

    /// <summary>
    /// Explicit cross-process acceptance. Requires the app to route --clipboard-lifetime-writer to WriteLifetimeFixtureAsync
    /// before normal startup/single-instance forwarding. The host process keeps the original clipboard backup alive throughout.
    /// </summary>
    internal static async Task<List<string>> RunCrossProcessLifetimeAsync(string applicationPath, string? reportDirectory = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = Run();
        using var lease = await PreserveForGuiTestAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo(applicationPath) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--clipboard-lifetime-writer");
            if (reportDirectory != null) { start.ArgumentList.Add("--report-dir"); start.ArgumentList.Add(reportDirectory); }
            using var writer = Process.Start(start) ?? throw new InvalidOperationException("无法启动剪贴板寿命验收写入进程。");
            var stdoutTask = writer.StandardOutput.ReadToEndAsync();
            var stderrTask = writer.StandardError.ReadToEndAsync();
            // Once the writer starts, finish its bounded clipboard operation before observing cancellation and restoring.
            // Killing it halfway through a write would lose the trustworthy sequence handshake.
            await writer.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false), stderr = await stderrTask.ConfigureAwait(false);
            uint sequence = 0; int markers = 0;
            foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.StartsWith("CLIPBOARD_TEST_SEQUENCE=", StringComparison.Ordinal))
                {
                    markers++;
                    if (!uint.TryParse(line.AsSpan("CLIPBOARD_TEST_SEQUENCE=".Length), out sequence))
                        throw new InvalidOperationException("写入进程没有返回有效剪贴板序列号。");
                }
            if (markers != 1) throw new InvalidOperationException("写入进程没有返回唯一剪贴板序列号；无法把当前剪贴板认定为验收写入。");
            using var fixture = Fixture(new Size(7, 5));
            // Adoption/verification occurs before exit-code/cancellation errors, allowing the lease to restore our partial writes too.
            await ClipboardImages.VerifyWriterExitAsync(ClipboardImages.CreatePayload(fixture), sequence, CancellationToken.None).ConfigureAwait(false);
            if (writer.ExitCode != 0) throw new InvalidOperationException("剪贴板写入进程失败：" + stderr);
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add("PASS: independent writer exited; host reads real PNG/image/png/DIB/DIBV5 bytes and valid DDB CF_BITMAP with every expected pixel");
        }
        finally
        {
            if (!await lease.RestoreAsync(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException("跨进程验收期间外部程序更新了剪贴板；保留其新内容，未覆盖恢复。");
        }
        lines.Add("PASS: host kept the original clipboard lease alive and restored every raw format plus a valid native Bitmap after writer exit");
        return lines;
    }

    private static Bitmap Fixture(Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < size.Height; y++) for (int x = 0; x < size.Width; x++)
        {
            int alpha = ((x + y * 3) % 5) switch { 0 => 255, 1 => 0, 2 => 1, 3 => 127, _ => 254 };
            bitmap.SetPixel(x, y, Color.FromArgb(alpha, (x * 31 + y * 17 + 13) & 255, (x * 7 + y * 41 + 29) & 255, (x * 43 + y * 11 + 47) & 255));
        }
        return bitmap;
    }

    private static void Compare(Bitmap expected, Bitmap actual, bool flattened, string format)
    {
        if (actual.Size != expected.Size) throw new Exception($"FAIL: clipboard {format} dimensions changed");
        for (int y = 0; y < expected.Height; y++) for (int x = 0; x < expected.Width; x++)
        {
            Color wanted = expected.GetPixel(x, y), got = actual.GetPixel(x, y);
            if (flattened) wanted = White(wanted);
            if (got.ToArgb() != wanted.ToArgb()) throw new Exception($"FAIL: clipboard {format} pixel changed at {x},{y}: expected {wanted}, actual {got}");
        }
    }

    private static void CheckDib(Bitmap fixture, byte[] dib, bool alpha)
    {
        int header = alpha ? 124 : 40, depth = alpha ? 32 : 24;
        int stride = ((fixture.Width * depth + 31) / 32) * 4;
        if (I32(dib, 0) != header || I32(dib, 4) != fixture.Width || I32(dib, 8) != fixture.Height || U16(dib, 12) != 1 || U16(dib, 14) != depth || I32(dib, 16) != (alpha ? 3 : 0) || I32(dib, 20) != stride * fixture.Height || dib.Length != header + stride * fixture.Height)
            throw new Exception("FAIL: clipboard DIB has invalid dimensions, stride, compression or bit depth");
        if (alpha && (U32(dib, 40) != 0x00ff0000 || U32(dib, 44) != 0x0000ff00 || U32(dib, 48) != 0x000000ff || U32(dib, 52) != 0xff000000 || U32(dib, 56) != 0x73524742))
            throw new Exception("FAIL: clipboard DIBV5 lacks explicit RGBA masks or sRGB color space");
        for (int y = 0; y < fixture.Height; y++) for (int x = 0; x < fixture.Width; x++)
        {
            Color wanted = fixture.GetPixel(x, y);
            if (!alpha) wanted = White(wanted);
            int offset = header + (fixture.Height - 1 - y) * stride + x * (depth / 8);
            if (dib[offset] != wanted.B || dib[offset + 1] != wanted.G || dib[offset + 2] != wanted.R || (alpha && dib[offset + 3] != wanted.A))
                throw new Exception($"FAIL: clipboard {(alpha ? "DIBV5" : "DIB")} changed pixel or orientation at {x},{y}");
        }
        for (int row = 0; row < fixture.Height; row++) for (int pad = fixture.Width * (depth / 8); pad < stride; pad++)
            if (dib[header + row * stride + pad] != 0) throw new Exception("FAIL: DIB row padding contains uninitialized bytes");
    }

    private static Color White(Color pixel)
    {
        int Component(byte value) => (int)Math.Round(value * (pixel.A / 255d) + 255 * (1 - pixel.A / 255d), MidpointRounding.AwayFromZero);
        return Color.FromArgb(255, Component(pixel.R), Component(pixel.G), Component(pixel.B));
    }
    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
