using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

/// <summary>Publishes eagerly rendered Windows image formats; their lifetime does not depend on an editor window.</summary>
internal static class ClipboardImages
{
    internal const uint BitmapFormat = 2, DibFormat = 8, DibV5Format = 17;
    internal const string PngFormat = "PNG", PngMimeFormat = "image/png";
    private static readonly SemaphoreSlim CopyGate = new(1, 1);
    private static readonly int[] RetryDelays = { 40, 80, 120, 180, 260, 360 };
    // Access is serialized by CopyGate. Never identify a write by reading the sequence after releasing the clipboard lock.
    private static uint lastPublishedSequence;
    private static long publicationGeneration;

    /// <summary>
    /// Copies a snapshot of the bitmap. Completes only after every format is accepted by Windows.
    /// Cancellation is observed before publication, never halfway through transferring clipboard ownership.
    /// </summary>
    public static async Task CopyAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        // Snapshot before the first await: closing or editing the caller's document cannot invalidate a queued copy.
        var payload = CreatePayload(bitmap, cancellationToken);
        await CopyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var prepared = Prepare(payload);
            await RetryAsync(owner =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Publish(prepared);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { CopyGate.Release(); }
    }

    internal static ClipboardImagePayload CreatePayload(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        Imaging.CheckSize(bitmap.Width, bitmap.Height);
        using var snapshot = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb);
        int width = snapshot.Width, height = snapshot.Height;
        int rgbaStride = checked(width * 4), dibStride = checked((width * 3 + 3) & ~3);
        var dib = new byte[checked(40 + dibStride * height)];
        var dibV5 = new byte[checked(124 + rgbaStride * height)];
        Header(dib, 40, width, height, 24, dibStride * height, 0);
        Header(dibV5, 124, width, height, 32, rgbaStride * height, 3);
        U32(dibV5, 40, 0x00ff0000); U32(dibV5, 44, 0x0000ff00);
        U32(dibV5, 48, 0x000000ff); U32(dibV5, 52, 0xff000000);
        U32(dibV5, 56, 0x73524742); // LCS_sRGB
        U32(dibV5, 108, 4); // LCS_GM_IMAGES
        var row = new byte[rgbaStride];
        var bits = snapshot.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(IntPtr.Add(bits.Scan0, checked(y * bits.Stride)), row, 0, row.Length);
                int bottom = height - 1 - y;
                Buffer.BlockCopy(row, 0, dibV5, 124 + bottom * rgbaStride, row.Length);
                for (int x = 0; x < width; x++)
                {
                    int source = x * 4, target = 40 + bottom * dibStride + x * 3, alpha = row[source + 3];
                    // Legacy DIB/Bitmap readers do not support alpha reliably. Composite onto white, preserving opaque RGB exactly.
                    for (int channel = 0; channel < 3; channel++)
                        dib[target + channel] = (byte)((row[source + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                }
            }
        }
        finally { snapshot.UnlockBits(bits); }
        cancellationToken.ThrowIfCancellationRequested();
        return new ClipboardImagePayload(width, height, Imaging.Png(snapshot), dib, dibV5);
    }

