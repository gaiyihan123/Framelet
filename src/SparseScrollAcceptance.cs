using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace QingCapture;

// Owned bitmaps exercise sparse chat content without a GUI, a native source
// hook, or an assumed relationship between wheel delta and document distance.
internal static class SparseScrollAcceptance
{
    internal static List<string> Run()
    {
        var lines=new List<string>();
        void Check(bool value,string label){if(!value)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        foreach(int width in new[]{400,1292})foreach(int rowCount in new[]{1,2}) {
            using var document=Document(width,rowCount);
            using var first=Frame(document,0,430);
            using var same=new Bitmap(first);
            Check(Stitcher.Match(first,same).State=="unchanged",$"an identical {width}px-wide chat frame with only {rowCount} text line(s) is stationary despite its large blank margins");
            foreach(int shift in new[]{17,81,120}) {
                using var next=Frame(document,shift,430);
                var match=Stitcher.Match(first,next);
                Check(match.State=="append"&&match.Shift==shift,$"full-resolution sparse chat with {rowCount} line(s) at width {width} identifies its unique actual {shift}px movement");
                using var result=Stitcher.Append(first,next,match);using var expected=Frame(document,0,430+shift);
                Check(Stitcher.SamePixels(result,expected),$"sparse {width}px chat joins all original rows exactly once for shift {shift}, retaining its header and footer once");
            }
        }
        using(var sparse=new Bitmap(400,430)) {
            using(var graphics=Graphics.FromImage(sparse)){graphics.Clear(Color.FromArgb(43,43,43));graphics.FillRectangle(Brushes.White,150,200,80,8);}
            using var same=new Bitmap(sparse);
            Check(Stitcher.Match(sparse,same).State=="unchanged","pixel-identical sparse frames are stationary even when their few edges occupy only one 32px region");
        }
        using(var document=new Bitmap(400,1000)) {
            using(var graphics=Graphics.FromImage(document)) {
                graphics.Clear(Color.FromArgb(43,43,43));using var font=new Font("Consolas",15,GraphicsUnit.Pixel);
                for(int y=0;y<document.Height;y+=42)graphics.DrawString("repeated code: { value = value; }",font,Brushes.White,20,y);
            }
            using var first=Crop(document,0,430);using var next=Crop(document,17,430);
            Check(Stitcher.Match(first,next).State!="append","pure periodic code rows remain rejected when more than one shift explains their repeated shapes");
        }
        using(var document=new Bitmap(1292,1000)) {
            using(var graphics=Graphics.FromImage(document)) {
                graphics.Clear(Color.FromArgb(43,43,43));graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                using var font=new Font("Consolas",17,GraphicsUnit.Pixel);
                for(int y=0;y<document.Height;y+=180)graphics.DrawString("same sparse code line: item = value;",font,Brushes.White,510,y);
            }
            using var first=Frame(document,0,430);using var next=Frame(document,17,430);
            var periodic=Stitcher.Match(first,next);
            Check(periodic.State!="append","sparse periodic code lines also reject competing exact shifts instead of using their blank spacing as a unique anchor: "+periodic);
        }
        using(var first=Document(400,1))using(var other=Document(400,1,"UNRELATED 9889 / completely different source 79331"))
        using(var a=Frame(first,0,430))using(var b=Frame(other,90,430))
            Check(Stitcher.Match(a,b).State!="append","unrelated sparse text cannot supply the unique overlapping RGB and edge evidence required for an append");
        RunStationaryRule(Check);
        RunContinuous(Check);
        return lines;
    }
    private static void RunStationaryRule(Action<bool,string> check)
    {
        foreach(int rows in new[]{1,4}) {
            using var first=new Bitmap(1920,800);using var next=new Bitmap(1920,800);
            void Paint(Bitmap image,int top)
            {
                using var graphics=Graphics.FromImage(image);graphics.Clear(Color.FromArgb(43,43,43));graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                graphics.FillRectangle(Brushes.Gray,35,0,1,800);using var font=new Font("Consolas",17,GraphicsUnit.Pixel);
                for(int n=0;n<rows;n++)graphics.DrawString($"Chat {n+47:0000}: unique source token {932773+n*7919}.",font,Brushes.White,650,top+n*25);
            }
            Paint(first,350);Paint(next,230);
            long allocated=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();var match=Stitcher.Match(first,next);clock.Stop();allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
            check(match.State=="append"&&match.Shift==120,$"a full-height stationary rule cannot hide {rows} moving chat line(s) in a 1920 by 800 frame (Match {clock.Elapsed.TotalMilliseconds:F1}ms, allocated {allocated} bytes)");
            using var same=new Bitmap(first);
            check(Stitcher.Match(first,same).State=="unchanged",$"the same rule and {rows} stationary line(s) remain an exact zero-displacement control");
        }
        foreach(var rendering in new[]{System.Drawing.Text.TextRenderingHint.AntiAliasGridFit,System.Drawing.Text.TextRenderingHint.ClearTypeGridFit}) {
            using var first=new Bitmap(1292,430);using var next=new Bitmap(1292,430);
            void Paint(Bitmap image,int top)
            {
                using var graphics=Graphics.FromImage(image);graphics.Clear(Color.FromArgb(43,43,43));graphics.TextRenderingHint=rendering;
                using var font=new Font("Consolas",17,GraphicsUnit.Pixel);graphics.DrawString("Chat 0047: unique source token 932773.",font,Brushes.White,510,top);
            }
            Paint(first,210);Paint(next,90);var match=Stitcher.Match(first,next);
            check(match.State=="append"&&match.Shift==120,$"the sparse matcher retains the exact 120px overlap when both frames use {rendering}");
        }
    }
    private static void RunContinuous(Action<bool,string> check)
    {
        const int width=1292,height=430,maximum=1457;
        var viewport=new Rectangle(-1200,80,width,height);int offset=0,dispatches=0;bool bounded=true;
        using var document=new Bitmap(width,maximum+height);
        using(var graphics=Graphics.FromImage(document)) {
            graphics.Clear(Color.FromArgb(43,43,43));graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            using var font=new Font("Consolas",17,GraphicsUnit.Pixel);
            for(int y=15,n=0;y<maximum+height;y+=180,n++) {
                graphics.DrawString($"Chat {n:0000}: unique token {n*7919+173}.",font,Brushes.White,510,y);
                if(n<2)for(int dense=1;dense<5;dense++)graphics.DrawString($"Detail {n}/{dense}: value {n*6151+dense*211}.",font,Brushes.Gainsboro,510,y+dense*25);
                else if(n%3==2)graphics.DrawString($"    return code_{n*6151+73}(item_{n});",font,Brushes.LightSteelBlue,510,y+25);
            }
        }
        using var initial=Frame(document,0,height);
        using var controller=new DragCaptureController(viewport,initial,area=> {
            bounded&=area==viewport;return Frame(document,offset,height);
        },()=>throw new InvalidOperationException("A fixed sparse-chat queue must not auto-scroll."),()=>offset==maximum,delta=> {
            dispatches++;offset=Math.Clamp(offset-delta,0,maximum);return true;
        });
        check(controller.FreezeSelection(viewport),"the continuous wide-chat controller keeps the user's exact 1292 by 430 rectangle");
        for(int step=1;step<=13;step++) {
            int delta=step<=12?-120:-17,expected=Math.Min(step*120,maximum);
            check(controller.QueueWheel(delta),$"continuous sparse-chat step {step} is accepted into the production manual queue");
            PumpUntil(()=>controller.PendingWheelCount==0&&!controller.SamplingActive,12000);
            check(controller.ScrollOffset==expected&&offset==expected&&!controller.NeedsOverlap&&controller.Error==null&&controller.FrameCount==step+1,$"continuous sparse-chat step {step} confirms exactly {expected}px through dense text, single-line gaps and two-line code");
        }
        check(controller.PendingWheelCount==0&&bounded&&dispatches==13&&controller.VisibleSelection==viewport,"every continuous sparse-chat step settles before release without extending the selected frame or dispatching an extra wheel");
        Task<Bitmap> task=controller.Finish();PumpUntil(()=>task.IsCompleted,12000);using var result=task.GetAwaiter().GetResult();using var expectedImage=Frame(document,0,height+maximum);
        check(result.Size==new Size(width,height+maximum)&&Stitcher.SamePixels(result,expectedImage)&&controller.CompletionWarning==null,"the continuous four-screen sparse-chat result equals the independent source pixel for pixel including the final 17px and fixed header/footer once");
        check(controller.ImagesReleased&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!controller.RangeProbeActive,"continuous sparse-chat completion releases its accepted images, queue and both production timers");
    }
    private static void PumpUntil(Func<bool> condition,int milliseconds)
    {
        var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<milliseconds) {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
        }
        if(!condition())throw new TimeoutException("The continuous sparse-chat production queue did not settle.");
    }
    private static Bitmap Document(int width,int rows,string? text=null)
    {
        var image=new Bitmap(width,900);using var graphics=Graphics.FromImage(image);graphics.Clear(Color.FromArgb(43,43,43));
        graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        using var font=new Font("Consolas",17,GraphicsUnit.Pixel);
        int x=width==1292?510:24;
        for(int row=0;row<rows;row++)graphics.DrawString(text??$"Chat {row+17:0000}: unique token {row*7919+173}.",font,Brushes.White,x,180+row*25);
        return image;
    }
    private static Bitmap Frame(Bitmap document,int offset,int height)
    {
        const int header=32,footer=24;
        var image=new Bitmap(document.Width,height);using var graphics=Graphics.FromImage(image);graphics.CompositingMode=CompositingMode.SourceCopy;
        graphics.Clear(Color.FromArgb(43,43,43));
        graphics.DrawImage(document,new Rectangle(0,header,document.Width,height-header-footer),new Rectangle(0,offset,document.Width,height-header-footer),GraphicsUnit.Pixel);
        using var headerBrush=new SolidBrush(Color.FromArgb(25,43,68));using var footerBrush=new SolidBrush(Color.FromArgb(67,32,73));
        graphics.FillRectangle(headerBrush,0,0,document.Width,header);graphics.FillRectangle(footerBrush,0,height-footer,document.Width,footer);
        graphics.CompositingMode=CompositingMode.SourceOver;
        using var font=new Font("Consolas",12,GraphicsUnit.Pixel);graphics.DrawString("FIXED HEADER",font,Brushes.White,12,7);graphics.DrawString("FIXED FOOTER",font,Brushes.White,12,height-footer+4);
        return image;
    }
    private static Bitmap Crop(Bitmap source,int y,int height)=>source.Clone(new Rectangle(0,y,source.Width,height),System.Drawing.Imaging.PixelFormat.Format32bppArgb);
}
