using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

internal static class WheelCaptureAcceptance
{
    public static List<string> Run()
    {
        var lines=new List<string>();
        void Check(bool ok,string label) {if(!ok)throw new Exception("FAIL: "+label);lines.Add("PASS: "+label);}
        var packet=Marshal.AllocHGlobal(Marshal.SizeOf<MouseWheelObserver.MouseData>());
        try {
            var data=new MouseWheelObserver.MouseData {X=-1440,Y=280,MouseDataValue=unchecked((uint)(-120<<16)),Time=uint.MaxValue-3};
            Marshal.StructureToPtr(data,packet,false);
            Check(MouseWheelObserver.TryReadWheel(0,MouseWheelObserver.MouseWheelMessage,packet,out var down)&&down.Position==new D.Point(-1440,280)&&down.Delta==-120&&down.NativeTime==uint.MaxValue-3,"native mouse-hook packet preserves negative monitor coordinates, signed downward wheel delta and uint timestamp");
            data.MouseDataValue=120u<<16;Marshal.StructureToPtr(data,packet,false);
            Check(MouseWheelObserver.TryReadWheel(0,MouseWheelObserver.MouseWheelMessage,packet,out var up)&&up.Delta==120,"native mouse-hook packet distinguishes upward wheel movement");
            Check(!MouseWheelObserver.TryReadWheel(-1,MouseWheelObserver.MouseWheelMessage,packet,out _)&&!MouseWheelObserver.TryReadWheel(0,0x020E,packet,out _)&&!MouseWheelObserver.TryReadWheel(0,MouseWheelObserver.MouseWheelMessage,IntPtr.Zero,out _),"hook decoder rejects negative hook codes, horizontal scrolling and empty packets");
            foreach(uint flags in new[]{1u,2u,3u}) {
                data.Flags=flags;Marshal.StructureToPtr(data,packet,false);
                Check(MouseWheelObserver.TryReadWheel(0,MouseWheelObserver.MouseWheelMessage,packet,out var assisted)&&assisted.Injected,$"hook decoder accepts assisted or automated wheel input flags {flags} while retaining its injection metadata");
            }
            Check(!MouseWheelObserver.TryReadWheel(0,MouseWheelObserver.MouseWheelMessage,packet,out _,controlPressed:true)&&!MouseWheelObserver.TryReadWheel(0,MouseWheelObserver.MouseWheelMessage,packet,out _,shiftPressed:true),"hook decoder leaves Ctrl zoom and Shift horizontal-scroll gestures to the original application without starting capture");
            data.Flags=0;data.MouseDataValue=0;Marshal.StructureToPtr(data,packet,false);
            Check(!MouseWheelObserver.TryReadWheel(0,MouseWheelObserver.MouseWheelMessage,packet,out _),"zero wheel delta does not create a capture request");
        } finally {Marshal.FreeHGlobal(packet);}
        var area=new D.Rectangle(-1600,200,800,600);
        using(var gate=new WheelCaptureGate(area)) {
            Check(!gate.TakeDue(10000,out _,out _)&&!gate.Pending,"idle gate does not request screen sampling");
            Check(!gate.Record(new(new D.Point(1,1),-120,1),10000)&&!gate.Record(new(new D.Point(-800,280),-120,1),10000)&&!gate.Record(new(new D.Point(-1440,280),-120,1,ControlPressed:true),10000)&&!gate.Record(new(new D.Point(-1440,280),-120,1,ShiftPressed:true),10000),"wheel events outside physical selection edges, Ctrl zoom and Shift horizontal-scroll gestures stay idle");
            long first=10000,second=first+WheelCaptureGate.QuietMilliseconds/2;
            Check(gate.Record(new(new D.Point(-1440,280),-120,1),first)&&gate.Record(new(new D.Point(-1440,280),-120,2),second),"in-selection wheel burst records actual downward events");
            long quietDue=second+WheelCaptureGate.QuietMilliseconds;
            Check(!gate.TakeDue(quietDue-1,out _,out _)&&gate.TakeDue(quietDue,out var revision,out var direction)&&revision==2&&direction==WheelCaptureDirection.Down&&!gate.TakeDue(20000,out _,out _),"a short wheel burst coalesces until its quiet interval and produces one request");
            gate.Record(new(new D.Point(-1440,280),120,3),30000);
            Check(gate.TakeDue(30000+WheelCaptureGate.QuietMilliseconds,out _,out direction)&&direction==WheelCaptureDirection.Up,"upward recovery keeps its direction through debounce");
            Check(gate.Record(new(new D.Point(-1440,280),-120,4,true),35000)&&gate.TakeDue(35000+WheelCaptureGate.QuietMilliseconds,out _,out direction)&&direction==WheelCaptureDirection.Down,"gate accepts valid assisted or automated vertical wheel events inside the selection");
            long continuousStart=36000;
            for(int i=0;i<3;i++)gate.Record(new(new D.Point(-1440,280),-120,(uint)(10+i)),continuousStart+i*WheelCaptureGate.MaxWaitMilliseconds/3);
            Check(!gate.TakeDue(continuousStart+WheelCaptureGate.MaxWaitMilliseconds-1,out _,out _)&&gate.TakeDue(continuousStart+WheelCaptureGate.MaxWaitMilliseconds,out _,out direction)&&direction==WheelCaptureDirection.Down,"continuous wheel events request an intermediate capture within the maximum wait instead of postponing sampling until the scroll stops");
            gate.Record(new(new D.Point(-1440,280),-120,4),40000);gate.Dispose();
            Check(!gate.Pending&&!gate.Record(new(new D.Point(-1440,280),-120,5),41000)&&!gate.TakeDue(42000,out _,out _),"disposed wheel gate clears pending work and rejects late callbacks");
        }
        using(var observer=new MouseWheelObserver(_=>{},()=>{})) {
            Check(observer.IsInstalled,"real WH_MOUSE_LL and WH_KEYBOARD_LL hooks install in the production process without injecting input");
            observer.Dispose();observer.Dispose();
            Check(!observer.IsInstalled&&observer.HooksReleased&&observer.LastUnhookError==0,"real native mouse and Escape hooks are released and disposal is idempotent");
        }
        foreach(var selection in new[]{new D.Rectangle(10,10,800,600),new D.Rectangle(-1600,200,800,600)}) {
            var screens=new[]{new D.Rectangle(0,0,1920,1040),new D.Rectangle(-1920,0,1920,1080)};
            var spot=ScrollWindow.ToolbarPosition(selection,426,87,screens);
            Check(!spot.IsEmpty&&!spot.IntersectsWith(selection)&&screens.Any(s=>s.Contains(spot)),"compact scroll toolbar stays outside its selection at 150% physical scale including negative coordinates");
        }
        Check(ScrollWindow.ToolbarPosition(new D.Rectangle(0,0,1920,1040),284,58,new[]{new D.Rectangle(0,0,1920,1040)}).IsEmpty,"full-monitor selection reports lack of external toolbar space instead of contaminating captured pixels");
        RunSession(Check);
        RunSeededSession(Check);
        RunContinuousExplorerSession(Check);
        return lines;
    }