    private static void Header(byte[] bytes, int size, int width, int height, ushort depth, int imageSize, uint compression)
    {
        I32(bytes, 0, size); I32(bytes, 4, width); I32(bytes, 8, height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), depth);
        U32(bytes, 16, compression); I32(bytes, 20, imageSize);
        I32(bytes, 24, 3780); I32(bytes, 28, 3780); // 96 DPI; pixel dimensions remain authoritative.
    }
    private static void I32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private static PreparedData Prepare(ClipboardImagePayload payload)
    {
        var data = new PreparedData();
        try
        {
            data.AddMemory(Register(PngFormat), payload.Png);
            data.AddMemory(Register(PngMimeFormat), payload.Png);
            data.AddMemory(DibV5Format, payload.DibV5);
            data.AddMemory(DibFormat, payload.Dib);
            data.Add(CreateBitmapHandle(payload));
            return data;
        }
        catch { data.Dispose(); throw; }
    }

    private static uint Register(string format)
    {
        uint id = RegisterClipboardFormat(format);
        if (id == 0) throw NativeFailure("注册图片剪贴板格式");
        return id;
    }

    private static OwnedHandle CreateBitmapHandle(ClipboardImagePayload payload)
        => new(BitmapFormat, CreateDeviceBitmap(payload.Dib, 40), true);

    private static IntPtr CreateDeviceBitmap(byte[] dib, int pixelOffset)
    {
        var pinned = GCHandle.Alloc(dib, GCHandleType.Pinned);
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) { pinned.Free(); throw NativeFailure("取得兼容位图设备"); }
        try
        {
            // CF_BITMAP must hold a DDB. A DIB section's process-local backing memory can die with its writer,
            // even when SetClipboardData accepted its GDI handle. PNG/DIB/V5 are separately held as global memory.
            IntPtr handle = CreateDIBitmap(dc, pinned.AddrOfPinnedObject(), 4,
                IntPtr.Add(pinned.AddrOfPinnedObject(), pixelOffset), pinned.AddrOfPinnedObject(), 0);
            if (handle == IntPtr.Zero) throw NativeFailure("创建兼容位图");
            if (GetObject(handle, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 || info.Bits != IntPtr.Zero)
            { DeleteObject(handle); throw new InvalidOperationException("Windows 未创建可跨进程持久保存的兼容位图。"); }
            return handle;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); pinned.Free(); }
    }

    private static IntPtr DuplicateBitmap(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var description) == 0)
            throw new InvalidOperationException("原剪贴板 Bitmap 句柄已失效，不能直接备份。");
        int width = description.Width, height = Math.Abs(description.Height);
        Imaging.CheckSize(width, height);
        int size = checked(width * height * 4);
        var dib = new byte[checked(40 + size)];
        Header(dib, 40, width, height, 32, size, 0);
        var pinned = GCHandle.Alloc(dib, GCHandleType.Pinned);
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) { pinned.Free(); throw NativeFailure("取得位图备份设备"); }
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, IntPtr.Add(pinned.AddrOfPinnedObject(), 40), pinned.AddrOfPinnedObject(), 0) != height)
                throw NativeFailure("读取原剪贴板位图像素；未修改剪贴板");
        }
        finally { ReleaseDC(IntPtr.Zero, dc); pinned.Free(); }
        return CreateDeviceBitmap(dib, 40);
    }

    // Used by the default acceptance test. This creates a GDI bitmap without accessing the clipboard.
    internal static Bitmap DecodeBitmapFallback(ClipboardImagePayload payload)
    {
        using var handle = CreateBitmapHandle(payload);
        return Image.FromHbitmap(handle.Handle);
    }

    internal static Bitmap DecodeDuplicatedBitmapFallback(ClipboardImagePayload payload)
    {
        using var original = CreateBitmapHandle(payload);
        using var duplicate = new OwnedHandle(BitmapFormat, DuplicateBitmap(original.Handle), true);
        return Image.FromHbitmap(duplicate.Handle);
    }

    internal static uint LastWrittenSequenceForTest => Volatile.Read(ref lastPublishedSequence);

    // Called only by the explicit lifetime test, after its known writer subprocess exited.
    internal static async Task VerifyWriterExitAsync(ClipboardImagePayload payload, uint writerSequence, CancellationToken token)
    {
        await CopyGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RetryAsync(owner =>
            {
                if (writerSequence == 0 || GetClipboardSequenceNumber() != writerSequence)
                    throw new InvalidOperationException("写入进程退出后剪贴板已被其他程序更新；保留较新的内容并停止跨进程验收。");
                // Trust only the sequence emitted by our own test writer, checked while holding the native clipboard lock.
                // Register it before verifying formats so the host's lease can still restore after a failed lifetime check.
                lastPublishedSequence = writerSequence; publicationGeneration++;
                CheckBytes(Register(PngFormat), payload.Png);
                CheckBytes(Register(PngMimeFormat), payload.Png);
                CheckBytes(DibFormat, payload.Dib);
                CheckBytes(DibV5Format, payload.DibV5);
                IntPtr bitmap = GetClipboardData(BitmapFormat);
                if (bitmap == IntPtr.Zero || GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var metadata) == 0)
                    throw new InvalidOperationException("写入进程退出后 CF_BITMAP 句柄无效，图片复制没有完整持久保存。");
                if (metadata.Bits != IntPtr.Zero) throw new InvalidOperationException("退出后 Bitmap 仍依赖 DIB section，未通过兼容位图持久化验收。");
                using var actual = Image.FromHbitmap(bitmap);
                using var expected = DecodeBitmapFallback(payload);
                if (actual.Size != expected.Size) throw new InvalidOperationException("退出后的 Bitmap 尺寸改变。");
                for (int y = 0; y < actual.Height; y++) for (int x = 0; x < actual.Width; x++)
                    if (actual.GetPixel(x, y).ToArgb() != expected.GetPixel(x, y).ToArgb())
                        throw new InvalidOperationException($"退出后的 Bitmap 像素改变：{x},{y}。");
            }, token).ConfigureAwait(false);
        }
        finally { CopyGate.Release(); }
    }

    private static async Task RetryAsync(Action<IntPtr> action, CancellationToken token)
    {
        int lastError = 0;
        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using (var owner = new ClipboardOwner())
            {
                if (OpenClipboard(owner.Handle))
                {
                    try { action(owner.Handle); }
                    finally { if (!CloseClipboard()) throw NativeFailure("释放剪贴板"); }
                    return;
                }
                lastError = Marshal.GetLastWin32Error();
            }
            if (attempt < RetryDelays.Length) await Task.Delay(RetryDelays[attempt], token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("剪贴板正被其他程序占用，多次重试仍未能复制。请稍后再试。", new Win32Exception(lastError));
    }

    private static void Publish(PreparedData data)
    {
        uint before = GetClipboardSequenceNumber();
        try
        {
            if (!EmptyClipboard()) throw NativeFailure("准备图片剪贴板");
            foreach (var value in data.Values)
            {
                if (SetClipboardData(value.Format, value.Handle) == IntPtr.Zero)
                    throw NativeFailure($"写入图片剪贴板格式 {value.Format}");
                value.Transferred = true; // Windows owns this handle from this point, including after our window/process exits.
            }
            foreach (var value in data.Values)
                if (!IsClipboardFormatAvailable(value.Format) || GetClipboardData(value.Format) == IntPtr.Zero)
                    throw new InvalidOperationException($"Windows 未保留图片剪贴板格式 {value.Format}，复制未完成。");
        }
        finally
        {
            // Also identify our partial failed writes: a test must be able to restore its backup after such a failure.
            uint after = GetClipboardSequenceNumber();
            if (after != 0 && after != before) { lastPublishedSequence = after; publicationGeneration++; }
        }
    }

    /// <summary>
    /// Explicit opt-in native clipboard test. Materializes and duplicates the previous clipboard before writing,
    /// then restores it before releasing the clipboard lock. Unsupported owner-managed formats abort before mutation.
    /// Never call this from normal self-tests or UI regression tests.
    /// </summary>
    internal static async Task VerifyNativeRoundTripAsync(ClipboardImagePayload payload, CancellationToken token)
    {
        await CopyGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var fixture = Prepare(payload);
            await RetryAsync(owner =>
            {
                using var previous = SnapshotClipboard();
                token.ThrowIfCancellationRequested();
                Exception? verificationFailure = null;
                try
                {
                    Publish(fixture);
                    CheckBytes(Register(PngFormat), payload.Png);
                    CheckBytes(Register(PngMimeFormat), payload.Png);
                    CheckBytes(DibFormat, payload.Dib);
                    CheckBytes(DibV5Format, payload.DibV5);
                    using var actual = Image.FromHbitmap(GetClipboardData(BitmapFormat));
                    using var expected = DecodeBitmapFallback(payload);
                    if (actual.Size != expected.Size) throw new InvalidOperationException("剪贴板 Bitmap 尺寸不一致。");
                    for (int y = 0; y < actual.Height; y++) for (int x = 0; x < actual.Width; x++)
                        if (actual.GetPixel(x, y).ToArgb() != expected.GetPixel(x, y).ToArgb())
                            throw new InvalidOperationException($"剪贴板 Bitmap 像素不一致：{x},{y}。");
                }
                catch (Exception ex) { verificationFailure = ex; }
                try { Publish(previous); }
                catch (Exception restoreFailure)
                {
                    throw new InvalidOperationException("剪贴板验收后恢复原内容失败，请保留此次错误报告。",
                        verificationFailure == null ? restoreFailure : new AggregateException(verificationFailure, restoreFailure));
                }
                if (verificationFailure != null) throw new InvalidOperationException("图片剪贴板往返验收失败；原剪贴板已恢复。", verificationFailure);
            }, token).ConfigureAwait(false);
        }
        finally { CopyGate.Release(); }
    }

    /// <summary>
    /// Explicit GUI-test helper only. Saves the previous contents, then releases the clipboard for normal application paste.
    /// Use in the same process as the tested CopyAsync calls. Always await lease.RestoreAsync in a finally block before Dispose.
    /// </summary>
    internal static Task<ClipboardSnapshotLease> PreserveForTestAsync(CancellationToken cancellationToken = default)
        => ClipboardSnapshotLease.CaptureAsync(cancellationToken);

    internal sealed class ClipboardSnapshotLease : IDisposable
    {
        private readonly PreparedData original;
        private readonly long initialGeneration;
        private int state; // 0 active, 1 restoration in progress, 2 finished/disposed
        public uint OriginalSequence { get; }

        private ClipboardSnapshotLease(PreparedData original, uint sequence, long generation)
        {
            this.original = original; OriginalSequence = sequence; initialGeneration = generation;
        }

        internal static async Task<ClipboardSnapshotLease> CaptureAsync(CancellationToken token)
        {
            await CopyGate.WaitAsync(token).ConfigureAwait(false);
            PreparedData? saved = null;
            uint sequence = 0;
            try
            {
                await RetryAsync(owner =>
                {
                    token.ThrowIfCancellationRequested();
                    saved = SnapshotClipboard();
                    sequence = GetClipboardSequenceNumber();
                    if (sequence == 0) throw new InvalidOperationException("无法可靠读取剪贴板序列号；未运行 GUI 剪贴板验收，也未修改原内容。");
                }, token).ConfigureAwait(false);
                return new ClipboardSnapshotLease(saved!, sequence, publicationGeneration);
            }
            catch { saved?.Dispose(); throw; }
            finally { CopyGate.Release(); }
        }

        /// <summary>
        /// Returns true if the original contents were unchanged or restored. Returns false, without writing anything,
        /// when another process has changed the clipboard since our latest image publication. Clipboard errors throw;
        /// the backup stays available after such errors so the caller can retry restoration. A successful/declined call ends the lease.
        /// </summary>
        public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                throw new InvalidOperationException("剪贴板备份正在恢复或已结束，不能重复使用。");
            bool entered = false, finished = false, restored = false;
            try
            {
                await CopyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                await RetryAsync(owner =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uint current = GetClipboardSequenceNumber();
                    if (current != 0 && current == OriginalSequence && !original.ReconstructedBitmap) { restored = true; return; }
                    // The check and write share one OpenClipboard lock, so an external writer cannot race restoration.
                    if (current == 0 || (current != OriginalSequence && (current != lastPublishedSequence || publicationGeneration <= initialGeneration))) return;
                    // Keep the full backup owned by the lease until restoration succeeds, including across partial write failures.
                    using var attempt = Duplicate(original);
                    Publish(attempt);
                    restored = true;
                }, cancellationToken).ConfigureAwait(false);
                finished = true;
                return restored;
            }
            finally
            {
                if (entered) CopyGate.Release();
                if (finished) { original.Dispose(); Volatile.Write(ref state, 2); }
                else Volatile.Write(ref state, 0);
            }
        }

        /// <summary>Frees backup handles only; it never overwrites the live clipboard. Await RestoreAsync first.</summary>
        public void Dispose()
        {
            int previous = Interlocked.CompareExchange(ref state, 2, 0);
            if (previous == 0) original.Dispose();
            else if (previous == 1) throw new InvalidOperationException("剪贴板备份正在恢复，请等待 RestoreAsync 完成后再释放。");
        }
    }

    private static PreparedData SnapshotClipboard()
    {
        var snapshot = new PreparedData();
        try
        {
            uint format = 0;
            while (true)
            {
                format = EnumClipboardFormats(format);
                if (format == 0)
                {
                    if (Marshal.GetLastWin32Error() != 0) throw NativeFailure("读取原剪贴板格式");
                    break;
                }
                // These use special GDI/owner handles, not movable memory. Refuse rather than risk losing them.
                if (format == 3 || format == 9 || format == 14 || format == 0x80 || format == 0x82 || format == 0x83 || format == 0x8e || (format >= 0x200 && format <= 0x2ff))
                    throw new InvalidOperationException($"原剪贴板包含无法安全恢复的格式 {format}；未运行真实剪贴板验收，也未修改其内容。");
                IntPtr original = GetClipboardData(format);
                if (original == IntPtr.Zero) throw NativeFailure("读取原剪贴板内容；未修改剪贴板");
                if (format == BitmapFormat)
                {
                    IntPtr copy;
                    if (GetObject(original, Marshal.SizeOf<NativeBitmap>(), out _) == 0)
                    {
                        // Recover the specific stale CF_BITMAP produced by the old DIB-section restoration.
                        // Valid raw formats are still copied byte for byte; divergent companions abort before any mutation.
                        copy = ReconstructClipboardBitmap();
                        snapshot.ReconstructedBitmap = true;
                    }
                    else copy = DuplicateBitmap(original);
                    snapshot.Add(new OwnedHandle(format, copy, true));
                }
                else snapshot.AddMemory(format, ReadMemory(original));
            }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    private static PreparedData Duplicate(PreparedData original)
    {
        var copy = new PreparedData();
        try
        {
            foreach (var value in original.Values)
            {
                if (value.Format == BitmapFormat)
                {
                    IntPtr handle = DuplicateBitmap(value.Handle);
                    copy.Add(new OwnedHandle(value.Format, handle, true));
                }
                else copy.AddMemory(value.Format, ReadMemory(value.Handle));
            }
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    private static IntPtr ReconstructClipboardBitmap()
    {
        IntPtr handle = GetClipboardData(DibFormat);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("原剪贴板 Bitmap 句柄已失效且没有可核对的 DIB；未修改剪贴板。");
        byte[] dib = ReadMemory(handle);
        var layout = ParseDib(dib);
        foreach (string name in new[] { PngFormat, PngMimeFormat })
        {
            uint format = Register(name);
            if (!IsClipboardFormatAvailable(format)) continue;
            IntPtr pngHandle = GetClipboardData(format);
            if (pngHandle == IntPtr.Zero) throw new InvalidOperationException("原剪贴板 PNG 无法读取，不能安全重建 Bitmap；未修改剪贴板。");
            using var stream = new MemoryStream(ReadMemory(pngHandle), false);
            using var png = new Bitmap(stream);
            if (png.Width != layout.Width || png.Height != layout.Height)
                throw new InvalidOperationException("原剪贴板 PNG 与 DIB 尺寸不一致，不能安全重建 Bitmap；未修改剪贴板。");
            for (int y = 0; y < png.Height; y++) for (int x = 0; x < png.Width; x++)
                if (!SameRgb(png.GetPixel(x, y), DibPixel(dib, layout, x, y)))
                    throw new InvalidOperationException("原剪贴板 PNG 与 DIB 像素不一致，不能安全重建 Bitmap；未修改剪贴板。");
        }
        if (IsClipboardFormatAvailable(DibV5Format))
        {
            byte[] v5 = ReadMemory(GetClipboardData(DibV5Format));
            var other = ParseDib(v5);
            if (other.Width != layout.Width || other.Height != layout.Height)
                throw new InvalidOperationException("原剪贴板 DIB 与 DIBV5 尺寸不一致，不能安全重建 Bitmap；未修改剪贴板。");
            for (int y = 0; y < layout.Height; y++) for (int x = 0; x < layout.Width; x++)
                if (!SameRgb(DibPixel(dib, layout, x, y), DibPixel(v5, other, x, y)))
                    throw new InvalidOperationException("原剪贴板 DIB 与 DIBV5 像素不一致，不能安全重建 Bitmap；未修改剪贴板。");
        }
        return CreateDeviceBitmap(dib, layout.PixelOffset);
    }

    private sealed record DibLayout(int Width, int Height, bool TopDown, int Depth, int Stride, int PixelOffset);
    private static DibLayout ParseDib(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidOperationException("原剪贴板 DIB 头部不完整；未修改剪贴板。");
        int size = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4, 4));
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4));
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(12, 2));
        ushort depth = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4));
        uint colors = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32, 4));
        if ((size != 40 && size != 108 && size != 124) || size > dib.Length || signedHeight == int.MinValue || planes != 1 || (depth != 24 && depth != 32) || (compression != 0 && compression != 3) || (depth == 24 && compression != 0) || colors != 0)
            throw new InvalidOperationException("原剪贴板 DIB 结构或颜色格式不受安全重建支持；未修改剪贴板。");
        int height = Math.Abs(signedHeight); Imaging.CheckSize(width, height);
        int offset = size + (size == 40 && compression == 3 ? 12 : 0);
        if (compression == 3)
        {
            if (dib.Length < 52 || BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(40, 4)) != 0x00ff0000 || BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(44, 4)) != 0x0000ff00 || BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(48, 4)) != 0x000000ff)
                throw new InvalidOperationException("原剪贴板 DIB 使用未知颜色掩码；未修改剪贴板。");
        }
        if (size == 124 && (BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(112, 4)) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(116, 4)) != 0))
            throw new InvalidOperationException("原剪贴板 DIB 包含附加颜色配置，不能安全重建；未修改剪贴板。");
        int stride = checked((width * (depth / 8) + 3) & ~3);
        if ((long)offset + (long)stride * height > dib.Length)
            throw new InvalidOperationException("原剪贴板 DIB 像素不完整；未修改剪贴板。");
        return new DibLayout(width, height, signedHeight < 0, depth, stride, offset);
    }
    private static Color DibPixel(byte[] dib, DibLayout layout, int x, int y)
    {
        int row = layout.TopDown ? y : layout.Height - 1 - y;
        int offset = layout.PixelOffset + row * layout.Stride + x * (layout.Depth / 8);
        return Color.FromArgb(dib[offset + 2], dib[offset + 1], dib[offset]);
    }
    private static bool SameRgb(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    private static void CheckBytes(uint format, byte[] expected)
    {
        IntPtr handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) throw NativeFailure($"读取剪贴板格式 {format}");
        var actual = ReadMemory(handle);
        if (actual.Length < expected.Length || !actual.AsSpan(0, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException($"剪贴板格式 {format} 的实际字节与图片不一致。");
    }

    private static byte[] ReadMemory(IntPtr handle)
    {
        ulong size = GlobalSize(handle).ToUInt64();
        if (size == 0 || size > int.MaxValue) throw new InvalidOperationException("剪贴板数据无法安全读取或备份；不执行真实剪贴板验收。");
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) throw NativeFailure("读取剪贴板内存");
        try { var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }

    private static Exception NativeFailure(string action) => new InvalidOperationException(action + "失败。", new Win32Exception(Marshal.GetLastWin32Error()));

    private sealed class PreparedData : IDisposable
    {
        internal List<OwnedHandle> Values { get; } = new();
        internal bool ReconstructedBitmap { get; set; }
        internal void Add(OwnedHandle handle) => Values.Add(handle);
        internal void AddMemory(uint format, byte[] bytes)
        {
            IntPtr handle = GlobalAlloc(0x42, (UIntPtr)(uint)bytes.Length); // GMEM_MOVEABLE | GMEM_ZEROINIT
            if (handle == IntPtr.Zero) throw NativeFailure("分配剪贴板内存");
            var owned = new OwnedHandle(format, handle, false);
            try
            {
                IntPtr pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) throw NativeFailure("准备剪贴板内存");
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { GlobalUnlock(handle); }
                Add(owned);
            }
            catch { owned.Dispose(); throw; }
        }
        public void Dispose() { foreach (var value in Values) value.Dispose(); }
    }

    private sealed class OwnedHandle : IDisposable
    {
        internal uint Format { get; }
        internal IntPtr Handle { get; }
        internal bool Transferred { get; set; }
        private readonly bool bitmap;
        internal OwnedHandle(uint format, IntPtr handle, bool bitmap) { Format = format; Handle = handle; this.bitmap = bitmap; }
        public void Dispose() { if (!Transferred) { if (bitmap) DeleteObject(Handle); else GlobalFree(Handle); Transferred = true; } }
    }

    private sealed class ClipboardOwner : IDisposable
    {
        internal IntPtr Handle { get; }
        internal ClipboardOwner()
        {
            // EmptyClipboard must have a real owner; opening with NULL can make SetClipboardData fail.
            Handle = CreateWindowEx(0, "STATIC", "Framelet Clipboard", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw NativeFailure("创建剪贴板所有者");
        }
        public void Dispose() => DestroyWindow(Handle);
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap
    {
        internal int Type, Width, Height, WidthBytes;
        internal ushort Planes, Depth;
        internal IntPtr Bits;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)] private static extern int GetObject(IntPtr bitmap, int size, out NativeBitmap description);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBitmap(IntPtr dc, IntPtr header, uint initialization, IntPtr bits, IntPtr info, uint usage);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, IntPtr bits, IntPtr info, uint usage);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
}

internal sealed record ClipboardImagePayload(int Width, int Height, byte[] Png, byte[] Dib, byte[] DibV5);
