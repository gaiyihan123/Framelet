using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace QingCapture;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public Rectangle Bounds => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent,Point point,uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll",EntryPoint="PostMessageW",SetLastError=true)] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
    internal static bool ForwardWheel(IntPtr target,MouseWheelSample sample,bool activate=true)
    {
        if(target==IntPtr.Zero)return false;
        var root=GetAncestor(target,2);if(activate&&root!=IntPtr.Zero)SetForegroundWindow(root);
        long delta=unchecked((uint)(sample.Delta<<16));
        long point=unchecked((uint)(((sample.Position.Y&0xffff)<<16)|(sample.Position.X&0xffff)));
        return PostMessage(target,MouseWheelObserver.MouseWheelMessage,new IntPtr(delta),new IntPtr(point));
    }
    internal static bool ForwardWheelUnderOverlay(IntPtr overlay,IntPtr target,MouseWheelSample sample,int timeoutMs=250)
    {
        if(overlay==IntPtr.Zero||target==IntPtr.Zero||sample.Delta==0||timeoutMs<=0)return false;
        var root=GetAncestor(target,2);if(root==IntPtr.Zero)root=target;
        if(root==overlay)return false;
        const int extendedStyle=-20;
        var original=GetWindowLongPtr(overlay,extendedStyle);
        var transparent=new IntPtr(original.ToInt64()|0x20); // WS_EX_TRANSPARENT.
        bool delivered=false;
        try {
            // Chromium rechecks WindowFromPoint while handling a wheel message.
            // Keep our native capture and let only this synchronous delivery see
            // through the layered overlay. The caller guards physical wheel input
            // for the whole session, including this temporary transparent period.
            SetWindowLongPtr(overlay,extendedStyle,transparent);
            if(GetWindowLongPtr(overlay,extendedStyle)!=transparent)return false;
            var hit=WindowFromPoint(new Point {X=sample.Position.X,Y=sample.Position.Y});
            if(hit==IntPtr.Zero||hit==overlay)return false;
            var hitRoot=GetAncestor(hit,2);if(hitRoot==IntPtr.Zero)hitRoot=hit;
            if(hitRoot!=root)return false;
            long delta=unchecked((uint)(sample.Delta<<16));
            long point=unchecked((uint)(((sample.Position.Y&0xffff)<<16)|(sample.Position.X&0xffff)));
            // A posted message could run after the overlay is opaque again.
            // Permit nonqueued messages while waiting; never activate the source.
            delivered=SendMessageTimeout(hit,MouseWheelObserver.MouseWheelMessage,new IntPtr(delta),new IntPtr(point),0x2|0x20,(uint)timeoutMs,out _)!=IntPtr.Zero;
        }finally {
            SetWindowLongPtr(overlay,extendedStyle,original);
            if(GetWindowLongPtr(overlay,extendedStyle)!=original)delivered=false;
        }
        return delivered;
    }
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out UIntPtr result);
    internal static bool? ReadVerticalScrollEnd(IntPtr target)
    {
        // Query the exact wheel recipient. An ancestor's bar may describe a
        // different pane, and custom-drawn/WPF content may have no Win32 range.
        if(target==IntPtr.Zero||(GetWindowLongPtr(target,-16).ToInt64()&0x00200000)==0)return null;
        var info=new ScrollInfo {Size=(uint)Marshal.SizeOf<ScrollInfo>(),Mask=1|2|4};
        if(!GetScrollInfo(target,1,ref info))return null; // SB_VERT; RANGE | PAGE | POS.
        return VerticalScrollEndForRange(info.Minimum,info.Maximum,info.Page,info.Position);
    }
    internal static bool? VerticalScrollEndForRange(int minimum,int maximum,uint page,int position)
    {
        // Win32's nMax is inclusive. Use long arithmetic even for ranges crossing
        // zero, and reject disabled/inconsistent ranges instead of claiming EOF.
        if(page==0||maximum<=minimum)return null;
        long span=(long)maximum-minimum+1,effectiveMax=(long)maximum-((long)page-1);
        if(page>=span||effectiveMax<=minimum||position<minimum||position>effectiveMax)return null;
        return position>=effectiveMax;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ScrollInfo
    {
        public uint Size,Mask;
        public int Minimum,Maximum;
        public uint Page;
        public int Position,TrackPosition;
    }
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetScrollInfo(IntPtr window,int bar,ref ScrollInfo info);
    internal static IntPtr WindowBelow(Point point,IntPtr excluded,Rectangle? requiredRegion=null)
    {
        IntPtr found=IntPtr.Zero;
        EnumWindows((window,_)=> {
            if(window==excluded||!IsWindowVisible(window)||IsIconic(window))return true;
            if((GetWindowLongPtr(window,-20).ToInt64()&0x20)!=0)return true;
            if(DwmGetWindowAttribute(window,14,out int cloaked,4)==0&&cloaked!=0)return true;
            if(!GetWindowRect(window,out var bounds)||!bounds.Bounds.Contains(point.X,point.Y))return true;
            if(requiredRegion.HasValue&&!bounds.Bounds.Contains(requiredRegion.Value))return true;
            found=window;return false;
        },IntPtr.Zero);
        for(int i=0;i<12&&found!=IntPtr.Zero;i++) {
            var local=point;if(!ScreenToClient(found,ref local))break;
            var child=ChildWindowFromPointEx(found,local,1|2|4);if(child==IntPtr.Zero||child==found)break;found=child;
        }
        return found;
    }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr param);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    public static List<Rectangle> Windows()
    {
        var list = new List<Rectangle>();
        EnumWindows((h, _) => {
            GetWindowThreadProcessId(h, out var pid);
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            long style=GetWindowLongPtr(h,-20).ToInt64();
            if((style & (0x20 | 0x80))!=0) return true; // click-through overlays / utility windows
            if(DwmGetWindowAttribute(h,14,out int cloaked,4)==0 && cloaked!=0) return true;
            if (DwmGetWindowAttribute(h, 9, out Rect r, Marshal.SizeOf<Rect>()) != 0) GetWindowRect(h, out r);
            if (r.Right > r.Left && r.Bottom > r.Top) list.Add(r.Bounds);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
