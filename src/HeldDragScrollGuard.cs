using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

internal readonly record struct HeldDragFilterDecision(bool Consume,bool ForwardWheel,bool Finish,uint KeyState);

// The source never received the selection drag's DOWN. Until its physical UP,
// keep that drag out of the source and deliver wheel messages without MK_LBUTTON.
// Install before releasing the overlay's capture, then install MouseWheelObserver:
// the newer observer sees each original wheel before this hook forwards it once.
internal sealed class HeldDragScrollGuard : IDisposable
{
    internal const int MouseMove=0x0200,LeftDown=0x0201,LeftUp=0x0202;
    private const uint LeftButton=0x0001;
    private delegate IntPtr HookProc(int code,IntPtr message,IntPtr packet);
    // DrainRelease outlives its screenshot window, including the callback delegate.
    private static readonly HashSet<HeldDragScrollGuard> active=new();
    private static readonly object activeGate=new();
    private readonly D.Rectangle area;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer releaseWatch;
    private readonly HookProc callback;
    private Action<Exception>? failed;
    private readonly int primaryPhysicalKey,secondaryPhysicalKey;
    private readonly Queue<(MouseWheelSample Sample,uint Keys)> pendingWheels=new();
    private IntPtr hook,target,handoffTarget;
    private bool awaitingRelease=true,draining,ending,cleanupQueued,releaseObserved,failureReported,handoffComplete;
    internal bool IsInstalled=>hook!=IntPtr.Zero&&!ending;
    internal bool AwaitingRelease=>awaitingRelease&&!ending;
    internal bool IsDraining=>draining&&AwaitingRelease;
    internal bool HooksReleased=>hook==IntPtr.Zero;
    internal int LastUnhookError {get;private set;}

    // Null means there is no held physical selection drag to protect. Installation
    // failure throws before the caller releases capture or removes the overlay.
    internal static HeldDragScrollGuard? TryCreate(D.Rectangle area,Action<Exception>? failed=null)
    {
        if(area.Width<1||area.Height<1)throw new ArgumentException("A held-drag guard needs a nonempty selection.",nameof(area));
        bool swapped=GetSystemMetrics(23)!=0;int primary=swapped?0x02:0x01;
        if((GetAsyncKeyState(primary)&0x8000)==0)return null;
        return new HeldDragScrollGuard(area,failed,primary,swapped?0x01:0x02);
    }

    private HeldDragScrollGuard(D.Rectangle area,Action<Exception>? failed,int primary,int secondary)
    {
        this.area=area;this.failed=failed;primaryPhysicalKey=primary;secondaryPhysicalKey=secondary;dispatcher=Dispatcher.CurrentDispatcher;callback=OnMouse;
        releaseWatch=new DispatcherTimer(DispatcherPriority.Background,dispatcher) {Interval=TimeSpan.FromMilliseconds(50)};
        releaseWatch.Tick+=(_,_)=> {
            if(ending){ReleaseHook();return;}
            // Covers a release between the initial state check and hook installation,
            // device loss, and a hook callback omitted by Windows. Cleanup is deferred
            // so an already-queued UP can still be consumed first.
            if(!PhysicalLeftDown())QueueCleanup();
        };
        lock(activeGate)active.Add(this);
        try {
            hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);
            if(hook==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法保护尚未松开的截图拖动。");
            releaseWatch.Start();
        } catch {
            awaitingRelease=false;ending=true;
            if(hook!=IntPtr.Zero) {
                if(UnhookWindowsHookEx(hook))hook=IntPtr.Zero;
                else LastUnhookError=Marshal.GetLastWin32Error();
            }
            // Retain the delegate if native teardown itself failed. Its callback
            // now passes input through; collecting an installed delegate is unsafe.
            if(hook==IntPtr.Zero)lock(activeGate)active.Remove(this);
            throw;
        }
    }

