using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

internal static class CaptureAcceptance
{
    public static List<string> Run(string? folder=null)
    {
        var lines=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new Exception("FAIL: "+text);lines.Add("PASS: "+text);}
        var desktop=new D.Rectangle(-1920,-800,3840,2160);var size=new Size(2560,1440);
        var front=new D.Rectangle(-1770,-650,450,300);var back=new D.Rectangle(-1920,-800,2000,1000);
        var gesture=new CaptureSelection(desktop,new[]{front,back},true);
        gesture.Hover(new Point(200,200),size);Check(gesture.Selection==front,"unified capture hover selects the topmost overlapping window in physical coordinates");
        gesture.Begin(new Point(200,200),size);Check(gesture.Finish(new Point(202,203),size,4,4)&&gesture.Selection==front,"small pointer jitter stays a window click and commits on release");
        gesture.Begin(new Point(300,300),size);gesture.Move(new Point(100,100),size,4,4);
        Check(gesture.IsDragging&&gesture.Finish(new Point(100,100),size,4,4)&&gesture.Selection==new D.Rectangle(-1770,-650,300,300),"reverse free selection overrides the window and maps mixed-DPI negative coordinates correctly");
        gesture.Begin(new Point(200,200),size);gesture.Move(new Point(208,200),size,4,4);
        Check(!gesture.Finish(new Point(200,200),size,4,4),"a drag returning to its start does not silently become a window click");
        gesture.Begin(new Point(200,200),size);gesture.Move(new Point(208,201),size,4,4);
        Check(!gesture.Finish(new Point(208,201),size,4,4),"too-narrow physical selections stay in capture mode rather than submitting");
        gesture.Begin(new Point(200,200),size);gesture.CancelGesture();Check(!gesture.Finish(new Point(300,300),size,4,4)&&!gesture.IsPressed,"lost capture or cancellation clears pending input and cannot submit a stale selection");
        var empty=new CaptureSelection(desktop,Array.Empty<D.Rectangle>(),true);empty.Begin(new Point(20,20),size);
        Check(!empty.Finish(new Point(21,21),size,4,4),"clicking without an automatic candidate does not submit an empty image");
        var region=new CaptureSelection(desktop,new[]{front},false);region.Hover(new Point(200,200),size);region.Begin(new Point(200,200),size);
        Check(region.Selection.IsEmpty&&!region.Finish(new Point(201,201),size,4,4),"long-image and translation region-only selection does not auto-pick a whole window");
        gesture.FullScreen();gesture.CancelGesture(clearSelection:false);Check(gesture.Selection==desktop,"full-screen selection preserves the complete virtual desktop after successful-close cleanup");
        foreach(double dpi in new[]{1d,1.25,1.5,2d}) {
            var surface=new Size(desktop.Width/dpi,desktop.Height/dpi);var g=new CaptureSelection(desktop,Array.Empty<D.Rectangle>(),true);
            g.Begin(new Point(120/dpi,80/dpi),surface);g.Move(new Point(620/dpi,380/dpi),surface,4,4);
            Check(g.Finish(new Point(620/dpi,380/dpi),surface,4,4)&&g.Selection==new D.Rectangle(-1800,-720,500,300),$"unified free selection preserves a 500x300 physical region at {dpi:P0} DPI");
        }
        var monitor=new D.Rectangle(-1600,200,1600,900);var control=CaptureOverlay.ControlPosition(desktop,monitor,size);
        Check(control.X>0&&control.Y>(monitor.Top-desktop.Top)*size.Height/desktop.Height,"capture controls are anchored to an actual monitor rather than an empty virtual-desktop corner");
        using var source=new D.Bitmap(100,80);
        var overlay=new CaptureOverlay(source,new D.Rectangle(0,0,100,80),"自动");
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(30) };
        timer.Tick+=(_,_)=> {timer.Stop();var button=Descendants<Button>((DependencyObject)overlay.Content).Single(b=>AutomationProperties.GetName(b)=="全屏截图");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
        timer.Start();bool? accepted=overlay.ShowDialog();
        Check(accepted==true&&overlay.Selection==new D.Rectangle(0,0,100,80)&&!overlay.ScrollRequested,"production full-screen control commits once and retains its selection after the modal overlay closes");
        var cancelOverlay=new CaptureOverlay(source,new D.Rectangle(0,0,100,80),"自动");
        var cancelTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(30) };
        cancelTimer.Tick+=(_,_)=> {
            cancelTimer.Stop();var button=Descendants<Button>((DependencyObject)cancelOverlay.Content).Single(b=>AutomationProperties.GetName(b)=="取消");button.Focus();
            cancelOverlay.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(cancelOverlay),0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent});
        };
        cancelTimer.Start();bool? cancelled=cancelOverlay.ShowDialog();
        Check(cancelled==false&&cancelOverlay.Selection.IsEmpty&&!cancelOverlay.ScrollRequested,"Enter on the focused cancel control cancels rather than taking an unintended full-screen screenshot");
        RunLoadedPointerPaths(Check);
        RunHeldDragFilter(Check);
        RunHeldDragHandoff(Check,folder);
        RunLoupePixels(Check);
        RunLoadedLoupe(Check,folder);
        return lines;
    }

    private static void RunHeldDragFilter(Action<bool,string> check)
    {
        var area=new D.Rectangle(-640,120,360,240);var inside=new D.Point(-500,200);var corner=new D.Point(area.Right,area.Bottom);
        var wheel=HeldDragScrollGuard.FilterForTest(MouseWheelObserver.MouseWheelMessage,corner,-120,area,keyState:0x1f);
        check(wheel.Consume&&wheel.ForwardWheel&&!wheel.Finish&&wheel.KeyState==0x1e,"held-drag wheel forwarding consumes the original once and removes only MK_LBUTTON while preserving Ctrl, Shift and other button flags");
        check(HeldDragScrollGuard.ClampToSelection(corner,area)==new D.Point(area.Right-1,area.Bottom-1),"held-drag wheel at the exact bottom-right edge is delivered to the last pixel inside the physical selection");
        var move=HeldDragScrollGuard.FilterForTest(HeldDragScrollGuard.MouseMove,inside,0,area);
        var up=HeldDragScrollGuard.FilterForTest(HeldDragScrollGuard.LeftUp,inside,0,area);
        check(move.Consume&&!move.Finish&&up.Consume&&up.Finish&&!up.ForwardWheel,"held-drag protection consumes the orphaned movement and physical button release before they reach the source application");
        var injectedUp=HeldDragScrollGuard.FilterForTest(HeldDragScrollGuard.LeftUp,inside,0,area,injected:true);
        check(injectedUp.Consume&&injectedUp.Finish&&!injectedUp.ForwardWheel,"an assisted release is drained as the end of the same logical screenshot drag");
        var freshDown=HeldDragScrollGuard.FilterForTest(HeldDragScrollGuard.LeftDown,inside,0,area,draining:true);
        var assistedDown=HeldDragScrollGuard.FilterForTest(HeldDragScrollGuard.LeftDown,inside,0,area,draining:true,injected:true);
        check(!freshDown.Consume&&freshDown.Finish&&!assistedDown.Consume&&assistedDown.Finish,"a new physical or assisted click ends stale drag protection and passes its button-down through even after capture cancellation");
        var draining=HeldDragScrollGuard.FilterForTest(MouseWheelObserver.MouseWheelMessage,inside,-120,area,draining:true);
        var noTarget=HeldDragScrollGuard.FilterForTest(MouseWheelObserver.MouseWheelMessage,inside,-120,area,hasTarget:false);
        check(draining.Consume&&!draining.ForwardWheel&&noTarget.Consume&&!noTarget.ForwardWheel,"finishing or cancelling before button release drains old wheel input without forwarding to a stale or absent target");
        var drainUp=HeldDragScrollGuard.FilterForTest(HeldDragScrollGuard.LeftUp,inside,0,area,draining:true);
        check(drainUp.Consume&&drainUp.Finish,"the physical release finishes the draining guard after the screenshot session has closed");
        bool nextClickPasses=true;
        foreach(int message in new[]{HeldDragScrollGuard.MouseMove,HeldDragScrollGuard.LeftDown,HeldDragScrollGuard.LeftUp}) {
            var decision=HeldDragScrollGuard.FilterForTest(message,inside,0,area,awaitingRelease:false);
            nextClickPasses&=!decision.Consume&&!decision.ForwardWheel&&!decision.Finish;
        }
        check(nextClickPasses,"normal pointer movement and both halves of the next click pass through after the held drag has ended");
        var outside=HeldDragScrollGuard.FilterForTest(MouseWheelObserver.MouseWheelMessage,new D.Point(area.Right+1,area.Bottom),-120,area);
        var zero=HeldDragScrollGuard.FilterForTest(MouseWheelObserver.MouseWheelMessage,inside,0,area);
        var right=HeldDragScrollGuard.FilterForTest(0x0204,inside,0,area);
        check(!outside.Consume&&!zero.Consume&&!right.Consume,"held-drag filtering leaves out-of-area scrolling, zero deltas and unrelated buttons untouched");
        using var edgeGate=new WheelCaptureGate(area,true);using var ordinaryGate=new WheelCaptureGate(area);
        var sample=new MouseWheelSample(corner,-120,1);
        check(edgeGate.Record(sample,1000)&&edgeGate.TakeDue(1000+WheelCaptureGate.QuietMilliseconds,out _,out var direction)&&direction==WheelCaptureDirection.Down&&!ordinaryGate.Record(sample,1000),"held-selection capture accepts subsequent wheel events on the closed right and bottom edges without changing ordinary selection gate boundaries");
        check(!edgeGate.Record(sample with {Position=new D.Point(area.Right+1,area.Bottom)},2000),"held-selection wheel gating still rejects input beyond the physical selection edge");
    }

    private static void RunHeldDragHandoff(Action<bool,string> check,string? folder)
    {
        var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
        using var document=LoupeFixture(desktop.Width,desktop.Height+64);
        using var snapshot=Imaging.Crop(document,new D.Rectangle(0,0,desktop.Width,desktop.Height));
        CaptureOverlay? overlay=null;ScrollWindow? session=null;D.Bitmap? completed=null;
        D.Rectangle selected=D.Rectangle.Empty;MouseWheelSample requested=default;Exception? error=null;
        try {
            overlay=new CaptureOverlay(snapshot,desktop,"自动",CaptureFollowUp.None,_=>{});
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(60)};
            timer.Tick+=(_,_)=> {
                timer.Stop();
                try {
                    Native.GetCursorPos(out var cursor);var monitor=System.Windows.Forms.Screen.FromPoint(new D.Point(cursor.X,cursor.Y)).Bounds;
                    int right=monitor.Right-cursor.X-12,left=cursor.X-monitor.Left-12,bottom=monitor.Bottom-cursor.Y-12,top=cursor.Y-monitor.Top-12;
                    int width=Math.Min(360,Math.Max(left,right)),height=Math.Min(240,Math.Max(top,bottom));
                    var origin=overlay.PointFromScreen(new Point(cursor.X,cursor.Y));
                    var endPixel=new D.Point(cursor.X+(right>=left?width:-width),cursor.Y+(bottom>=top?height:-height));
                    var end=overlay.PointFromScreen(new Point(endPixel.X,endPixel.Y));
                    selected=D.Rectangle.FromLTRB(Math.Min(cursor.X,endPixel.X),Math.Min(cursor.Y,endPixel.Y),Math.Max(cursor.X,endPixel.X),Math.Max(cursor.Y,endPixel.Y));
                    requested=new MouseWheelSample(new D.Point(selected.Right,selected.Bottom),-120,17);
                    check(!overlay.BeginScrollFromWheel(requested),"wheel input before a selection drag cannot request held-drag long capture");
                    overlay.BeginPointer(origin);
                    check(!overlay.BeginScrollFromWheel(requested),"a pressed pointer without a real drag cannot request long capture");
                    overlay.MovePointer(overlay.PointFromScreen(new Point(cursor.X+(right>=left?8:-8),cursor.Y+(bottom>=top?40:-40))));
                    check(overlay.Dragging&&!overlay.BeginScrollFromWheel(requested)&&overlay.IsMouseCaptured,"a held drag below the minimum long-capture dimensions stays in the selection overlay");
                    overlay.MovePointer(end);
                    check(overlay.IsVisible&&overlay.IsMouseCaptured&&overlay.Dragging&&overlay.Selection==selected&&!overlay.ScrollRequested,"loaded capture overlay is still dragging with native mouse capture before any button release or wheel handoff");
                    check(!overlay.BeginScrollFromWheel(requested with {Delta=120})&&!overlay.BeginScrollFromWheel(requested with {Delta=0})&&!overlay.BeginScrollFromWheel(requested with {ControlPressed=true})&&!overlay.BeginScrollFromWheel(requested with {ShiftPressed=true})&&overlay.IsMouseCaptured&&overlay.Selection==selected,"upward, zero, Ctrl and Shift wheel gestures leave the in-progress selection drag intact");
                    // Deliberately do not call EndPointer or send a left-button-up.
                    check(overlay.BeginScrollFromWheel(requested),"downward wheel starts long capture directly from CaptureOverlay while the selection drag has not been released");
                }catch(Exception ex){error=ex;if(overlay.IsVisible)overlay.Close();}
            };
            bool? accepted;
            try {timer.Start();accepted=overlay.ShowDialog();}finally{timer.Stop();}
            if(error!=null)throw new InvalidOperationException("Held-drag overlay handoff failed",error);
            check(accepted==true&&overlay.ScrollRequested&&overlay.InitialWheel==requested&&overlay.Selection==selected&&!overlay.Dragging&&!overlay.IsMouseCaptured,"wheel handoff commits the exact physical selection and initial wheel while safely releasing native mouse capture");
            overlay.EndPointer(new Point(1,1));
            check(overlay.Selection==selected&&overlay.InitialWheel==requested,"a late button-release callback cannot clear or recommit the wheel-selected region");
            check(!Application.Current.Windows.OfType<ScreenEditorWindow>().Any(window=>window.IsVisible),"drag-wheel handoff does not create an intermediate frozen editor");
            const int shift=48;int forwarded=0;MouseWheelSample delivery=default;
            session=App.CreateDragScroll(overlay,snapshot,desktop,image=>completed=new D.Bitmap(image),
                area=>Imaging.Crop(document,new D.Rectangle(area.X-desktop.X,area.Y-desktop.Y+shift,area.Width,area.Height)),
                sample=> {forwarded++;delivery=sample;});
            // The shared factory must have cloned its seed before this source changes.
            using(var graphics=D.Graphics.FromImage(snapshot))graphics.Clear(D.Color.Magenta);
            session.Show();PumpHeldDragUntil(()=>session.FrameCount==2&&!session.SamplingActive,6000);
            check(session.ObserverAttached&&session.SelectionFrameVisible&&session.SelectionFrameBounds==selected&&session.FrameCount==2,$"the production drag-scroll factory preserves the selection frame and appends the first live frame (frames={session.FrameCount}, captures={session.CaptureCount}, status={session.StatusText})");
            check(forwarded==1&&delivery.Position==new D.Point(selected.Right-1,selected.Bottom-1)&&delivery.Delta==requested.Delta,"the drag-scroll factory forwards the initiating corner wheel once through the safe sink with its position clamped inside the selection");
            session.FinishForAcceptance();PumpHeldDragUntil(()=>!session.IsVisible,6000);
            using var expected=Imaging.Crop(document,new D.Rectangle(selected.X-desktop.X,selected.Y-desktop.Y,selected.Width,selected.Height+shift));
            check(completed!=null&&Imaging.Png(completed).SequenceEqual(Imaging.Png(expected)),"drag-wheel handoff retains the complete original frozen seed and the first appended rows pixel for pixel");
            check(session.ImagesReleased&&session.SelectionFrameReleased&&!session.ObserverAttached&&!session.SamplingActive,"drag-wheel completion releases the blue frame, native hooks, timers and owned bitmaps");
            if(folder!=null&&completed!=null){Directory.CreateDirectory(folder);Imaging.Save(completed,Path.Combine(folder,"held-drag-scroll-result.png"));}
        }finally{session?.Close();overlay?.ReleaseUnusedDragGuard();overlay?.Close();completed?.Dispose();}

        var translate=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Translate,_=>{});var translationTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(60)};Exception? translationError=null;D.Rectangle translatedSelection=D.Rectangle.Empty;
        translationTimer.Tick+=(_,_)=> {
            translationTimer.Stop();
            try {
                Native.GetCursorPos(out var cursor);var monitor=System.Windows.Forms.Screen.FromPoint(new D.Point(cursor.X,cursor.Y)).Bounds;
                int dx=monitor.Right-cursor.X>220?200:-200,dy=monitor.Bottom-cursor.Y>170?150:-150;
                var origin=translate.PointFromScreen(new Point(cursor.X,cursor.Y));var end=translate.PointFromScreen(new Point(cursor.X+dx,cursor.Y+dy));
                translate.BeginPointer(origin);translate.MovePointer(end);translatedSelection=translate.Selection;
                check(translate.Dragging&&!translate.BeginScrollFromWheel(new MouseWheelSample(new D.Point(translatedSelection.Right,translatedSelection.Bottom),-120,19))&&translate.IsMouseCaptured,"translation-region dragging does not switch to long capture on a wheel event");
                translate.EndPointer(end);
            }catch(Exception ex){translationError=ex;translate.Close();}
        };
        try {
            translationTimer.Start();bool? accepted=translate.ShowDialog();
            if(translationError!=null)throw new InvalidOperationException("Translation drag acceptance failed",translationError);
            check(accepted==true&&!translate.ScrollRequested&&translate.Selection==translatedSelection&&!translate.IsMouseCaptured,"normal button-release selection remains available after long-capture wheel rejection");
        }finally{translationTimer.Stop();translate.Close();}
    }

    private static void PumpHeldDragUntil(Func<bool> condition,int milliseconds)
    {
        var deadline=DateTime.UtcNow.AddMilliseconds(milliseconds);
        while(!condition()&&DateTime.UtcNow<deadline) {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
        }
        if(!condition())throw new TimeoutException("Held-drag long capture did not settle.");
    }

    private static void RunLoupePixels(Action<bool,string> check)
    {
        var desktop=new D.Rectangle(-730,-410,80,64);
        using var fixture=LoupeFixture(desktop.Width,desktop.Height);
        var loupe=new CaptureLoupe(Imaging.Source(fixture));
        check(loupe.Preview.PixelWidth==25&&loupe.Preview.PixelHeight==17&&!loupe.IsHitTestVisible&&!loupe.Focusable,"pixel loupe uses a 25x17 physical-pixel preview without intercepting selection input");
        check(RenderOptions.GetBitmapScalingMode(Descendants<Image>(loupe).Single())==BitmapScalingMode.NearestNeighbor,"pixel loupe enlarges original pixels with nearest-neighbor sampling");
        foreach(double dpi in new[]{1d,1.25,1.5,2d}) {
            var surface=new Size(desktop.Width/dpi,desktop.Height/dpi);
            var samples=new[] {
                (point:new Point(0,0),pixel:new D.Point(0,0)),
                (point:new Point(surface.Width-.01,0),pixel:new D.Point(desktop.Width-1,0)),
                (point:new Point(0,surface.Height-.01),pixel:new D.Point(0,desktop.Height-1)),
                (point:new Point(surface.Width-.01,surface.Height-.01),pixel:new D.Point(desktop.Width-1,desktop.Height-1)),
                (point:new Point(23.99/dpi,31.01/dpi),pixel:new D.Point(23,31))
            };
            foreach(var sample in samples) {
                var physical=CaptureLoupe.MapPixel(sample.point,surface,desktop);
                var expected=new D.Point(desktop.Left+sample.pixel.X,desktop.Top+sample.pixel.Y);
                check(physical==expected,$"loupe maps the physical pixel {sample.pixel.X},{sample.pixel.Y} on a negative-coordinate screen at {dpi:P0} DPI without rounding to its neighbor");
                loupe.UpdateSample(physical,desktop);
                var color=fixture.GetPixel(sample.pixel.X,sample.pixel.Y);
                var bytes=new byte[25*17*4];loupe.Preview.CopyPixels(bytes,25*4,0);bool exact=SameColor(loupe.SampleColor,color)&&loupe.SamplePosition==expected;
                for(int row=0;row<17;row++)for(int col=0;col<25;col++) {
                    var original=fixture.GetPixel(Math.Clamp(sample.pixel.X+col-12,0,desktop.Width-1),Math.Clamp(sample.pixel.Y+row-8,0,desktop.Height-1));int at=(row*25+col)*4;
                    exact&=bytes[at]==original.B&&bytes[at+1]==original.G&&bytes[at+2]==original.R&&bytes[at+3]==original.A;
                }
                check(exact,$"loupe preview and center color retain every source pixel around {sample.pixel.X},{sample.pixel.Y} at {dpi:P0} DPI, including replicated screen edges");
            }
            check(CaptureLoupe.MapPixel(new Point(-20,-10),surface,desktop)==desktop.Location&&CaptureLoupe.MapPixel(new Point(surface.Width+20,surface.Height+10),surface,desktop)==new D.Point(desktop.Right-1,desktop.Bottom-1),$"loupe clamps outside coordinates to the first and last physical pixel at {dpi:P0} DPI");
            var monitor=new Rect(-1600/dpi,-600/dpi,1600/dpi,900/dpi);var controls=new Rect(monitor.Left+24,monitor.Top+24,190,60);
            foreach(var pointer in new[]{monitor.TopLeft,monitor.TopRight,monitor.BottomLeft,monitor.BottomRight,new Point(monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2)}) {
                var card=CaptureLoupe.CardBounds(pointer,monitor,new Size(222,240),controls);
                check(monitor.Contains(card)&&card.Width>0&&card.Height>0,$"loupe card remains inside the actual negative-coordinate monitor at {dpi:P0} DPI, including screen corners");
            }
        }
        var roomy=new Rect(0,0,1200,800);var occupied=new Rect(24,24,230,80);
        check(!CaptureLoupe.CardBounds(new Point(400,300),roomy,new Size(222,240),occupied).IntersectsWith(occupied),"loupe placement keeps the capture controls clear when adjacent space is available");
    }

    private static void RunLoadedLoupe(Action<bool,string> check,string? folder)
    {
        var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
        using var fixture=LoupeFixture(desktop.Width,desktop.Height);var copied=new List<string>();
        var overlay=new CaptureOverlay(fixture,desktop,"自动",CaptureFollowUp.None,copied.Add);
        // The loupe must keep reading the frozen source created by the constructor.
        using(var g=D.Graphics.FromImage(fixture))g.Clear(D.Color.Magenta);
        Exception? error=null;bool sawLoaded=false;var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
        overlay.Loaded+=(_,_)=> {
            try {
                Native.GetCursorPos(out var cursor);int x=Math.Clamp(cursor.X-desktop.X,0,desktop.Width-1),y=Math.Clamp(cursor.Y-desktop.Y,0,desktop.Height-1);
                sawLoaded=overlay.Loupe.HasSample&&overlay.Loupe.Visibility==Visibility.Visible&&!overlay.IsMouseCaptured;
                check(sawLoaded&&overlay.Loupe.SamplePosition==new D.Point(desktop.Left+x,desktop.Top+y)&&SameColor(overlay.Loupe.SampleColor,LoupeColor(x,y)),"loaded screenshot overlay shows the current cursor pixel loupe before any click or drag");
            }catch(Exception ex){error=ex;}
        };
        timer.Tick+=(_,_)=> {
            timer.Stop();
            try {
                if(error!=null)return;
                Native.GetCursorPos(out var cursor);var monitor=System.Windows.Forms.Screen.FromPoint(new D.Point(cursor.X,cursor.Y)).Bounds;
                var pixel=new D.Point(monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2);
                overlay.MovePointer(overlay.PointFromScreen(new Point(pixel.X+.25,pixel.Y+.25)));
                var loupe=overlay.Loupe;var expected=LoupeColor(pixel.X-desktop.Left,pixel.Y-desktop.Top);
                check(loupe.HasSample&&loupe.SamplePosition==pixel&&SameColor(loupe.SampleColor,expected),"hover movement reads the immutable screenshot pixel instead of the selection mask, frame or later bitmap changes");
                var preview=new byte[4];loupe.Preview.CopyPixels(new Int32Rect(12,8,1,1),preview,4,0);
                check(preview[0]==expected.B&&preview[1]==expected.G&&preview[2]==expected.R&&preview[3]==expected.A,"loaded loupe crosshair center is the exact hovered physical source pixel");
                var selection=overlay.Selection;var presentation=PresentationSource.FromVisual(overlay);
                KeyEventArgs Press(Key key) {var args=new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};overlay.RaiseEvent(args);return args;}
                string rgb=$"RGB({expected.R}, {expected.G}, {expected.B})";
                check(loupe.IsRgb&&loupe.ColorText==rgb&&Press(Key.C).Handled&&copied.Count==1&&copied[0]==rgb,"C copies the displayed RGB color through the injected sink without touching the system clipboard");
                string hex=$"#{expected.R:X2}{expected.G:X2}{expected.B:X2}";
                check(Press(Key.LeftShift).Handled&&!loupe.IsRgb&&loupe.ColorText==hex&&Press(Key.C).Handled&&copied.Count==2&&copied[1]==hex,"Shift switches to HEX and C copies the exact displayed value through the production key route");
                check(overlay.HandleColorKey(Key.RightShift,ModifierKeys.Shift,repeat:true)&&!loupe.IsRgb,"holding Shift does not repeatedly toggle the loupe color format");
                check(!overlay.HandleColorKey(Key.C,ModifierKeys.Control)&&!overlay.HandleColorKey(Key.C,ModifierKeys.Alt)&&!overlay.HandleColorKey(Key.C,ModifierKeys.Windows)&&!overlay.HandleColorKey(Key.LeftShift,ModifierKeys.Control|ModifierKeys.Shift)&&copied.Count==2,"color shortcuts leave modified commands untouched and do not make additional clipboard writes");
                check(overlay.HandleColorKey(Key.RightShift)&&loupe.IsRgb&&loupe.ColorText==rgb&&overlay.Selection==selection&&!overlay.IsMouseCaptured&&overlay.IsVisible,"RGB and HEX switching preserves the hovered screenshot selection and leaves capture open");
                if(folder!=null) {
                    Directory.CreateDirectory(folder);overlay.UpdateLayout();
                    foreach(double dpi in new[]{1d,1.25,1.5,2d}) {
                        var visual=new DrawingVisual();using(var drawing=visual.RenderOpen())drawing.DrawRectangle(new VisualBrush(loupe),null,new Rect(0,0,loupe.ActualWidth,loupe.ActualHeight));
                        var rendered=new RenderTargetBitmap((int)Math.Ceiling(loupe.ActualWidth*dpi),(int)Math.Ceiling(loupe.ActualHeight*dpi),96*dpi,96*dpi,PixelFormats.Pbgra32);rendered.Render(visual);
                        Save(rendered,Path.Combine(folder,$"capture-loupe-{dpi*100:0}.png"));
                    }
                    var root=(FrameworkElement)overlay.Content;var shown=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);shown.Render(root);Save(shown,Path.Combine(folder,"capture-overlay-loupe.png"));
                    check(true,"loaded loupe rendered at 100/125/150/200% with frozen screenshot overlay evidence");
                }
            }catch(Exception ex){error=ex;}
            finally {Descendants<Button>((DependencyObject)overlay.Content).Single(button=>AutomationProperties.GetName(button)=="取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
        };
        try {
            timer.Start();bool? result=overlay.ShowDialog();
            if(error!=null)throw new InvalidOperationException("Loaded pixel loupe acceptance failed",error);
            check(sawLoaded&&result==false&&overlay.Selection.IsEmpty&&!overlay.Loupe.HasSample&&overlay.Loupe.Visibility==Visibility.Collapsed,"cancelling after color inspection clears the selection and hides the loupe without committing a screenshot");
        }finally {timer.Stop();overlay.Close();}
    }

    private static D.Bitmap LoupeFixture(int width,int height)
    {
        var bitmap=new D.Bitmap(width,height,D.Imaging.PixelFormat.Format32bppArgb);var bytes=new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++){var color=LoupeColor(x,y);int at=(y*width+x)*4;bytes[at]=color.B;bytes[at+1]=color.G;bytes[at+2]=color.R;bytes[at+3]=color.A;}
        var data=bitmap.LockBits(new D.Rectangle(0,0,width,height),D.Imaging.ImageLockMode.WriteOnly,D.Imaging.PixelFormat.Format32bppArgb);
        try{for(int y=0;y<height;y++)Marshal.Copy(bytes,y*width*4,IntPtr.Add(data.Scan0,y*data.Stride),width*4);}finally{bitmap.UnlockBits(data);}
        return bitmap;
    }
    private static D.Color LoupeColor(int x,int y)=>D.Color.FromArgb(255,(x*17+y*5)%256,(x*7+y*19)%256,(x*31+y*11)%256);
    private static bool SameColor(Color actual,D.Color expected)=>actual.A==expected.A&&actual.R==expected.R&&actual.G==expected.G&&actual.B==expected.B;
    private static void Save(BitmapSource source,string path){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));using var file=File.Create(path);encoder.Save(file);}
    private static void RunLoadedPointerPaths(Action<bool,string> check)
    {
        var target=new Window {Title="Framelet · 截图检测目标",Width=320,Height=240,Left=80,Top=100,Topmost=true,ResizeMode=ResizeMode.NoResize,Background=Ui.Surface};
        try {
            target.Show();target.UpdateLayout();var handle=new WindowInteropHelper(target).EnsureHandle();
            // Use the physical pointer position as the click origin. Capturing
            // the mouse itself emits a real MouseMove at that position; a made-up
            // origin would turn a synthetic click into an unrelated long drag.
            Native.GetCursorPos(out var pointer);Native.SetWindowPos(handle,new IntPtr(-1),pointer.X-100,pointer.Y-90,400,300,0x40);target.UpdateLayout();
            Native.Rect nativeBounds;
            if(Native.DwmGetWindowAttribute(handle,9,out nativeBounds,System.Runtime.InteropServices.Marshal.SizeOf<Native.Rect>())!=0)Native.GetWindowRect(handle,out nativeBounds);
            var expectedWindow=nativeBounds.Bounds;var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
            using var screen=Imaging.Capture(desktop);
            foreach(string path in new[]{"window","region","lost"}) {
                target.Activate();
                var overlay=new CaptureOverlay(screen,desktop,"自动");var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(60)};string trace="";D.Rectangle expectedRegion=D.Rectangle.Empty;
                timer.Tick+=(_,_)=> {
                    timer.Stop();
                    Native.GetCursorPos(out var currentPointer);
                    var origin=overlay.PointFromScreen(new Point(currentPointer.X,currentPointer.Y));
                    overlay.MovePointer(origin);var hovered=overlay.Selection;overlay.BeginPointer(origin);var begun=overlay.Selection;bool captured=overlay.IsMouseCaptured;
                    if(path=="window")overlay.EndPointer(origin);
                    else if(path=="region") {
                        expectedRegion=D.Rectangle.Intersect(desktop,new D.Rectangle(currentPointer.X,currentPointer.Y,200,100));
                        var end=overlay.PointFromScreen(new Point(currentPointer.X+200,currentPointer.Y+100));
                        overlay.MovePointer(end);overlay.EndPointer(end);
                    } else {
                        overlay.ReleaseMouseCapture();overlay.EndPointer(origin);
                        Descendants<Button>((DependencyObject)overlay.Content).Single(b=>AutomationProperties.GetName(b)=="取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    trace=$"hovered={hovered}, begun={begun}, mouseCaptured={captured}, after={overlay.Selection}, origin={origin}, surface={overlay.ActualWidth}x{overlay.ActualHeight}";
                    if(overlay.IsVisible)Descendants<Button>((DependencyObject)overlay.Content).Single(b=>AutomationProperties.GetName(b)=="取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                timer.Start();bool? result=overlay.ShowDialog();
                if(path=="window")check(result==true&&overlay.Selection==D.Rectangle.Intersect(desktop,expectedWindow),$"loaded production overlay uses real window detection and retains the clicked window after closing (result={result}, selection={overlay.Selection}, expected={expectedWindow}, {trace})");
                else if(path=="region")check(result==true&&overlay.Selection==expectedRegion&&!overlay.ScrollRequested,"loaded production drag handler preserves the physical crop after native mouse-release events");
                else check(result==false&&overlay.Selection.IsEmpty,"loaded production overlay cannot submit after its native mouse capture is lost");
            }
        } finally {target.Close();}
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is T value)yield return value;foreach(var nested in Descendants<T>(child))yield return nested;}
    }
}
