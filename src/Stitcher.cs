using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QingCapture;

internal record StitchMatch(string State, int Shift = 0, int Footer = 0, double Error = 0, int Header = 0);
internal static class Stitcher
{
    private static byte[] Gray(Bitmap source, out int w, out int h,int maximumWidth=128)
    {
        w = Math.Min(maximumWidth, source.Width); h = source.Height;
        using var scaled = new Bitmap(w,h,PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(scaled)) { g.DrawImage(source,0,0,w,h); }
        var data = scaled.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
        try {
            var raw = new byte[data.Stride*h]; Marshal.Copy(data.Scan0,raw,0,raw.Length); var gray = new byte[w*h];
            for (int y=0;y<h;y++) for(int x=0;x<w;x++) { int i=y*data.Stride+x*3; gray[y*w+x]=(byte)((raw[i]*29+raw[i+1]*150+raw[i+2]*77)>>8); }
            return gray;
        } finally { scaled.UnlockBits(data); }
    }
    public static StitchMatch Match(Bitmap previous, Bitmap next)
    {
        if(previous.Size!=next.Size) return new("mismatch");
        // Frame stability is independent of how many text anchors it contains.
        // A single stationary line must not exhaust the controller's sampling
        // retries merely because the ordinary matcher requires three regions.
        if(SamePixels(previous,next))return new("unchanged");
        var a=Gray(previous,out int w,out int h); var b=Gray(next,out _,out _);
        if(h<80 || w<30) return new("mismatch");
        double unchanged=0; int samples=0;
        for(int y=0;y<h;y+=3) for(int x=3;x<w-3;x+=2) { unchanged+=Math.Abs(a[y*w+x]-b[y*w+x]); samples++; }
        // Repeated file rows can have the same thumbnail/icon and prefix at this
        // scale even after scrolling. Confirm their small suffixes before saying
        // there is no new content.
        byte[]? fineA=null,fineB=null;int fineWidth=0;
        if(unchanged/samples<0.8) {
            fineA=Gray(previous,out fineWidth,out _,512);fineB=Gray(next,out _,out _,512);
            var zero=FineScore(fineA,fineB,fineWidth,h,4,4,0);
            if(zero.Error<=0.0005||zero.Features==0)return ConfirmSparse(previous,next,new("unchanged"));
        }
        int header=StableBorder(a,b,w,h,true), footer=StableBorder(a,b,w,h,false);
        int top=Math.Max(4,header), bottom=Math.Max(4,footer), bestShift=0; double best=double.MaxValue;
        var scores=new double[h]; Array.Fill(scores,double.MaxValue);
        for(int shift=1;shift<h*0.82;shift++) {
            if(h-shift-top-bottom<96) continue;
            double error=0; int count=0;
            for(int y=top+shift;y<h-bottom;y+=4) for(int x=4;x<w-4;x+=2) {
                int i=y*w+x, j=(y-shift)*w+x;
                if(Math.Abs(a[i]-a[i-1])>12 || Math.Abs(a[i]-a[i-w])>12 || Math.Abs(b[j]-b[j-1])>12) {
                    error+=Math.Abs(a[i]-b[j]); count++;
                }
            }
            if(count<60) continue;
            scores[shift]=error/count;
            if(scores[shift]<best) { best=scores[shift]; bestShift=shift; }
        }
        double second=double.MaxValue;
        for(int s=1;s<h*0.82;s++) if(Math.Abs(s-bestShift)>5) second=Math.Min(second,scores[s]);
        // Preserve the cheap path for a strong exact overlap. Sparse, highlighted
        // or periodic text needs detail from all columns to disambiguate it.
        if(bestShift>=4&&best<=0.3&&second-best>=0.8&&fineA==null)return ConfirmSparse(previous,next,new("append",bestShift,footer,best,header));
        fineA??=Gray(previous,out fineWidth,out _,512);fineB??=Gray(next,out _,out _,512);
        var detail=FineMatch(fineA,fineB,fineWidth,h);
        if(detail.State is "append" or "unchanged")return ConfirmSparse(previous,next,detail);
        // A translucent fixed fade can attenuate otherwise identical text edges.
        // Only promote a separately verified geometric match; retain the original
        // rejection if contrast-normalized evidence is also ambiguous or absent.
        var geometry=FineMatch(fineA,fineB,fineWidth,h,normalizeEdges:true);
        if(geometry.State is "append" or "unchanged")return ConfirmSparse(previous,next,geometry);
        var sparse=SparseMatch(previous,next,out _);
        return sparse.State=="append"?sparse:detail;
    }

