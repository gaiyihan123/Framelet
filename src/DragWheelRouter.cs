using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

// The active selection owns vertical input even during the brief native
// pass-through required by Chromium's WindowFromPoint wheel rerouting.
internal sealed class DragWheelRouter : IDisposable
{
    private delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    private static readonly HashSet<DragWheelRouter> retained=new();
    private readonly HookProc callback;
    private readonly Dispatcher dispatcher=Dispatcher.CurrentDispatcher;
    private readonly Func<bool> active;
    private readonly Action<MouseWheelSample> observed;
    private readonly Action<Exception> failed;
    private readonly Queue<MouseWheelSample> pending=new();
    private IntPtr hook;
    private bool disposed,scheduled;
    internal int NativeWheelCount {get;private set;}
    internal bool HooksReleased=>hook==IntPtr.Zero;

    internal DragWheelRouter(Func<bool> active,Action<MouseWheelSample> observed,Action<Exception> failed)
    {
        this.active=active;this.observed=observed;this.failed=failed;callback=OnMouse;
        hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);
        if(hook==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法接收长截图滚轮，请重试。");
        retained.Add(this);
    }
    private IntPtr OnMouse(int code,IntPtr message,IntPtr packet)
    {
        if(disposed||code<0||packet==IntPtr.Zero||message.ToInt64()!=MouseWheelObserver.MouseWheelMessage)
            return CallNextHookEx(IntPtr.Zero,code,message,packet);
        bool consume=false;
        try {
            if(!active())return CallNextHookEx(IntPtr.Zero,code,message,packet);
            var data=Marshal.PtrToStructure<MouseWheelObserver.MouseData>(packet);
            int delta=unchecked((short)(data.MouseDataValue>>16));
            if(delta==0)return CallNextHookEx(IntPtr.Zero,code,message,packet);
            consume=true;NativeWheelCount++;
            // Modifier gestures do not zoom or redirect the underlying page.
            if(!Pressed(0x10)&&!Pressed(0x11)&&!Pressed(0x12)&&!Pressed(0x5B)&&!Pressed(0x5C)) {
                pending.Enqueue(new MouseWheelSample(new D.Point(data.X,data.Y),delta,data.Time,(data.Flags&3)!=0));
                if(!scheduled){scheduled=true;dispatcher.BeginInvoke(DispatcherPriority.Send,new Action(Flush));}
            }
        }catch(Exception ex){try{dispatcher.BeginInvoke(new Action(()=>failed(ex)));}catch{/* Never unwind a native hook. */}}
        return consume?new IntPtr(1):CallNextHookEx(IntPtr.Zero,code,message,packet);
    }
    internal void Flush()
    {
        scheduled=false;if(disposed){pending.Clear();return;}
        try{while(pending.Count>0){var sample=pending.Dequeue();if(active())observed(sample);}}
        catch(Exception ex){pending.Clear();failed(ex);}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;pending.Clear();
        if(hook!=IntPtr.Zero&&(UnhookWindowsHookEx(hook)||Marshal.GetLastWin32Error()==1404))hook=IntPtr.Zero;
        // A failed native teardown must retain the callback until process exit.
        if(hook==IntPtr.Zero)retained.Remove(this);GC.KeepAlive(callback);
    }
    private static bool Pressed(int key)=>(GetAsyncKeyState(key)&0x8000)!=0;
    [DllImport("user32.dll",EntryPoint="SetWindowsHookExW",SetLastError=true)]private static extern IntPtr SetWindowsHookEx(int kind,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr packet);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? module);
}
