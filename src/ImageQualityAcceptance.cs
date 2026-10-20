using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using D=System.Drawing;

namespace QingCapture;

internal static class ImageQualityAcceptance
{
    public static List<string> Run(HomeWindow home,EditorWindow editor,string folder)
    {
        var lines=new List<string>();
        CheckEditorPixelPaths(folder,lines);
        if(PixelPreview.ZoomLabel(1)!="100% · 1:1"||PixelPreview.ZoomLabel(.996)!="99.6%"||PixelPreview.ZoomLabel(.99999)!="<100%"||PixelPreview.ZoomLabel(1.00001)!=">100%")throw new Exception("Fractional zoom was falsely labelled as physical 1:1");
        lines.Add("PASS: exact 100% is labelled 1:1; fractional zoom is not rounded into a false 100% label");
        using var fixture=new D.Bitmap(64,48);
        for(int y=0;y<48;y++)for(int x=0;x<64;x++)fixture.SetPixel(x,y,D.Color.FromArgb((x%2)*255,(y%2)*255,(x*13+y*7)%256));
        var source=Imaging.Source(fixture);
        foreach(double scale in new[]{1d,1.25,1.5,2d}) {
            var dpi=new DpiScale(scale,scale);var size=PixelPreview.Size(64,48,1,dpi);
            var image=new Image { Source=source,Width=size.Width,Height=size.Height,Stretch=Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image,PixelPreview.Sampling(1));
            image.Measure(size);image.Arrange(new Rect(size));image.UpdateLayout();
            var rendered=new RenderTargetBitmap(64,48,96*scale,96*scale,PixelFormats.Pbgra32);rendered.Render(image);
            var bytes=new byte[64*48*4];rendered.CopyPixels(bytes,64*4,0);
            for(int y=0;y<48;y++)for(int x=0;x<64;x++) {
                var c=fixture.GetPixel(x,y);int i=(y*64+x)*4;
                if(bytes[i]!=c.B||bytes[i+1]!=c.G||bytes[i+2]!=c.R||bytes[i+3]!=255)throw new Exception($"Pixel preview changed color/detail at {scale}: {x},{y}");
            }
            foreach(double zoom in new[]{.5,1,1.25,2d}) {
                var p=PixelPreview.Pixel(new Point(17*zoom/scale,23*zoom/scale),zoom,dpi);
                if(Math.Abs(p.X-17)>1e-9||Math.Abs(p.Y-23)>1e-9)throw new Exception("Annotation pixel mapping drift");
            }
            var fit=PixelPreview.Fit(800,2200,300,250,dpi);var fitted=PixelPreview.Size(800,2200,fit,dpi);
            if(fitted.Width>300.001||fitted.Height>250.001)throw new Exception("Tall image fit exceeds viewport");
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using(var file=File.Create(Path.Combine(folder,$"pixel-preview-{scale*100:0}.png")))encoder.Save(file);
            lines.Add($"PASS: {scale:P0} physical 1:1 preserves every RGB pixel; annotation mapping and tall-image fit correct");
        }
        if(PixelPreview.Sampling(.5)!=BitmapScalingMode.HighQuality||PixelPreview.Sampling(1.25)!=BitmapScalingMode.HighQuality||PixelPreview.Sampling(2)!=BitmapScalingMode.NearestNeighbor)throw new Exception("Incorrect resampling policy");
        lines.Add("PASS: fractional/downscaled previews use high quality; integer pixel zoom stays sharp");
        foreach(var dimensions in new[]{new D.Size(100,60000),new D.Size(60000,100)}) {
            using var extreme=new D.Bitmap(dimensions.Width,dimensions.Height);var compact=new EditorWindow(extreme,null,autoCopy:false);
            try {
                var root=(FrameworkElement)compact.Content;root.Measure(new Size(620,400));root.Arrange(new Rect(0,0,620,400));root.UpdateLayout();
                var preview=Descendants<Image>(root).Single(x=>x.Name=="EditorPreviewImage");
                double fit=PixelPreview.Fit(extreme.Width,extreme.Height,500,250,VisualTreeHelper.GetDpi(preview));compact.SetZoom(fit);root.UpdateLayout();
                if(fit>=.05||preview.Width>500.01||preview.Height>250.01)throw new Exception("Maximum-length image fit is still clamped to a large minimum zoom");
            } finally {compact.Close();}
            lines.Add($"PASS: {dimensions.Width}x{dimensions.Height} maximum-length image can fit below 5% without cropping or resampling the export");
        }
        var path=Path.Combine(folder,"lossless-quality-fixture.png");Imaging.Save(fixture,path);
        using(var saved=new D.Bitmap(path))using(var copied=Imaging.FromSource(source)) {
            if(saved.Size!=fixture.Size||copied.Size!=fixture.Size)throw new Exception("Image dimensions changed");
            for(int y=0;y<48;y++)for(int x=0;x<64;x++)if(saved.GetPixel(x,y)!=fixture.GetPixel(x,y)||copied.GetPixel(x,y)!=fixture.GetPixel(x,y))throw new Exception("PNG/clipboard conversion changed pixels");
        }
        lines.Add("PASS: PNG save and clipboard bitmap conversion preserve original dimensions and all RGB pixels");
        foreach(var window in new Window[]{home,editor}) {
            window.ShowActivated=false;window.Show();
            var handle=new WindowInteropHelper(window).EnsureHandle();
            foreach(int kind in new[]{0,1}) {
                var iconHandle=SendMessage(handle,0x007f,new IntPtr(kind),IntPtr.Zero);
                if(iconHandle==IntPtr.Zero)throw new Exception("Visible window has no native icon");
                using var nativeIcon=D.Icon.FromHandle(iconHandle);
                using var nativePixels=nativeIcon.ToBitmap();
                nativePixels.Save(Path.Combine(folder,$"native-{(window==home?"home":"editor")}-{(kind==0?"small":"big")}.png"),D.Imaging.ImageFormat.Png);
            }
            if(AppIdentity.WindowId(window)!=AppIdentity.Id)throw new Exception("Window-level taskbar identity mismatch");
            lines.Add($"PASS: {window.Title} has independent Framelet.Desktop window taskbar identity");
            if(AppIdentity.WindowIconResource(window)!=AppIdentity.ShellIconResource||AppIdentity.WindowRelaunchName(window)!="Framelet")throw new Exception("Taskbar relaunch icon or display name still resolves through the former EXE cache");
            lines.Add($"PASS: {window.Title} explicitly exposes Framelet's new F icon resource to the Windows taskbar");
            if(window==home) {
                if(window.ResizeMode!=ResizeMode.CanMinimize)throw new Exception("Home window still resizable");
                SendMessage(handle,0x0112,new IntPtr(0xf030),IntPtr.Zero);
                if(window.WindowState!=WindowState.Normal)throw new Exception("Home system maximize was not blocked");
                SendMessage(handle,0x00a3,new IntPtr(2),IntPtr.Zero);
                if(window.WindowState!=WindowState.Normal)throw new Exception("Home caption double click maximized window");
                window.WindowState=WindowState.Maximized;
                if(window.WindowState!=WindowState.Normal)throw new Exception("Home programmatic maximize was not restored");
                lines.Add("PASS: home remains a small window after system maximize, caption double click and state change");
            }else CheckEditorCaption(editor,folder,lines);
            window.Hide();
        }
        var bar=((DockPanel)editor.Content).Children.OfType<Grid>().Single(x=>x.Name=="EditorTitlebar");
        var controls=bar.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
        if(!controls.Select(AutomationProperties.GetName).SequenceEqual(new[]{"最小化","最大化","关闭"}))throw new Exception("Editor caption actions mismatch");
        lines.Add("PASS: editor titlebar exposes minimize, maximize and close");
        return lines;
    }
    private static void CheckEditorCaption(EditorWindow editor,string folder,List<string> lines)
    {
        if(editor.ResizeMode!=ResizeMode.CanResize)throw new Exception("Editor cannot resize or maximize");
        var bar=((DockPanel)editor.Content).Children.OfType<Grid>().Single(x=>x.Name=="EditorTitlebar");
        var buttons=bar.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
        var maximize=buttons.Single(button=>AutomationProperties.GetName(button)=="最大化");
        if(buttons.Length!=3||buttons.Any(button=>!WindowChrome.GetIsHitTestVisibleInChrome(button)))throw new Exception("Editor caption buttons are not clickable in window chrome");
        var normal=new Rect(editor.Left,editor.Top,editor.Width,editor.Height);
        void Settle() {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(150) };
            timer.Tick+=(_,_)=> {timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);editor.UpdateLayout();
        }
        void Snapshot(string name) {
            var root=(FrameworkElement)editor.Content;var dpi=VisualTreeHelper.GetDpi(root);
            var rendered=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);rendered.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using var file=File.Create(Path.Combine(folder,name+".png"));encoder.Save(file);
        }
        Settle();Snapshot("editor-window-normal");
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Settle();
        if(editor.WindowState!=WindowState.Maximized||AutomationProperties.GetName(maximize)!="还原"||maximize.ToolTip?.ToString()!="还原")throw new Exception("Editor maximize button failed to maximize or expose restore");
        Snapshot("editor-window-maximized");
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Settle();
        if(editor.WindowState!=WindowState.Normal||AutomationProperties.GetName(maximize)!="最大化")throw new Exception("Editor restore button failed");
        var restored=new Rect(editor.Left,editor.Top,editor.Width,editor.Height);
        if(Math.Abs(restored.X-normal.X)>1||Math.Abs(restored.Y-normal.Y)>1||Math.Abs(restored.Width-normal.Width)>1||Math.Abs(restored.Height-normal.Height)>1)throw new Exception("Editor restore lost its normal window bounds");
        Snapshot("editor-window-restored");
        lines.Add("PASS: editor maximize/restore caption button changes state and accessible label, then restores original window bounds");
        var handle=new WindowInteropHelper(editor).Handle;
        SendMessage(handle,0x0112,new IntPtr(0xf030),IntPtr.Zero);Settle();
        if(editor.WindowState!=WindowState.Maximized)throw new Exception("Editor system maximize is blocked");
        SendMessage(handle,0x00a3,new IntPtr(2),IntPtr.Zero);Settle();
        if(editor.WindowState!=WindowState.Normal)throw new Exception("Editor caption double click failed to restore");
        SendMessage(handle,0x00a3,new IntPtr(2),IntPtr.Zero);Settle();
        if(editor.WindowState!=WindowState.Maximized)throw new Exception("Editor caption double click failed to maximize");
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Settle();
        lines.Add("PASS: editor supports native system maximize and titlebar double-click maximize/restore");
    }
    private static void CheckEditorPixelPaths(string folder,List<string> lines)
    {
        const int width=445,height=391;
        var expected=new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++) {
            int i=(y*width+x)*4;expected[i]=(byte)((x*13+y*7)%256);expected[i+1]=(byte)((y%2)*255);expected[i+2]=(byte)((x%2)*255);expected[i+3]=255;
        }
        using var fixture=new D.Bitmap(width,height,D.Imaging.PixelFormat.Format32bppArgb);
        var data=fixture.LockBits(new D.Rectangle(0,0,width,height),D.Imaging.ImageLockMode.WriteOnly,D.Imaging.PixelFormat.Format32bppArgb);
        try { Marshal.Copy(expected,0,data.Scan0,expected.Length); } finally { fixture.UnlockBits(data); }
        Imaging.Save(fixture,Path.Combine(folder,"odd-original.png"));
        foreach(double scale in new[]{1d,1.25,1.5,2d})foreach(int parity in new[]{0,1,2}) {
            var dpi=new DpiScale(scale,scale);
            var editor=new EditorWindow(fixture,null,autoCopy:false);
            try {
                var root=(FrameworkElement)editor.Content;
                VisualTreeHelper.SetRootDpi(root,dpi);
                int extra=parity==1?1:0;
                int targetWidth=(int)(1120*scale)+extra,targetHeight=(int)(800*scale)+extra;
                var size=new Size(targetWidth/scale,targetHeight/scale);
                void Layout() { root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout(); }
                Layout();
                Descendants<Button>(root).Single(button=>AutomationProperties.GetName(button)=="1:1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout();
                var viewer=Descendants<ScrollViewer>(root).Single(element=>element.Name=="EditorPreviewScroll");
                if(parity==2) {
                    viewer.Width=320/scale;viewer.Height=260/scale;Layout();
                    viewer.ScrollToHorizontalOffset(13.5/scale);viewer.ScrollToVerticalOffset(17.5/scale);Layout();Layout();
                    if(viewer.HorizontalOffset<=0||viewer.VerticalOffset<=0)throw new Exception("Production scroll probe did not scroll");
                }
                var image=Descendants<Image>(root).Single(element=>element.Name=="EditorPreviewImage");
                var actualDpi=VisualTreeHelper.GetDpi(image);
                if(Math.Abs(actualDpi.DpiScaleX-scale)>1e-9||Math.Abs(actualDpi.DpiScaleY-scale)>1e-9)throw new Exception("Production preview did not inherit the test root DPI");
                var origin=image.TransformToAncestor(root).Transform(new Point());
                double px=origin.X*scale,py=origin.Y*scale,pw=image.RenderSize.Width*scale,ph=image.RenderSize.Height*scale;
                bool aligned=Math.Abs(pw-width)<=.001&&Math.Abs(ph-height)<=.001&&Math.Abs(px-Math.Round(px))<=.001&&Math.Abs(py-Math.Round(py))<=.001;
                var rendered=new RenderTargetBitmap(targetWidth,targetHeight,96*scale,96*scale,PixelFormats.Pbgra32);rendered.Render(root);
                var bytes=new byte[targetWidth*targetHeight*4];rendered.CopyPixels(bytes,targetWidth*4,0);
                int left=(int)Math.Round(px),top=(int)Math.Round(py);
                int fromX=0,fromY=0,toX=width,toY=height;
                if(parity==2) {
                    var presenter=Descendants<ScrollContentPresenter>(viewer).Single();
                    var clip=presenter.TransformToAncestor(root).TransformBounds(new Rect(new Point(),presenter.RenderSize));
                    fromX=Math.Max(0,(int)Math.Ceiling(clip.Left*scale)+2-left);fromY=Math.Max(0,(int)Math.Ceiling(clip.Top*scale)+2-top);
                    toX=Math.Min(width,(int)Math.Floor(clip.Right*scale)-2-left);toY=Math.Min(height,(int)Math.Floor(clip.Bottom*scale)-2-top);
                    if(toX-fromX<20||toY-fromY<20)throw new Exception("Production scroll probe has no useful visible crop");
                }
                int different=0,interiorDifferent=0;
                for(int y=fromY;y<toY;y++)for(int x=fromX;x<toX;x++) {
                    int a=((top+y)*targetWidth+left+x)*4,b=(y*width+x)*4;
                    if(bytes[a]!=expected[b]||bytes[a+1]!=expected[b+1]||bytes[a+2]!=expected[b+2]||bytes[a+3]!=expected[b+3]) {different++;if(x>0&&x<width-1&&y>0&&y<height-1)interiorDifferent++;}
                }
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));
                using(var file=File.Create(Path.Combine(folder,$"editor-pixel-path-{scale*100:0}-{parity}.png")))encoder.Save(file);
                if(!aligned||different!=0)throw new Exception($"Production 445x391 preview changes pixels at {scale:P0}, parity={parity}: origin={px},{py}; size={pw},{ph}; layoutRounding={image.UseLayoutRounding}; differentPixels={different}; interiorDifferentPixels={interiorDifferent}");
                lines.Add($"PASS: production editor 445x391 image preserves every {(parity==2?"visible ":"")}pixel at {scale:P0} with {(parity==2?"fractional scroll input":parity==0?"even viewport":"odd viewport")} and pixel-aligned origin");
                if(parity==0)foreach(double zoom in new[]{.05,.5,.996,1.25}) {
                    editor.SetZoom(zoom);Layout();
                    foreach(var point in new[]{new D.Point(0,0),new D.Point(17,23),new D.Point(width/2,height/2),new D.Point(width-1,height-1)}) {
                        var shown=new Point((point.X+.5)*image.RenderSize.Width/width,(point.Y+.5)*image.RenderSize.Height/height);
                        if(editor.Pixel(shown)!=point)throw new Exception($"Production annotation mapping drift at DPI {scale:P0}, zoom={zoom}, source={point}, renderSize={image.RenderSize}");
                    }
                    var selection=PixelPreview.Selection(new Rect(17,23,100,90),new Size(width,height),image.RenderSize);
                    var inset=new Vector(image.RenderSize.Width/width/2,image.RenderSize.Height/height/2);
                    if(editor.Pixel(selection.TopLeft+inset)!=new D.Point(17,23)||editor.Pixel(selection.BottomRight-inset)!=new D.Point(116,112))throw new Exception("Selection overlay no longer matches the actual rounded image geometry");
                    lines.Add($"PASS: production annotation and selection mapping reaches all source edges at DPI {scale:P0}, zoom={zoom:P2}, using actual rounded render size");
                }
            } finally { editor.Close(); }
        }
        CheckLiveEditor(fixture,folder,lines);
    }
    private static void CheckLiveEditor(D.Bitmap fixture,string folder,List<string> lines)
    {
        var editor=new EditorWindow(fixture,null,autoCopy:false) { ShowActivated=false,Topmost=true };
        try {
            editor.Show();
            var frame=new DispatcherFrame();var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(250) };
            timer.Tick+=(_,_)=> { timer.Stop();frame.Continue=false; };timer.Start();Dispatcher.PushFrame(frame);
            var root=(FrameworkElement)editor.Content;editor.SetZoom(1);root.UpdateLayout();
            var image=Descendants<Image>(root).Single(element=>element.Name=="EditorPreviewImage");var dpi=VisualTreeHelper.GetDpi(image);
            var origin=image.PointToScreen(new Point());
            if(Math.Abs(origin.X-Math.Round(origin.X))>.001||Math.Abs(origin.Y-Math.Round(origin.Y))>.001||Math.Abs(image.RenderSize.Width*dpi.DpiScaleX-fixture.Width)>.001||Math.Abs(image.RenderSize.Height*dpi.DpiScaleY-fixture.Height)>.001)throw new Exception("Live editor image is not physical 1:1 on the actual desktop");
            using var screen=Imaging.Capture(new D.Rectangle((int)Math.Round(origin.X),(int)Math.Round(origin.Y),fixture.Width,fixture.Height));
            Imaging.Save(screen,Path.Combine(folder,$"live-editor-pixels-{dpi.DpiScaleX*100:0}.png"));
            for(int y=0;y<fixture.Height;y++)for(int x=0;x<fixture.Width;x++) {
                var actual=screen.GetPixel(x,y);var expected=fixture.GetPixel(x,y);
                if(actual.R!=expected.R||actual.G!=expected.G||actual.B!=expected.B)throw new Exception($"Actual desktop preview changed RGB at DPI {dpi.DpiScaleX:P0}, pixel={x},{y}, actual={actual}, expected={expected}");
            }
            lines.Add($"PASS: visible production editor on the actual {dpi.DpiScaleX:P0} desktop preserves every RGB pixel at physical 1:1");
        } finally { editor.Close(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i);if(child is T value)yield return value;foreach(var nested in Descendants<T>(child))yield return nested; }
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
}
