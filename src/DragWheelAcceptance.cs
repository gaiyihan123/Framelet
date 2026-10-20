using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using W=System.Windows;
using Controls=System.Windows.Controls;
using Input=System.Windows.Input;
using Media=System.Windows.Media;
using Interop=System.Windows.Interop;

namespace QingCapture;

internal static class DragWheelAcceptance
{
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window,bool enabled);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    internal static List<string> Run(string? folder=null)
    {
        var lines=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        RunFifoFinish(Check);
        RunCancel(Check);
        RunManualPriority(Check);
        RunFreezeSelection(Check);
        RunInFlightFreeze(Check);
        RunRewind(Check,false);
        RunRewind(Check,true);
        RunNativeOverlay(Check,folder);
        return lines;
    }

    private static void RunFifoFinish(Action<bool,string> check)
    {
        const int width=360,height=650,maximum=481;var viewport=new Rectangle(-620,120,width,height);
        using var document=Document(width,height+maximum);var initial=Imaging.Crop(document,new Rectangle(0,0,width,height));
        int offset=0,auto=0,readsSinceDispatch=0;bool serial=true,physical=true;var deltas=new List<int>();
        using var controller=new DragCaptureController(viewport,initial,area=> {
            physical&=area==viewport;readsSinceDispatch++;return Imaging.Crop(document,new Rectangle(0,offset,width,height));
        },()=> {auto++;return false;},()=>offset>=maximum,delta=> {
            if(deltas.Count>0)serial&=readsSinceDispatch>=2;
            readsSinceDispatch=0;deltas.Add(delta);offset=Math.Clamp(offset-delta,0,maximum);return true;
        });
        initial.Dispose();controller.UpdatePointer(new Point(viewport.Left+60,viewport.Top+100));
        check(!controller.QueueWheel(0)&&controller.QueueWheel(-300)&&controller.QueueWheel(-180)&&controller.QueueWheel(-1),"manual drag scrolling accepts queued vertical movement and its sub-notch remainder while rejecting a zero delta");
        int pending=controller.PendingWheelCount;
        check(!controller.QueueWheel(int.MinValue)&&!controller.QueueWheel(-120*129)&&controller.PendingWheelCount==pending,"oversized wheel events including int.MinValue are rejected atomically without overflow or changing the bounded manual queue");
        check(controller.ManualWheelMode&&controller.VisibleSelection==viewport&&controller.PendingWheelCount>0,"a controller without a narrower frozen selection retains its original viewport and queued manual work");
        PumpUntil(()=>controller.PendingWheelCount==0&&!controller.SamplingActive,12000);
        using var result=Finish(controller);
        check(deltas.SequenceEqual(new[]{-120,-120,-60,-120,-60,-1})&&serial&&physical&&auto==0,"large manual deltas split into FIFO steps no larger than 120 with a stable production capture between dispatches and no automatic interleaving");
        check(controller.Error==null&&controller.ScrollOffset==maximum&&offset==maximum&&Equal(result,document),"finishing after the FIFO settles preserves the complete image including its final single physical pixel");
        check(controller.PendingWheelCount==0&&controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&!controller.QueueWheel(-120),"manual completion closes the queue and releases images and timers without accepting a late wheel");
    }

    private static void RunCancel(Action<bool,string> check)
    {
        const int width=360,height=650;var viewport=new Rectangle(80,100,width,height);
        using var document=Document(width,1500);using var initial=Imaging.Crop(document,new Rectangle(0,0,width,height));int offset=0,dispatches=0,captures=0;
        using var controller=new DragCaptureController(viewport,initial,_=> {captures++;return Imaging.Crop(document,new Rectangle(0,offset,width,height));},()=>false,null,delta=> {dispatches++;offset=Math.Clamp(offset-delta,0,document.Height-height);return true;});
        check(controller.QueueWheel(-720),"cancellation fixture has accepted manual wheel work before disposal");controller.Dispose();
        PumpUntil(()=>controller.ImagesReleased&&!controller.SamplingActive,4000);int stoppedDispatches=dispatches,stoppedCaptures=captures;PumpFor(350);
        bool cancelled=false;try{using var output=Finish(controller);}catch(OperationCanceledException){cancelled=true;}
        check(cancelled&&controller.PendingWheelCount==0&&!controller.QueueWheel(-120)&&dispatches==stoppedDispatches&&captures==stoppedCaptures&&!controller.RangeProbeActive,"cancelling clears unissued manual steps and prevents late dispatch, capture or screenshot publication");
    }

    private static void RunManualPriority(Action<bool,string> check)
    {
        const int width=360,height=650;var viewport=new Rectangle(80,100,width,height);
        using var document=Document(width,1500);using var initial=Imaging.Crop(document,new Rectangle(0,0,width,height));
        int offset=0,auto=0,manual=0;var order=new List<string>();
        using var controller=new DragCaptureController(viewport,initial,_=>Imaging.Crop(document,new Rectangle(0,offset,width,height)),()=> {
            auto++;order.Add("auto");offset+=80;return true;
        },()=>false,delta=> {manual++;order.Add("manual");offset-=delta;return true;});
        controller.UpdatePointer(new Point(viewport.Right,viewport.Bottom-20));check(controller.QueueWheel(-120),"a manual wheel can take priority while the pointer is in the automatic-scroll zone");
        PumpUntil(()=>controller.PendingWheelCount==0&&!controller.SamplingActive||controller.Error!=null,5000);
        PumpFor(1000);
        check(controller.Error==null&&manual==1&&auto==0&&order.SequenceEqual(new[]{"manual"})&&!controller.AutoScrolling,"manual input takes priority and a stationary bottom-zone pointer does not resume automatic scrolling after 800ms");
        controller.UpdatePointer(new Point(viewport.Right,viewport.Bottom));PumpFor(350);
        check(auto==0&&!controller.AutoScrolling&&!controller.SamplingActive,"pointer updates at the bottom cannot restart edge scrolling after manual input has taken over");
        using var result=Finish(controller);
        using var expected=Imaging.Crop(document,new Rectangle(0,0,width,height+offset));
        check(Equal(result,expected)&&controller.ImagesReleased,"manual takeover finishes with exactly the accepted content and no unrequested automatic rows");
    }

    private static void RunFreezeSelection(Action<bool,string> check)
    {
        const int width=360,height=650,selectedHeight=430;var viewport=new Rectangle(-620,120,width,height);
        using var document=Document(width,1300);using var initial=Imaging.Crop(document,new Rectangle(0,0,width,height));
        int offset=0,auto=0,captures=0;bool bounded=true;
        var selected=new Rectangle(viewport.Left,viewport.Top,width,selectedHeight);
        using var controller=new DragCaptureController(viewport,initial,area=> {
            captures++;bounded&=area==selected;return Imaging.Crop(document,new Rectangle(0,offset,area.Width,area.Height));
        },()=> {auto++;return false;},()=>false,delta=> {offset-=delta;return true;});
        check(!controller.FreezeSelection(new Rectangle(viewport.Left,viewport.Top,width,239))&&!controller.ManualWheelMode&&controller.Viewport==viewport,"a short manual frame is rejected instead of expanded to the source window");
        check(controller.FreezeSelection(selected)&&controller.Viewport==selected&&controller.VisibleSelection==selected,"manual takeover freezes the exact shorter user frame before the first wheel");
        controller.UpdatePointer(new Point(viewport.Right+100,viewport.Bottom));
        check(controller.VisibleSelection==selected&&!controller.FreezeSelection(viewport)&&controller.FreezeSelection(selected),"later pointer drift and attempts to widen the frozen rectangle cannot change any selected edge");
        check(controller.QueueWheel(-120),"a frozen shorter frame accepts its manual scroll step");
        PumpUntil(()=>controller.PendingWheelCount==0&&!controller.SamplingActive,6000);
        using var result=Finish(controller);using var expected=Imaging.Crop(document,new Rectangle(0,0,width,selectedHeight+120));
        check(controller.Error==null&&captures>=2&&bounded&&auto==0&&controller.ScrollOffset==120&&Equal(result,expected),"every manual capture stays inside the frozen rectangle and excludes the original viewport's lower tail from the output");
        check(controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive,"shorter-frame completion releases its cropped images and timers");
    }

    private static void RunInFlightFreeze(Action<bool,string> check)
    {
        const int width=360,height=650,selectedHeight=430;var viewport=new Rectangle(-620,120,width,height);
        var selected=new Rectangle(viewport.Left,viewport.Top,width,selectedHeight);
        using var document=Document(width,1500);using var initial=Imaging.Crop(document,new Rectangle(0,0,width,height));
        int offset=0,autoCalls=0,manualCalls=0;bool serialized=true;var captures=new List<Rectangle>();var order=new List<string>();
        DragCaptureController? observed=null;
        using var controller=new DragCaptureController(viewport,initial,area=> {
            captures.Add(area);return Imaging.Crop(document,new Rectangle(0,offset,area.Width,area.Height));
        },()=> {autoCalls++;order.Add("auto");offset+=120;return true;},()=>false,delta=> {
            manualCalls++;order.Add("manual");
            serialized&=observed!.ScrollOffset==120&&captures.Count>=2&&captures.All(area=>area==viewport);
            using(var croppedLast=observed!.CopyLastFrame())serialized&=croppedLast!=null&&croppedLast.Size==selected.Size;
            offset-=delta;return true;
        });
        observed=controller;
        controller.UpdatePointer(new Point(viewport.Right,viewport.Bottom-20));
        PumpUntil(()=>autoCalls==1||controller.Error!=null,3000);
        check(controller.Error==null&&autoCalls==1&&offset==120&&controller.ScrollOffset==0&&controller.SamplingActive,"freeze fixture observes an automatic dispatch whose original-height capture and match are still in flight");
        check(controller.FreezeSelection(selected)&&controller.QueueWheel(-120)&&controller.VisibleSelection==selected&&!controller.AutoScrolling,"freezing during an in-flight automatic step retains the exact shorter frame and queues manual input without starting another auto step");
        PumpUntil(()=>controller.PendingWheelCount==0&&!controller.SamplingActive,8000);
        using var result=Finish(controller);using var expected=Imaging.Crop(document,new Rectangle(0,0,width,selectedHeight+240));
        int changedAt=captures.FindIndex(area=>area==selected);
        bool ordered=changedAt>=2&&captures.Count>=changedAt+2&&captures.Take(changedAt).All(area=>area==viewport)&&captures.Skip(changedAt).All(area=>area==selected);
        check(controller.Error==null&&controller.CompletionWarning==null&&autoCalls==1&&manualCalls==1&&order.SequenceEqual(new[]{"auto","manual"})&&serialized&&ordered,"the old 650px observation finishes before the serialized crop and every later manual observation uses only the frozen 430px rectangle");
        check(offset==240&&controller.ScrollOffset==240&&result.Size==new Size(width,670)&&Equal(result,expected),"finishing after the serialized height transition preserves exactly the first 670 source rows without a crop or bitmap-disposal race");
        check(controller.PendingWheelCount==0&&controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive,"completed in-flight freeze work releases cropped images, both timers and the manual queue");
    }

    private static void RunRewind(Action<bool,string> check,bool recover)
    {
        const int width=360,height=650;var viewport=new Rectangle(80,100,width,height);
        using var document=Document(width,1200);using var initial=Imaging.Crop(document,new Rectangle(0,0,width,height));int offset=0,auto=0;
        using var controller=new DragCaptureController(viewport,initial,_=>Imaging.Crop(document,new Rectangle(0,offset,width,height)),()=> {auto++;return true;},()=>false,delta=> {offset=Math.Clamp(offset-delta,0,document.Height-height);return true;});
        controller.QueueWheel(-120);PumpUntil(()=>controller.ScrollOffset==120||controller.Error!=null,5000);int acceptedFrames=controller.FrameCount;
        check(controller.Error==null&&controller.QueueWheel(80),"manual upward movement remains available inside the same drag session");
        PumpUntil(()=>controller.Rewound||controller.Error!=null,5000);
        check(controller.Error==null&&controller.Rewound&&offset==40&&controller.ScrollOffset==120&&controller.FrameCount==acceptedFrames,"an 80px manual rewind preserves the accepted composite and checkpoint instead of appending earlier rows");
        controller.UpdatePointer(new Point(viewport.Right,viewport.Bottom-20));PumpFor(850);
        check(controller.Rewound&&auto==0,"a rewound drag does not let the automatic timer pull the source back down after manual priority expires");
        controller.UpdatePointer(new Point(viewport.Left,viewport.Top+100));int expectedOffset=120;
        if(recover) {
            check(controller.QueueWheel(-120),"downward scrolling can overshoot the accepted checkpoint from a partially rewound source");
            PumpUntil(()=>controller.ScrollOffset==160||controller.Error!=null,6000);expectedOffset=160;
            check(controller.Error==null&&!controller.Rewound&&offset==160&&controller.ScrollOffset==160&&controller.FrameCount==acceptedFrames+1,"80px up followed by 120px down appends only the newly revealed 40px without requiring an exact checkpoint landing");
        }
        using var result=Finish(controller);using var expected=Imaging.Crop(document,new Rectangle(0,0,width,height+expectedOffset));
        check(Equal(result,expected)&&controller.ImagesReleased,recover?"partial-rewind recovery finishes with exactly one copy of every accepted source row":"releasing while rewound returns the previously accepted image without refreshing it from earlier page content");
    }

    private static void RunNativeOverlay(Action<bool,string> check,string? folder)
    {
        const int width=800,height=650,bodyHeight=430,maximum=481,column=320,chosenWidth=300,topInset=4;
        var app=W.Application.Current as App??new App();app.ShutdownMode=W.ShutdownMode.OnExplicitShutdown;Ui.Resources(app);
        using var document=Document(width,bodyHeight+maximum);var image=new Controls.Image {Stretch=Media.Stretch.Fill,UseLayoutRounding=true,SnapsToDevicePixels=true};
        var source=new W.Window {Title="Framelet manual drag-wheel acceptance",Content=image,WindowStyle=W.WindowStyle.None,ResizeMode=W.ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,Background=Media.Brushes.White,UseLayoutRounding=true,SnapsToDevicePixels=true,Width=width,Height=height};
        CaptureOverlay? overlay=null;DragCaptureController? controller=null;Bitmap? result=null;DispatcherTimer? timer=null;Exception? error=null;bool finished=false;int offset=0;var delivered=new List<int>();
        try {
            Native.GetCursorPos(out var cursor);var monitor=System.Windows.Forms.Screen.FromPoint(new Point(cursor.X,cursor.Y)).WorkingArea;
            int left=monitor.Left+Math.Min(24,monitor.Width-width),top=monitor.Top+Math.Min(24,monitor.Height-height);
            source.Show();var handle=new Interop.WindowInteropHelper(source).EnsureHandle();Native.SetWindowPos(handle,new IntPtr(-1),left,top,width,height,0x40);source.UpdateLayout();
            var dpi=Media.VisualTreeHelper.GetDpi(source);image.Width=width/dpi.DpiScaleX;image.Height=height/dpi.DpiScaleY;Media.RenderOptions.SetBitmapScalingMode(image,Media.BitmapScalingMode.NearestNeighbor);
            Bitmap Frame() {
                var frame=new Bitmap(width,height);using var graphics=Graphics.FromImage(frame);graphics.CompositingMode=CompositingMode.SourceCopy;
                graphics.Clear(Color.FromArgb(22,22,22));
                graphics.DrawImage(document,new Rectangle(0,topInset,width,bodyHeight),new Rectangle(0,offset,width,bodyHeight),GraphicsUnit.Pixel);
                using var composer=new SolidBrush(Color.FromArgb(117,35,79));graphics.FillRectangle(composer,0,topInset+bodyHeight,width,height-topInset-bodyHeight);
                graphics.CompositingMode=CompositingMode.SourceOver;
                using var font=new Font("Segoe UI",24,GraphicsUnit.Pixel);graphics.DrawString("FIXED COMPOSER - OUTSIDE SELECTION",font,Brushes.White,24,topInset+bodyHeight+35);
                return frame;
            }
            using(var first=Frame())image.Source=Imaging.Source(first);
            Interop.HwndSource.FromHwnd(handle)!.AddHook((IntPtr _,int message,IntPtr w,IntPtr l,ref bool handled)=> {
                if(message==MouseWheelObserver.MouseWheelMessage) {
                    int delta=unchecked((short)(w.ToInt64()>>16));delivered.Add(delta);offset=Math.Clamp(offset-delta,0,maximum);
                    using var frame=Frame();image.Source=Imaging.Source(frame);source.UpdateLayout();handled=true;
                }
                return IntPtr.Zero;
            });
            source.Activate();source.UpdateLayout();PumpFor(300);Native.SetWindowPos(handle,new IntPtr(-1),0,0,0,0,0x13);
            var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;using var snapshot=Imaging.Capture(desktop);
            overlay=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Scroll,_=>{});timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
            timer.Tick+=(_,_)=> {
                timer.Stop();
                try {
                    // Modal WPF disables same-thread windows; re-enable only
                    // this fixture so native WindowFromPoint can find its source.
                    EnableWindow(handle,true);
                    check(IsWindowEnabled(handle),"the self-owned manual-wheel source is enabled for native wheel hit testing during the modal overlay");
                    Native.SetWindowPos(handle,new Interop.WindowInteropHelper(overlay).Handle,0,0,0,0,0x13);
                    W.Point Local(int pixelX,int pixelY)=>overlay.PointFromScreen(new W.Point(pixelX+.25,pixelY+.25));int x=left+column,y=top+topInset;
                    overlay.BeginPointer(Local(x,y));overlay.MovePointer(Local(x+chosenWidth,y+bodyHeight));
                    var selected=overlay.Selection;
                    var position=new Point(x+chosenWidth/2,y+bodyHeight/2);var sample=new MouseWheelSample(position,-300,71);
                    check(overlay.DragCapture==null&&overlay.IsMouseCaptured,"the loaded manual-wheel fixture starts from an unlocked but sufficiently large selection drag");
                    check(selected==new Rectangle(x,y,chosenWidth,bodyHeight),"the native manual frame initially covers only the selected body and ends above the fixed composer");
                    bool rejected=!overlay.HandleDragWheel(sample with {Delta=0});
                    foreach(var modifier in new[]{Input.ModifierKeys.Control,Input.ModifierKeys.Shift,Input.ModifierKeys.Alt,Input.ModifierKeys.Windows})rejected&=!overlay.HandleDragWheel(sample,modifier);
                    rejected&=!overlay.HandleDragWheel(sample with {ControlPressed=true})&&!overlay.HandleDragWheel(sample with {ShiftPressed=true});
                    check(rejected&&overlay.DragCapture==null&&overlay.IsMouseCaptured&&delivered.Count==0,"zero and Ctrl/Shift/Alt/Win wheel gestures do not start manual drag capture or dispatch source input");
                    check(overlay.HandleDragWheel(sample),"manual wheel input can lock the current width before a separate vertical locking leg");controller=overlay.DragCapture;
                    check(controller!=null&&controller.ManualWheelMode&&controller.Viewport==selected&&overlay.Selection==selected&&overlay.IsMouseCaptured&&!overlay.ScrollRequested&&!W.Application.Current.Windows.OfType<ScrollWindow>().Any(window=>window.IsVisible),"the first native manual wheel preserves every selected edge, capture and the same session without expanding toward the composer or old ScrollWindow");
                    check(overlay.HandleDragWheel(sample with {Delta=-180})&&overlay.HandleDragWheel(sample with {Delta=-1}),"the loaded overlay accepts subsequent manual deltas into the same production session");
                    overlay.MovePointer(Local(left+width-1,top+height-1));
                    check(overlay.Selection==selected&&controller!.Viewport==selected,"moving beyond the body after manual takeover cannot add the fixed footer or composer to the frame");
                    PumpUntil(()=>offset==maximum&&controller!.ScrollOffset==maximum&&controller.PendingWheelCount==0&&!controller.SamplingActive,12000);
                    overlay.EndPointer(Local(left+width-1,top+height-1));PumpUntil(()=>!overlay.IsVisible,10000);result=overlay.TakeDragResult();
                    using var expected=Imaging.Crop(document,new Rectangle(column,0,chosenWidth,document.Height));
                    if(folder!=null&&result!=null){Directory.CreateDirectory(folder);Imaging.Save(result,Path.Combine(folder,"drag-wheel-native-actual.png"));Imaging.Save(expected,Path.Combine(folder,"drag-wheel-native-golden.png"));}
                    check(delivered.SequenceEqual(new[]{-120,-120,-60,-120,-60,-1})&&offset==maximum,"production manual wheel dispatch reaches the native dark source in serial bounded steps, including the final 1px remainder");
                    check(overlay.DialogResult==true&&result!=null&&result.Size==new Size(chosenWidth,911)&&Equal(result,expected),"release after native scrolling settles returns the exact 300x911 body image with each legitimate graphic and final pixel, excluding the fixed composer");
                    check(controller!.ImagesReleased&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!controller.RangeProbeActive&&!overlay.IsMouseCaptured,"native manual-wheel completion releases the queue, images, timers and pointer capture");
                }catch(Exception ex){error=ex;}
                finally {if(overlay.IsVisible)overlay.Close();finished=true;}
            };
            timer.Start();overlay.ShowDialog();if(!finished)PumpUntil(()=>finished,12000);
            if(error!=null)throw new InvalidOperationException("Native manual drag-wheel acceptance failed",error);
        }finally{timer?.Stop();overlay?.Close();controller?.Dispose();source.Close();result?.Dispose();}
    }

    private static Bitmap Document(int width,int height)
    {
        var bitmap=SelfTests.Fixture(width,height);using var graphics=Graphics.FromImage(bitmap);using var font=new Font("Segoe UI",16,GraphicsUnit.Pixel);using var dark=new SolidBrush(Color.FromArgb(43,43,43));using var ink=new SolidBrush(Color.FromArgb(241,241,241));
        for(int y=20,n=0;y<height-24;y+=97,n++) {
            graphics.FillRectangle(dark,20,y,width-40,65);graphics.DrawString($"Row {n:0000}: keep source {n*7919+37}",font,ink,34,y+10);
            if(width>600)graphics.DrawString($"Chat {n:0000} / {n*6151+17}",font,ink,330,y+10);
        }
        int x=width>600?360:100;using var blue=new SolidBrush(Color.FromArgb(48,105,188));graphics.FillRectangle(blue,x,210,130,70);
        for(int row=0;row<35;row++){using var fade=new SolidBrush(Color.FromArgb(row*6,43,43,43));graphics.FillRectangle(fade,x,245+row,130,1);}
        using var arrow=new Pen(Color.White,2);graphics.DrawEllipse(arrow,x+43,220,34,34);graphics.DrawLine(arrow,x+60,229,x+60,247);graphics.DrawLine(arrow,x+53,241,x+60,248);graphics.DrawLine(arrow,x+60,248,x+67,241);
        for(int row=Math.Max(0,height-4);row<height;row++){using var color=new SolidBrush(Color.FromArgb(255,(row*43+17)%256,(row*67+31)%256,(row*97+53)%256));graphics.FillRectangle(color,0,row,width,1);}
        return bitmap;
    }
    private static bool Equal(Bitmap actual,Bitmap expected)=>actual.Size==expected.Size&&Imaging.Png(actual).SequenceEqual(Imaging.Png(expected));
    private static Bitmap Finish(DragCaptureController controller){var task=controller.Finish();PumpUntil(()=>task.IsCompleted,12000);return task.GetAwaiter().GetResult();}
    private static void PumpUntil(Func<bool> condition,int milliseconds)
    {
        var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<milliseconds)PumpFor(20);
        if(!condition())throw new TimeoutException("Manual drag-wheel acceptance did not settle.");
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
