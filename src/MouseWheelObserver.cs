using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using D=System.Drawing;

namespace QingCapture;

internal readonly record struct MouseWheelSample(D.Point Position,int Delta,uint NativeTime,bool Injected=false,bool ControlPressed=false,bool ShiftPressed=false);
internal enum WheelCaptureDirection {Down,Up}

// The original application receives every event. These hooks never handle input.
internal sealed class MouseWheelObserver : IDisposable
{
    internal const int MouseWheelMessage=0x020A;
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData {public int X,Y;public uint MouseDataValue,Flags,Time;public UIntPtr ExtraInfo;}
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData {public uint VirtualKey,ScanCode,Flags,Time;public UIntPtr ExtraInfo;}
    private delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    private readonly HookProc mouseProc,keyboardProc;
    private readonly Action<MouseWheelSample> wheel;
    private readonly Action? escape;
    private readonly Action? complete;
    private readonly Action<Exception>? fault;
    private IntPtr mouseHook,keyboardHook;
    private bool disposed;
    internal bool IsInstalled=>!disposed&&mouseHook!=IntPtr.Zero&&((escape==null&&complete==null)||keyboardHook!=IntPtr.Zero);
    internal bool HooksReleased=>mouseHook==IntPtr.Zero&&keyboardHook==IntPtr.Zero;
    internal int LastUnhookError {get;private set;}

    internal MouseWheelObserver(Action<MouseWheelSample> observed,Action? cancelled=null,Action<Exception>? failed=null,Action? completed=null)
    {
        wheel=observed;escape=cancelled;fault=failed;complete=completed;mouseProc=OnMouse;keyboardProc=OnKeyboard;
        mouseHook=SetWindowsHookEx(14,mouseProc,GetModuleHandle(null),0);
        if(mouseHook==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法监听鼠标滚轮。");
        if(escape!=null||complete!=null) {
            keyboardHook=SetWindowsHookEx(13,keyboardProc,GetModuleHandle(null),0);
            if(keyboardHook==IntPtr.Zero) {int error=Marshal.GetLastWin32Error();Dispose();throw new Win32Exception(error,"无法监听长截图取消键。");}
        }
    }
    internal static bool TryReadWheel(int code,int message,IntPtr packet,out MouseWheelSample sample,bool controlPressed=false,bool shiftPressed=false)
    {
        sample=default;if(code<0||message!=MouseWheelMessage||packet==IntPtr.Zero)return false;
        var data=Marshal.PtrToStructure<MouseData>(packet);
        int delta=unchecked((short)(data.MouseDataValue>>16));bool injected=(data.Flags&3)!=0;
        if(delta==0||controlPressed||shiftPressed)return false;
        sample=new MouseWheelSample(new D.Point(data.X,data.Y),delta,data.Time,injected);return true;
    }
    private IntPtr OnMouse(int code,IntPtr message,IntPtr packet)
    {
        try {if(!disposed&&TryReadWheel(code,unchecked((int)message.ToInt64()),packet,out var sample,(GetAsyncKeyState(0x11)&0x8000)!=0,(GetAsyncKeyState(0x10)&0x8000)!=0))wheel(sample);}
        catch(Exception ex) {Report(ex);}
        return CallNextHookEx(IntPtr.Zero,code,message,packet);
    }
    private IntPtr OnKeyboard(int code,IntPtr message,IntPtr packet)
    {
        try {
            int kind=unchecked((int)message.ToInt64());
            if(!disposed&&code>=0&&(kind==0x0100||kind==0x0104)&&packet!=IntPtr.Zero) {
                var data=Marshal.PtrToStructure<KeyboardData>(packet);
                if(data.VirtualKey==0x1B)escape?.Invoke();
                if(data.VirtualKey==0x0D&&complete!=null&&(GetAsyncKeyState(0x11)&0x8000)==0&&(GetAsyncKeyState(0x10)&0x8000)==0&&(GetAsyncKeyState(0x12)&0x8000)==0&&(GetAsyncKeyState(0x5B)&0x8000)==0&&(GetAsyncKeyState(0x5C)&0x8000)==0) {
                    complete();return new IntPtr(1); // Finish capture instead of opening an Explorer item.
                }
            }
        } catch(Exception ex) {Report(ex);}
        return CallNextHookEx(IntPtr.Zero,code,message,packet);
    }
    private void Report(Exception error) {try {fault?.Invoke(error);}catch {/* Never unwind through a Windows hook. */}}
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        Release(ref keyboardHook);Release(ref mouseHook);GC.KeepAlive(mouseProc);GC.KeepAlive(keyboardProc);
    }
    private void Release(ref IntPtr hook)
    {
        if(hook==IntPtr.Zero)return;
        if(!UnhookWindowsHookEx(hook))LastUnhookError=Marshal.GetLastWin32Error();
        hook=IntPtr.Zero;
    }
    [DllImport("user32.dll",EntryPoint="SetWindowsHookExW",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int kind,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr packet);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}

internal sealed class WheelCaptureGate : IDisposable
{
    internal const int QuietMilliseconds=60,StableMilliseconds=55,MaxWaitMilliseconds=90;
    private readonly D.Rectangle area;
    private readonly bool allowSelectionEdges;
    private long lastWheel,firstWheel;
    private bool disposed;
    internal long Revision {get;private set;}
    internal bool Pending {get;private set;}
    internal WheelCaptureDirection Direction {get;private set;}
    internal WheelCaptureGate(D.Rectangle bounds,bool allowSelectionEdges=false) {area=bounds;this.allowSelectionEdges=allowSelectionEdges;}
    internal bool Record(MouseWheelSample sample,long now)
    {
        bool inside=area.Contains(sample.Position)||(allowSelectionEdges&&sample.Position.X>=area.Left&&sample.Position.X<=area.Right&&sample.Position.Y>=area.Top&&sample.Position.Y<=area.Bottom);
        if(disposed||sample.ControlPressed||sample.ShiftPressed||sample.Delta==0||!inside)return false;
        Direction=sample.Delta<0?WheelCaptureDirection.Down:WheelCaptureDirection.Up;
        if(!Pending)firstWheel=now;lastWheel=now;Revision++;Pending=true;return true;
    }
    internal bool TakeDue(long now,out long revision,out WheelCaptureDirection direction)
    {
        revision=Revision;direction=Direction;
        if(disposed||!Pending||(now-lastWheel<QuietMilliseconds&&now-firstWheel<MaxWaitMilliseconds))return false;
        Pending=false;return true;
    }
    internal void Clear() {Pending=false;Revision++;}
    public void Dispose() {if(disposed)return;disposed=true;Clear();}
}
