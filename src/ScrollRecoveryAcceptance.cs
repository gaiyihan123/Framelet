using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

// Uses only owned bitmap fixtures and the production controller's dispatcher
// timers. No windows, native wheel messages, clipboard or user input are involved.
internal static class ScrollRecoveryAcceptance
{
    internal static List<string> Run()
    {
        var lines=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        RunResume(Check);
        RunPartialFinish(Check);
        RunCancel(Check);
        RunFullQueueRecovery(Check);
        RunPersistentAnimation(Check);
        return lines;
    }

    private sealed class Source : IDisposable
    {
        internal readonly D.Rectangle Viewport=new(80,100,800,430);
        internal readonly D.Bitmap Document=SelfTests.Fixture(800,1600);
        internal readonly List<int> Dispatches=new();
        internal int Offset,Captures,Automatic;
        internal bool Blank=true;
        internal D.Bitmap Initial()=>Imaging.Crop(Document,new D.Rectangle(0,0,Viewport.Width,Viewport.Height));
        internal D.Bitmap Capture(D.Rectangle area)
        {
            if(area!=Viewport)throw new InvalidOperationException("Recovery changed its frozen observation viewport.");
            Captures++;
            if(!Blank)return Imaging.Crop(Document,new D.Rectangle(0,Offset,area.Width,area.Height));
            var empty=new D.Bitmap(area.Width,area.Height);using var graphics=D.Graphics.FromImage(empty);graphics.Clear(D.Color.White);return empty;
        }
        internal bool Wheel(int delta)
        {
            Dispatches.Add(delta);Offset=Math.Clamp(Offset-delta,0,Document.Height-Viewport.Height);return true;
        }
        internal bool Auto(){Automatic++;throw new InvalidOperationException("A frozen manual recovery must not generate an automatic scroll.");}
        public void Dispose()=>Document.Dispose();
    }