    private static StitchMatch ConfirmSparse(Bitmap previous,Bitmap next,StitchMatch candidate)
    {
        // A true sparse winner may have three regions while a larger periodic
        // rival leaves only one. The general region gate must not hide that
        // rival; sparse content needs this full-resolution uniqueness check even
        // when the ordinary matcher already found an acceptable alignment.
        var sparse=SparseMatch(previous,next,out bool applicable);
        return applicable?sparse:candidate;
    }

    private readonly record struct SparseScore(double Error,double Geometry,double Color,double BadFraction,int Features);
    private readonly record struct SparseEdge(int X,int Color,short Dx,short Dy);
    private sealed class SparseFrame
    {
        internal readonly SparseEdge[][] Rows;
        internal int TexturedRows,Features;
        internal SparseFrame(int height){Rows=new SparseEdge[height][];Array.Fill(Rows,Array.Empty<SparseEdge>());}
    }
    private static int Luma(int rgb)=>((rgb&255)*29+((rgb>>8)&255)*150+((rgb>>16)&255)*77)>>8;
    private static bool ReadSparse(Bitmap previous,Bitmap next,out SparseFrame a,out SparseFrame b,out int header,out int footer)
    {
        int w=previous.Width,h=previous.Height;a=new(h);b=new(h);header=footer=0;
        // Row buffers and a bounded edge index replace full-frame RGB/gradient
        // copies. Even a thin rule on every row cannot trigger an O(w*h*h) scan.
        if(w>8192||h>8192)return false;
        var firstA=new int[w];var firstB=new int[w];var rowA=new int[w];var rowB=new int[w];
        var constant=new bool[w];Array.Fill(constant,true);var unchanged=new bool[h];
        BitmapData? dataA=null,dataB=null;
        try {
            var bounds=new Rectangle(0,0,w,h);dataA=previous.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);dataB=next.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            Marshal.Copy(dataA.Scan0,firstA,0,w);Marshal.Copy(dataB.Scan0,firstB,0,w);
            for(int y=0;y<h;y++) {
                Marshal.Copy(IntPtr.Add(dataA.Scan0,y*dataA.Stride),rowA,0,w);Marshal.Copy(IntPtr.Add(dataB.Scan0,y*dataB.Stride),rowB,0,w);int changes=0;
                for(int x=0;x<w;x++) {
                    if(rowA[x]!=firstA[x]||rowB[x]!=firstB[x]||firstA[x]!=firstB[x])constant[x]=false;
                    if(x>=4&&x<w-8&&Math.Abs(Luma(rowA[x])-Luma(rowB[x]))>4)changes++;
                }
                unchanged[y]=changes<3;
            }
            while(header<h/5&&unchanged[header])header++;while(footer<h/5&&unchanged[h-1-footer])footer++;
            if(header<8)header=0;if(footer<8)footer=0;
            int top=Math.Max(4,header),bottom=Math.Max(4,footer);
            var grayA=new byte[w];var grayB=new byte[w];var oldA=new byte[w];var oldB=new byte[w];
            for(int y=0;y<h-bottom;y++) {
                Marshal.Copy(IntPtr.Add(dataA.Scan0,y*dataA.Stride),rowA,0,w);Marshal.Copy(IntPtr.Add(dataB.Scan0,y*dataB.Stride),rowB,0,w);
                for(int x=0;x<w;x++){grayA[x]=(byte)Luma(rowA[x]);grayB[x]=(byte)Luma(rowB[x]);}
                if(y>=top) {
                    var edgesA=new List<SparseEdge>();var edgesB=new List<SparseEdge>();
                    for(int x=4;x<w-4;x++) {
                        // Columns identical for their entire height and in both
                        // frames contain stationary borders, not moving anchors.
                        if(constant[x]&&constant[x-1])continue;
                        int dx=grayA[x]-grayA[x-1],dy=grayA[x]-oldA[x];
                        if(Math.Abs(dx)+Math.Abs(dy)>=12)edgesA.Add(new(x,rowA[x],(short)dx,(short)dy));
                        dx=grayB[x]-grayB[x-1];dy=grayB[x]-oldB[x];
                        if(Math.Abs(dx)+Math.Abs(dy)>=12)edgesB.Add(new(x,rowB[x],(short)dx,(short)dy));
                    }
                    a.Rows[y]=edgesA.ToArray();b.Rows[y]=edgesB.ToArray();
                    if(edgesA.Count>0)a.TexturedRows++;if(edgesB.Count>0)b.TexturedRows++;
                    a.Features+=edgesA.Count;b.Features+=edgesB.Count;
                    if(a.TexturedRows>80||b.TexturedRows>80||a.Features>24000||b.Features>24000)return false;
                }
                (grayA,oldA)=(oldA,grayA);(grayB,oldB)=(oldB,grayB);
            }
            return a.TexturedRows>=3&&b.TexturedRows>=3;
        }finally{if(dataB!=null)next.UnlockBits(dataB);if(dataA!=null)previous.UnlockBits(dataA);}
    }
    private static StitchMatch SparseMatch(Bitmap previous,Bitmap next,out bool applicable)
    {
        applicable=false;
        int w=previous.Width,h=previous.Height;
        if(w<30||h<96)return new("mismatch");
        if(!ReadSparse(previous,next,out var a,out var b,out int header,out int footer))return new("mismatch");
        int top=Math.Max(4,header),bottom=Math.Max(4,footer);
        // This separate path is deliberately limited to sparse text. It compares
        // every full-resolution body edge and its RGB pixels, rather than lowering
        // the general matcher's three-region or ambiguity requirements.
        applicable=true;
        var scores=new SparseScore[h];int bestShift=-1;double best=double.MaxValue;
        for(int shift=0;shift<h*0.82;shift++) {
            scores[shift]=new(double.MaxValue,double.MaxValue,double.MaxValue,1,0);
            if(h-shift-top-bottom<96)continue;
            var score=SparseOverlap(a,b,w,h,top,bottom,shift);scores[shift]=score;
            if(score.Error<best){best=score.Error;bestShift=shift;}
        }
        if(bestShift<0)return new("mismatch");
        var winning=scores[bestShift];
        if(winning.Geometry>0.015||winning.Color>0.01||winning.BadFraction>0.02||best>0.02)return new("mismatch");
        double second=double.MaxValue;
        // Include zero and adjacent pixels: a repeated code row or a thin-rule
        // alias cannot become a new captured section through this fallback.
        for(int shift=0;shift<h*0.82;shift++)if(shift!=bestShift)second=Math.Min(second,scores[shift].Error);
        if(second-best<0.04||best>second*0.25)return new("ambiguous");
        if(bestShift==0)return new("unchanged",Error:best);
        return new("append",bestShift,footer,best,header);
    }
    private static SparseScore SparseOverlap(SparseFrame a,SparseFrame b,int w,int h,int top,int bottom,int shift)
    {
        int count=0,bad=0,rows=0,columns=0;double geometry=0,color=0;var usedColumns=new bool[w];
        for(int y=top+shift;y<h-bottom;y++) {
            var firstEdges=a.Rows[y];var secondEdges=b.Rows[y-shift];int left=0,right=0,rowCount=0;
            while(left<firstEdges.Length||right<secondEdges.Length) {
                int x=Math.Min(left<firstEdges.Length?firstEdges[left].X:int.MaxValue,right<secondEdges.Length?secondEdges[right].X:int.MaxValue);
                bool hasFirst=left<firstEdges.Length&&firstEdges[left].X==x,hasSecond=right<secondEdges.Length&&secondEdges[right].X==x;
                var firstEdge=hasFirst?firstEdges[left++]:default;var secondEdge=hasSecond?secondEdges[right++]:default;
                int strength=Math.Max(Math.Abs(firstEdge.Dx)+Math.Abs(firstEdge.Dy),Math.Abs(secondEdge.Dx)+Math.Abs(secondEdge.Dy));
                double edge=(double)(Math.Abs(firstEdge.Dx-secondEdge.Dx)+Math.Abs(firstEdge.Dy-secondEdge.Dy))/(strength+8);
                int first=firstEdge.Color,second=secondEdge.Color;
                double rgb=hasFirst&&hasSecond?(Math.Abs((first&255)-(second&255))+Math.Abs(((first>>8)&255)-((second>>8)&255))+Math.Abs(((first>>16)&255)-((second>>16)&255)))/(3d*255):1;
                geometry+=edge;color+=rgb;count++;rowCount++;
                if(edge>0.1||rgb>0.03)bad++;
                if(!usedColumns[x]){usedColumns[x]=true;columns++;}
            }
            if(rowCount>=4)rows++;
        }
        if(count<60||rows<3||columns<12)return new(double.MaxValue,double.MaxValue,double.MaxValue,1,count);
        geometry/=count;color/=count;
        return new(geometry+color*0.5,geometry,color,(double)bad/count,count);
    }
    private readonly record struct DetailScore(double Error,int Features,int Regions);
    private static DetailScore FineScore(byte[] a,byte[] b,int w,int h,int top,int bottom,int shift,bool normalizeEdges=false)
    {
        int bands=(h+31)/32;var errors=new double[bands];var counts=new int[bands];
        int features=0;
        for(int y=top+shift;y<h-bottom;y+=2)for(int x=4;x<w-8;x+=2) {
            int i=y*w+x,j=(y-shift)*w+x;
            int ax=a[i]-a[i-1],ay=a[i]-a[i-w],bx=b[j]-b[j-1],by=b[j]-b[j-w];
            int strength=Math.Max(Math.Abs(ax)+Math.Abs(ay),Math.Abs(bx)+Math.Abs(by));
            if(strength<6)continue;
            // Local edge direction survives a translucent fade's contrast loss.
            // In the geometric pass normalize each observation independently;
            // absent, reversed or displaced edges still disagree.
            double error=normalizeEdges?EdgeError(ax,ay,bx,by):Math.Min(1d,(double)(Math.Abs(ax-bx)+Math.Abs(ay-by))/(strength+8));
            int band=(y-top-shift)/32;errors[band]+=error;counts[band]++;features++;
        }
        int regions=0;var regionErrors=new double[bands];var regionCounts=new int[bands];
        for(int i=0;i<bands;i++)if(counts[i]>=12){regionErrors[regions]=errors[i]/counts[i];regionCounts[regions++]=counts[i];}
        if(features<60||regions<3)return new(double.MaxValue,features,regions);
        Array.Sort(regionErrors,regionCounts,0,regions);
        // A single hover or animation may damage a small part of the overlap.
        // Keep at least three independently textured vertical regions, and trim
        // no more than one region in six. All competing shifts get the same rule.
        int retained=regions-regions/6;double sum=0;int count=0;
        for(int i=0;i<retained;i++){sum+=regionErrors[i]*regionCounts[i];count+=regionCounts[i];}
        return new(sum/count,features,regions);
    }
    private static double EdgeError(int ax,int ay,int bx,int by)
    {
        int first=Math.Abs(ax)+Math.Abs(ay),second=Math.Abs(bx)+Math.Abs(by);
        if(first==0||second==0)return first==second?0:1;
        return (double)(Math.Abs(ax*second-bx*first)+Math.Abs(ay*second-by*first))/(2d*first*second);
    }
    private static StitchMatch FineMatch(byte[] a,byte[] b,int w,int h,bool normalizeEdges=false)
    {
        int header=DetailBorder(a,b,w,h,true),footer=DetailBorder(a,b,w,h,false);
        int top=Math.Max(4,header),bottom=Math.Max(4,footer),bestShift=-1;
        double best=double.MaxValue;var scores=new double[h];Array.Fill(scores,double.MaxValue);
        for(int shift=0;shift<h*0.82;shift++) {
            if(h-shift-top-bottom<96)continue;
            var score=FineScore(a,b,w,h,top,bottom,shift,normalizeEdges);scores[shift]=score.Error;
            if(score.Error<best){best=score.Error;bestShift=shift;}
        }
        if(bestShift<0||best>0.08)return new("mismatch",Error:best);
        double second=double.MaxValue;int rival=-1;
        // At the page bottom even a one-pixel scroll adds real content. Such a
        // small shift must beat no movement and every neighboring alignment;
        // the wider peak exclusion used for normal scrolling is unsafe here.
        bool small=bestShift<=3;
        for(int shift=0;shift<h*0.82;shift++)if(shift!=bestShift&&(small||shift==0||Math.Abs(shift-bestShift)>5)&&scores[shift]<second){second=scores[shift];rival=shift;}
        // A nearly equal row-period alias is unsafe. Besides a score gap, require
        // the winning alignment to beat it on distinct details in three regions.
        // Only a rival which itself meets the absolute quality requirement is a
        // viable alignment. A rejected, much larger shift can leave too little
        // shared textured content near page-end padding for three unique anchors.
        if(second-best<0.003||best>second*0.5||(rival>=0&&second<=0.08&&!DistinctAnchors(a,b,w,h,top,bottom,bestShift,rival,normalizeEdges)))return new("ambiguous",Error:best);
        if(bestShift==0)return new("unchanged",Error:best);
        return new("append",bestShift,footer,best,header);
    }
    private static bool DistinctAnchors(byte[] a,byte[] b,int w,int h,int top,int bottom,int shift,int rival,bool normalizeEdges=false)
    {
        int start=top+Math.Max(shift,rival),regions=0,advantages=0,band=-1;
        for(int y=start;y<h-bottom;y+=2) {
            int current=(y-start)/32;
            if(current!=band){if(advantages>=8)regions++;advantages=0;band=current;}
            for(int x=4;x<w-8;x+=2) {
                int i=y*w+x,j=(y-shift)*w+x,k=(y-rival)*w+x;
                int ax=a[i]-a[i-1],ay=a[i]-a[i-w],bx=b[j]-b[j-1],by=b[j]-b[j-w],cx=b[k]-b[k-1],cy=b[k]-b[k-w];
                int strength=Math.Max(Math.Abs(ax)+Math.Abs(ay),Math.Max(Math.Abs(bx)+Math.Abs(by),Math.Abs(cx)+Math.Abs(cy)));
                if(strength<6)continue;
                double good=normalizeEdges?EdgeError(ax,ay,bx,by):(double)(Math.Abs(ax-bx)+Math.Abs(ay-by))/(strength+8);
                double other=normalizeEdges?EdgeError(ax,ay,cx,cy):(double)(Math.Abs(ax-cx)+Math.Abs(ay-cy))/(strength+8);
                if(good<=0.08&&other-good>0.15)advantages++;
            }
        }
        if(advantages>=8)regions++;
        return regions>=3;
    }
    private static int DetailBorder(byte[] a,byte[] b,int w,int h,bool fromTop)
    {
        int length=0;
        for(int row=0;row<h/5;row++) {
            int y=fromTop?row:h-1-row,changed=0;
            for(int x=4;x<w-8;x++)if(Math.Abs(a[y*w+x]-b[y*w+x])>4)changed++;
            // Unlike a whole-row average, a few changed glyph strokes count as
            // moving content even on an otherwise blank, low-contrast file row.
            if(changed>=3)break;
            length++;
        }
        return length>=8?length:0;
    }
    private static int StableBorder(byte[] a,byte[] b,int w,int h,bool fromTop)
    {
        int length=0;
        for(int row=0;row<h/5;row++) {
            int y=fromTop?row:h-1-row;
            double error=0; for(int x=4;x<w-4;x++) error+=Math.Abs(a[y*w+x]-b[y*w+x]);
            if(error/(w-8)>1) break;
            length++;
        }
        return length>=8?length:0;
    }
    public static Bitmap Append(Bitmap composite, Bitmap next, StitchMatch match)
    {
        if(match.State!="append"||match.Shift<1)throw new ArgumentException("未通过匹配，不能拼接。");
        int join=OverlapJoin(next.Height,match);
        // Join in the middle of the verified moving overlap: retain the clean
        // upper part of the older frame without copying a fixed top overlay into
        // later body rows, and replace old bottom overlays with newer body pixels.
        // The latest footer remains once; repeated text keeps its coordinates.
        int prefix=composite.Height-next.Height+match.Shift+join;
        return ReplaceTail(composite,next,prefix,join,checked(composite.Height+match.Shift));
    }
    internal static int OverlapJoin(int height,StitchMatch match)
    {
        int top=Math.Max(4,match.Header),bottom=height-match.Shift-Math.Max(4,match.Footer);
        if(match.Shift<0||match.Header<0||match.Footer<0||top>=bottom)throw new ArgumentException("没有可回写的正文重叠区域。");
        return top+(bottom-top)/2;
    }
    internal static Bitmap RefreshTail(Bitmap composite,Bitmap next,int header)
    {
        // The caller must establish zero displacement before replacing a changed
        // final viewport (for example, a disappearing scroll-to-bottom button).
        if(header<0||header>=next.Height)throw new ArgumentOutOfRangeException(nameof(header));
        return ReplaceTail(composite,next,composite.Height-next.Height+header,header,composite.Height);
    }
    private static Bitmap ReplaceTail(Bitmap composite,Bitmap next,int prefix,int sourceTop,int height)
    {
        if(composite.Width!=next.Width||composite.Height<next.Height||prefix<0||prefix>composite.Height||prefix+next.Height-sourceTop!=height)
            throw new ArgumentException("拼接画面尺寸或正文映射无效。");
        Imaging.CheckSize(composite.Width,height);
        var result=new Bitmap(composite.Width,height,PixelFormat.Format32bppArgb);
        try {
            using var g=Graphics.FromImage(result);g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            if(prefix>0)g.DrawImage(composite,new Rectangle(0,0,result.Width,prefix),new Rectangle(0,0,composite.Width,prefix),GraphicsUnit.Pixel);
            g.DrawImage(next,new Rectangle(0,prefix,result.Width,next.Height-sourceTop),new Rectangle(0,sourceTop,next.Width,next.Height-sourceTop),GraphicsUnit.Pixel);
            return result;
        }catch{result.Dispose();throw;}
    }
    internal static bool SamePixels(Bitmap a,Bitmap b)
    {
        if(ReferenceEquals(a,b))return true;
        if(a.Size!=b.Size)return false;
        // Production frames are 32bpp. An unexpected format is treated as changed
        // rather than compared through lossy grayscale or encoded PNG bytes.
        if(Image.GetPixelFormatSize(a.PixelFormat)!=32||Image.GetPixelFormatSize(b.PixelFormat)!=32)return false;
        BitmapData? first=null,second=null;
        try {
            var bounds=new Rectangle(0,0,a.Width,a.Height);
            first=a.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            second=b.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            int count=checked(a.Width*4);var left=new byte[count];var right=new byte[count];
            for(int y=0;y<a.Height;y++) {
                Marshal.Copy(IntPtr.Add(first.Scan0,y*first.Stride),left,0,count);
                Marshal.Copy(IntPtr.Add(second.Scan0,y*second.Stride),right,0,count);
                if(!left.AsSpan().SequenceEqual(right))return false;
            }
            return true;
        }finally{try{if(second!=null)b.UnlockBits(second);}finally{if(first!=null)a.UnlockBits(first);}}
    }
}
