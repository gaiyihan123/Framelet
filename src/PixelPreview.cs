using System;
using System.Windows;
using System.Windows.Media;

namespace QingCapture;

internal static class PixelPreview
{
    // Zoom is a physical pixel ratio, not WPF device-independent units.
    public static Size Size(int width,int height,double zoom,DpiScale dpi)=>new(width*zoom/dpi.DpiScaleX,height*zoom/dpi.DpiScaleY);
    public static Point Pixel(Point position,double zoom,DpiScale dpi)=>new(position.X*dpi.DpiScaleX/zoom,position.Y*dpi.DpiScaleY/zoom);
    public static Point Pixel(Point position,Size sourcePixels,Size rendered)
    {
        if(rendered.Width<=0||rendered.Height<=0)return new Point();
        return new Point(position.X*sourcePixels.Width/rendered.Width,position.Y*sourcePixels.Height/rendered.Height);
    }
    public static Rect Selection(Rect pixels,Size sourcePixels,Size rendered)=>new(pixels.X*rendered.Width/sourcePixels.Width,pixels.Y*rendered.Height/sourcePixels.Height,pixels.Width*rendered.Width/sourcePixels.Width,pixels.Height*rendered.Height/sourcePixels.Height);
    public static double Fit(int width,int height,double availableWidth,double availableHeight,DpiScale dpi)=>Math.Min(1,Math.Min(Math.Max(1,availableWidth)*dpi.DpiScaleX/width,Math.Max(1,availableHeight)*dpi.DpiScaleY/height));
    public static BitmapScalingMode Sampling(double zoom)=>zoom>=1 && Math.Abs(zoom-Math.Round(zoom))<.00001?BitmapScalingMode.NearestNeighbor:BitmapScalingMode.HighQuality;
    public static string ZoomLabel(double zoom)
    {
        if(zoom==1)return "100% · 1:1";
        double percent=Math.Round(zoom*100,2,MidpointRounding.AwayFromZero);
        if(percent==100)return zoom<1?"<100%":">100%";
        return $"{percent:0.##}%";
    }
}
