using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using D=System.Drawing;

namespace QingCapture;

// Explicit test mode only. Saves/restores native display configuration in finally.
internal static class LiveDisplayAcceptance
{
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags,ref uint paths,IntPtr pathData,ref uint modes,IntPtr modeData,IntPtr topology);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint paths,IntPtr pathData,uint modes,IntPtr modeData,uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,ref Native.Rect rect,IntPtr data);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public Native.Rect Monitor,Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    private sealed record Display(string Device,D.Rectangle Bounds);
    private static List<Display> Displays()
    {
        var result=new List<Display>();
        EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr h,IntPtr dc,ref Native.Rect rect,IntPtr data)=> {
            var info=new MonitorInfo { Size=Marshal.SizeOf<MonitorInfo>(),Device="" };
            if(GetMonitorInfo(h,ref info)) result.Add(new Display(info.Device,info.Monitor.Bounds)); return true;
        },IntPtr.Zero);
        return result;
    }
    private sealed class Configuration : IDisposable
    {
        public uint Paths,Modes; public IntPtr PathData,ModeData;
        public Configuration()
        {
            int result=GetDisplayConfigBufferSizes(2,out Paths,out Modes); if(result!=0) throw new InvalidOperationException("GetDisplayConfigBufferSizes: "+result);
            PathData=Marshal.AllocHGlobal(checked((int)Paths*72)); ModeData=Marshal.AllocHGlobal(checked((int)Modes*64));
            result=QueryDisplayConfig(2,ref Paths,PathData,ref Modes,ModeData,IntPtr.Zero);
            if(result!=0) { Dispose(); throw new InvalidOperationException("QueryDisplayConfig: "+result); }
        }
        public int Restore()=>SetDisplayConfig(Paths,PathData,Modes,ModeData,0xA0);
        public void Dispose() { if(PathData!=IntPtr.Zero) Marshal.FreeHGlobal(PathData); if(ModeData!=IntPtr.Zero) Marshal.FreeHGlobal(ModeData); PathData=ModeData=IntPtr.Zero; }
    }
    public static int Run(string[] args)
    {
        string folder=args.SkipWhile(x=>x!="--report-dir").Skip(1).FirstOrDefault()??Path.Combine(Environment.CurrentDirectory,"display-test-results");
        Directory.CreateDirectory(folder); var lines=new List<string>(); var windows=new List<Window>(); int exitCode=1;
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown }; Ui.Resources(app);
        app.Startup+=async(_,_)=> {
            Configuration? original=null; bool changed=false;
            try {
                var before=Displays(); lines.Add("Original displays: "+JsonSerializer.Serialize(before));
                if(!args.Contains("--current-display-config")) {
                    original=new Configuration();
                    int validate=SetDisplayConfig(0,IntPtr.Zero,0,IntPtr.Zero,0x44); if(validate!=0) throw new InvalidOperationException("Extend validation failed: "+validate);
                    int apply=SetDisplayConfig(0,IntPtr.Zero,0,IntPtr.Zero,0x84); if(apply!=0) throw new InvalidOperationException("Extend failed: "+apply); changed=true;
                    await Task.Delay(1500);
                } else lines.Add("Testing current display configuration without changing it.");
                var displays=Displays();
                if(displays.Count<2) throw new InvalidOperationException("Only "+displays.Count+" active display after Extend");
                lines.Add("Extended displays: "+JsonSerializer.Serialize(displays));
                var colors=new[]{D.Color.FromArgb(18,132,111),D.Color.FromArgb(184,62,78),D.Color.FromArgb(35,94,170)};
                var dpiValues=new List<uint>();
                for(int i=0;i<displays.Count;i++) {
                    var display=displays[i]; var color=colors[i%colors.Length];
                    var window=new Window { Title=AppBrand.Title("双屏验收 "+(i+1)),WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Topmost=true,Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R,color.G,color.B)),Content=new TextBlock { Text=AppBrand.Title("实际显示器 "+(i+1)),FontSize=48,Foreground=System.Windows.Media.Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center } };
                    windows.Add(window); window.Show(); var handle=new WindowInteropHelper(window).Handle;
                    Native.SetWindowPos(handle,new IntPtr(-1),display.Bounds.X,display.Bounds.Y,display.Bounds.Width,display.Bounds.Height,0x40);
                    await Task.Delay(250); dpiValues.Add(GetDpiForWindow(handle));
                }
                await Task.Delay(350); DwmFlush();
                var desktop=new D.Rectangle(GetSystemMetrics(76),GetSystemMetrics(77),GetSystemMetrics(78),GetSystemMetrics(79));
                using var snapshot=Imaging.Capture(desktop);
                var overlay=new CaptureOverlay(snapshot,desktop,"区域"); windows.Add(overlay); overlay.Show(); await Task.Delay(300);
                Native.GetWindowRect(new WindowInteropHelper(overlay).Handle,out var overlayBounds);
                if(overlayBounds.Bounds!=desktop) throw new InvalidOperationException("Overlay does not cover actual virtual desktop");
                for(int i=0;i<displays.Count;i++) {
                    var display=displays[i]; var target=new D.Rectangle(display.Bounds.X+display.Bounds.Width/4,display.Bounds.Y+display.Bounds.Height/4,120,90);
                    var start=overlay.PointFromScreen(new System.Windows.Point(target.Left,target.Top)); var end=overlay.PointFromScreen(new System.Windows.Point(target.Right,target.Bottom));
                    var actual=CaptureOverlay.MapSelection(start,end,overlay.ActualWidth,overlay.ActualHeight,desktop);
                    if(actual!=target) throw new InvalidOperationException($"Selection mismatch on {display.Device}: {actual} != {target}");
                    using var crop=Imaging.Crop(snapshot,new D.Rectangle(actual.X-desktop.X,actual.Y-desktop.Y,actual.Width,actual.Height));
                    var c=crop.GetPixel(60,45); var expected=colors[i%colors.Length];
                    if(Math.Abs(c.R-expected.R)>3||Math.Abs(c.G-expected.G)>3||Math.Abs(c.B-expected.B)>3) throw new InvalidOperationException("Unexpected captured pixels on "+display.Device);
                    crop.Save(Path.Combine(folder,$"monitor-{i+1}.png"));
                    lines.Add($"PASS: {display.Device} actual DPI {dpiValues[i]} ({dpiValues[i]/96d:P0}), selection {actual}, captured 120x90 exact expected pixels");
                }
                // A selection spanning the whole desktop uses the same live WPF screen transform.
                var a=overlay.PointFromScreen(new System.Windows.Point(desktop.Left+30,desktop.Top+30));
                var b=overlay.PointFromScreen(new System.Windows.Point(desktop.Right-30,desktop.Bottom-30));
                var spanning=CaptureOverlay.MapSelection(a,b,overlay.ActualWidth,overlay.ActualHeight,desktop);
                if(spanning!=new D.Rectangle(desktop.Left+30,desktop.Top+30,desktop.Width-60,desktop.Height-60)) throw new InvalidOperationException("Cross-display selection mismatch");
                lines.Add("PASS: cross-display selection bounds mapped to physical pixels");
                if(dpiValues.Distinct().Count()<2) throw new InvalidOperationException("Two displays active, but same DPI; mixed-DPI condition not established");
                lines.Add("PASS: real mixed-DPI desktop condition established"); exitCode=0;
            } catch(Exception ex) { lines.Add("FAIL: "+ex); }
            finally {
                foreach(var window in windows.AsEnumerable().Reverse()) { try { window.Close(); } catch { } }
                if(changed&&original!=null) {
                    int restored=original.Restore(); lines.Add("Restore exact original display configuration: "+restored);
                    if(restored!=0) { lines.Add("Fallback external-only restore: "+SetDisplayConfig(0,IntPtr.Zero,0,IntPtr.Zero,0x88)); exitCode=1; }
                    await Task.Delay(800); lines.Add("Restored displays: "+JsonSerializer.Serialize(Displays()));
                }
                original?.Dispose(); File.WriteAllLines(Path.Combine(folder,"display-tests.txt"),lines); app.Shutdown(exitCode);
            }
        };
        return app.Run();
    }
}
