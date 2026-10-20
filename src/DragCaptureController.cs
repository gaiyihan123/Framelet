using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

// A fixed observation viewport measures real page displacement. The user's frame
// and final crop grow independently, so the last short tail stays selectable.
internal sealed class DragCaptureController : IDisposable
{
    private D.Rectangle viewport;
    private D.Rectangle? manualSelection;
    private readonly Func<D.Rectangle,D.Bitmap> capture;
    private readonly Func<bool> scroll;
    private readonly Func<int,bool>? scrollWheel;
    private readonly Queue<int> wheelQueue=new();
    private readonly Queue<int> recoveryWheels=new();
    private readonly Func<bool?>? sourceAtBottom;
    private readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromMilliseconds(130)};
    private readonly DispatcherTimer endProbe=new() {Interval=TimeSpan.FromMilliseconds(1200)};
    private readonly CancellationTokenSource stop=new();
    private D.Bitmap? composite,last;
    private Task work=Task.CompletedTask;
    private Task<D.Bitmap>? finishTask;
    private bool finishing,cancelled,disposed,scrollAttempted,finishBudgetExpired;
    private int pointerY,unchanged,refreshFrom;
    private int finalHeight;
    private long automaticResumeAt,finishDeadline;
    private int abandonedWheels;
    internal event Action? Changed;
    internal D.Rectangle Viewport=>manualSelection??viewport;
    internal D.Rectangle VisibleSelection=>manualSelection??new(viewport.X,viewport.Y,viewport.Width,VisibleHeight);
    private int VisibleHeight=>manualSelection?.Height??(pointerY>=viewport.Bottom-36?viewport.Height:Math.Clamp(pointerY-viewport.Top+1,1,viewport.Height));
    internal int OutputHeight=>composite==null?finalHeight:Math.Min(composite.Height,ScrollOffset+VisibleHeight);
    internal int ScrollOffset {get;private set;}
    internal int FrameCount {get;private set;}=1;
    internal int CaptureCount {get;private set;}
    internal bool AtBottom {get;private set;}
    internal bool ManualWheelMode {get;private set;}
    internal bool Rewound {get;private set;}
    internal int PendingWheelCount=>wheelQueue.Count+recoveryWheels.Count;
    internal bool Recovering {get;private set;}
    internal bool NeedsOverlap {get;private set;}
    internal string? CompletionWarning {get;private set;}
    internal bool WaitingForScroll=>unchanged>=2&&!AtBottom;
    private bool CanAutoScroll=>!ManualWheelMode&&!NeedsOverlap&&!finishing&&!cancelled&&!AtBottom&&!Rewound&&Error==null&&pointerY>=viewport.Bottom-36;
    internal bool AutoScrolling=>CanAutoScroll&&wheelQueue.Count==0&&Environment.TickCount64>=automaticResumeAt;
    internal bool ImagesReleased=>composite==null&&last==null;
    internal bool SamplingActive=>!work.IsCompleted||timer.IsEnabled;
    internal bool RangeProbeActive=>endProbe.IsEnabled;
    internal string? Error {get;private set;}
    internal string? InputWarning {get;private set;}
    internal int DispatchCount {get;private set;}
    internal int RejectedDispatchCount {get;private set;}
    internal int MaximumPendingWheelCount {get;private set;}
    internal int AbandonedWheelCount=>abandonedWheels;
    internal bool FinishBudgetExpired=>finishBudgetExpired;
    internal StitchMatch? LastMatch {get;private set;}
    internal D.Bitmap? CopyLastFrame()=>last==null?null:new D.Bitmap(last);
    internal DragCaptureController(D.Rectangle viewport,D.Bitmap initial,Func<D.Rectangle,D.Bitmap> capture,Func<bool> scroll,Func<bool?>? sourceAtBottom=null,Func<int,bool>? scrollWheel=null)
    {
        if(viewport.Width<30||viewport.Height<240||initial.Size!=viewport.Size)throw new ArgumentException("拖动长截图的可见区域至少需要30×240像素，请从页面较靠上的位置开始。");
        this.viewport=viewport;this.capture=capture;this.scroll=scroll;this.sourceAtBottom=sourceAtBottom;this.scrollWheel=scrollWheel;last=new D.Bitmap(initial);composite=new D.Bitmap(initial);pointerY=viewport.Top;
        timer.Tick+=(_,_)=> {
            timer.Stop();if(!work.IsCompleted)return;
            if(recoveryWheels.Count>0)work=Step(recoveryWheels.Dequeue());
            else if(wheelQueue.Count>0&&!NeedsOverlap)work=Step(wheelQueue.Dequeue());
            else if(AutoScrolling)work=Step();
            else Schedule();
        };
        endProbe.Tick+=(_,_)=> {
            if(finishing||cancelled||!AtBottom||pointerY<viewport.Bottom-36){endProbe.Stop();return;}
            if(ReadSourceEnd()==true)return;
            AtBottom=false;unchanged=0;endProbe.Stop();Schedule();Changed?.Invoke();
        };
    }
    internal void UpdatePointer(D.Point point)
    {
        if(finishing||cancelled)return;
        bool wasAtEdge=pointerY>=viewport.Bottom-36;
        pointerY=Math.Clamp(point.Y,viewport.Top,viewport.Bottom);
        // Moving out and back in is an explicit retry, including after a source
        // reported its old end before loading more content.
        if(pointerY>=viewport.Bottom-36&&(!wasAtEdge||(AtBottom&&ReadSourceEnd()!=true))) {
            AtBottom=false;unchanged=0;timer.Interval=TimeSpan.FromMilliseconds(130);
        }
        Schedule();
        Changed?.Invoke();
    }
    internal bool QueueWheel(int delta)
    {
        if(finishing||cancelled||Error!=null||delta==0||scrollWheel==null)return false;
        // Do not move farther away from a missing anchor. Only an upward
        // recovery gesture may move the source until overlap has been restored.
        if(NeedsOverlap&&delta<0)return false;
        long count=((long)Math.Abs((long)delta)+119)/120;
        // Keep recovery input available even when the suspended normal queue is
        // full. Otherwise no upward request could ever restore its checkpoint.
        if(count>(NeedsOverlap?16-recoveryWheels.Count:128-PendingWheelCount))return false;
        int remaining=delta;
        var destination=NeedsOverlap?recoveryWheels:wheelQueue;
        while(remaining!=0){int step=Math.Clamp(remaining,-120,120);destination.Enqueue(step);remaining-=step;}
        MaximumPendingWheelCount=Math.Max(MaximumPendingWheelCount,PendingWheelCount);
        manualSelection??=viewport;
        ManualWheelMode=true;AtBottom=false;unchanged=0;automaticResumeAt=Environment.TickCount64+800;
        Schedule();Changed?.Invoke();return true;
    }
    internal bool FreezeSelection(D.Rectangle selection)
    {
        if(finishing||cancelled||selection.X!=viewport.X||selection.Y!=viewport.Y||selection.Width!=viewport.Width||selection.Height<240||selection.Height>viewport.Height)return false;
        if(manualSelection.HasValue)return manualSelection.Value==selection;
        manualSelection=selection;ManualWheelMode=true;AtBottom=false;endProbe.Stop();Schedule();Changed?.Invoke();return true;
    }
    private void ApplyManualSelection()
    {
        if(!manualSelection.HasValue||manualSelection.Value==viewport)return;
        var selected=manualSelection.Value;
        // Only the serialized worker may replace image dimensions. Preserve the
        // accepted prefix after any automatic scroll already in flight finishes.
        var selectedLast=Imaging.Crop(last!,new D.Rectangle(0,0,selected.Width,selected.Height));
        D.Bitmap? selectedComposite=null;
        try {selectedComposite=Imaging.Crop(composite!,new D.Rectangle(0,0,selected.Width,selected.Height+ScrollOffset));}
        catch {selectedLast.Dispose();throw;}
        last!.Dispose();composite!.Dispose();last=selectedLast;composite=selectedComposite;viewport=selected;refreshFrom=Math.Min(refreshFrom,selected.Height/2);
    }
    private void Schedule()
    {
        if(finishing||cancelled||Error!=null){timer.Stop();endProbe.Stop();return;}
        double? milliseconds=null;
        if(recoveryWheels.Count>0||(wheelQueue.Count>0&&!NeedsOverlap))milliseconds=1;
        else if(CanAutoScroll) {
            long delay=automaticResumeAt-Environment.TickCount64;
            milliseconds=delay>0?delay:WaitingForScroll?Math.Min(2400,800+(unchanged-2)*400):130;
        }
        if(milliseconds.HasValue) {
            var interval=TimeSpan.FromMilliseconds(milliseconds.Value);
            if(timer.Interval!=interval)timer.Interval=interval;
            timer.Start();
        }else timer.Stop();
        if(AtBottom&&pointerY>=viewport.Bottom-36)endProbe.Start();else endProbe.Stop();
    }
    private async Task Step(int? wheelDelta=null)
    {
        timer.Stop();
        try {
            ApplyManualSelection();
            scrollAttempted=true;
            // A successful dispatch is not evidence that the source scrolled;
            // a rejected dispatch is not evidence that it reached its end.
            bool up=wheelDelta>0;
            DispatchCount++;
            bool delivered=wheelDelta.HasValue?scrollWheel!(wheelDelta.Value):scroll();
            if(!delivered)RejectedDispatchCount++;
            InputWarning=delivered?null:"原窗口未接收滚轮，请保持原框稍候重试；不会扩大选区。";
            if(up)Rewound=true;
            bool moved=await CaptureStableAndAppend(waitForMotion:true,rewinding:up);
            if(cancelled)return;
            if(moved||ReadSourceEnd()==true)InputWarning=null;
            unchanged=moved?0:unchanged+1;
            AtBottom=!NeedsOverlap&&!Rewound&&PendingWheelCount==0&&unchanged>=2&&ReadSourceEnd()==true;
            // Unknown/no progress remains recoverable. Back off instead of
            // permanently stopping or repeatedly flooding a slow source.
            timer.Interval=TimeSpan.FromMilliseconds(WaitingForScroll?Math.Min(2400,800+(unchanged-2)*400):130);
        }catch(OperationCanceledException) when(cancelled) { }
        catch(Exception ex){if(!cancelled)Error=ex.Message;}
        finally {
            if(!cancelled) {
                Changed?.Invoke();
                Schedule();
            }else ReleaseImages();
        }
    }
    private bool? ReadSourceEnd()
    {
        try{return sourceAtBottom?.Invoke();}catch{return null;}
    }
    private async Task<bool> CaptureStableAndAppend(bool waitForMotion=false,bool rewinding=false)
    {
        var token=stop.Token;await Task.Delay(35,token);
        long deadline=Environment.TickCount64+(waitForMotion?700:0),recoveryDeadline=Environment.TickCount64+1800;
        bool moved=false;int stillFrames=0;
        D.Bitmap? candidate=null;StitchMatch? candidateMatch=null;
        try {
        while(true) {
            if(FinishBudgetElapsed())return moved;
            using var stable=rewinding?await StableFrame(token):capture(viewport);
            if(!rewinding){CaptureCount++;if(stable!.Size!=viewport.Size)throw new InvalidOperationException("长截图画面尺寸发生变化。");}
            token.ThrowIfCancellationRequested();
            if(stable==null) {
                if(FinishBudgetElapsed())return moved;
                Recovering=true;Changed?.Invoke();
                if(waitForMotion&&Environment.TickCount64<recoveryDeadline){await Task.Delay(90,token);continue;}
                Recovering=false;NeedsOverlap=true;return moved;
            }
            if(rewinding) {
                if(NeedsOverlap) {
                    var restored=await Task.Run(()=>Stitcher.Match(last!,stable),token);token.ThrowIfCancellationRequested();
                    if(restored.State=="unchanged"){NeedsOverlap=false;Recovering=false;Rewound=false;}
                }
                return false;
            }
            StitchMatch? match=null;
            if(candidate!=null) {
                var continuity=await Task.Run(()=>Stitcher.Match(candidate,stable),token);token.ThrowIfCancellationRequested();
                if(continuity.State is "append" or "unchanged") {
                    await AppendConfirmed(candidate,candidateMatch!,token);moved=true;match=continuity;
                }else {Recovering=true;stillFrames=0;Changed?.Invoke();}
                candidate.Dispose();candidate=null;candidateMatch=null;
            }
            match??=await Task.Run(()=>Stitcher.Match(last!,stable),token);
            LastMatch=match;
            token.ThrowIfCancellationRequested();
            if(Rewound&&match.State=="append") {
                var reverse=await Task.Run(()=>Stitcher.Match(stable,last!),token);token.ThrowIfCancellationRequested();
                // Repeated sections can offer plausible alignments in both
                // directions. A return path must not append either alias.
                if(reverse.State is "append" or "unchanged")return moved;
            }
            if(match.State=="unchanged") {
                Rewound=false;NeedsOverlap=false;Recovering=false;
                stillFrames++;
                // The document can stay still while a floating button fades or
                // disappears. Keep the new real pixels without inventing rows.
                if(stillFrames>=2&&!Stitcher.SamePixels(last!,stable)) {
                    var refreshed=await Task.Run(()=>Stitcher.RefreshTail(composite!,stable,refreshFrom),token);
                    if(token.IsCancellationRequested){refreshed.Dispose();token.ThrowIfCancellationRequested();}
                    composite!.Dispose();composite=refreshed;last!.Dispose();last=new D.Bitmap(stable);
                }
                if(stillFrames>=2&&(moved||Environment.TickCount64>=deadline))return moved;
                if(Environment.TickCount64>=recoveryDeadline){NeedsOverlap=true;Recovering=false;return moved;}
                await Task.Delay(45,token);continue;
            }
            stillFrames=0;
            if(match.State!="append") {
                if(Rewound) {
                    var reverse=await Task.Run(()=>Stitcher.Match(stable,last!),token);token.ThrowIfCancellationRequested();
                    if(reverse.State=="append")return moved;
                    // A distant return path can have no overlap with the last
                    // accepted viewport yet. Keep the accepted image intact.
                    return moved;
                }
                Recovering=true;Changed?.Invoke();
                if(waitForMotion&&Environment.TickCount64<recoveryDeadline){await Task.Delay(45,token);continue;}
                Recovering=false;NeedsOverlap=true;return moved;
            }
            // Preserve every verified intermediate position of smooth scrolling.
            // Require the following observation to remain at or beyond this
            // candidate. A forward/backward animation must not commit its first
            // displaced frame as though the page had permanently scrolled.
            if(Environment.TickCount64>=recoveryDeadline){NeedsOverlap=true;Recovering=false;return moved;}
            candidate=new D.Bitmap(stable);candidateMatch=match;
            await Task.Delay(45,token);
        }
        }finally{candidate?.Dispose();}
    }
    private async Task AppendConfirmed(D.Bitmap frame,StitchMatch match,CancellationToken token)
    {
        var merged=await Task.Run(()=>Stitcher.Append(composite!,frame,match),token);
        if(token.IsCancellationRequested){merged.Dispose();token.ThrowIfCancellationRequested();}
        composite!.Dispose();composite=merged;last!.Dispose();last=new D.Bitmap(frame);refreshFrom=Stitcher.OverlapJoin(frame.Height,match);
        Rewound=false;NeedsOverlap=false;Recovering=false;InputWarning=null;ScrollOffset+=match.Shift;FrameCount++;Changed?.Invoke();
    }
    private async Task<D.Bitmap?> StableFrame(CancellationToken token)
    {
        D.Bitmap? previous=null,next=null;
        try {
            for(int attempt=0;attempt<14;attempt++) {
                if(FinishBudgetElapsed())return null;
                token.ThrowIfCancellationRequested();next=capture(viewport);CaptureCount++;
                if(next.Size!=viewport.Size){next.Dispose();throw new InvalidOperationException("长截图画面尺寸发生变化。");}
                if(previous!=null) {
                    var match=await Task.Run(()=>Stitcher.Match(previous,next),token);
                    token.ThrowIfCancellationRequested();
                    if(match.State=="unchanged"){previous.Dispose();previous=null;var result=next;next=null;return result;}
                }
                previous?.Dispose();previous=next;next=null;await Task.Delay(65,token);
            }
            return null; // An animation/virtualized redraw is recoverable, not a capture failure.
        }finally{previous?.Dispose();next?.Dispose();}
    }
    internal Task<D.Bitmap> Finish()
    {
        if(finishTask!=null)return finishTask;
        if(cancelled)throw new OperationCanceledException();finishing=true;finishDeadline=Environment.TickCount64+1500;timer.Stop();endProbe.Stop();
        // Release means stop moving the page now. These requests have never
        // reached the source, so draining them would scroll after the button-up
        // and keep the modal overlay open for an unbounded number of seconds.
        abandonedWheels=PendingWheelCount;wheelQueue.Clear();recoveryWheels.Clear();
        var previous=work;finishTask=FinishAfter(previous);work=finishTask;return finishTask;
    }
    private async Task<D.Bitmap> FinishAfter(Task previous)
    {
        try {
        await previous;
        if(cancelled)throw new OperationCanceledException();
        stop.Token.ThrowIfCancellationRequested();
        ApplyManualSelection();
        // Even after the last scroll callback returned, the source may render its
        // final few pixels asynchronously. Always take one fresh stable sample.
        try {if(!Rewound&&!FinishBudgetElapsed())await CaptureStableAndAppend(waitForMotion:scrollAttempted&&!AtBottom);}catch(OperationCanceledException){throw;}catch(Exception ex){Error??=ex.Message;}
        if(NeedsOverlap||PendingWheelCount>0)CompletionWarning="后续画面未能可靠衔接，本次只保留已确认的部分，请重新截取未收齐的内容。";
        if(InputWarning!=null)CompletionWarning="原窗口未响应后续滚轮，本次只保留已确认的截图部分，请重新尝试未收齐的内容。";
        if(abandonedWheels>0)CompletionWarning??="松手后已停止继续滚动，并保存已采集的内容；尚未执行的滚轮请求已取消。";
        if(finishBudgetExpired)CompletionWarning??="收尾时页面仍未稳定，已结束等待并保留确认的截图内容。";
        wheelQueue.Clear();recoveryWheels.Clear();
        var result=Imaging.Crop(composite!,new D.Rectangle(0,0,viewport.Width,Math.Max(1,OutputHeight)));
        finalHeight=result.Height;ReleaseImages();Changed?.Invoke();return result;
        }finally{if(cancelled)ReleaseImages();}
    }
    private bool FinishBudgetElapsed()
    {
        if(!finishing||Environment.TickCount64<finishDeadline)return false;
        finishBudgetExpired=true;Recovering=false;return true;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;cancelled=true;wheelQueue.Clear();recoveryWheels.Clear();timer.Stop();endProbe.Stop();stop.Cancel();
        if(work.IsCompleted)ReleaseImages();
    }
    private void ReleaseImages(){last?.Dispose();composite?.Dispose();last=composite=null;}
}

