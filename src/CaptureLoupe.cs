using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shape=System.Windows.Shapes;
using D=System.Drawing;

namespace QingCapture;

// Reads the immutable screenshot, never the screen containing our own controls.
internal sealed class CaptureLoupe : Border
{
    internal const int Columns=25,Rows=17,Zoom=8;
    private readonly BitmapSource source;
    private readonly WriteableBitmap preview=new(Columns,Rows,96,96,PixelFormats.Bgra32,null);
    private readonly byte[] pixels=new byte[Columns*Rows*4];
    private readonly TextBlock position=Ui.Text("",12,Ui.OnPrimary),value=Ui.Text("",13,Ui.OnPrimary),hint=Ui.Text("按 C 复制颜色值",11,Ui.OnPrimary);
    private readonly Border swatch=new() {Width=16,Height=16,BorderBrush=Brushes.White,BorderThickness=new Thickness(1),Margin=new Thickness(0,0,8,0)};
    internal D.Point SamplePosition {get;private set;}
    internal Color SampleColor {get;private set;}
    internal bool HasSample {get;private set;}
    internal bool IsRgb {get;private set;}=true;
    internal string ColorText=>IsRgb?$"RGB({SampleColor.R}, {SampleColor.G}, {SampleColor.B})":$"#{SampleColor.R:X2}{SampleColor.G:X2}{SampleColor.B:X2}";
    internal BitmapSource Preview=>preview;
    internal CaptureLoupe(BitmapSource screenshot)
    {
        source=screenshot.Format==PixelFormats.Bgra32?screenshot:new FormatConvertedBitmap(screenshot,PixelFormats.Bgra32,null,0);source.Freeze();
        Name="CapturePixelLoupe";Width=222;Background=Ui.Brush("#EE202736");BorderBrush=Ui.Brush("#8CFFFFFF");BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(7);Padding=new Thickness(9);IsHitTestVisible=false;Focusable=false;ClipToBounds=true;Visibility=Visibility.Collapsed;
        var body=new StackPanel {Width=Columns*Zoom+2};
        var image=new Image {Source=preview,Width=Columns*Zoom,Height=Rows*Zoom,Stretch=Stretch.Fill};RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor);
        var canvas=new Canvas {Width=Columns*Zoom,Height=Rows*Zoom,ClipToBounds=true};canvas.Children.Add(image);
        double cx=(Columns/2+.5)*Zoom,cy=(Rows/2+.5)*Zoom;
        canvas.Children.Add(new Shape.Line {X1=0,Y1=cy,X2=Columns*Zoom,Y2=cy,Stroke=Ui.Brush("#99FFFFFF"),StrokeThickness=1});
        canvas.Children.Add(new Shape.Line {X1=cx,Y1=0,X2=cx,Y2=Rows*Zoom,Stroke=Ui.Brush("#99FFFFFF"),StrokeThickness=1});
        var center=new Shape.Rectangle {Width=Zoom,Height=Zoom,Stroke=Brushes.White,StrokeThickness=1.5};Canvas.SetLeft(center,Columns/2*Zoom);Canvas.SetTop(center,Rows/2*Zoom);canvas.Children.Add(center);
        body.Children.Add(new Border {Child=canvas,BorderBrush=Ui.Border,BorderThickness=new Thickness(1)});
        position.HorizontalAlignment=HorizontalAlignment.Center;position.FontFamily=new FontFamily("Consolas");position.Margin=new Thickness(0,6,0,5);body.Children.Add(position);
        var colorRow=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,7)};colorRow.Children.Add(swatch);value.FontFamily=new FontFamily("Consolas");colorRow.Children.Add(value);body.Children.Add(colorRow);
        hint.Margin=new Thickness(0);hint.HorizontalAlignment=HorizontalAlignment.Center;body.Children.Add(hint);
        var help=Ui.Text("按 Shift 切换 RGB/HEX",11,Ui.OnPrimary);help.Margin=new Thickness(0,2,0,0);help.HorizontalAlignment=HorizontalAlignment.Center;body.Children.Add(help);Child=new Viewbox {Child=body,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly};
    }
    internal void UpdateSample(D.Point physical,D.Rectangle desktop)
    {
        int x=Math.Clamp(physical.X-desktop.X,0,source.PixelWidth-1),y=Math.Clamp(physical.Y-desktop.Y,0,source.PixelHeight-1);
        var clamped=new D.Point(desktop.X+x,desktop.Y+y);
        if(HasSample&&clamped==SamplePosition){Visibility=Visibility.Visible;return;}
        int left=Math.Max(0,x-Columns/2),top=Math.Max(0,y-Rows/2),right=Math.Min(source.PixelWidth,x+Columns/2+1),bottom=Math.Min(source.PixelHeight,y+Rows/2+1);
        int width=right-left,height=bottom-top;var sample=new byte[width*height*4];source.CopyPixels(new Int32Rect(left,top,width,height),sample,width*4,0);
        for(int row=0;row<Rows;row++)for(int col=0;col<Columns;col++) {
            int sx=Math.Clamp(x+col-Columns/2,left,right-1)-left,sy=Math.Clamp(y+row-Rows/2,top,bottom-1)-top;
            Buffer.BlockCopy(sample,(sy*width+sx)*4,pixels,(row*Columns+col)*4,4);
        }
        preview.WritePixels(new Int32Rect(0,0,Columns,Rows),pixels,Columns*4,0);
        int middle=((Rows/2)*Columns+Columns/2)*4;SampleColor=Color.FromArgb(pixels[middle+3],pixels[middle+2],pixels[middle+1],pixels[middle]);SamplePosition=clamped;HasSample=true;
        position.Text=$"({clamped.X}, {clamped.Y})";swatch.Background=new SolidColorBrush(Color.FromRgb(SampleColor.R,SampleColor.G,SampleColor.B));value.Text=ColorText;hint.Text="按 C 复制颜色值";Visibility=Visibility.Visible;
    }
    internal void ToggleFormat(){IsRgb=!IsRgb;value.Text=ColorText;hint.Text="按 C 复制颜色值";}
    internal void ShowCopyResult(string message)=>hint.Text=message;
    internal void HideSample(){HasSample=false;Visibility=Visibility.Collapsed;}
    internal static D.Point MapPixel(Point point,Size surface,D.Rectangle desktop)=>new(desktop.X+Math.Clamp((int)Math.Floor(point.X*desktop.Width/Math.Max(1,surface.Width)),0,desktop.Width-1),desktop.Y+Math.Clamp((int)Math.Floor(point.Y*desktop.Height/Math.Max(1,surface.Height)),0,desktop.Height-1));
    internal static Rect CardBounds(Point pointer,Rect monitor,Size requested,Rect controls)
    {
        var safe=new Rect(monitor.Left+6,monitor.Top+6,Math.Max(1,monitor.Width-12),Math.Max(1,monitor.Height-12));
        double width=Math.Min(requested.Width,safe.Width),height=Math.Min(requested.Height,safe.Height);
        var candidates=new[]{new Point(pointer.X+24,pointer.Y+24),new Point(pointer.X-width-24,pointer.Y+24),new Point(pointer.X+24,pointer.Y-height-24),new Point(pointer.X-width-24,pointer.Y-height-24)};
        foreach(var at in candidates){var rect=new Rect(at,new Size(width,height));if(safe.Contains(rect)&&!rect.IntersectsWith(controls))return rect;}
        return new Rect(Math.Clamp(pointer.X+24,safe.Left,safe.Right-width),Math.Clamp(pointer.Y+24,safe.Top,safe.Bottom-height),width,height);
    }
}