    internal void AttachTarget(IntPtr window,Action<Exception>? onFailure=null)
    {
        if(!dispatcher.CheckAccess()){dispatcher.Invoke(()=>AttachTarget(window,onFailure));return;}
        failed=onFailure??failed;
        if(window==IntPtr.Zero){DetachTarget();return;}
        if(draining)return; // A cancelled session cannot resume forwarding.
        handoffTarget=window;
    }
    internal void FlushPending(Action<MouseWheelSample> observed)
    {
        if(draining||handoffTarget==IntPtr.Zero){pendingWheels.Clear();throw new InvalidOperationException("拖选交接未完成，请松开左键后重新截图。");}
        var destination=handoffTarget;
        while(pendingWheels.Count>0) {
            var item=pendingWheels.Dequeue();
            if(!Forward(item.Sample,item.Keys,destination)){var error=new Win32Exception(Marshal.GetLastWin32Error(),"无法补齐交接期间的滚轮。");StopForwarding(error);throw error;}
            observed(item.Sample);
        }
        handoffComplete=true;handoffTarget=IntPtr.Zero;
        if(awaitingRelease&&!ending)target=destination;
        if(releaseObserved||!PhysicalLeftDown())QueueCleanup();
    }

    internal void DetachTarget()
    {
        if(!dispatcher.CheckAccess()){dispatcher.Invoke(DetachTarget);return;}
        target=handoffTarget=IntPtr.Zero;draining=true;handoffComplete=true;pendingWheels.Clear();
        if(!PhysicalLeftDown())QueueCleanup();
    }

    // Disposing a live session detaches its target immediately. Its small mouse
    // guard remains rooted only until this physical drag's release is drained.
    public void Dispose()=>DetachTarget();

    internal static D.Point ClampToSelection(D.Point point,D.Rectangle selection)=>new(
        Math.Clamp(point.X,selection.Left,selection.Right-1),Math.Clamp(point.Y,selection.Top,selection.Bottom-1));

    internal static HeldDragFilterDecision FilterForTest(int message,D.Point position,int delta,D.Rectangle area,
        bool awaitingRelease=true,bool draining=false,bool hasTarget=true,bool injected=false,uint keyState=0)
    {
        uint clean=keyState&~LeftButton;
        if(!awaitingRelease)return new(false,false,false,clean);
        // Async button state is OS state, including accessibility input. Either
        // kind of UP ends this logical drag and must not reach a source with no DOWN.
        if(message==LeftUp)return new(true,false,true,clean);
        // A fresh physical DOWN proves that an older UP was missed. Do not consume
        // either half of the new click, even when the old session was cancelled.
        if(message==LeftDown)return new(false,false,true,clean);
        if(message==MouseMove)return new(true,false,false,clean);
        bool inside=area.Width>0&&area.Height>0&&position.X>=area.Left&&position.X<=area.Right&&position.Y>=area.Top&&position.Y<=area.Bottom;
        bool wheel=message==MouseWheelObserver.MouseWheelMessage&&delta!=0&&inside;
        // With no live target there is nowhere to deliver a sanitized wheel.
        // Do not leak this old held button back into the source while draining.
        return new(wheel,wheel&&hasTarget&&!draining,false,clean);
    }