// Lock after a genuine vertical leg; a horizontal or diagonal first leg still
// establishes the chosen width. Applies only to the explicit long-capture mode.
internal sealed class DragWidthSelection
{
    private readonly D.Point origin;
    private D.Point widthPoint;
    internal bool Locked {get;private set;}
    internal D.Rectangle Selection {get;private set;}
    internal DragWidthSelection(D.Point origin){this.origin=origin;widthPoint=origin;}
    internal bool LockCurrent()
    {
        if(Selection.Width<30||Selection.Height<80)return false;
        widthPoint.X=Selection.Left==origin.X?Selection.Right:Selection.Left;Locked=true;return true;
    }
    internal void Move(D.Point point)
    {
        if(!Locked) {
            if(Math.Abs(point.X-widthPoint.X)>8||point.Y<widthPoint.Y)widthPoint=point;
            if(Math.Abs(widthPoint.X-origin.X)>=30&&point.Y-origin.Y>=80&&point.Y-widthPoint.Y>=24)Locked=true;
        }
        int endX=Locked?widthPoint.X:point.X;
        Selection=D.Rectangle.FromLTRB(Math.Min(origin.X,endX),origin.Y,Math.Max(origin.X,endX),Math.Max(origin.Y+1,point.Y+1));
    }
}
