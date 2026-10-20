using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

// The production timer must request every scroll. The test never advances the
// document from its pointer loop or sets a desired final offset directly.
internal static class DragCaptureAcceptance
{
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window,bool enabled);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    internal static List<string> Run(string? folder=null)
    {
        var lines=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        RunWidthSelection(Check);
        RunMinimumViewport(Check);
        RunSelectionGrowth(Check);
        foreach(int tail in new[]{1,2,3,17})RunAutomaticTail(Check,tail,folder);
        foreach(int tail in new[]{17,35})RunEarlyBottomRelease(Check,tail);
        foreach(bool delivered in new[]{true,false})RunIgnoredScrollRecovery(Check,delivered);
        foreach(bool failedRange in new[]{false,true})RunUnknownBottom(Check,failedRange);
        RunReliableDelayedBottom(Check);
        RunRangeGrowth(Check);
        RunFreshFinish(Check);
        RunCancellation(Check);
        RunWaitingCancellation(Check);
        RunLoadedOverlay(Check,folder);
        return lines;
    }

    private static void RunWidthSelection(Action<bool,string> check)
    {
        var selection=new DragWidthSelection(new D.Point(-600,100));
        selection.Move(new D.Point(-280,180));
        check(!selection.Locked&&selection.Selection==new D.Rectangle(-600,100,320,81),"the first long-capture leg establishes its physical width without prematurely locking a diagonal drag");
        selection.Move(new D.Point(-279,240));
        check(selection.Locked&&selection.Selection==new D.Rectangle(-600,100,320,141),"a genuine downward leg locks the chosen width on a negative-coordinate screen");
        selection.Move(new D.Point(-100,360));var first=selection.Selection;
        selection.Move(new D.Point(-700,400));
        check(first.Width==320&&first.Height==261&&selection.Selection==new D.Rectangle(-600,100,320,301),"after width lock, horizontal pointer drift cannot widen or move the capture while downward dragging keeps increasing its height");
        var reversed=new DragWidthSelection(new D.Point(-200,-100));
        reversed.Move(new D.Point(-520,-20));reversed.Move(new D.Point(-520,40));reversed.Move(new D.Point(100,70));
        check(reversed.Locked&&reversed.Selection==new D.Rectangle(-520,-100,320,171),"a width chosen toward the left remains fixed while the long capture grows downward");
        var narrow=new DragWidthSelection(new D.Point(10,10));narrow.Move(new D.Point(20,100));narrow.Move(new D.Point(25,200));
        check(!narrow.Locked,"a narrow accidental drag cannot lock a long-capture width");
    }

    private static void RunSelectionGrowth(Action<bool,string> check)
    {
        var viewport=new D.Rectangle(-620,120,360,650);using var document=Fixture(viewport.Width,800);
        var initial=Imaging.Crop(document,new D.Rectangle(0,0,viewport.Width,viewport.Height));int scrolls=0,captures=0;bool exactViewport=true;
        using var controller=new DragCaptureController(viewport,initial,area=> {
            captures++;exactViewport&=area==viewport;return Imaging.Crop(document,new D.Rectangle(0,0,area.Width,area.Height));
        },()=> {scrolls++;return false;});
        initial.Dispose();
        controller.UpdatePointer(new D.Point(viewport.Left+80,viewport.Top+120));int first=controller.OutputHeight;
        controller.UpdatePointer(new D.Point(viewport.Right+600,viewport.Top+300));
        check(first==121&&controller.OutputHeight==301&&controller.VisibleSelection==new D.Rectangle(viewport.Left,viewport.Top,viewport.Width,301),"dragging downward grows the physical selection height while the controller ignores horizontal pointer drift");
        PumpFor(350);
        check(scrolls==0&&captures==0&&!controller.AutoScrolling&&!controller.SamplingActive,"a pointer away from the viewport bottom does not request scrolling or periodic screen captures");
        using var result=Finish(controller);
        using var expected=Imaging.Crop(document,new D.Rectangle(0,0,viewport.Width,301));
        check(captures>=2&&exactViewport&&Imaging.Png(result).SequenceEqual(Imaging.Png(expected)),"releasing before the bottom takes fresh stable samples and returns exactly the selected height with the chosen width");
        check(controller.ImagesReleased&&!controller.SamplingActive,"ordinary drag completion releases the controller images and timer");
    }

    private static void RunMinimumViewport(Action<bool,string> check)
    {
        using var initial=Fixture(360,239);bool rejected=false;int captures=0,scrolls=0;
        try {
            using var controller=new DragCaptureController(new D.Rectangle(80,100,360,239),initial,_=> {captures++;return new D.Bitmap(initial);},()=> {scrolls++;return false;});
        }catch(ArgumentException){rejected=true;}
        check(rejected&&captures==0&&scrolls==0,"a viewport shorter than 240 physical pixels is rejected before any capture or automatic scroll request");
    }

    private static void RunEarlyBottomRelease(Action<bool,string> check,int tail)
    {
        const int width=360,height=650,step=224;int maximum=step*3+tail;
        var viewport=new D.Rectangle(-620,120,width,height);using var document=Fixture(width,height+maximum);
        using var initial=Imaging.Crop(document,new D.Rectangle(0,0,width,height));
        int offset=0,scrolls=0;var displacements=new List<int>();
        using var controller=new DragCaptureController(viewport,initial,_=>Imaging.Crop(document,new D.Rectangle(0,offset,width,height)),()=> {
            scrolls++;int amount=Math.Min(step,maximum-offset);
            if(amount<=0)return false;
            offset+=amount;displacements.Add(amount);return true;
        },()=>offset>=maximum);
        controller.UpdatePointer(new D.Point(viewport.Right+100,viewport.Bottom-20));
        check(controller.AutoScrolling&&controller.VisibleSelection==viewport,$"the bottom-zone pointer keeps the full visible height before bottom detection for a {tail}px terminal block");
        PumpUntil(()=>controller.ScrollOffset==maximum||controller.Error!=null,15000);
        check(controller.Error==null&&controller.ScrollOffset==maximum&&!controller.AtBottom&&scrolls==4&&displacements.SequenceEqual(new[]{step,step,step,tail}),$"the final {tail}px block is appended by automatic scrolling before a subsequent unchanged frame can establish page bottom");
        using var result=Finish(controller);
        check(result.Width==width&&result.Height==document.Height&&Imaging.Png(result).SequenceEqual(Imaging.Png(document)),$"immediate release 20px above the viewport bottom preserves the entire growing image and final {tail}px without waiting for bottom detection");
        check(controller.ImagesReleased&&!controller.SamplingActive&&scrolls==4,$"early release after the {tail}px terminal block stops further auto-scroll requests and releases all owned images");
    }

    private static void RunAutomaticTail(Action<bool,string> check,int tail,string? folder)
    {
        const int width=360,height=650,step=224;
        int maximum=step*3+tail;var viewport=new D.Rectangle(-620,120,width,height);
        using var document=Fixture(width,height+maximum);var initial=Imaging.Crop(document,new D.Rectangle(0,0,width,height));
        int offset=0,scrolls=0,captures=0;bool exactViewport=true;var displacements=new List<int>();
        using var controller=new DragCaptureController(viewport,initial,area=> {
            captures++;exactViewport&=area==viewport;return Imaging.Crop(document,new D.Rectangle(0,offset,width,height));
        },()=> {
            scrolls++;int amount=Math.Min(step,maximum-offset);
            if(amount<=0)return false;
            offset+=amount;displacements.Add(amount);return true;
        },()=>offset>=maximum);
        initial.Dispose();
        controller.UpdatePointer(new D.Point(viewport.Left+width/2,viewport.Top+100));PumpFor(170);
        check(scrolls==0&&captures==0,$"the {tail}px-tail fixture remains idle before the pointer enters the bottom auto-scroll zone");
        controller.UpdatePointer(new D.Point(viewport.Right+400,viewport.Bottom-2));
        check(controller.AutoScrolling&&controller.VisibleSelection.Width==width,$"the bottom pointer zone starts automatic scrolling without a wheel event and keeps the locked width for a {tail}px final tail");
        PumpUntil(()=>controller.AtBottom||controller.Error!=null,15000);
        check(controller.Error==null&&controller.AtBottom&&offset==maximum&&controller.ScrollOffset==maximum&&displacements.SequenceEqual(new[]{step,step,step,tail}),$"production timer scrolling reaches the final {tail}px after three full scroll steps without skipping a short terminal displacement (source={offset}, stitched={controller.ScrollOffset}, error={controller.Error})");
        check(controller.OutputHeight==height+maximum&&controller.VisibleSelection==viewport&&controller.FrameCount==5&&exactViewport,$"at the page bottom the frame fills the remaining visible area and retains the complete {tail}px tail with a fixed-width observation viewport");
        int stoppedScrolls=scrolls,stoppedCaptures=captures;PumpFor(300);
        check(!controller.AutoScrolling&&!controller.SamplingActive&&scrolls==stoppedScrolls&&captures==stoppedCaptures,$"a page that cannot scroll further stops automatic sampling after its {tail}px tail is captured");
        using var result=Finish(controller);
        check(result.Width==width&&result.Height==document.Height&&Imaging.Png(result).SequenceEqual(Imaging.Png(document)),$"automatic downward dragging preserves every source pixel including the final {tail}px at the bottom, without changing the chosen width");
        using var actualTail=Imaging.Crop(result,new D.Rectangle(0,result.Height-tail,width,tail));
        using var expectedTail=Imaging.Crop(document,new D.Rectangle(0,document.Height-tail,width,tail));
        check(Imaging.Png(actualTail).SequenceEqual(Imaging.Png(expectedTail))&&controller.ImagesReleased&&!controller.SamplingActive,$"the last {tail}px block survives mouse-release completion and all owned images and timer activity are released");
        if(folder!=null){Directory.CreateDirectory(folder);Imaging.Save(result,Path.Combine(folder,$"drag-capture-tail-{tail}.png"));}
    }

    private static void RunIgnoredScrollRecovery(Action<bool,string> check,bool reportedDelivery)
    {
        const int width=360,height=650,step=224,tail=17;int maximum=step+tail;
        var viewport=new D.Rectangle(80,100,width,height);using var document=Fixture(width,height+maximum);
        using var initial=Imaging.Crop(document,new D.Rectangle(0,0,width,height));int offset=0,scrolls=0,captures=0;
        using var controller=new DragCaptureController(viewport,initial,_=> {captures++;return Imaging.Crop(document,new D.Rectangle(0,offset,width,height));},()=> {
            scrolls++;if(scrolls<=3)return reportedDelivery;
            int amount=Math.Min(step,maximum-offset);if(amount<=0)return false;offset+=amount;return true;
        },()=>false);
        var bottom=new D.Point(viewport.Right,viewport.Bottom-20);controller.UpdatePointer(bottom);
        PumpUntil(()=>controller.WaitingForScroll||controller.Error!=null,16000);
        check(controller.Error==null&&controller.WaitingForScroll&&!controller.AtBottom&&offset==0&&controller.FrameCount==1,$"three initially ignored scroll attempts enter recoverable waiting instead of reporting bottom when delivery reports {reportedDelivery}");
        if(!reportedDelivery) {
            controller.UpdatePointer(new D.Point(viewport.Left,viewport.Top+100));PumpUntil(()=>!controller.SamplingActive,6000);
            int pausedScrolls=scrolls,pausedCaptures=captures;PumpFor(350);
            check(!controller.AutoScrolling&&scrolls==pausedScrolls&&captures==pausedCaptures,"leaving the bottom zone stops waiting retries and captures after initially rejected deliveries");
            controller.UpdatePointer(bottom);
        }
        PumpUntil(()=>controller.ScrollOffset==maximum||controller.Error!=null,20000);
        check(controller.Error==null&&scrolls>=5&&controller.ScrollOffset==maximum&&!controller.AtBottom,reportedDelivery?"a stationary bottom-zone pointer automatically retries ignored scrolls and appends later content without requiring another mouse move":"reentering the bottom zone resumes initially rejected scrolls and appends later content");
        using var result=Finish(controller);
        check(Imaging.Png(result).SequenceEqual(Imaging.Png(document))&&controller.ImagesReleased&&!controller.SamplingActive,$"recovered scrolling retains the complete original pixels and terminal tail when delivery reports {reportedDelivery}");
    }

    private static void RunUnknownBottom(Action<bool,string> check,bool failedRange)
    {
        const int width=360,height=650;var viewport=new D.Rectangle(80,100,width,height);
        using var document=Fixture(width,height);using var initial=new D.Bitmap(document);int scrolls=0,captures=0;
        Func<bool?>? range=failedRange?()=>throw new InvalidOperationException("Synthetic scroll-range lookup failure"):null;
        using var controller=new DragCaptureController(viewport,initial,_=> {captures++;return new D.Bitmap(document);},()=> {scrolls++;return false;},range);
        controller.UpdatePointer(new D.Point(viewport.Left+20,viewport.Bottom-20));
        PumpUntil(()=>controller.WaitingForScroll||controller.Error!=null,16000);
        check(controller.Error==null&&controller.WaitingForScroll&&!controller.AtBottom&&controller.FrameCount==1,$"an unchanged real page end with {(failedRange?"failed":"absent")} range evidence remains unknown instead of falsely reporting bottom");
        int priorScrolls=scrolls;PumpUntil(()=>scrolls>priorScrolls||controller.Error!=null,7000);
        check(controller.Error==null&&!controller.AtBottom&&controller.FrameCount==1&&controller.ScrollOffset==0,"unknown-bottom waiting continues recoverable retries without duplicating unchanged content");
        int stoppedScrolls=scrolls;var watch=Stopwatch.StartNew();using var result=Finish(controller);
        check(watch.Elapsed<TimeSpan.FromSeconds(5)&&scrolls==stoppedScrolls&&Imaging.Png(result).SequenceEqual(Imaging.Png(document)),"releasing during unknown-bottom waiting finishes in bounded time without sending more scrolls or inventing additional rows");
        int stoppedCaptures=captures;PumpFor(250);
        check(controller.ImagesReleased&&!controller.SamplingActive&&scrolls==stoppedScrolls&&captures==stoppedCaptures,"finishing unknown-bottom waiting releases images and leaves no late retry or capture");
    }

    private static void RunReliableDelayedBottom(Action<bool,string> check)
    {
        const int width=360,height=650,step=224,tail=17;int maximum=step+tail;
        var viewport=new D.Rectangle(80,100,width,height);using var document=Fixture(width,height+maximum);
        using var initial=Imaging.Crop(document,new D.Rectangle(0,0,width,height));int offset=0,scrolls=0,staleReads=0;bool reportedEnd=false,prematureBottom=false;
        using var controller=new DragCaptureController(viewport,initial,_=> {
            if(reportedEnd&&++staleReads>=3)offset=maximum;
            return Imaging.Crop(document,new D.Rectangle(0,offset,width,height));
        },()=> {
            scrolls++;if(scrolls==1){offset=step;return true;}
            reportedEnd=true;return false;
        },()=>reportedEnd);
        controller.Changed+=()=> {if(controller.AtBottom&&controller.ScrollOffset<maximum)prematureBottom=true;};
        controller.UpdatePointer(new D.Point(viewport.Right,viewport.Bottom-20));
        PumpUntil(()=>controller.AtBottom||controller.Error!=null,18000);
        check(controller.Error==null&&controller.AtBottom&&!prematureBottom&&staleReads>=4&&controller.ScrollOffset==maximum&&controller.FrameCount==3,"reliable range-end evidence does not discard a 17px terminal paint delayed behind two unchanged stable screenshots");
        int stoppedScrolls=scrolls;PumpFor(350);
        check(!controller.AutoScrolling&&!controller.SamplingActive&&scrolls==stoppedScrolls,"reliable page-end evidence stops retries after the final changed frame and two subsequent stable unchanged checks");
        using var result=Finish(controller);
        check(Imaging.Png(result).SequenceEqual(Imaging.Png(document))&&controller.ImagesReleased,"confirmed bottom retains every delayed terminal pixel in the finished image");
    }

    private static void RunRangeGrowth(Action<bool,string> check)
    {
        const int width=360,height=650,step=224,tail=17;var viewport=new D.Rectangle(80,100,width,height);
        using var document=Fixture(width,height+step+tail);using var initial=Imaging.Crop(document,new D.Rectangle(0,0,width,height));int offset=0,available=step,scrolls=0,captures=0,rangeReads=0;
        using var controller=new DragCaptureController(viewport,initial,_=> {captures++;return Imaging.Crop(document,new D.Rectangle(0,offset,width,height));},()=> {
            scrolls++;
            int amount=Math.Min(step,available-offset);if(amount<=0)return false;offset+=amount;return true;
        },()=> {rangeReads++;return offset>=available;});
        var bottom=new D.Point(viewport.Right,viewport.Bottom-20);controller.UpdatePointer(bottom);
        PumpUntil(()=>controller.AtBottom||controller.Error!=null,16000);
        check(controller.Error==null&&controller.AtBottom&&controller.ScrollOffset==step&&controller.RangeProbeActive&&!controller.SamplingActive,"an initial reliable scroll range confirms the current end and keeps only a lightweight range probe active");
        int stoppedScrolls=scrolls,stoppedCaptures=captures;PumpFor(350);
        check(scrolls==stoppedScrolls&&captures==stoppedCaptures&&!controller.SamplingActive&&controller.RangeProbeActive,"confirmed-bottom range probing does not continue scrolling or screen captures");
        int previousReads=rangeReads;available=step+tail;
        // No UpdatePointer: the independent range probe must discover new content.
        PumpUntil(()=>controller.ScrollOffset==step+tail||controller.Error!=null,16000);
        check(controller.Error==null&&rangeReads>previousReads&&scrolls>stoppedScrolls&&controller.ScrollOffset==step+tail,"a stationary bottom-zone pointer resumes capture automatically when the source's reliable range grows");
        PumpUntil(()=>controller.AtBottom||controller.Error!=null,16000);
        using var result=Finish(controller);
        check(controller.Error==null&&controller.ScrollOffset==step+tail&&Imaging.Png(result).SequenceEqual(Imaging.Png(document))&&!controller.RangeProbeActive&&controller.ImagesReleased,"rearmed capture appends newly available content without duplicating the old image and release stops the range probe too");
    }

    private static void RunFreshFinish(Action<bool,string> check)
    {
        const int width=360,height=650,step=224,tail=17;var viewport=new D.Rectangle(80,100,width,height);
        using var document=Fixture(width,height+step+tail);using var initial=Imaging.Crop(document,new D.Rectangle(0,0,width,height));
        int offset=0,scrolls=0,lateReads=0;bool tailRequested=false,allowFinalPaint=false;
        using var controller=new DragCaptureController(viewport,initial,_=> {
            // The source has accepted the pending scroll but does not paint its
            // tail until release starts. The final stable probe must retry too.
            if(tailRequested&&allowFinalPaint&&++lateReads>=3)offset=step+tail;
            return Imaging.Crop(document,new D.Rectangle(0,offset,width,height));
        },()=> {
            scrolls++;
            if(scrolls==1){offset=step;return true;}
            tailRequested=true;return false;
        });
        controller.UpdatePointer(new D.Point(viewport.Left+10,viewport.Bottom-1));
        PumpUntil(()=>controller.WaitingForScroll||controller.Error!=null,16000);
        check(controller.Error==null&&controller.WaitingForScroll&&!controller.AtBottom&&tailRequested&&offset==step&&controller.ScrollOffset==step,"a pending final render enters recoverable waiting rather than declaring page bottom from unchanged frames or a failed delivery");
        int stoppedScrolls=scrolls;allowFinalPaint=true;
        using var result=Finish(controller);
        check(lateReads>=4&&offset==step+tail&&scrolls==stoppedScrolls&&result.Height==document.Height&&Imaging.Png(result).SequenceEqual(Imaging.Png(document)),"mouse release retries fresh stable frames for a delayed terminal paint without sending another scroll request");
        check(controller.ImagesReleased&&!controller.SamplingActive,"fresh-frame completion releases images and leaves no active sampling timer");
    }

    private static void RunCancellation(Action<bool,string> check)
    {
        const int width=360,height=650;var viewport=new D.Rectangle(80,100,width,height);
        using var document=Fixture(width,1200);using var initial=Imaging.Crop(document,new D.Rectangle(0,0,width,height));int offset=0,scrolls=0,captures=0;
        using var controller=new DragCaptureController(viewport,initial,_=> {captures++;return Imaging.Crop(document,new D.Rectangle(0,offset,width,height));},()=> {scrolls++;offset=Math.Min(offset+100,document.Height-height);return true;});
        controller.UpdatePointer(new D.Point(viewport.Right,viewport.Bottom));PumpUntil(()=>scrolls>0,4000);
        controller.Dispose();PumpUntil(()=>controller.ImagesReleased&&!controller.SamplingActive,4000);
        int stoppedScrolls=scrolls,stoppedCaptures=captures;controller.UpdatePointer(new D.Point(viewport.Left,viewport.Bottom));PumpFor(350);
        check(controller.ImagesReleased&&!controller.AutoScrolling&&!controller.SamplingActive&&scrolls==stoppedScrolls&&captures==stoppedCaptures,"cancelling an active drag stops further scroll requests and captures, ignores late pointer updates and releases owned images");
        bool cancelled=false;try{using var output=Finish(controller);}catch(OperationCanceledException){cancelled=true;}
        check(cancelled,"a cancelled drag cannot later publish a screenshot on button release");
    }

    private static void RunWaitingCancellation(Action<bool,string> check)
    {
        const int width=360,height=650;var viewport=new D.Rectangle(80,100,width,height);
        using var document=Fixture(width,height);using var initial=new D.Bitmap(document);int scrolls=0,captures=0;
        using var controller=new DragCaptureController(viewport,initial,_=> {captures++;return new D.Bitmap(document);},()=> {scrolls++;return false;},()=>false);
        controller.UpdatePointer(new D.Point(viewport.Right,viewport.Bottom-20));PumpUntil(()=>controller.WaitingForScroll||controller.Error!=null,16000);
        check(controller.Error==null&&controller.WaitingForScroll&&!controller.AtBottom,"cancellation fixture reaches retryable waiting while reliable range evidence still says content remains");
        Task<D.Bitmap> finishing=controller.Finish();controller.Dispose();PumpUntil(()=>finishing.IsCompleted&&controller.ImagesReleased&&!controller.SamplingActive,6000);
        bool cancelled=false;try{using var result=finishing.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
        int stoppedScrolls=scrolls,stoppedCaptures=captures;controller.UpdatePointer(new D.Point(viewport.Left,viewport.Bottom));PumpFor(350);
        check(cancelled&&controller.ImagesReleased&&!controller.SamplingActive&&scrolls==stoppedScrolls&&captures==stoppedCaptures,"cancelling while release is settling from waiting stops retries, releases frames and never returns a late image");
    }

    internal static void RunLoadedOverlay(Action<bool,string> check,string? folder)
    {
        var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Ui.Resources(app);
        var viewer=new ScrollViewer {CanContentScroll=false,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=Brushes.White,UseLayoutRounding=true,SnapsToDevicePixels=true};
        var source=new Window {Title="Framelet downward-drag acceptance",Content=viewer,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,Background=Brushes.White,UseLayoutRounding=true,SnapsToDevicePixels=true,Width=800,Height=650};
        CaptureOverlay? overlay=null;D.Bitmap? document=null,result=null,initialViewport=null,finalViewport=null;DispatcherTimer? timer=null;Exception? error=null;
        int nativeWheels=0;bool wheelTargetsBody=true,callbackCompleted=false;DragCaptureController? controller=null;
        try {
            Native.GetCursorPos(out var cursor);var monitor=System.Windows.Forms.Screen.FromPoint(new D.Point(cursor.X,cursor.Y));var working=monitor.WorkingArea;
            int physicalWidth=Math.Min(850,working.Width),physicalHeight=Math.Min(650,working.Height);
            int left=working.Left+Math.Min(24,working.Width-physicalWidth),top=working.Top+Math.Min(24,working.Height-physicalHeight);
            source.Show();var handle=new WindowInteropHelper(source).EnsureHandle();
            Native.SetWindowPos(handle,new IntPtr(-1),left,top,physicalWidth,physicalHeight,0x40);source.UpdateLayout();
            var dpi=VisualTreeHelper.GetDpi(source);const int step=80,tail=17;
            document=Fixture(physicalWidth,physicalHeight+step*3+tail);
            var image=new Image {Source=Imaging.Source(document),Width=document.Width/dpi.DpiScaleX,Height=document.Height/dpi.DpiScaleY,Stretch=Stretch.Fill,UseLayoutRounding=true,SnapsToDevicePixels=true};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor);viewer.Content=image;
            // Only the controller sends these native messages. The test script
            // never scrolls the viewer or sends a wheel message itself.
            HwndSource.FromHwnd(handle)!.AddHook((IntPtr _,int message,IntPtr w,IntPtr l,ref bool handled)=> {
                if(message==MouseWheelObserver.MouseWheelMessage) {
                    nativeWheels++;int delta=unchecked((short)(w.ToInt64()>>16));
                    int wheelY=unchecked((short)(l.ToInt64()>>16));
                    if(wheelY<top+physicalHeight/3){wheelTargetsBody=false;handled=true;return IntPtr.Zero;}
                    viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset-delta/120d*step/dpi.DpiScaleY,0,viewer.ScrollableHeight));handled=true;
                }
                return IntPtr.Zero;
            });
            source.Activate();source.UpdateLayout();PumpFor(300);source.UpdateLayout();
            Native.SetWindowPos(handle,new IntPtr(-1),0,0,0,0,0x13);
            var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;using var snapshot=Imaging.Capture(desktop);
            overlay=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Scroll,_=>{});
            timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
            timer.Tick+=(_,_)=> {
                timer.Stop();
                try {
                    // Modal WPF disables same-thread windows; the native source
                    // must remain enabled for real WindowFromPoint wheel routing.
                    EnableWindow(handle,true);
                    check(IsWindowEnabled(handle),"the self-owned downward-drag source is enabled for native wheel hit testing during the modal overlay");
                    Native.SetWindowPos(handle,new WindowInteropHelper(overlay).Handle,0,0,0,0,0x13);
                    var originPixel=new D.Point(left+64,top+40);
                    // Place the controlled source away from unrelated topmost
                    // desktop layers. Pointer intent is sent through production
                    // methods; the hardware pointer is never moved by this test.
                    if(originPixel.Y>top+physicalHeight-290||originPixel.Y<top+4)originPixel.Y=top+40;
                    if(originPixel.X<left+4||originPixel.X>left+physicalWidth-40)originPixel.X=left+40;
                    int chosenWidth=Math.Min(300,Math.Max(originPixel.X-left, left+physicalWidth-originPixel.X)-16);
                    int sign=left+physicalWidth-originPixel.X>=originPixel.X-left?1:-1;
                    Point Local(D.Point pixel)=>overlay.PointFromScreen(new Point(pixel.X+.25,pixel.Y+.25));
                    var origin=Local(originPixel);
                    overlay.BeginPointer(origin);
                    overlay.MovePointer(Local(new D.Point(originPixel.X+sign*chosenWidth,originPixel.Y+50)));
                    check(overlay.DragCapture==null&&overlay.IsMouseCaptured,"loaded long-capture overlay first establishes width while retaining native pointer capture");
                    overlay.MovePointer(Local(new D.Point(originPixel.X+sign*chosenWidth,originPixel.Y+100)));
                    controller=overlay.DragCapture;
                    check(controller!=null&&overlay.IsMouseCaptured&&overlay.IsVisible,$"a vertical second leg starts the production drag controller inside the loaded overlay without a wheel event (controller={controller!=null}, captured={overlay.IsMouseCaptured}, error={overlay.DragError}, expectedSource={handle}, {overlay.DragDiagnostics})");
                    var viewport=controller!.Viewport;initialViewport=Imaging.Crop(snapshot,new D.Rectangle(viewport.X-desktop.X,viewport.Y-desktop.Y,viewport.Width,viewport.Height));
                    int firstHeight=overlay.Selection.Height;
                    int middle=Math.Min(viewport.Bottom-50,originPixel.Y+150);
                    overlay.MovePointer(Local(new D.Point(originPixel.X-sign*100,middle)));
                    check(overlay.Selection.Width==chosenWidth&&overlay.Selection.Height>firstHeight&&nativeWheels==0&&overlay.IsMouseCaptured,"loaded overlay keeps its initial width and grows downward despite horizontal drift before any automatic native scroll");
                    var bottom=new D.Point(viewport.Left+viewport.Width/2,viewport.Bottom-1);
                    overlay.MovePointer(Local(bottom));
                    PumpUntil(()=>(controller.WaitingForScroll&&Math.Abs(viewer.VerticalOffset-viewer.ScrollableHeight)<.01)||controller.Error!=null||!overlay.IsVisible,22000);
                    if(controller.Error!=null&&folder!=null) {
                        using var previous=controller.CopyLastFrame();using var failed=Imaging.Capture(viewport);
                        if(previous!=null)Imaging.Save(previous,Path.Combine(folder,"drag-native-rejected-previous.png"));
                        Imaging.Save(failed,Path.Combine(folder,"drag-native-rejected-current.png"));
                    }
                    check(overlay.IsVisible&&overlay.IsMouseCaptured&&controller.Error==null&&controller.WaitingForScroll&&!controller.AtBottom&&nativeWheels>0&&Math.Abs(viewer.VerticalOffset-viewer.ScrollableHeight)<.01,$"automatic native scrolling reaches the real viewer end but remains in recoverable waiting without reliable native range evidence (wheels={nativeWheels}, offset={viewer.VerticalOffset}, maximum={viewer.ScrollableHeight}, error={controller.Error}, match={controller.LastMatch})");
                    check(wheelTargetsBody,"automatic native wheel delivery targets the page body instead of the non-scrolling header near the original selection top");
                    int physicalOffset=(int)Math.Round(viewer.VerticalOffset*dpi.DpiScaleY);
                    check(physicalOffset==step*3+tail&&controller.ScrollOffset==physicalOffset&&overlay.Selection.Width==chosenWidth,"native downward dragging captures the source's non-step-sized terminal displacement while preserving the selected physical width");
                    finalViewport=Imaging.Capture(viewport);
                    if(folder!=null) {
                        Directory.CreateDirectory(folder);overlay.UpdateLayout();var root=(FrameworkElement)overlay.Content;
                        var rendered=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);rendered.Render(root);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using var file=File.Create(Path.Combine(folder,"drag-extend-overlay.png"));encoder.Save(file);
                    }
                    overlay.EndPointer(Local(bottom));
                    PumpUntil(()=>!overlay.IsVisible,8000);result=overlay.TakeDragResult();
                    check(overlay.DialogResult==true&&result!=null&&result.Width==chosenWidth&&result.Height==viewport.Height+physicalOffset,"releasing the loaded overlay returns the complete growing image directly with its fixed width and full scrolled height");
                    using(var prefix=Imaging.Crop(result!,new D.Rectangle(0,0,viewport.Width,viewport.Height)))check(Imaging.Png(prefix).SequenceEqual(Imaging.Png(initialViewport)),"the real overlay drag result preserves the original frozen viewport pixel for pixel");
                    using(var actualTail=Imaging.Crop(result!,new D.Rectangle(0,result!.Height-tail,result.Width,tail)))
                    using(var expectedTail=Imaging.Crop(finalViewport,new D.Rectangle(0,finalViewport.Height-tail,finalViewport.Width,tail)))
                        check(Imaging.Png(actualTail).SequenceEqual(Imaging.Png(expectedTail)),"the real native-scroll result includes the final 17 physical pixels at the bottom of the source viewport");
                    check(controller.ImagesReleased&&!controller.SamplingActive&&!overlay.IsMouseCaptured&&overlay.TakeDragResult()==null,"loaded drag completion releases its native pointer capture and controller resources and transfers result ownership only once");
                    if(folder!=null)Imaging.Save(result!,Path.Combine(folder,"drag-extend-native-result.png"));
                }catch(Exception ex){error=ex;}
                finally {if(overlay.IsVisible)overlay.Close();callbackCompleted=true;}
            };
            timer.Start();overlay.ShowDialog();
            if(!callbackCompleted)PumpUntil(()=>callbackCompleted,20000);
            if(error!=null)throw new InvalidOperationException("Loaded downward-drag capture acceptance failed",error);
        }finally{timer?.Stop();overlay?.Close();controller?.Dispose();source.Close();result?.Dispose();initialViewport?.Dispose();finalViewport?.Dispose();document?.Dispose();}
    }

    private static D.Bitmap Fixture(int width,int height)
    {
        var bitmap=SelfTests.Fixture(width,height);using var graphics=D.Graphics.FromImage(bitmap);
        for(int y=Math.Max(0,height-24);y<height;y++) {
            using var color=new D.SolidBrush(D.Color.FromArgb(255,(y*43+17)%256,(y*67+31)%256,(y*97+53)%256));
            graphics.FillRectangle(color,0,y,width,1);
        }
        return bitmap;
    }
    private static D.Bitmap Finish(DragCaptureController controller)
    {
        Task<D.Bitmap> task=controller.Finish();PumpUntil(()=>task.IsCompleted,12000);return task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> condition,int milliseconds)
    {
        var elapsed=Stopwatch.StartNew();while(!condition()&&elapsed.ElapsedMilliseconds<milliseconds)PumpFor(20);
        if(!condition())throw new TimeoutException("Drag capture acceptance did not settle.");
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