    private static void RunSession(Action<bool,string> check)
    {
        var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Ui.Resources(app);
        using var fixture=SelfTests.Fixture();int offset=0;bool fail=false;D.Bitmap? completed=null;
        var area=new D.Rectangle(80,100,800,650);
        D.Bitmap Capture(D.Rectangle _) {if(fail)throw new InvalidOperationException("受控抓屏失败");return Imaging.Crop(fixture,new D.Rectangle(0,offset,800,650));}
        var window=new ScrollWindow(area,b=>completed=new D.Bitmap(b),Capture);
        var sample=new MouseWheelSample(new D.Point(100,150),-120,1);
        try {
            window.Show();PumpUntil(()=>window.ObserverAttached,2500);
            check(window.ObserverAttached&&window.CaptureCount==1&&!window.SamplingActive,"loaded production long-capture toolbar installs real hooks and takes exactly one initial frame");
            check(window.SelectionFrameVisible&&window.SelectionFrameBounds==area,"loaded long capture retains the blue frame at the exact physical selection bounds");
            check(Native.GetWindowRect(new WindowInteropHelper(window).Handle,out var bounds)&&!bounds.Bounds.IntersectsWith(area)&&window.ActualHeight<=60&&window.ActualWidth<=286,"loaded compact toolbar has physical bounds outside the captured area");
            PumpFor(400);check(window.CaptureCount==1&&!window.SamplingActive,"loaded idle session performs no periodic screen captures");
            offset=240;window.ObserveWheel(sample);window.ObserveWheel(sample);window.ObserveWheel(sample);
            PumpUntil(()=>window.FrameCount==2&&!window.SamplingActive,4000);
            check(window.FrameCount==2&&window.CaptureCount>=2,"actual production dispatcher coalesces three wheel samples and appends their one distinct frame");
            offset=1200;window.ObserveWheel(sample);PumpUntil(()=>!window.SamplingActive,3000);
            check(window.FrameCount==2&&window.StatusText.Contains("滚动跨度过大"),$"an unreliable large wheel jump preserves the previous composite and shows recovery guidance (frames={window.FrameCount}, captures={window.CaptureCount}, status={window.StatusText})");
            offset=0;window.ObserveWheel(sample with {Delta=120});PumpUntil(()=>!window.SamplingActive,3000);
            check(window.FrameCount==2&&window.StatusText.Contains("向上回退"),"upward recovery never duplicates or appends a previously captured section");
            offset=240;window.ObserveWheel(sample);PumpUntil(()=>!window.SamplingActive,3000);
            check(window.FrameCount==2&&window.StatusText.Contains("没有新内容"),"returning to the last accepted frame retains the same composite");
            fail=true;window.ObserveWheel(sample);PumpUntil(()=>!window.SamplingActive,3000);
            check(window.FrameCount==2&&!window.ObserverAttached&&window.StatusText.Contains("受控抓屏失败"),"capture failure pauses sampling, releases hooks and explains the error while preserving the completed image");
            Button(window,"完成").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(()=>!window.IsVisible,3000);
            using var expected=Imaging.Crop(fixture,new D.Rectangle(0,0,800,890));
            check(completed!=null&&Imaging.Png(completed).SequenceEqual(Imaging.Png(expected)),"finishing wheel-driven long capture returns a composite equal to every expected source pixel");
            check(window.ImagesReleased&&!window.ObserverAttached&&!window.SamplingActive&&window.SelectionFrameReleased&&!window.SelectionFrameVisible,"finished session releases native hooks, timer, blue selection frame and owned bitmaps");
        } finally {window.Close();completed?.Dispose();}

        int finalOffset=0;D.Bitmap? finalImage=null;
        var finishing=new ScrollWindow(area,b=>finalImage=new D.Bitmap(b),_=>Imaging.Crop(fixture,new D.Rectangle(0,finalOffset,800,650)));
        try {
            finishing.Show();PumpUntil(()=>finishing.ObserverAttached,2500);
            finalOffset=240;finishing.ObserveWheel(sample);
            Button(finishing,"完成").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(()=>!finishing.IsVisible,4000);
            check(finalImage?.Height==890&&finishing.FrameCount==2&&finishing.ImagesReleased&&!finishing.SamplingActive&&finishing.SelectionFrameReleased,$"Finish flushes the final pending wheel burst before returning the stitched image and releasing resources (imageHeight={finalImage?.Height}, frames={finishing.FrameCount}, released={finishing.ImagesReleased}, sampling={finishing.SamplingActive}, visible={finishing.IsVisible}, captures={finishing.CaptureCount}, status={finishing.StatusText})");
        } finally {finishing.Close();finalImage?.Dispose();}

        foreach(bool escape in new[]{false,true}) {
            var cancelled=new ScrollWindow(area,_=>throw new InvalidOperationException("Cancellation must not complete"),_=>Imaging.Crop(fixture,new D.Rectangle(0,0,800,650)));
            try {
                cancelled.Show();PumpUntil(()=>cancelled.ObserverAttached,2500);cancelled.ObserveWheel(sample);
                if(escape)cancelled.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(cancelled),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                else Button(cancelled,"取消").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpFor(250);
                check(!cancelled.IsVisible&&!cancelled.ObserverAttached&&!cancelled.SamplingActive&&cancelled.ImagesReleased&&cancelled.SelectionFrameReleased&&!cancelled.SelectionFrameVisible,$"{(escape?"Escape":"cancel control")} closes a pending wheel session and releases images, blue frame and hooks without completing");
            } finally {cancelled.Close();}
        }
    }

