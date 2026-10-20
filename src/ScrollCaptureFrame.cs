using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using D=System.Drawing;

namespace QingCapture;

// A transparent native window preserves the selection while the real app scrolls.
// Its ring is wholly outside the captured pixels, even without capture exclusion.
internal sealed class ScrollCaptureFrame : Window
{
    private readonly D.Rectangle area;
    private readonly System.Windows.Shapes.Rectangle ring=new() {Stroke=Ui.Primary,StrokeThickness=2,IsHitTestVisible=false};
    internal D.Rectangle SelectionBounds=>area;
    internal ScrollCaptureFrame(D.Rectangle selection)
    {
        area=selection;Title=AppBrand.Title("长截图选区");WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=false;ShowActivated=false;IsHitTestVisible=false;Content=ring;
        SourceInitialized+=(_,_)=> {
            var hwnd=new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(hwnd,-20,new IntPtr(Native.GetWindowLongPtr(hwnd,-20).ToInt64()|0x08000000|0x20|0x80));
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _,int message,IntPtr w,IntPtr l,ref bool handled)=> {if(message==0x84){handled=true;return new IntPtr(-1);}if(message==0x21){handled=true;return new IntPtr(3);}return IntPtr.Zero;});
            Place();
        };
        Loaded+=(_,_)=>Place();DpiChanged+=(_,_)=>Place();
    }
    private void Place()
    {
        var hwnd=new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd,new IntPtr(-1),area.Left-3,area.Top-3,area.Width+6,area.Height+6,0x10|0x40);
        var dpi=VisualTreeHelper.GetDpi(this);ring.StrokeThickness=2/Math.Max(dpi.DpiScaleX,dpi.DpiScaleY);
        ring.Margin=new Thickness(0); // At outer HWND edge: no stroke touches the selection.
    }
}
