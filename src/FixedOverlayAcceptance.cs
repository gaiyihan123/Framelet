using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using W=System.Windows;
using Controls=System.Windows.Controls;
using Media=System.Windows.Media;
using WpfImaging=System.Windows.Media.Imaging;
using WpfInterop=System.Windows.Interop;

namespace QingCapture;

// Production receives only rendered, occluded screenshots. The independent golden
// document is retained by this test solely to check recovered pixels and structure.
internal static class FixedOverlayAcceptance
{
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window,bool enabled);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    private const int Width=800,Height=650,Header=56,Footer=76,Fade=96,Padding=120;
    private static readonly Color Background=Color.FromArgb(42,42,42);
    [Flags]private enum Layers {Button=1,Gradient=2,Both=3}

    internal static List<string> Run(string? folder=null)
    {
        var lines=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        foreach(var layers in new[]{Layers.Button,Layers.Gradient,Layers.Both})RunPair(Check,layers,folder);
        foreach(int tail in new[]{1,2,3,17})RunSequence(Check,tail,folder);
        RunDragController(Check,folder);
        RunNativeDarkOverlay(Check,folder);
        RunNativeDarkOverlay(Check,folder,cancelWhileCaptured:true);
        return lines;
    }

    private static void RunPair(Action<bool,string> check,Layers layers,string? folder)
    {
        const int shift=126;using var document=Document(Height-Header-Footer+shift);
        using var first=Frame(document,0,layers,false);using var next=Frame(document,shift,layers,true);using var golden=Golden(document);
        var match=Stitcher.Match(first,next);
        check(match.State=="append"&&match.Shift==shift,$"dark chat {layers} screenshots preserve the independently known 126px scroll displacement");
        check(match.Header>0&&match.Footer>0,$"dark chat {layers} identifies its fixed header and footer without treating the scrolling body as fixed");
        using var legacy=TailOnly(first,next,match);
        check(!Equal(legacy,golden),$"the old tail-only append demonstrably leaves {layers} contamination and cannot pass the dark-chat golden");
        using var actual=Stitcher.Append(first,next,match);
        check(Equal(actual,golden),$"a later clean overlap repairs the dark chat {layers} layer while retaining every original body pixel and one header/footer");
        if(folder!=null) {
            Directory.CreateDirectory(folder);Imaging.Save(first,Path.Combine(folder,$"fixed-overlay-{layers}-before.png"));Imaging.Save(next,Path.Combine(folder,$"fixed-overlay-{layers}-after.png"));
            Imaging.Save(golden,Path.Combine(folder,$"fixed-overlay-{layers}-golden.png"));Imaging.Save(actual,Path.Combine(folder,$"fixed-overlay-{layers}-actual.png"));
        }
    }

    private static void RunSequence(Action<bool,string> check,int tail,string? folder)
    {
        const int step=126;int maximum=step*5+tail;using var document=Document(Height-Header-Footer+maximum);using var golden=Golden(document);
        Bitmap previous=Frame(document,0,Layers.Both,false),composite=new(previous);bool shifts=true;
        try {
            int offset=0;
            foreach(int nextOffset in new[]{step,step*2,step*3,step*4,step*5,maximum}) {
                using var next=Frame(document,nextOffset,Layers.Both,nextOffset==maximum);var match=Stitcher.Match(previous,next);
                shifts&=match.State=="append"&&match.Shift==nextOffset-offset;
                if(match.State!="append")throw new InvalidOperationException($"Dark chat overlap rejected at {offset}->{nextOffset}: {match}");
                var merged=Stitcher.Append(composite,next,match);composite.Dispose();composite=merged;previous.Dispose();previous=new Bitmap(next);offset=nextOffset;
            }
            check(shifts,$"dark mixed-color chat matches five 126px steps and a final {tail}px displacement without weakening shift confidence");
            check(composite.Size==golden.Size&&Equal(composite,golden),$"dark chat preserves the complete body without repeated floating arrows or translucent text bands after a {tail}px terminal step");
            var inline=new Rectangle(128,Header+210,190,78);
            using(var expectedInline=Imaging.Crop(golden,inline))using(var actualInline=Imaging.Crop(composite,inline))
                check(Equal(actualInline,expectedInline),$"legitimate in-document arrow and gradient remain intact rather than being erased as fixed UI with a {tail}px terminal step");
            if(folder!=null){Directory.CreateDirectory(folder);Imaging.Save(composite,Path.Combine(folder,$"fixed-overlay-tail-{tail}.png"));}
        }finally{previous.Dispose();composite.Dispose();}
    }

    private static void RunDragController(Action<bool,string> check,string? folder)
    {
        const int step=126,tail=1;int maximum=step*3+tail;using var document=Document(Height-Header-Footer+maximum);using var golden=Golden(document);
        using var initial=Frame(document,0,Layers.Both,false);int offset=0,scrolls=0,cleanFrames=0;bool finishClean=false;
        using var controller=new DragCaptureController(new Rectangle(80,100,Width,Height),initial,_=> {
            if(finishClean)cleanFrames++;
            return Frame(document,offset,Layers.Both,finishClean);
        },()=> {
            scrolls++;int amount=Math.Min(step,maximum-offset);if(amount<=0)return false;offset+=amount;return true;
        },()=>offset>=maximum);
        controller.UpdatePointer(new Point(400,100+Height-20));PumpUntil(()=>controller.AtBottom||controller.Error!=null,14000);
        check(controller.Error==null&&controller.AtBottom&&controller.ScrollOffset==maximum&&scrolls>=4,"the production drag controller captures layered dark chat through three 126px steps and the final 1px using automatic scroll requests");
        // A final source observation hides its floating control without moving.
        // Finish must refresh the existing overlap even when the shift is zero.
        int framesBeforeFinish=controller.FrameCount,offsetBeforeFinish=controller.ScrollOffset;
        finishClean=true;Task<Bitmap> finishing=controller.Finish();PumpUntil(()=>finishing.IsCompleted,6000);using var result=finishing.GetAwaiter().GetResult();
        check(cleanFrames>=2&&Equal(result,golden)&&controller.FrameCount==framesBeforeFinish&&controller.ScrollOffset==offsetBeforeFinish,"production drag completion uses a zero-displacement clean observation to remove the last floating arrow without inventing another frame or scroll displacement");
        check(controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive,"fixed-overlay drag completion releases the frame images and both sampling and range timers");
        if(folder!=null){Directory.CreateDirectory(folder);Imaging.Save(result,Path.Combine(folder,"fixed-overlay-drag-controller.png"));}
    }

    internal static void RunNativeDarkOverlay(Action<bool,string> check,string? folder,bool cancelWhileCaptured=false)
    {
        const int step=126,tail=1,chosenWidth=300,column=320,topInset=4;int maximum=step*3+tail;
        var app=W.Application.Current as App??new App();app.ShutdownMode=W.ShutdownMode.OnExplicitShutdown;Ui.Resources(app);
        using var document=Document(Height-Header-Footer+maximum);using var golden=Golden(document);
        var image=new Controls.Image {Stretch=Media.Stretch.Fill,UseLayoutRounding=true,SnapsToDevicePixels=true};
        var source=new W.Window {Title="Framelet fixed-overlay native acceptance",Content=image,WindowStyle=W.WindowStyle.None,ResizeMode=W.ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,Background=Media.Brushes.Black,UseLayoutRounding=true,SnapsToDevicePixels=true,Width=Width,Height=Height};
        CaptureOverlay? overlay=null;Bitmap? result=null;DispatcherTimer? timer=null;DragCaptureController? controller=null;Exception? error=null;
        int offset=0,nativeWheels=0;bool callbackCompleted=false;
        try {
            Native.GetCursorPos(out var cursor);var monitor=System.Windows.Forms.Screen.FromPoint(new Point(cursor.X,cursor.Y)).WorkingArea;
            if(monitor.Width<Width||monitor.Height<Height)throw new InvalidOperationException("The native dark-chat fixture requires an 800x650 physical-pixel working area.");
            int left=monitor.Left+Math.Min(24,monitor.Width-Width),top=monitor.Top+Math.Min(24,monitor.Height-Height);
            source.Show();var handle=new WpfInterop.WindowInteropHelper(source).EnsureHandle();Native.SetWindowPos(handle,new IntPtr(-1),left,top,Width,Height,0x40);source.UpdateLayout();
            var dpi=Media.VisualTreeHelper.GetDpi(source);image.Width=Width/dpi.DpiScaleX;image.Height=Height/dpi.DpiScaleY;Media.RenderOptions.SetBitmapScalingMode(image,Media.BitmapScalingMode.NearestNeighbor);
            using(var first=Frame(document,0,Layers.Both,false))image.Source=Imaging.Source(first);
            WpfInterop.HwndSource.FromHwnd(handle)!.AddHook((IntPtr _,int message,IntPtr w,IntPtr l,ref bool handled)=> {
                if(message==MouseWheelObserver.MouseWheelMessage) {
                    nativeWheels++;int delta=unchecked((short)(w.ToInt64()>>16));
                    if(delta<0)offset=Math.Min(maximum,offset+step);
                    using var frame=Frame(document,offset,Layers.Both,offset==maximum);image.Source=Imaging.Source(frame);source.UpdateLayout();handled=true;
                }
                return IntPtr.Zero;
            });
            source.Activate();source.UpdateLayout();PumpFor(300);source.UpdateLayout();
            Native.SetWindowPos(handle,new IntPtr(-1),0,0,0,0,0x13);
            var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;using var snapshot=Imaging.Capture(desktop);
            using(var nativeFirst=Imaging.Crop(snapshot,new Rectangle(left+column-desktop.Left,top+topInset-desktop.Top,chosenWidth,Height-topInset)))
            using(var first=Frame(document,0,Layers.Both,false))
            using(var expectedFirst=Imaging.Crop(first,new Rectangle(column,topInset,chosenWidth,Height-topInset))) {
                if(folder!=null){Directory.CreateDirectory(folder);Imaging.Save(nativeFirst,Path.Combine(folder,"fixed-overlay-native-before.png"));}
                check(Equal(nativeFirst,expectedFirst),"the actual native source displays the dark text, alpha gradient and floating arrow at exact physical 1:1 before capture starts");
            }
            overlay=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Scroll,_=>{});timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
            timer.Tick+=(_,_)=> {
                timer.Stop();
                try {
                    // Enable only the fixture's own source after modal WPF
                    // disables it, preserving actual native hit-test routing.
                    EnableWindow(handle,true);
                    check(IsWindowEnabled(handle),"the self-owned fixed-overlay source is enabled for native wheel hit testing during the modal overlay");
                    Native.SetWindowPos(handle,new WpfInterop.WindowInteropHelper(overlay).Handle,0,0,0,0,0x13);
                    W.Point Local(int x,int y)=>overlay.PointFromScreen(new W.Point(x+.25,y+.25));
                    // This locked column contains the floating arrow and its fade.
                    int originX=left+column,originY=top+topInset;
                    overlay.BeginPointer(Local(originX,originY));overlay.MovePointer(Local(originX+chosenWidth,originY+50));overlay.MovePointer(Local(originX+chosenWidth,originY+100));
                    controller=overlay.DragCapture;
                    check(controller!=null&&overlay.IsMouseCaptured&&overlay.IsVisible&&controller.Viewport.Width==chosenWidth,"the loaded dark-chat source enters production drag capture with its floating control inside the locked physical column");
                    var viewport=controller!.Viewport;check(viewport.Left==originX&&viewport.Top==originY&&viewport.Bottom==top+Height,"the native dark-chat observation includes the source header, scrolling body and fixed footer at physical 1:1");
                    if(cancelWhileCaptured) {
                        overlay.Close();
                        PumpUntil(()=>controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&!overlay.IsMouseCaptured,1500);
                        check(!overlay.IsVisible&&overlay.TakeDragResult()==null&&controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&!overlay.IsMouseCaptured&&nativeWheels==0,"closing a loaded drag overlay while native mouse capture is held cancels without reentering selection completion, publishing an image or leaking capture, bitmaps or timers");
                        return;
                    }
                    var bottom=Local(viewport.Left+chosenWidth/2,viewport.Bottom-1);overlay.MovePointer(bottom);
                    PumpUntil(()=>controller.ScrollOffset==maximum||controller.Error!=null||!overlay.IsVisible,7500);
                    if(controller.Error!=null&&folder!=null) {
                        using var previous=controller.CopyLastFrame();using var failed=Imaging.Capture(viewport);
                        if(previous!=null)Imaging.Save(previous,Path.Combine(folder,"dark-native-rejected-previous.png"));
                        Imaging.Save(failed,Path.Combine(folder,"dark-native-rejected-current.png"));
                    }
                    check(controller.Error==null&&overlay.IsVisible&&overlay.IsMouseCaptured&&offset==maximum&&controller.ScrollOffset==maximum&&nativeWheels>=4,$"production native auto-scroll captures layered dark chat at 126px steps and the final 1px (wheels={nativeWheels}, source={offset}, stitched={controller.ScrollOffset}, error={controller.Error}, match={controller.LastMatch})");
                    overlay.EndPointer(bottom);PumpUntil(()=>!overlay.IsVisible,4000);result=overlay.TakeDragResult();
                    using var expected=Imaging.Crop(golden,new Rectangle(column,topInset,chosenWidth,golden.Height-topInset));
                    if(folder!=null&&result!=null) {
                        Directory.CreateDirectory(folder);Imaging.Save(result,Path.Combine(folder,"fixed-overlay-native-actual.png"));Imaging.Save(expected,Path.Combine(folder,"fixed-overlay-native-golden.png"));
                    }
                    check(overlay.DialogResult==true&&result!=null&&Equal(result,expected),"real native screen captures of dark chat match the independent locked-column golden without repeated arrows, dark bands, missing text or altered colors");
                    using(var body=Imaging.Crop(result!,new Rectangle(0,Header-topInset,chosenWidth,document.Height)))
                    using(var expectedBody=Imaging.Crop(document,new Rectangle(column,0,chosenWidth,document.Height)))
                        check(Equal(body,expectedBody),"every dark-chat scrolling-body pixel survives the real Overlay, native dispatch, screen capture and production stitching path");
                    check(controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&!overlay.IsMouseCaptured,"native fixed-overlay completion releases pointer capture, bitmaps and both controller timers");
                }catch(Exception ex){error=ex;}
                finally {if(overlay.IsVisible)overlay.Close();callbackCompleted=true;}
            };
            timer.Start();overlay.ShowDialog();if(!callbackCompleted)PumpUntil(()=>callbackCompleted,11000);
            if(error!=null)throw new InvalidOperationException("Native dark fixed-overlay acceptance failed",error);
        }finally{timer?.Stop();overlay?.Close();controller?.Dispose();source.Close();result?.Dispose();}
    }

    private static Bitmap Document(int height)
    {
        var bitmap=new Bitmap(Width,height,PixelFormat.Format32bppArgb);using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Background);
        using var title=new Font("Microsoft YaHei UI",20,GraphicsUnit.Pixel);using var text=new Font("Microsoft YaHei UI",17,GraphicsUnit.Pixel);using var small=new Font("Segoe UI",13,GraphicsUnit.Pixel);
        using var white=new SolidBrush(Color.FromArgb(240,240,240));using var muted=new SolidBrush(Color.FromArgb(185,188,194));using var blue=new SolidBrush(Color.FromArgb(43,103,190));using var separator=new Pen(Color.FromArgb(69,69,69));
        int contentBottom=height-Padding;
        for(int top=14,n=0;top<contentBottom;top+=104,n++) {
            int rowHeight=Math.Min(88,contentBottom-top);
            if(n%2==0) {
                graphics.FillRectangle(blue,300,top,Width-320,Math.Min(72,rowHeight));
                graphics.DrawString($"消息 {n:000} · Ref {n*7919+37}",title,white,316,top+7);
                if(rowHeight>50)graphics.DrawString("请保留完整正文、颜色与最后一行。",text,white,316,top+39);
            }else {
                graphics.DrawString($"正文 {n:000} — 文件路径 / item-{n*173+91}.md",title,white,64,top+4);
                if(rowHeight>48)graphics.DrawString($"• 条件与顺序必须保持。 Timestamp 10:{n%60:00}",text,white,74,top+36);
                if(rowHeight>74)graphics.DrawString("128 / 256 / 512  •  source pixels",small,muted,74,top+66);
            }
            graphics.DrawLine(separator,64,top+Math.Min(96,rowHeight),Width-25,top+Math.Min(96,rowHeight));
        }
        // This graphic scrolls with the document and must survive any UI cleanup.
        if(contentBottom>300) {
            graphics.FillRectangle(blue,128,210,190,78);DrawGradient(graphics,new Rectangle(128,226,190,62));DrawArrow(graphics,190,232);
        }
        return bitmap;
    }

    private static Bitmap Frame(Bitmap document,int offset,Layers layers,bool cleanEnd)
    {
        var bitmap=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Background);
        int bodyHeight=Height-Header-Footer;
        graphics.DrawImage(document,new Rectangle(0,Header,Width,bodyHeight),new Rectangle(0,offset,Width,bodyHeight),GraphicsUnit.Pixel);
        DrawHeader(graphics);DrawFooter(graphics,Height);
        if(layers.HasFlag(Layers.Gradient))DrawGradient(graphics,new Rectangle(0,Header+bodyHeight-Fade,Width,Fade));
        if(layers.HasFlag(Layers.Button)&&!cleanEnd)DrawArrow(graphics,(int)(Width*.58)-21,Header+bodyHeight-60);
        return bitmap;
    }

    private static Bitmap Golden(Bitmap document)
    {
        var bitmap=new Bitmap(Width,Header+document.Height+Footer,PixelFormat.Format32bppArgb);using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Background);
        graphics.DrawImage(document,new Rectangle(0,Header,Width,document.Height),new Rectangle(0,0,Width,document.Height),GraphicsUnit.Pixel);
        DrawHeader(graphics);DrawFooter(graphics,bitmap.Height);return bitmap;
    }

    private static void DrawHeader(Graphics graphics)
    {
        using var panel=new SolidBrush(Color.FromArgb(35,35,35));using var font=new Font("Microsoft YaHei UI",19,GraphicsUnit.Pixel);using var ink=new SolidBrush(Color.FromArgb(245,245,245));using var line=new Pen(Color.FromArgb(68,68,68));
        graphics.FillRectangle(panel,0,0,Width,Header);graphics.DrawString("检查完整截图 · 聊天正文",font,ink,20,15);graphics.DrawLine(line,0,Header-1,Width,Header-1);
    }
    private static void DrawFooter(Graphics graphics,int height)
    {
        using var panel=new SolidBrush(Background);using var input=new SolidBrush(Color.FromArgb(79,79,79));using var line=new Pen(Color.FromArgb(119,119,119));using var font=new Font("Microsoft YaHei UI",16,GraphicsUnit.Pixel);using var ink=new SolidBrush(Color.FromArgb(185,185,185));
        graphics.FillRectangle(panel,0,height-Footer,Width,Footer);graphics.FillRectangle(input,30,height-Footer+10,Width-60,56);graphics.DrawRectangle(line,30,height-Footer+10,Width-60,56);
        graphics.DrawString("与 Framelet 协作",font,ink,46,height-Footer+24);graphics.DrawString("+    本地离线",font,ink,Width-190,height-Footer+24);
    }
    private static void DrawGradient(Graphics graphics,Rectangle area)
    {
        for(int row=0;row<area.Height;row++) {
            using var brush=new SolidBrush(Color.FromArgb((int)Math.Round(232d*row/Math.Max(1,area.Height-1)),Background));
            graphics.FillRectangle(brush,area.X,area.Y+row,area.Width,1);
        }
    }
    private static void DrawArrow(Graphics graphics,int x,int y)
    {
        var old=graphics.SmoothingMode;graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var fill=new SolidBrush(Color.FromArgb(46,46,46));using var ring=new Pen(Color.FromArgb(98,98,98),1);using var arrow=new Pen(Color.FromArgb(245,245,245),2.2f) {StartCap=LineCap.Round,EndCap=LineCap.Round};
        graphics.FillEllipse(fill,x,y,42,42);graphics.DrawEllipse(ring,x,y,42,42);graphics.DrawLine(arrow,x+21,y+11,x+21,y+30);graphics.DrawLine(arrow,x+13,y+23,x+21,y+31);graphics.DrawLine(arrow,x+21,y+31,x+29,y+23);graphics.SmoothingMode=old;
    }

    // Deliberate negative control: a faithful copy of the former append strategy.
    private static Bitmap TailOnly(Bitmap composite,Bitmap next,StitchMatch match)
    {
        var bitmap=new Bitmap(composite.Width,composite.Height+match.Shift,PixelFormat.Format32bppArgb);using var graphics=Graphics.FromImage(bitmap);
        graphics.DrawImageUnscaled(composite,0,0);int count=match.Shift+match.Footer;
        graphics.DrawImage(next,new Rectangle(0,composite.Height-match.Footer,bitmap.Width,count),new Rectangle(0,next.Height-count,next.Width,count),GraphicsUnit.Pixel);return bitmap;
    }
    private static bool Equal(Bitmap actual,Bitmap expected)=>actual.Size==expected.Size&&Imaging.Png(actual).SequenceEqual(Imaging.Png(expected));
    private static void PumpUntil(Func<bool> condition,int milliseconds)
    {
        var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<milliseconds) {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
        }
        if(!condition())throw new TimeoutException("Fixed-overlay acceptance did not settle.");
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
