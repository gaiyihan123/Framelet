using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

// Owned pixels and real dispatcher timers exercise release under backpressure,
// missing overlap, and smooth motion. There are no windows or native input hooks.
internal static class ReleaseCaptureAcceptance
{
    internal static List<string> Run()
    {
        var lines=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        RunUndispatchedBacklog(Check);
        RunInFlightRelease(Check);
        RunDirectionalRecovery(Check);
        RunSmoothMotion(Check);
        return lines;
    }

    private sealed class Source : IDisposable
    {
        internal readonly D.Rectangle Viewport=new(80,100,800,430);
        internal readonly D.Bitmap Document=SelfTests.Fixture(800,2400);
        internal readonly List<int> Dispatches=new();
        internal readonly List<int> CapturedOffsets=new();
        private readonly DispatcherTimer motion=new() {Interval=TimeSpan.FromMilliseconds(50)};
        private double destination;
        internal int Offset;
        internal bool Blank,Smooth;
        internal bool Moving=>motion.IsEnabled;
        internal Source()
        {
            motion.Tick+=(_,_)=> {
                int target=(int)Math.Round(destination);
                if(Offset<target)Offset=Math.Min(target,Offset+40);
                else if(Offset>target)Offset=Math.Max(target,Offset-40);
                if(Offset==target)motion.Stop();
            };
        }
        internal D.Bitmap Initial()=>Imaging.Crop(Document,new D.Rectangle(0,0,Viewport.Width,Viewport.Height));
        internal D.Bitmap Capture(D.Rectangle area)
        {
            if(area!=Viewport)throw new InvalidOperationException("Release acceptance changed the user's frozen viewport.");
            CapturedOffsets.Add(Offset);
            if(!Blank)return Imaging.Crop(Document,new D.Rectangle(0,Offset,area.Width,area.Height));
            var empty=new D.Bitmap(area.Width,area.Height);using var graphics=D.Graphics.FromImage(empty);graphics.Clear(D.Color.White);return empty;
        }
        internal bool Wheel(int delta)
        {
            Dispatches.Add(delta);
            if(Smooth) {
                // A single logical -120 input scrolls 520px in total. Smaller
                // production delivery fragments retain the same total motion.
                destination=Math.Clamp(destination-delta*520d/120,0,Document.Height-Viewport.Height);
                motion.Start();
            }else Offset=Math.Clamp(Offset-delta,0,Document.Height-Viewport.Height);
            return true;
        }
        internal bool Auto()=>throw new InvalidOperationException("Manual release acceptance must not auto-scroll.");
        public void Dispose(){motion.Stop();Document.Dispose();}
    }