    private static DragCaptureController Controller(Source source,Action<bool,string> check)
    {
        using var initial=source.Initial();
        var controller=new DragCaptureController(source.Viewport,initial,source.Capture,source.Auto,()=>false,source.Wheel);
        try {check(controller.FreezeSelection(source.Viewport),"recovery preserves the user's exact 800x430 manual frame");return controller;}
        catch {controller.Dispose();throw;}
    }
    private static void ReachPause(DragCaptureController controller,Source source,Action<bool,string> check)
    {
        bool sawRecovering=false;
        controller.Changed+=()=>sawRecovering|=controller.Recovering;
        check(controller.QueueWheel(-360),"a three-notch manual request is accepted before the source temporarily renders blank");
        PumpUntil(()=>controller.NeedsOverlap&&!controller.SamplingActive,9000);
        check(sawRecovering&&source.Captures>=4&&!controller.Recovering&&controller.Error==null,"stable blank content is retried in place before a recoverable overlap pause, without a permanent error");
        check(source.Dispatches.Count==1&&source.Dispatches[0]==-120&&source.Offset==120&&controller.PendingWheelCount==2,"a missing overlap pauses after the first dispatch and retains both unsent manual steps");
        using(var last=controller.CopyLastFrame())using(var original=source.Initial())
            check(last!=null&&Stitcher.SamePixels(last,original)&&controller.ScrollOffset==0&&controller.FrameCount==1,"rejected blank observations cannot replace the last accepted anchor or invent captured rows");
        int dispatches=source.Dispatches.Count,captures=source.Captures;PumpFor(300);
        check(source.Dispatches.Count==dispatches&&source.Captures==captures&&!controller.AutoScrolling&&!controller.RangeProbeActive,"an unresolved overlap remains paused without consuming queued intent or flooding the source");
    }
    private static void RunResume(Action<bool,string> check)
    {
        using var source=new Source();using var controller=Controller(source,check);ReachPause(controller,source,check);
        source.Blank=false;
        check(controller.QueueWheel(120),"an upward recovery gesture is accepted while the original downward queue is paused");
        PumpUntil(()=>controller.PendingWheelCount==0&&!controller.SamplingActive&&!controller.NeedsOverlap,12000);
        check(source.Dispatches.Count==4&&source.Dispatches[0]==-120&&source.Dispatches[1]==120&&source.Dispatches[2]==-120&&source.Dispatches[3]==-120,"recovery reaches the source before either preserved ordinary wheel step is resumed");
        check(source.Offset==240&&controller.ScrollOffset==240&&controller.FrameCount==3&&!controller.Rewound&&!controller.Recovering&&controller.Error==null,"returning to the verified anchor resumes both remaining steps with exactly 240 new physical pixels");
        using var result=Finish(controller);using var expected=Imaging.Crop(source.Document,new D.Rectangle(0,0,source.Viewport.Width,source.Viewport.Height+240));
        check(result.Height==670&&Stitcher.SamePixels(result,expected)&&controller.CompletionWarning==null,"successful recovery returns the complete 430+240px document prefix with no missing-content warning");
        check(controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&source.Automatic==0,"successful recovered completion releases both frame images and every controller timer");
    }
    private static void RunPartialFinish(Action<bool,string> check)
    {
        using var source=new Source();using var controller=Controller(source,check);ReachPause(controller,source,check);
        int dispatched=source.Dispatches.Count;
        using var result=Finish(controller);using var expected=source.Initial();
        check(result.Height==430&&Stitcher.SamePixels(result,expected)&&controller.ScrollOffset==0&&controller.FrameCount==1,"finishing with a persistent blank gap returns only the pixel-exact accepted 430px frame");
        string? warning=controller.CompletionWarning;
        check(warning!=null&&warning.Contains("后续",StringComparison.Ordinal)&&warning.Contains("部分",StringComparison.Ordinal)&&warning.Contains("未",StringComparison.Ordinal),"partial completion explicitly identifies unconfirmed following content instead of reporting a complete capture");
        check(controller.Error==null&&controller.PendingWheelCount==0&&controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive,"partial completion clears retained work only after warning and releases controller images and timers");
        int captures=source.Captures;PumpFor(300);
        check(source.Dispatches.Count==dispatched&&source.Captures==captures&&!controller.QueueWheel(-120),"partial completion never dispatches its abandoned queue or captures a late frame");
    }
    private static void RunCancel(Action<bool,string> check)
    {
        using var source=new Source();using var controller=Controller(source,check);ReachPause(controller,source,check);
        int dispatched=source.Dispatches.Count,captures=source.Captures;controller.Dispose();PumpFor(300);
        check(controller.PendingWheelCount==0&&controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&source.Dispatches.Count==dispatched&&source.Captures==captures,"cancelling an overlap pause drops pending work and releases all images and timers without late dispatch");
        bool cancelled=false;try{using var result=Finish(controller);}catch(OperationCanceledException){cancelled=true;}
        check(cancelled&&!controller.QueueWheel(120),"a cancelled overlap recovery cannot restart or publish an image on release");
    }
    private static void RunFullQueueRecovery(Action<bool,string> check)
    {
        using var source=new Source {Blank=false};using var initial=source.Initial();
        using var controller=new DragCaptureController(source.Viewport,initial,source.Capture,()=> {
            source.Automatic++;source.Offset=120;source.Blank=true;return true;
        },()=>false,source.Wheel);
        controller.UpdatePointer(new D.Point(source.Viewport.Left+100,source.Viewport.Bottom-1));
        PumpUntil(()=>source.Automatic==1&&controller.SamplingActive,3000);
        check(controller.QueueWheel(-120*128)&&controller.PendingWheelCount==128&&!controller.QueueWheel(-120),"an in-flight automatic sample permits exactly 128 queued ordinary manual steps without changing the ordinary capacity");
        PumpUntil(()=>controller.NeedsOverlap&&!controller.SamplingActive,9000);
        check(controller.Error==null&&source.Automatic==1&&source.Dispatches.Count==0&&controller.PendingWheelCount==128,"a blank in-flight sample pauses a completely full ordinary queue before dispatching any of its 128 steps");
        check(controller.QueueWheel(120)&&controller.PendingWheelCount==129,"an overlap-recovery upward request has reserved capacity even when all 128 ordinary queue slots are occupied");
        int captures=source.Captures;controller.Dispose();PumpFor(300);
        check(controller.PendingWheelCount==0&&controller.ImagesReleased&&!controller.SamplingActive&&!controller.RangeProbeActive&&source.Automatic==1&&source.Dispatches.Count==0&&source.Captures==captures,"cancelling a full-queue recovery releases its 129 pending steps and all images and timers without a late recovery or ordinary dispatch");
    }
    private static void RunPersistentAnimation(Action<bool,string> check)
    {
        var viewport=new D.Rectangle(80,100,800,430);
        using var document=SelfTests.Fixture(800,1600);
        using var first=Imaging.Crop(document,new D.Rectangle(0,0,viewport.Width,viewport.Height));
        using var second=Imaging.Crop(document,new D.Rectangle(0,100,viewport.Width,viewport.Height));
        check(Stitcher.Match(first,second).State!="unchanged"&&Stitcher.Match(second,first).State!="unchanged","the persistent-animation fixture alternates genuinely different textured frames rather than featureless solid colors");
        int captures=0,dispatches=0;bool sawRecovering=false;
        using var controller=new DragCaptureController(viewport,first,area=> {
            if(area!=viewport)throw new InvalidOperationException("Animation recovery changed its frozen viewport.");
            captures++;return new D.Bitmap(captures%2==0?second:first);
        },()=>throw new InvalidOperationException("A frozen animation recovery must not auto-scroll."),()=>false,_=>{dispatches++;return true;});
        controller.Changed+=()=>sawRecovering|=controller.Recovering;
        check(controller.FreezeSelection(viewport)&&controller.QueueWheel(-120),"a fixed manual capture accepts one wheel request before persistent textured animation");
        PumpUntil(()=>controller.NeedsOverlap&&!controller.SamplingActive,12000);
        check(sawRecovering&&captures>=14&&dispatches==1&&controller.Error==null&&!controller.Recovering,$"exhausting the stable-frame animation attempts enters a recoverable overlap pause without a permanent error or extra scroll dispatch (recoveringSeen={sawRecovering}, captures={captures}, dispatches={dispatches}, error={controller.Error}, recovering={controller.Recovering})");
        using(var last=controller.CopyLastFrame())
            check(last!=null&&Stitcher.SamePixels(last,first)&&controller.FrameCount==1&&controller.ScrollOffset==0,"persistent alternating frames cannot append content or overwrite the accepted anchor");
        int stoppedCaptures=captures;controller.Dispose();PumpFor(300);
        check(controller.ImagesReleased&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!controller.RangeProbeActive&&captures==stoppedCaptures&&dispatches==1,"cancelling persistent-animation recovery releases all frame images and timers with no late capture or dispatch");
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
        if(!condition())throw new TimeoutException("Scroll recovery acceptance did not settle.");
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
