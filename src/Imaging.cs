using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace QingCapture;

internal static class Imaging
{
    public const long MaxPixels = 48_000_000;
    public static void CheckSize(int w, int h)
    {
        if (w < 1 || h < 1 || (long)w * h > MaxPixels || w > 60000 || h > 60000)
            throw new InvalidOperationException("图片过大，请分段截图（上限 4800 万像素、单边 60000 像素）。");
    }
    public static Bitmap Capture(Rectangle area)
    {
        CheckSize(area.Width, area.Height);
        var b = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        try { using var g = Graphics.FromImage(b); g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy); return b; }
        catch { b.Dispose(); throw; }
    }
    public static Bitmap Crop(Bitmap source, Rectangle r) => source.Clone(r, PixelFormat.Format32bppArgb);
    public static byte[] Png(Bitmap image) { using var s = new MemoryStream(); image.Save(s, ImageFormat.Png); return s.ToArray(); }
    public static BitmapSource Source(Bitmap b)
    {
        using var stream = new MemoryStream(Png(b));
        var src = new BitmapImage(); src.BeginInit(); src.CacheOption = BitmapCacheOption.OnLoad; src.StreamSource = stream; src.EndInit(); src.Freeze(); return src;
    }
    public static Bitmap FromSource(BitmapSource source)
    {
        using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(stream);
        stream.Position = 0; using var b = new Bitmap(stream); return new Bitmap(b);
    }
    public static void Save(Bitmap b, string path, bool jpeg = false)
    {
        if (!jpeg) { b.Save(path, ImageFormat.Png); return; }
        using var flat = new Bitmap(b.Width, b.Height); using var g = Graphics.FromImage(flat); g.Clear(Color.White); g.DrawImage(b,new Rectangle(0,0,b.Width,b.Height),new Rectangle(0,0,b.Width,b.Height),GraphicsUnit.Pixel);
        var enc = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var pars = new EncoderParameters(1); pars.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 100L); flat.Save(path, enc, pars);
    }
    public static Rectangle Rect(Point a, Point b) => Rectangle.FromLTRB(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y), Math.Max(a.X,b.X), Math.Max(a.Y,b.Y));
}

internal sealed class ImageDocument : IDisposable
{
    public Bitmap Current { get; private set; }
    private readonly LinkedList<Bitmap> undo = new();
    private readonly Stack<Bitmap> redo = new();
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public ImageDocument(Bitmap image) { Current = new Bitmap(image); }
    public void Apply(Action<Bitmap> draw)
    {
        var next = new Bitmap(Current);
        try { draw(next); } catch { next.Dispose(); throw; }
        undo.AddLast(Current); Current = next;
        foreach (var b in redo) b.Dispose(); redo.Clear();
        while (undo.Count > 20 || (long)Current.Width * Current.Height * 4 * undo.Count > 192_000_000)
        { undo.First!.Value.Dispose(); undo.RemoveFirst(); }
    }
    public void Undo() { if (!CanUndo) return; redo.Push(Current); Current = undo.Last!.Value; undo.RemoveLast(); }
    public void Redo() { if (!CanRedo) return; undo.AddLast(Current); Current = redo.Pop(); }
    public void Dispose() { Current.Dispose(); foreach (var b in undo) b.Dispose(); foreach (var b in redo) b.Dispose(); }
}

internal static class Annotation
{
    public static void Draw(Bitmap b, string tool, Point start, Point end, string text, Color color, float width)
    {
        var r = Rectangle.Intersect(new Rectangle(0, 0, b.Width, b.Height), Imaging.Rect(start, end));
        if (tool == "马赛克")
        {
            if (r.Width < 1 || r.Height < 1) return;
            // The actual pixels are replaced; translation never sees pre-redaction pixels.
            using var patch = Imaging.Crop(b, r); using var g2 = Graphics.FromImage(b);
            for (int y = 0; y < r.Height; y += 12) for (int x = 0; x < r.Width; x += 12)
            { using var brush = new SolidBrush(patch.GetPixel(Math.Min(x+6,r.Width-1), Math.Min(y+6,r.Height-1))); g2.FillRectangle(brush, r.X+x,r.Y+y, Math.Min(12,r.Width-x), Math.Min(12,r.Height-y)); }
            return;
        }
        using var g = Graphics.FromImage(b); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, width); using var ink = new SolidBrush(color);
        if (tool == "箭头") { using var cap = new AdjustableArrowCap(5, 6); pen.CustomEndCap = cap; g.DrawLine(pen, start, end); }
        if (tool == "框线" && r.Width > 0 && r.Height > 0) g.DrawRectangle(pen, r);
        if (tool == "文字") { using var font = new Font("Microsoft YaHei UI", Math.Max(18,width*7), GraphicsUnit.Pixel); g.DrawString(text, font, ink, new PointF(start.X,start.Y)); }
    }
}