    private static DragCaptureController Controller(Source source)
    {
        using var initial=source.Initial();
        return new DragCaptureController(source.Viewport,initial,source.Capture,source.Auto,()=>false,source.Wheel);
    }
    private static void RunUndispatchedBacklog(Action<bool,string> check)
    {
        using var source=new Source();using var controller=Controller(source);
        check(controller.FreezeSelection(source.Viewport)&&controller.QueueWheel(-120*120),"a fixed capture accepts a rapid 120-notch backlog before its dispatcher has sent any wheel");
        check(source.Dispatches.Count==0&&controller.PendingWheelCount>=100,"the release backlog contains over one hundred genuinely undispatched steps");
        using var result=FinishWithin(controller,2500);
        using var expected=source.Initial();
        check(source.Dispatches.Count==0&&controller.ScrollOffset==0&&Stitcher.SamePixels(result,expected),"release returns the exact confirmed original frame without sending any previously undispatched wheel");
        check(!string.IsNullOrWhiteSpace(controller.CompletionWarning),"discarding an undispatched release backlog explicitly warns that the requested continuation was not captured");
        AssertReleased(controller,source,0,check,"release with an undispatched backlog");
    }
    private static void RunInFlightRelease(Action<bool,string> check)
    {
        using var source=new Source();using var controller=Controller(source);
        check(controller.FreezeSelection(source.Viewport)&&controller.QueueWheel(-120),"an in-flight release test starts one accepted manual step");
        PumpUntil(()=>source.Dispatches.Count>0&&controller.SamplingActive,3000,"the first wheel was never dispatched");
        int sent=source.Dispatches.Count,actualOffset=source.Offset;
        check(sent==1&&actualOffset>0&&controller.ScrollOffset==0,"the source moved once while its first confirmation is still in flight");
        check(controller.QueueWheel(-120*110)&&controller.PendingWheelCount>=100,"over one hundred later steps can accumulate behind the single in-flight observation");
        using var result=FinishWithin(controller,2500);
        using var expected=Imaging.Crop(source.Document,new D.Rectangle(0,0,source.Viewport.Width,source.Viewport.Height+actualOffset));
        check(source.Dispatches.Count==sent&&source.Offset==actualOffset&&controller.ScrollOffset==actualOffset&&Stitcher.SamePixels(result,expected),"release confirms the already dispatched motion and its last pixels without draining any later input");
        check(!string.IsNullOrWhiteSpace(controller.CompletionWarning),"in-flight release reports the abandoned later queue while preserving its confirmed prefix");
        AssertReleased(controller,source,sent,check,"release behind a single in-flight wheel");
    }
    private static void RunDirectionalRecovery(Action<bool,string> check)
    {
        using var source=new Source {Blank=true};using var controller=Controller(source);
        check(controller.FreezeSelection(source.Viewport)&&controller.QueueWheel(-120),"a directional recovery starts with one wheel followed by a blank source redraw");
        PumpUntil(()=>controller.NeedsOverlap&&!controller.SamplingActive,9000,"blank content did not enter a recoverable overlap pause");
        check(controller.Error==null&&controller.ScrollOffset==0&&controller.FrameCount==1&&source.Dispatches.Count==1,"a missing overlap preserves the verified anchor after only the original source dispatch");
        int pending=controller.PendingWheelCount,dispatches=source.Dispatches.Count;
        _=controller.QueueWheel(-120);
        check(controller.PendingWheelCount==pending,"a new downward request cannot enter a recovery queue while the prior overlap is missing");
        PumpFor(120);
        check(source.Dispatches.Count==dispatches&&controller.NeedsOverlap,"a downward gesture cannot move the source farther away from the missing anchor");
        source.Blank=false;
        check(controller.QueueWheel(120),"an upward gesture remains available to find the verified anchor during the same overlap pause");
        PumpUntil(()=>!controller.NeedsOverlap&&controller.PendingWheelCount==0&&!controller.SamplingActive,7000,"upward recovery never restored the anchor");
        check(source.Offset==0&&source.Dispatches.Count==dispatches+1&&source.Dispatches[^1]>0&&controller.ScrollOffset==0&&!controller.Rewound&&controller.Error==null,"upward recovery reaches the original document pixels without fabricating appended rows");
        using var result=FinishWithin(controller,2500);using var expected=source.Initial();
        check(Stitcher.SamePixels(result,expected)&&controller.CompletionWarning==null,"a fully restored overlap can finish normally without a stale partial-capture warning");
        AssertReleased(controller,source,dispatches+1,check,"a recovered overlap release");
    }
    private static void RunSmoothMotion(Action<bool,string> check)
    {
        using var source=new Source {Smooth=true};using var controller=Controller(source);
        const int finalOffset=520;
        using(var first=source.Initial())using(var final=Imaging.Crop(source.Document,new D.Rectangle(0,finalOffset,source.Viewport.Width,source.Viewport.Height)))
            check(finalOffset>source.Viewport.Height*.8&&Stitcher.Match(first,final).State!="append","the smooth source's single logical wheel exceeds eighty percent of the selected height and its final frame alone cannot be appended");
        check(controller.FreezeSelection(source.Viewport)&&controller.QueueWheel(-120),"one logical wheel begins real timer-driven smooth source motion rather than jumping directly to a test frame");
        PumpUntil(()=>source.Offset==finalOffset&&!source.Moving&&controller.ScrollOffset==finalOffset&&controller.PendingWheelCount==0&&!controller.SamplingActive,10000,"intermediate smooth-motion observations were lost before the final nonoverlapping frame");
        check(source.CapturedOffsets.Any(offset=>offset>0&&offset<finalOffset)&&controller.FrameCount>=3&&!controller.NeedsOverlap&&controller.Error==null,"continuous capture accepts intermediate overlapping document frames before the final position settles");
        using var result=FinishWithin(controller,2500);
        using var expected=Imaging.Crop(source.Document,new D.Rectangle(0,0,source.Viewport.Width,source.Viewport.Height+finalOffset));
        check(result.Height==950&&Stitcher.SamePixels(result,expected)&&controller.CompletionWarning==null,"the entire 430+520px smooth-motion prefix is pixel-exact with no gap, duplicate section, or partial warning");
        AssertReleased(controller,source,source.Dispatches.Count,check,"a smooth-motion release");
    }
    private static D.Bitmap FinishWithin(DragCaptureController controller,int milliseconds)
    {
        var clock=Stopwatch.StartNew();Task<D.Bitmap> request=controller.Finish();
        // About two seconds, with half a second for dispatcher and bitmap work.
        PumpUntil(()=>request.IsCompleted,Math.Max(1,milliseconds-(int)clock.ElapsedMilliseconds),"release exceeded its bounded completion budget");
        if(clock.ElapsedMilliseconds>milliseconds)throw new TimeoutException("Release exceeded its bounded completion budget.");
        return request.GetAwaiter().GetResult();
    }
    private static void AssertReleased(DragCaptureController controller,Source source,int expectedDispatches,Action<bool,string> check,string label)
    {
        int captures=source.CapturedOffsets.Count;PumpFor(150);
        check(controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&controller.PendingWheelCount==0&&!controller.QueueWheel(-120)&&source.Dispatches.Count==expectedDispatches&&source.CapturedOffsets.Count==captures,
            label+" releases owned images, clears input, stops all timers, and cannot dispatch or sample late work");
    }
    private static void PumpUntil(Func<bool> condition,int milliseconds,string reason)
    {
        var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<milliseconds)PumpFor(15);
        if(!condition())throw new TimeoutException("Release acceptance: "+reason);
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