    private static void RunSeededSession(Action<bool,string> check)
    {
        using var fixture=SelfTests.Fixture();var area=new D.Rectangle(80,100,800,650);
        var initialWheel=new MouseWheelSample(new D.Point(100,150),-120,1);
        D.Bitmap? completed=null;int captureCalls=0,forwardCalls=0;MouseWheelSample? forwarded=null;
        var seed=Imaging.Crop(fixture,new D.Rectangle(0,0,800,650));
        var window=new ScrollWindow(area,b=>completed=new D.Bitmap(b),_=> {captureCalls++;return Imaging.Crop(fixture,new D.Rectangle(0,240,800,650));},seed,initialWheel,sample=> {forwardCalls++;forwarded=sample;});
        seed.Dispose();
        try {
            window.Show();PumpUntil(()=>window.ObserverAttached&&window.FrameCount==2&&!window.SamplingActive,4000);
            check(window.ObserverAttached&&window.FrameCount==2&&captureCalls>0&&window.SelectionFrameVisible,"the first wheel event starts from an owned frozen seed and appends the already-scrolled live frame without replacing the original top");
            check(forwardCalls==1&&forwarded==initialWheel,"seeded capture forwards the initiating wheel exactly once through the test sink without scrolling another application");
            Button(window,"完成").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));PumpUntil(()=>!window.IsVisible,4000);
            using var expected=Imaging.Crop(fixture,new D.Rectangle(0,0,800,890));
            check(completed!=null&&Imaging.Png(completed).SequenceEqual(Imaging.Png(expected)),"seeded wheel capture preserves every original top pixel and includes the first scroll exactly once");
            check(window.ImagesReleased&&window.SelectionFrameReleased&&!window.ObserverAttached,"seeded wheel capture releases its own images, selection frame and hooks after completion");
        } finally {window.Close();completed?.Dispose();}
    }

    private static void RunContinuousExplorerSession(Action<bool,string> check)
    {
        const int width=800,viewport=650,header=64,footer=30,step=56,steps=16;
        using var document=ExplorerDocument(width,2200);
        D.Bitmap Frame(int offset,int height=viewport)=>ExplorerFrame(document,offset,height,header,footer);
        using(var first=Frame(0))using(var next=Frame(step*4)) {
            var match=Stitcher.Match(first,next);
            check(match.State=="append"&&match.Shift==step*4,"Explorer-style repeated file rows with small unique names retain their exact scroll displacement");
        }
        int offset=0;D.Bitmap? completed=null;var area=new D.Rectangle(80,100,width,viewport);
        var sample=new MouseWheelSample(new D.Point(100,150),-120,1);
        var window=new ScrollWindow(area,b=>completed=new D.Bitmap(b),_=>Frame(offset));
        try {
            window.Show();PumpUntil(()=>window.ObserverAttached,2500);
            check(window.ObserverAttached&&window.SelectionFrameVisible,"Explorer-style continuous capture starts with its hook and visible blue selection frame");
            for(int i=1;i<=steps;i++) {
                offset=i*step;window.ObserveWheel(sample with {NativeTime=(uint)i});
                PumpFor(i==steps?20:40);
            }
            check(window.FrameCount>1&&window.SelectionFrameVisible,"continuous scrolling appends overlapping intermediate frames before the final wheel quiet interval while retaining the blue frame");
            Button(window,"完成").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));PumpUntil(()=>!window.IsVisible,7000);
            using var expected=Frame(0,viewport+step*steps);
            check(completed!=null&&Imaging.Png(completed).SequenceEqual(Imaging.Png(expected)),"fast continuous Explorer-style scrolling preserves every file row with one fixed header and footer and flushes the final pending frame");
            check(window.FrameCount>2&&window.ImagesReleased&&window.SelectionFrameReleased&&!window.ObserverAttached&&!window.SamplingActive,"continuous capture completion releases all images, hooks, timer and selection frame after more than one appended frame");
        } finally {window.Close();completed?.Dispose();}
    }

    private static D.Bitmap ExplorerDocument(int width,int height)
    {
        var bitmap=new D.Bitmap(width,height);using var g=D.Graphics.FromImage(bitmap);g.Clear(D.Color.White);
        using var name=new D.Font("Segoe UI",14,D.GraphicsUnit.Pixel);using var metadata=new D.Font("Segoe UI",12,D.GraphicsUnit.Pixel);
        using var line=new D.Pen(D.Color.FromArgb(235,238,242));using var icon=new D.SolidBrush(D.Color.FromArgb(92,146,207));
        for(int top=0,n=0;top<height;top+=28,n++) {
            g.FillRectangle(icon,10,top+6,14,16);
            g.DrawString($"Screenshot documentation export {n:0000}.txt",name,D.Brushes.Black,34,top+4);
            g.DrawString($"2026-10-14 09:{n%60:00}",metadata,D.Brushes.DimGray,470,top+5);
            g.DrawString("Text document",metadata,D.Brushes.DimGray,610,top+5);
            g.DrawString($"{n*731+17:000000} KB",metadata,D.Brushes.DimGray,724,top+5);
            g.DrawLine(line,0,top+27,width,top+27);
        }
        return bitmap;
    }

    private static D.Bitmap ExplorerFrame(D.Bitmap document,int offset,int height,int header,int footer)
    {
        var bitmap=new D.Bitmap(document.Width,height);using var g=D.Graphics.FromImage(bitmap);g.Clear(D.Color.White);
        int content=height-header-footer;
        g.DrawImage(document,new D.Rectangle(0,header,document.Width,content),new D.Rectangle(0,offset,document.Width,content),D.GraphicsUnit.Pixel);
        using var font=new D.Font("Segoe UI",13,D.GraphicsUnit.Pixel);using var panel=new D.SolidBrush(D.Color.FromArgb(246,248,251));using var line=new D.Pen(D.Color.FromArgb(212,217,225));
        g.FillRectangle(panel,0,0,document.Width,header);g.DrawString("Documents > Screenshot exports",font,D.Brushes.Black,14,9);
        g.DrawString("Name",font,D.Brushes.DimGray,34,40);g.DrawString("Date modified",font,D.Brushes.DimGray,470,40);g.DrawString("Type",font,D.Brushes.DimGray,610,40);g.DrawString("Size",font,D.Brushes.DimGray,724,40);
        g.DrawLine(line,0,header-1,document.Width,header-1);g.FillRectangle(panel,0,height-footer,document.Width,footer);g.DrawLine(line,0,height-footer,document.Width,height-footer);
        g.DrawString("79 items",font,D.Brushes.DimGray,14,height-footer+7);return bitmap;
    }
    private static Button Button(ScrollWindow window,string name)=>Descendants<Button>((DependencyObject)window.Content).Single(b=>AutomationProperties.GetName(b)==name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is T value)yield return value;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
    private static void PumpUntil(Func<bool> condition,int timeout)
    {
        var elapsed=Stopwatch.StartNew();while(!condition()&&elapsed.ElapsedMilliseconds<timeout)PumpFor(20);
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=> {timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
