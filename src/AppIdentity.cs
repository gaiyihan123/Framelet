using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QingCapture;

internal static class AppIdentity
{
    // Stable and shared with the Start-menu shortcut, independent of the launcher.
    internal const string Id="Framelet.Desktop";
    internal static void Initialize()=>Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(Id));
    internal static void Configure(Window window)=>window.SourceInitialized+=(_,_)=>SetWindowIdentity(new WindowInteropHelper(window).Handle);
    private static readonly PropertyKey AppIdKey=new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),5);
    private static readonly PropertyKey RelaunchCommandKey=new(AppIdKey.Format,2),RelaunchIconKey=new(AppIdKey.Format,3),RelaunchDisplayNameKey=new(AppIdKey.Format,4);
    internal static string ShellIconPath=>Path.Combine(AppContext.BaseDirectory,AppBrand.ShellIconFile);
    internal static string ShellIconResource=>ShellIconPath+",0";
    private static IPropertyStore Store(IntPtr hwnd)
    {
        var iid=typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd,ref iid,out var store));return store;
    }
    private static void SetString(IPropertyStore store,PropertyKey key,string text)
    {
        var value=new PropVariant { Type=31,Pointer=Marshal.StringToCoTaskMemUni(text) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key,ref value)); }finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }
    private static void SetWindowIdentity(IntPtr hwnd)
    {
        var store=Store(hwnd);
        try {
            if(!File.Exists(ShellIconPath))throw new FileNotFoundException("Framelet taskbar icon is missing",ShellIconPath);
            SetString(store,RelaunchCommandKey,"\""+Path.Combine(AppContext.BaseDirectory,"QingCapture.exe")+"\"");
            SetString(store,RelaunchIconKey,ShellIconResource);
            SetString(store,RelaunchDisplayNameKey,AppBrand.Name);
            SetString(store,AppIdKey,Id);
            Marshal.ThrowExceptionForHR(store.Commit());
        } finally { Marshal.ReleaseComObject(store); }
    }
    private static string WindowString(Window window,PropertyKey key)
    {
        var store=Store(new WindowInteropHelper(window).Handle);
        try { Marshal.ThrowExceptionForHR(store.GetValue(ref key,out var value));try { return value.Type==31?Marshal.PtrToStringUni(value.Pointer)??"":""; }finally { PropVariantClear(ref value); } }
        finally { Marshal.ReleaseComObject(store); }
    }
    internal static string WindowId(Window window)=>WindowString(window,AppIdKey);
    internal static string WindowIconResource(Window window)=>WindowString(window,RelaunchIconKey);
    internal static string WindowRelaunchName(Window window)=>WindowString(window,RelaunchDisplayNameKey);
    internal static string Current()
    {
        Marshal.ThrowExceptionForHR(GetCurrentProcessExplicitAppUserModelID(out var value));
        try { return Marshal.PtrToStringUni(value)??""; }
        finally { Marshal.FreeCoTaskMem(value); }
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll")] private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr id);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd,ref Guid iid,[MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format;public uint Id;public PropertyKey(Guid format,uint id){Format=format;Id=id;} }
    [StructLayout(LayoutKind.Explicit,Size=24)] private struct PropVariant { [FieldOffset(0)] public ushort Type;[FieldOffset(8)] public IntPtr Pointer; }
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index,out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key,out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key,ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