    private IntPtr OnMouse(int code,IntPtr message,IntPtr packet)
    {
        if(code<0||ending||packet==IntPtr.Zero)return CallNextHookEx(IntPtr.Zero,code,message,packet);
        bool consume=false;
        try {
            int kind=unchecked((int)message.ToInt64());
            var data=Marshal.PtrToStructure<MouseWheelObserver.MouseData>(packet);
            bool injected=(data.Flags&3)!=0;
            int delta=unchecked((short)(data.MouseDataValue>>16));
            if(kind==MouseWheelObserver.MouseWheelMessage&&!handoffComplete&&!draining) {
                uint keys=CurrentKeyState()&~LeftButton;
                if(FilterForTest(kind,new D.Point(data.X,data.Y),delta,area,hasTarget:false,keyState:keys).Consume) {
                    pendingWheels.Enqueue((new MouseWheelSample(new D.Point(data.X,data.Y),delta,data.Time,injected,ControlPressed:(keys&8)!=0,ShiftPressed:(keys&4)!=0),keys));
                    return new IntPtr(1);
                }
            }
            // Never freeze pointer movement after the real release has already
            // become observable, even if its UP hook callback was missed.
            if(kind==MouseMove&&!PhysicalLeftDown()) {QueueCleanup();return CallNextHookEx(IntPtr.Zero,code,message,packet);}
            var decision=FilterForTest(kind,new D.Point(data.X,data.Y),delta,area,awaitingRelease,draining,target!=IntPtr.Zero,injected,
                kind==MouseWheelObserver.MouseWheelMessage?CurrentKeyState():0);
            consume=decision.Consume;
            if(decision.Finish) {
                awaitingRelease=false;releaseObserved=true;target=IntPtr.Zero;QueueCleanup();
            }
            else if(decision.ForwardWheel) {
                if(!Forward(new MouseWheelSample(new D.Point(data.X,data.Y),delta,data.Time,injected),decision.KeyState,target))
                    StopForwarding(new Win32Exception(Marshal.GetLastWin32Error(),"无法转发按住拖选时的滚轮；请松开鼠标后继续。"));
            }
        } catch(Exception ex) {StopForwarding(ex);}
        return consume?new IntPtr(1):CallNextHookEx(IntPtr.Zero,code,message,packet);
    }
    private bool Forward(MouseWheelSample sample,uint keys,IntPtr destination)
    {
        var position=ClampToSelection(sample.Position,area);
        uint wParam=unchecked((uint)(sample.Delta<<16))|(keys&~LeftButton);
        uint lParam=unchecked((uint)(((position.Y&0xffff)<<16)|(position.X&0xffff)));
        return PostMessage(destination,MouseWheelObserver.MouseWheelMessage,new IntPtr((long)wParam),new IntPtr((long)lParam));
    }

    private void StopForwarding(Exception error)
    {
        target=handoffTarget=IntPtr.Zero;draining=true;handoffComplete=true;pendingWheels.Clear();
        if(!failureReported) {
            failureReported=true;
            try {dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=> {try {failed?.Invoke(error);}catch {/* Never unwind through a native hook. */}}));}
            catch {/* Shutdown owns the remaining process-local hook lifetime. */}
        }
        if(!PhysicalLeftDown())QueueCleanup();
    }

    private void QueueCleanup()
    {
        if(cleanupQueued||ending)return;cleanupQueued=true;
        try {dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=> {
            cleanupQueued=false;
            if(!handoffComplete&&!draining)return;
            if(!releaseObserved&&PhysicalLeftDown())return;
            awaitingRelease=false;ending=true;target=IntPtr.Zero;ReleaseHook();
        }));}catch {cleanupQueued=false;}
    }

    private void ReleaseHook()
    {
        if(hook!=IntPtr.Zero) {
            if(!UnhookWindowsHookEx(hook)) {
                LastUnhookError=Marshal.GetLastWin32Error();
                // An already-removed hook has no callback lifetime to preserve.
                if(LastUnhookError!=1404)return;
            }
            hook=IntPtr.Zero;
        }
        releaseWatch.Stop();lock(activeGate)active.Remove(this);GC.KeepAlive(callback);
    }

    private bool PhysicalLeftDown()=>(GetAsyncKeyState(primaryPhysicalKey)&0x8000)!=0;
    private uint CurrentKeyState()
    {
        return ((GetAsyncKeyState(primaryPhysicalKey)&0x8000)!=0?1u:0u)
            |((GetAsyncKeyState(secondaryPhysicalKey)&0x8000)!=0?2u:0u)
            |((GetAsyncKeyState(0x10)&0x8000)!=0?4u:0u)
            |((GetAsyncKeyState(0x11)&0x8000)!=0?8u:0u)
            |((GetAsyncKeyState(0x04)&0x8000)!=0?16u:0u)
            |((GetAsyncKeyState(0x05)&0x8000)!=0?32u:0u)
            |((GetAsyncKeyState(0x06)&0x8000)!=0?64u:0u);
    }

    [DllImport("user32.dll",EntryPoint="SetWindowsHookExW",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int kind,HookProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr packet);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",EntryPoint="PostMessageW",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr target,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
