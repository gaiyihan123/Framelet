using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

// Fault-inject the production controller's native-dispatch result while capture
// continues to supply stable, independently cropped document pixels. No windows,
// input, clipboard, or replacement controller implementation are involved.
internal static class DispatchFailureAcceptance
{
    internal static List<string> Run()
    {
        var lines=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        RunFailedCompletion(Check);
        RunRetryAfterFailure(Check);
        return lines;
    }

    private sealed class Source : IDisposable
    {
        internal readonly D.Rectangle Viewport=new(80,100,800,430);
        internal readonly D.Bitmap Document=SelfTests.Fixture(800,1600);
        internal readonly List<int> Dispatches=new();
        internal int Offset,Captures;
        internal bool AcceptWheel;
        internal D.Bitmap Initial()=>Imaging.Crop(Document,new D.Rectangle(0,0,Viewport.Width,Viewport.Height));
        internal D.Bitmap Capture(D.Rectangle area)
        {
            if(area!=Viewport)throw new InvalidOperationException("Dispatch failure changed the user's frozen viewport.");
            Captures++;
            return Imaging.Crop(Document,new D.Rectangle(0,Offset,area.Width,area.Height));
        }
        internal bool Wheel(int delta)
        {
            Dispatches.Add(delta);
            if(!AcceptWheel)return false;
            Offset=Math.Clamp(Offset-delta,0,Document.Height-Viewport.Height);
            return true;
        }
        internal bool Auto()=>throw new InvalidOperationException("A frozen manual dispatch-failure test must not auto-scroll.");
        public void Dispose()=>Document.Dispose();
    }

    private static DragCaptureController Controller(Source source)
    {
        using var first=source.Initial();
        return new DragCaptureController(source.Viewport,first,source.Capture,source.Auto,()=>false,source.Wheel);
    }
    private static void WaitForDispatches(DragCaptureController controller,Source source,int count)
    {
        PumpUntil(()=>source.Dispatches.Count==count&&controller.PendingWheelCount==0&&!controller.SamplingActive,10000);
    }
    private static void RunFailedCompletion(Action<bool,string> check)
    {
        using var source=new Source();using var controller=Controller(source);
        check(controller.FreezeSelection(source.Viewport)&&controller.QueueWheel(-120),"a manual wheel request is accepted before its native-dispatch callback reports failure");
        WaitForDispatches(controller,source,1);
        check(source.Offset==0&&controller.ScrollOffset==0&&controller.FrameCount==1&&source.Captures>=2&&controller.InputWarning!=null&&controller.Error==null&&!controller.NeedsOverlap,
            "a rejected dispatch with unchanged stable pixels reports an input warning without appending rows, inventing an overlap gap, or becoming fatal");
        using var result=Finish(controller);using var expected=source.Initial();
        check(result.Size==source.Viewport.Size&&Stitcher.SamePixels(result,expected),"finishing a rejected dispatch returns only the exact independently cropped original frame");
        check(!string.IsNullOrWhiteSpace(controller.CompletionWarning),"a rejected wheel that never moved remains an explicit incomplete-capture warning after Finish");
        int captures=source.Captures;PumpFor(180);
        check(controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&controller.PendingWheelCount==0&&!controller.QueueWheel(-120)&&source.Dispatches.Count==1&&source.Captures==captures,
            "failed-dispatch completion releases images and timers and cannot dispatch or sample late work");
    }
    private static void RunRetryAfterFailure(Action<bool,string> check)
    {
        using var source=new Source();using var controller=Controller(source);
        check(controller.FreezeSelection(source.Viewport)&&controller.QueueWheel(-120),"a second capture accepts a wheel before an initially unresponsive source");
        WaitForDispatches(controller,source,1);
        check(controller.InputWarning!=null&&controller.Error==null&&source.Offset==0,"an initial rejected retry-source dispatch remains recoverable and visibly warned");
        source.AcceptWheel=true;
        check(controller.QueueWheel(-240),"the same warned session accepts two later successful wheel steps without restarting or enlarging its frame");
        WaitForDispatches(controller,source,3);
        check(source.Dispatches[0]==-120&&source.Dispatches[1]==-120&&source.Dispatches[2]==-120&&source.Offset==240&&controller.ScrollOffset==240&&controller.FrameCount==3&&controller.InputWarning==null&&controller.Error==null&&!controller.NeedsOverlap,
            "real matched motion after dispatch recovery clears the warning and appends exactly the two successful 120px steps");
        check(controller.Viewport==source.Viewport&&controller.VisibleSelection==source.Viewport&&!controller.AutoScrolling,"dispatch recovery preserves all four selected edges and manual input mode");
        using var result=Finish(controller);using var expected=Imaging.Crop(source.Document,new D.Rectangle(0,0,source.Viewport.Width,source.Viewport.Height+240));
        check(result.Height==670&&Stitcher.SamePixels(result,expected)&&controller.CompletionWarning==null&&controller.InputWarning==null,
            "successful retry finishes with the exact 670px document prefix and no stale input or partial-completion warning");
        check(controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&source.Dispatches.Count==3,"successful dispatch recovery releases frame ownership and timers without adding an unrequested wheel");
    }
    private static D.Bitmap Finish(DragCaptureController controller)
    {
        Task<D.Bitmap>? request=null;Exception? failure=null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>{try{request=controller.Finish();}catch(Exception error){failure=error;}}));
        PumpUntil(()=>failure!=null||request?.IsCompleted==true,12000);
        if(failure!=null)throw failure;
        return request!.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> condition,int milliseconds)
    {
        var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<milliseconds)PumpFor(20);
        if(!condition())throw new TimeoutException("Dispatch-failure acceptance did not settle.");
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
