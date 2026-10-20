using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tesseract;

namespace QingCapture;

internal sealed class OcrBlock
{
    public int Number { get; set; }
    public Rectangle Bounds { get; set; }
    public string Original { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Error { get; set; } = "";
}

internal static class LocalOcr
{
    public static Task<List<OcrBlock>> Read(Bitmap source,IProgress<string> progress,CancellationToken ct) => Task.Run(() => {
        var result=new List<OcrBlock>();
        using var engine=new TesseractEngine(Path.Combine(AppContext.BaseDirectory,"tessdata"),"eng",EngineMode.Default);
        // Overlapping strips prevent words on a strip boundary from being lost.
        const int tile=1600, overlap=160;
        for(int top=0;top<source.Height;top+=tile-overlap) {
            ct.ThrowIfCancellationRequested(); progress.Report($"本机识别文字 {Math.Min(top+tile,source.Height)} / {source.Height}");
            int height=Math.Min(tile,source.Height-top);
            using var part=Imaging.Crop(source,new Rectangle(0,top,source.Width,height));
            using var pix=Pix.LoadFromMemory(Imaging.Png(part)); using var page=engine.Process(pix,PageSegMode.Auto);
            using var it=page.GetIterator(); it.Begin();
            do {
                string text=(it.GetText(PageIteratorLevel.TextLine)??"").Trim();
                if(!Regex.IsMatch(text,"[A-Za-z]{2}") || it.GetConfidence(PageIteratorLevel.TextLine)<55 || !it.TryGetBoundingBox(PageIteratorLevel.TextLine,out var r)) continue;
                var rect=new Rectangle(r.X1,top+r.Y1,r.Width,r.Height);
                double center=(r.Y1+r.Y2)/2d;
                if(top>0 && center<overlap/2) continue;
                if(top+height<source.Height && center>=height-overlap/2) continue;
                if(result.Any(x=>Math.Abs(x.Bounds.Top-rect.Top)<12 && x.Original==text)) continue;
                result.Add(new OcrBlock { Number=result.Count+1,Bounds=rect,Original=text });
            } while(it.Next(PageIteratorLevel.TextLine));
            if(top+height>=source.Height) break;
        }
        // Auto layout can split a visual line into separate columns. Merge nearby
        // fragments on the same baseline, then number in visual reading order.
        var sorted=result.OrderBy(b=>b.Bounds.Y).ThenBy(b=>b.Bounds.X).ToList();
        var merged=new List<OcrBlock>();
        foreach(var block in sorted) {
            var neighbor=merged.LastOrDefault(b=>Math.Abs(b.Bounds.Y-block.Bounds.Y)<Math.Min(b.Bounds.Height,block.Bounds.Height)*0.5 && block.Bounds.X>=b.Bounds.Right && block.Bounds.X-b.Bounds.Right<Math.Max(b.Bounds.Height,block.Bounds.Height)*5);
            if(neighbor!=null) { neighbor.Original+=" "+block.Original; neighbor.Bounds=Rectangle.Union(neighbor.Bounds,block.Bounds); }
            else merged.Add(block);
        }
        for(int i=0;i<merged.Count;i++) merged[i].Number=i+1;
        return merged;
    },ct);
}


internal static class TranslationText
{
    public static string SingleParagraph(string text)=>Regex.Replace(text,@"\s+"," ").Trim();
    public static string Original(IReadOnlyList<OcrBlock> blocks)=>SingleParagraph(string.Join(" ",blocks.Select(block=>block.Original)));
    public static string Translated(IReadOnlyList<OcrBlock> blocks)=>SingleParagraph(string.Join(" ",blocks.Select(block=>block.Translation.Length>0?block.Translation:$"[{(block.Error.Length>0?block.Error:"未翻译")}]")));
    public static List<OcrBlock> MergeForTranslation(IReadOnlyList<OcrBlock> blocks)
    {
        string text=Original(blocks);var result=new List<OcrBlock>();
        if(text.Length==0)return result;
        var bounds=blocks.Aggregate(Rectangle.Empty,(area,block)=>area.IsEmpty?block.Bounds:Rectangle.Union(area,block.Bounds));
        // OCR line breaks must not split a sentence. Keep the existing per-request
        // limit for very long captures, preferring complete sentences at a boundary.
        const int limit=16000;
        for(int start=0;start<text.Length;) {
            int end=Math.Min(text.Length,start+limit);
            if(end<text.Length) {
                int boundary=-1;
                for(int i=end-1;i>start;i--)if(text[i]==' '&&".!?。！？".Contains(text[i-1])) {boundary=i;break;}
                if(boundary<0)boundary=text.LastIndexOf(' ',end-1,end-start);
                if(boundary>start)end=boundary;
                else if(char.IsHighSurrogate(text[end-1]))end--;
            }
            result.Add(new OcrBlock { Number=result.Count+1,Bounds=bounds,Original=text.Substring(start,end-start) });
            start=end;while(start<text.Length&&text[start]==' ')start++;
        }
        return result;
    }
}

internal static class TranslationLayout
{
    public static Bitmap Compose(Bitmap original,IReadOnlyList<OcrBlock> blocks)
    {
        int column=Math.Clamp(original.Width/2,360,620), gap=52;
        using var measure=new Bitmap(1,1); using var mg=Graphics.FromImage(measure);
        using var font=new Font("Microsoft YaHei UI",18,GraphicsUnit.Pixel);
        string value=TranslationText.Translated(blocks);
        int textHeight=(int)Math.Ceiling(mg.MeasureString(value,font,column-32).Height)+8;
        int h=Math.Max(original.Height,68+textHeight+24), w=original.Width+gap+column;
        Imaging.CheckSize(w,h); var image=new Bitmap(w,h,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(image); g.Clear(Color.FromArgb(243,244,248)); g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
        g.DrawImage(original,new Rectangle(0,0,original.Width,original.Height),new Rectangle(0,0,original.Width,original.Height),GraphicsUnit.Pixel); using var plum=new SolidBrush(Color.FromArgb(82,102,206)); using var dark=new SolidBrush(Color.FromArgb(41,50,72));
        using var line=new Pen(Color.FromArgb(220,224,236));
        int x=original.Width+gap; g.DrawLine(line,x-12,0,x-12,h); g.DrawString("英语 → 简体中文",font,plum,x+16,20);
        g.DrawString(value,font,dark,new RectangleF(x+16,68,column-32,textHeight));
        return image;
    }
}

