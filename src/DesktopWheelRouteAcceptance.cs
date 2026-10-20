using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using D=System.Drawing;
using W=System.Windows;
using Controls=System.Windows.Controls;
using Forms=System.Windows.Forms;
using Interop=System.Windows.Interop;
using Media=System.Windows.Media;

namespace QingCapture;

// This source is a real native RichEdit. Its only custom wheel behavior is an
// occlusion guard; accepted messages go to the control's default WndProc.
internal static class DesktopWheelRouteAcceptance
{
    private const string OverlayTitle="Framelet Desktop Wheel Route Probe 20261020";
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window,bool enabled);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);

    internal static List<string> Run(string folder,bool waitForPhysical=false)
    {
        Directory.CreateDirectory(folder);
        var lines=new List<string>();
        void Check(bool value,string label){if(!value)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        var app=W.Application.Current as App??new App();app.ShutdownMode=W.ShutdownMode.OnExplicitShutdown;Ui.Resources(app);
        using var text=new GuardedRichTextBox {
            BorderStyle=Forms.BorderStyle.None,ReadOnly=true,WordWrap=false,ScrollBars=Forms.RichTextBoxScrollBars.Vertical,
            BackColor=D.Color.FromArgb(38,42,50),ForeColor=D.Color.FromArgb(241,243,248),Font=new D.Font("Consolas",13,D.FontStyle.Regular,D.GraphicsUnit.Point),
            Text=string.Join("\n",Enumerable.Range(0,200).Select(n=>$"Line {n:0000} | source token {n*7919+37:0000000} | keep order {n*6151+17:0000000}"))
        };
        using var host=new WindowsFormsHost {Child=text};
        var source=new W.Window {
            Title="Framelet native RichEdit default-wheel source 20261020",Content=host,WindowStyle=W.WindowStyle.None,ResizeMode=W.ResizeMode.NoResize,
            ShowInTaskbar=false,Topmost=true,Background=Media.Brushes.Black,Width=800,Height=700
        };
        CaptureOverlay? overlay=null;DragCaptureController? controller=null;D.Bitmap? result=null;
        DispatcherTimer? begin=null;Exception? error=null;bool completed=false;
        var trace=new List<string>();
        try {
            Native.GetCursorPos(out var cursor);var monitor=Forms.Screen.FromPoint(new D.Point(cursor.X,cursor.Y)).WorkingArea;
            Check(monitor.Width>=800&&monitor.Height>=700,"the native desktop probe fits wholly inside the monitor work area");
            int left=monitor.Left+Math.Min(24,monitor.Width-800),top=monitor.Top+Math.Min(24,monitor.Height-700);
            source.Show();var sourceHandle=new Interop.WindowInteropHelper(source).EnsureHandle();
            Native.SetWindowPos(sourceHandle,new IntPtr(-1),left,top,800,700,0x40);source.UpdateLayout();
            text.SourceRoot=sourceHandle;text.Focus();PumpFor(200);
            Check(text.IsHandleCreated&&Native.GetAncestor(text.Handle,2)==sourceHandle,"a real native RichEdit child belongs to the self-owned WPF source window");
            Check(text.FirstVisibleLine==0,"the independently numbered source starts at its real first visible line");
            var desktop=Forms.SystemInformation.VirtualScreen;using var snapshot=Imaging.Capture(desktop);
            overlay=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Scroll,_=>{}) {Title=OverlayTitle,ShowInTaskbar=waitForPhysical};
            begin=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
            begin.Tick+=(_,_)=> {
                begin.Stop();
                try {
                    var handle=new Interop.WindowInteropHelper(overlay).Handle;
                    // ShowDialog disables other windows on this UI thread. Only
                    // this fixture's source is re-enabled, so default native
                    // wheel behavior is compared on an enabled real control.
                    EnableWindow(sourceHandle,true);EnableWindow(text.Handle,true);
                    Check(IsWindowEnabled(sourceHandle)&&IsWindowEnabled(text.Handle),"the self-owned source and its native RichEdit remain enabled during the modal overlay test");
                    Native.SetWindowPos(sourceHandle,handle,0,0,0,0,0x13);
                    W.Point Local(int x,int y)=>overlay.PointFromScreen(new W.Point(x+.25,y+.25));
                    var selected=new D.Rectangle(left+100,top+70,400,430);
                    overlay.BeginPointer(Local(selected.Left,selected.Top));
                    overlay.MovePointer(Local(selected.Right,selected.Top+329));
                    overlay.MovePointer(Local(selected.Right,selected.Bottom-1));
                    controller=overlay.DragCapture;
                    Check(controller!=null&&overlay.Selection==selected&&overlay.IsMouseCaptured,"a real loaded desktop overlay locks width with two drag legs and retains native mouse capture");
                    Check(controller!.FreezeSelection(selected)&&controller.Viewport==selected&&!controller.AutoScrolling&&controller.PendingWheelCount==0,"the original 400 by 430 rectangle freezes without extending or dispatching a wheel");
                    PumpFor(120);
                    var point=new Native.Point {X=selected.Left+selected.Width/2,Y=selected.Top+selected.Height/2};
                    var target=Native.WindowBelow(point,handle,selected);
                    Check(target==text.Handle&&Native.WindowFromPoint(point)==handle,"production lookup finds the native RichEdit while the visible overlay remains the normal hit-test target");
                    int first=text.FirstVisibleLine,dropped=text.Dropped,passed=text.Passed;
                    var sample=new MouseWheelSample(new D.Point(point.X,point.Y),-120,unchecked((uint)Environment.TickCount));
                    Check(Native.ForwardWheel(target,sample,activate:false),"the old direct PostMessage route reports native delivery to the real source");
                    PumpFor(550);
                    trace.Add($"old: received={text.Received}, dropped={text.Dropped}, passed={text.Passed}, firstLine={text.FirstVisibleLine}");
                    Check(text.Dropped==dropped+1&&text.Passed==passed&&text.FirstVisibleLine==first,"negative control: an overlay-blocked wheel fails the source hit guard and does not move the real RichEdit");
                    Check(overlay.HandleDragWheel(sample),"the same session accepts a manual wheel through the production serialized queue");
                    PumpUntil(()=>text.FirstVisibleLine>first&&controller.ScrollOffset>0&&controller.PendingWheelCount==0&&!controller.SamplingActive,20000,"The production native route did not move and capture the real RichEdit default scrollbar.");
                    trace.Add($"new: received={text.Received}, dropped={text.Dropped}, passed={text.Passed}, firstLine={text.FirstVisibleLine}, offset={controller.ScrollOffset}");
                    Check(text.Passed==passed+1&&text.FirstVisibleLine>first&&controller.Error==null&&!controller.NeedsOverlap,"the new route passes exactly one wheel to default RichEdit processing and appends its actual scrolling pixels");
                    Check(overlay.Selection==selected&&controller.Viewport==selected&&controller.VisibleSelection==selected&&overlay.IsMouseCaptured,"default source scrolling preserves every selected edge and the overlay's native capture");
                    if(waitForPhysical) {
                        var canvas=(Controls.Canvas)overlay.Content;
                        var local=overlay.PointFromScreen(new W.Point(point.X,point.Y));
                        var marker=new Controls.TextBlock {
                            Text="滚轮输入测试点",FontSize=18,Foreground=Media.Brushes.White,Background=Media.Brushes.DarkSlateBlue,
                            Padding=new W.Thickness(8),IsHitTestVisible=false
                        };
                        AutomationProperties.SetName(marker,"滚轮输入测试点");canvas.Children.Add(marker);
                        Controls.Canvas.SetLeft(marker,local.X-75);Controls.Canvas.SetTop(marker,local.Y-18);overlay.UpdateLayout();
                        int beforeNative=overlay.NativeWheelCount,beforeLine=text.FirstVisibleLine,beforePassed=text.Passed,beforeOffset=controller.ScrollOffset;
                        WriteJson(folder,"phase.json",new {phase="await-physical-wheel",overlayTitle=OverlayTitle,overlayHandle=handle.ToInt64(),marker="滚轮输入测试点",localPoint=new {x=local.X,y=local.Y},physicalPoint=new {x=point.X,y=point.Y},selection=new {x=selected.X,y=selected.Y,width=selected.Width,height=selected.Height},beforeNative,beforeLine,beforePassed,beforeOffset});
                        PumpUntil(()=>overlay.NativeWheelCount>beforeNative&&text.FirstVisibleLine>beforeLine&&controller.ScrollOffset>beforeOffset&&controller.PendingWheelCount==0&&!controller.SamplingActive,240000,"No external native wheel completed the desktop overlay route within four minutes.");
                        int nativeEvents=overlay.NativeWheelCount-beforeNative,sourceEvents=text.Passed-beforePassed;
                        trace.Add($"physical: nativeEvents={nativeEvents}, sourceEvents={sourceEvents}, firstLine={text.FirstVisibleLine}, offset={controller.ScrollOffset}");
                        Check(nativeEvents>0&&sourceEvents==nativeEvents,"external OS wheel events enter the native overlay router and each reaches the real source exactly once without double dispatch");
                        Check(text.FirstVisibleLine>beforeLine&&controller.ScrollOffset>beforeOffset&&overlay.Selection==selected&&overlay.IsMouseCaptured,"external native wheel input scrolls and extends the image while retaining the user's original frame and capture");
                        canvas.Children.Remove(marker);
                    }
                    overlay.EndPointer(Local(selected.Right,selected.Bottom-1));
                    PumpUntil(()=>!overlay.IsVisible,20000,"The desktop default-wheel capture did not finish.");
                    result=overlay.TakeDragResult();
                    Check(result!=null&&result.Width==selected.Width&&result.Height>selected.Height&&controller.Error==null&&controller.CompletionWarning==null,"default RichEdit scrolling completes as a longer image without a partial-capture warning");
                    Imaging.Save(result!,Path.Combine(folder,"desktop-wheel-route-result.png"));
                    Check(controller.ImagesReleased&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!overlay.IsMouseCaptured&&overlay.WheelHookReleased,"completion releases the source frames, queue, timer, mouse capture and native wheel hook");
                    WriteJson(folder,"phase.json",new {phase="completed",firstLine=text.FirstVisibleLine,text.Passed,text.Dropped,nativeWheelCount=overlay.NativeWheelCount,offset=controller.ScrollOffset,result=new {width=result!.Width,height=result.Height},hookReleased=overlay.WheelHookReleased});
                }catch(Exception ex){error=ex;WriteJson(folder,"phase.json",new {phase="failed",error=ex.ToString(),text.Received,text.Passed,text.Dropped,firstLine=text.FirstVisibleLine,offset=controller?.ScrollOffset});}
                finally{if(overlay.IsVisible)overlay.Close();completed=true;}
            };
            begin.Start();overlay.ShowDialog();
            if(!completed)throw new InvalidOperationException("The desktop probe overlay closed before its checks completed.");
            if(error!=null)throw new InvalidOperationException("Native desktop default-wheel acceptance failed",error);
            RunCancellation(Check,text,sourceHandle,new D.Rectangle(left+100,top+70,400,430),desktop,folder,waitForPhysical);
            RunBacklogRelease(Check,text,sourceHandle,new D.Rectangle(left+100,top+70,400,430),desktop,folder,waitForPhysical);
            return lines;
        }finally {
            begin?.Stop();if(overlay?.IsVisible==true)overlay.Close();controller?.Dispose();source.Close();result?.Dispose();
            File.WriteAllLines(Path.Combine(folder,"desktop-wheel-route-checks.txt"),lines);File.WriteAllLines(Path.Combine(folder,"desktop-wheel-route-trace.txt"),trace);
        }
    }

    private static void RunCancellation(Action<bool,string> check,GuardedRichTextBox text,IntPtr sourceHandle,D.Rectangle selected,D.Rectangle desktop,string folder,bool showInTaskbar)
    {
        using var snapshot=Imaging.Capture(desktop);
        var overlay=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Scroll,_=>{}) {Title=OverlayTitle+" cancellation",ShowInTaskbar=showInTaskbar};
        DragCaptureController? controller=null;DispatcherTimer? begin=null;Exception? error=null;bool completed=false;
        try {
            begin=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
            begin.Tick+=(_,_)=> {
                begin.Stop();
                try {
                    EnableWindow(sourceHandle,true);EnableWindow(text.Handle,true);
                    check(IsWindowEnabled(sourceHandle)&&IsWindowEnabled(text.Handle),"the same self-owned native source remains enabled for the second cancellation session");
                    var handle=new Interop.WindowInteropHelper(overlay).Handle;
                    Native.SetWindowPos(sourceHandle,handle,0,0,0,0,0x13);
                    W.Point Local(int x,int y)=>overlay.PointFromScreen(new W.Point(x+.25,y+.25));
                    overlay.BeginPointer(Local(selected.Left,selected.Top));
                    overlay.MovePointer(Local(selected.Right,selected.Top+329));
                    overlay.MovePointer(Local(selected.Right,selected.Bottom-1));
                    controller=overlay.DragCapture;
                    check(controller!=null&&!overlay.WheelHookReleased&&overlay.IsMouseCaptured&&overlay.Selection==selected,"a second loaded overlay installs its native wheel hook and retains capture over the same real source");
                    check(controller!.FreezeSelection(selected)&&!controller.AutoScrolling&&controller.PendingWheelCount==0,"the cancellation session freezes the same rectangle without starting automatic scrolling");
                    int received=text.Received,passed=text.Passed,firstLine=text.FirstVisibleLine;
                    var sample=new MouseWheelSample(new D.Point(selected.Left+selected.Width/2,selected.Top+selected.Height/2),-120,unchecked((uint)Environment.TickCount));
                    check(overlay.HandleDragWheel(sample)&&controller.PendingWheelCount==1&&controller.SamplingActive&&controller.CaptureCount==0,"one manual wheel is queued while its first dispatcher sampling callback has not run");
                    // No pump occurs between queuing and Close: cancellation
                    // must remove this unsent request, not drain it to the source.
                    overlay.Close();
                    check(overlay.WheelHookReleased&&!overlay.IsMouseCaptured&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!controller.RangeProbeActive&&controller.ImagesReleased,"closing before dispatch releases the native hook, capture, queue, both timers and all owned frames");
                    PumpFor(350);
                    check(text.Received==received&&text.Passed==passed&&text.FirstVisibleLine==firstLine&&controller.CaptureCount==0&&overlay.TakeDragResult()==null,"the cancelled second session sends no delayed source wheel, performs no late capture and publishes no screenshot");
                    WriteJson(folder,"desktop-wheel-route-cancel.json",new {phase="cancelled-cleanly",received=text.Received,text.Passed,firstLine=text.FirstVisibleLine,hookReleased=overlay.WheelHookReleased,controller.ImagesReleased,controller.PendingWheelCount,controller.CaptureCount});
                }catch(Exception ex){error=ex;}
                finally{if(overlay.IsVisible)overlay.Close();completed=true;}
            };
            begin.Start();overlay.ShowDialog();
            if(!completed)throw new InvalidOperationException("The cancellation overlay closed before its checks completed.");
            if(error!=null)throw new InvalidOperationException("Native desktop wheel cancellation acceptance failed",error);
        }finally{begin?.Stop();if(overlay.IsVisible)overlay.Close();controller?.Dispose();}
    }

    private static void RunBacklogRelease(Action<bool,string> check,GuardedRichTextBox text,IntPtr sourceHandle,D.Rectangle selected,D.Rectangle desktop,string folder,bool showInTaskbar)
    {
        using var snapshot=Imaging.Capture(desktop);
        var overlay=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Scroll,_=>{}) {Title=OverlayTitle+" backlog release",ShowInTaskbar=showInTaskbar};
        DragCaptureController? controller=null;D.Bitmap? result=null;DispatcherTimer? begin=null;Exception? error=null;bool completed=false;
        try {
            begin=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
            begin.Tick+=(_,_)=> {
                begin.Stop();
                try {
                    EnableWindow(sourceHandle,true);EnableWindow(text.Handle,true);
                    check(IsWindowEnabled(sourceHandle)&&IsWindowEnabled(text.Handle),"the owned default-wheel source remains enabled for the loaded-overlay release-backlog session");
                    var handle=new Interop.WindowInteropHelper(overlay).Handle;
                    Native.SetWindowPos(sourceHandle,handle,0,0,0,0,0x13);
                    W.Point Local(int x,int y)=>overlay.PointFromScreen(new W.Point(x+.25,y+.25));
                    overlay.BeginPointer(Local(selected.Left,selected.Top));
                    overlay.MovePointer(Local(selected.Right,selected.Top+329));
                    overlay.MovePointer(Local(selected.Right,selected.Bottom-1));
                    controller=overlay.DragCapture;
                    check(controller!=null&&controller.FreezeSelection(selected)&&overlay.Selection==selected&&overlay.IsMouseCaptured&&!controller.AutoScrolling,
                        "a loaded desktop overlay owns capture over the user's exact fixed rectangle before release under backpressure");
                    int received=text.Received,passed=text.Passed,firstLine=text.FirstVisibleLine;
                    var sample=new MouseWheelSample(new D.Point(selected.Left+selected.Width/2,selected.Top+selected.Height/2),-120*120,unchecked((uint)Environment.TickCount));
                    check(overlay.HandleDragWheel(sample)&&controller!.PendingWheelCount>=100&&controller.CaptureCount==0&&text.Received==received,
                        "over one hundred accepted manual steps accumulate before the real native source receives any of them");
                    // No dispatcher pump occurs between enqueue and button-up.
                    // EndPointer must close this real modal overlay promptly,
                    // rather than drain its unsent request into the source.
                    var elapsed=Stopwatch.StartNew();
                    overlay.EndPointer(Local(selected.Right,selected.Bottom-1));
                    PumpUntil(()=>!overlay.IsVisible,2500,"Button-up with a native-source backlog left the capture overlay open beyond 2.5 seconds.");
                    result=overlay.TakeDragResult();
                    check(elapsed.ElapsedMilliseconds<=2500&&overlay.DialogResult==true&&result!=null&&result.Size==selected.Size&&overlay.Selection==selected&&controller!.ScrollOffset==0,
                        "button-up closes the loaded backlog overlay within 2.5 seconds and returns only its original selected rectangle");
                    check(text.Received==received&&text.Passed==passed&&text.FirstVisibleLine==firstLine&&!string.IsNullOrWhiteSpace(controller!.CompletionWarning),
                        "release sends no delayed native wheel or source motion and explicitly warns that its unsent continuation was cancelled");
                    check(controller!.ImagesReleased&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!controller.RangeProbeActive&&!overlay.IsMouseCaptured&&overlay.WheelHookReleased,
                        "bounded backlog release frees all frame ownership, timers, queue, mouse capture and native hook");
                    int captures=controller.CaptureCount;PumpFor(200);
                    check(text.Received==received&&text.Passed==passed&&text.FirstVisibleLine==firstLine&&controller.CaptureCount==captures&&!controller.QueueWheel(-120),
                        "a completed backlog release cannot later scroll the source, sample another frame or accept new input");
                    WriteJson(folder,"desktop-wheel-route-release.json",new {
                        phase="released-backlog-without-scrolling",elapsedMs=elapsed.ElapsedMilliseconds,received=text.Received,text.Passed,
                        firstLine=text.FirstVisibleLine,selection=new {x=selected.X,y=selected.Y,width=selected.Width,height=selected.Height},
                        result=new {width=result!.Width,height=result.Height},controller.CompletionWarning,controller.ImagesReleased,
                        controller.PendingWheelCount,hookReleased=overlay.WheelHookReleased
                    });
                }catch(Exception ex){error=ex;}
                finally{if(overlay.IsVisible)overlay.Close();completed=true;}
            };
            begin.Start();overlay.ShowDialog();
            if(!completed)throw new InvalidOperationException("The backlog-release overlay closed before its checks completed.");
            if(error!=null)throw new InvalidOperationException("Native desktop release-backlog acceptance failed",error);
        }finally{begin?.Stop();if(overlay.IsVisible)overlay.Close();controller?.Dispose();result?.Dispose();}
    }

    private sealed class GuardedRichTextBox : Forms.RichTextBox
    {
        [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
        internal IntPtr SourceRoot;
        internal int Received {get;private set;}
        internal int Dropped {get;private set;}
        internal int Passed {get;private set;}
        internal int FirstVisibleLine=>checked((int)SendMessage(Handle,0xCE,IntPtr.Zero,IntPtr.Zero).ToInt64()); // EM_GETFIRSTVISIBLELINE, read only.
        protected override void WndProc(ref Forms.Message message)
        {
            if(message.Msg==MouseWheelObserver.MouseWheelMessage&&SourceRoot!=IntPtr.Zero) {
                Received++;
                long packed=message.LParam.ToInt64();
                var hit=Native.WindowFromPoint(new Native.Point {X=unchecked((short)packed),Y=unchecked((short)(packed>>16))});
                var root=hit==IntPtr.Zero?IntPtr.Zero:Native.GetAncestor(hit,2);if(root==IntPtr.Zero)root=hit;
                if(root!=SourceRoot){Dropped++;message.Result=IntPtr.Zero;return;}
                Passed++;
            }
            base.WndProc(ref message);
        }
    }
    private static void WriteJson(string folder,string name,object value)
    {
        string path=Path.Combine(folder,name),temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(value,new JsonSerializerOptions {WriteIndented=true}));File.Move(temporary,path,true);
    }
    private static void PumpUntil(Func<bool> condition,int milliseconds,string timeout)
    {
        var elapsed=Stopwatch.StartNew();while(!condition()&&elapsed.ElapsedMilliseconds<milliseconds)PumpFor(25);
        if(!condition())throw new TimeoutException(timeout);
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
