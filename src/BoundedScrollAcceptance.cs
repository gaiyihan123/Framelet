using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using W=System.Windows;
using Controls=System.Windows.Controls;
using Interop=System.Windows.Interop;
using Media=System.Windows.Media;
using ImagingW=System.Windows.Media.Imaging;

namespace QingCapture;

// The source paints an independently numbered twelve-screen document. Its
// composer is outside the user's rectangle, and the test never scrolls it
// except in response to production native wheel delivery.
internal static class BoundedScrollAcceptance
{
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window,bool enabled);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    internal static List<string> Run(string? folder=null)
    {
        var lines=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        RunNativeLongChat(Check,folder);
        return lines;
    }

    private static void RunNativeLongChat(Action<bool,string> check,string? folder)
    {
        const int width=800,height=650,bodyTop=20,bodyHeight=430,column=300,chosenWidth=300;
        const int documentHeight=bodyHeight*12+1,maximum=documentHeight-bodyHeight;
        var app=W.Application.Current as App??new App();app.ShutdownMode=W.ShutdownMode.OnExplicitShutdown;Ui.Resources(app);
        using var document=Document(width,documentHeight,column);
        var image=new Controls.Image {Stretch=Media.Stretch.Fill,UseLayoutRounding=true,SnapsToDevicePixels=true};
        var source=new W.Window {Title="Framelet bounded long-chat acceptance",Content=image,WindowStyle=W.WindowStyle.None,ResizeMode=W.ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,Background=Media.Brushes.Black,UseLayoutRounding=true,SnapsToDevicePixels=true,Width=width,Height=height};
        CaptureOverlay? overlay=null;DragCaptureController? controller=null;Bitmap? result=null;
        DispatcherTimer? begin=null,repaint=null;Exception? error=null;bool completed=false,blank=false;
        bool serial=true,insideSelection=true,stableFrameBeforeNext=true;int offset=0,blankCount=0,restoredCount=0;
        var delivered=new List<int>();var trace=new List<string>();Rectangle selected=Rectangle.Empty;
        int left=0,top=0;
        void Paint(bool blankBody)
        {
            using var frame=new Bitmap(width,height);using(var graphics=Graphics.FromImage(frame)) {
                graphics.CompositingMode=CompositingMode.SourceCopy;graphics.Clear(Color.FromArgb(43,43,43));
                if(!blankBody)graphics.DrawImage(document,new Rectangle(0,bodyTop,width,bodyHeight),new Rectangle(0,offset,width,bodyHeight),GraphicsUnit.Pixel);
                using var composer=new SolidBrush(Color.FromArgb(255,219,19,153));graphics.FillRectangle(composer,0,500,width,150);
                graphics.CompositingMode=CompositingMode.SourceOver;
                using var labelFont=new Font("Segoe UI",18,GraphicsUnit.Pixel);
                graphics.DrawString("FIXED COMPOSER OUTSIDE THE CHOSEN RECTANGLE",labelFont,Brushes.White,20,540);
            }
            image.Source=Imaging.Source(frame);source.UpdateLayout();
        }
        try {
            Native.GetCursorPos(out var cursor);var monitor=System.Windows.Forms.Screen.FromPoint(new Point(cursor.X,cursor.Y)).WorkingArea;
            check(monitor.Width>=width&&monitor.Height>=height,"the native bounded-scroll fixture has room for its complete source window and separate composer");
            left=monitor.Left+Math.Min(24,monitor.Width-width);top=monitor.Top+Math.Min(24,monitor.Height-height);
            source.Show();var sourceHandle=new Interop.WindowInteropHelper(source).EnsureHandle();
            Native.SetWindowPos(sourceHandle,new IntPtr(-1),left,top,width,height,0x40);source.UpdateLayout();
            var dpi=Media.VisualTreeHelper.GetDpi(source);image.Width=width/dpi.DpiScaleX;image.Height=height/dpi.DpiScaleY;
            Media.RenderOptions.SetBitmapScalingMode(image,Media.BitmapScalingMode.NearestNeighbor);Paint(false);
            Interop.HwndSource.FromHwnd(sourceHandle)!.AddHook((IntPtr _,int message,IntPtr w,IntPtr l,ref bool handled)=> {
                if(message!=MouseWheelObserver.MouseWheelMessage)return IntPtr.Zero;
                int delta=unchecked((short)(w.ToInt64()>>16));
                int wheelX=unchecked((short)l.ToInt64()),wheelY=unchecked((short)(l.ToInt64()>>16));
                insideSelection&=selected.Contains(wheelX,wheelY);
                serial&=controller!=null&&controller.ScrollOffset==offset;
                stableFrameBeforeNext&=!blank;
                trace.Add($"dispatch {delivered.Count+1}: delta={delta}; sourceBefore={offset}; accepted={controller?.ScrollOffset}; blank={blank}; at={wheelX},{wheelY}");
                delivered.Add(delta);offset=Math.Clamp(offset-delta,0,maximum);
                if(delivered.Count==12) {
                    blank=true;blankCount++;Paint(true);
                    repaint=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(400)};
                    repaint.Tick+=(_,_)=>{repaint.Stop();blank=false;restoredCount++;Paint(false);};repaint.Start();
                }else Paint(false);
                handled=true;return IntPtr.Zero;
            });
            source.Activate();source.UpdateLayout();PumpFor(300);Native.SetWindowPos(sourceHandle,new IntPtr(-1),0,0,0,0,0x13);
            var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;using var snapshot=Imaging.Capture(desktop);
            overlay=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Scroll,_=>{});
            begin=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
            begin.Tick+=(_,_)=> {
                begin.Stop();
                try {
                    // Re-enable only this test source after WPF's modal window
                    // disabling, so native hit testing sees its actual content.
                    EnableWindow(sourceHandle,true);
                    check(IsWindowEnabled(sourceHandle),"the self-owned bounded-chat source is enabled for native wheel hit testing during the modal overlay");
                    var overlayHandle=new Interop.WindowInteropHelper(overlay).Handle;
                    Native.SetWindowPos(sourceHandle,overlayHandle,0,0,0,0,0x13);
                    W.Point Local(int pixelX,int pixelY)=>overlay.PointFromScreen(new W.Point(pixelX+.25,pixelY+.25));
                    int x=left+column,y=top+bodyTop;
                    overlay.BeginPointer(Local(x,y));overlay.MovePointer(Local(x+chosenWidth,y+bodyHeight));
                    selected=overlay.Selection;
                    check(selected==new Rectangle(x,y,chosenWidth,bodyHeight)&&overlay.DragCapture==null&&overlay.IsMouseCaptured,"the loaded overlay displays exactly the user's 300 by 430 rectangle above the excluded composer before any wheel input");
                    var sample=new MouseWheelSample(new Point(x+chosenWidth,y+bodyHeight),-120,101);
                    check(overlay.HandleDragWheel(sample),"the first manual wheel is accepted from the existing user-selected rectangle");controller=overlay.DragCapture;
                    check(controller!=null&&controller.ManualWheelMode&&controller.Viewport==selected&&controller.VisibleSelection==selected&&overlay.Selection==selected,"the first manual wheel freezes all four user-selected edges without expanding observation or output to the source-window bottom");
                    var deltas=Enumerable.Repeat(-120,39).Concat(new[]{-50,-1}).ToArray();
                    bool accepted=true;for(int i=1;i<deltas.Length;i++)accepted&=overlay.HandleDragWheel(sample with {Delta=deltas[i],NativeTime=unchecked((uint)(101+i))});
                    check(accepted&&deltas.Length>20&&controller!.PendingWheelCount>20,"more than twenty rapid consecutive wheel events are accepted into the same bounded capture session before source scrolling starts");
                    overlay.MovePointer(Local(left+width-5,top+height-2));
                    check(overlay.Selection==selected&&controller!.Viewport==selected&&controller.VisibleSelection==selected&&!controller.AutoScrolling,"pointer drift beneath the selected bottom cannot enlarge the frozen rectangle or trigger automatic scrolling during manual capture");
                    SaveOverlay(overlay,folder);
                    PumpFor(80); // Commit the live layered-window alpha before native hit testing.
                    var hit=new Native.Point {X=selected.Left+selected.Width/2,Y=selected.Top+selected.Height/2};
                    check(Native.WindowFromPoint(hit)==overlayHandle,"the rendered live overlay remains the native hit-test target over its otherwise empty interior so physical wheel input cannot bypass capture");
                    overlay.Background=Media.Brushes.Transparent;PumpFor(80);
                    check(Native.WindowFromPoint(hit)!=overlayHandle,"the former zero-alpha overlay is a negative control: native hit testing bypasses it over the selected interior");
                    overlay.Background=new Media.SolidColorBrush(Media.Color.FromArgb(1,0,0,0));PumpFor(80);
                    check(Native.WindowFromPoint(hit)==overlayHandle,"restoring nonzero input alpha restores the capture target without changing the user's rectangle or source pixels");
                    PumpUntil(()=>controller!.PendingWheelCount==0&&!controller.SamplingActive,45000);
                    overlay.EndPointer(Local(left+width-5,top+height-2));
                    check(overlay.Selection==selected&&controller!.Viewport==selected,"the final mouse-up point outside the rectangle also preserves the selected capture bounds");
                    PumpUntil(()=>!overlay.IsVisible,45000);result=overlay.TakeDragResult();
                    if(folder!=null) {
                        Directory.CreateDirectory(folder);File.WriteAllLines(Path.Combine(folder,"bounded-chat-native-dispatch.txt"),trace);
                        if(result!=null)Imaging.Save(result,Path.Combine(folder,"bounded-chat-native-actual.png"));
                    }
                    check(blankCount==1&&restoredCount==1&&stableFrameBeforeNext&&serial,"a stable blank frame mid-document is retried in place and resolved before the next queued wheel is sent, preserving every accepted checkpoint");
                    check(delivered.Count==deltas.Length&&delivered.SequenceEqual(deltas)&&delivered.Sum(delta=>(long)delta)==-maximum&&offset==maximum&&insideSelection,"the production native queue reaches the actual twelve-screen source end in order with delivery points inside the chosen rectangle and a separate final 1px step");
                    check(controller!.Error==null&&overlay.DialogResult==true&&result!=null&&result.Size==new Size(chosenWidth,documentHeight)&&controller.ScrollOffset==maximum,"all twelve screens are confirmed while held and release preserves the complete long-chat image");
                    using var golden=Imaging.Crop(document,new Rectangle(column,0,chosenWidth,documentHeight));
                    if(folder!=null)Imaging.Save(golden,Path.Combine(folder,"bounded-chat-native-golden.png"));
                    check(result!=null&&Equal(result,golden),"the twelve-screen capture equals the independently generated golden document pixel for pixel and includes no pixels from the external fixed composer");
                    bool everyNumber=true;int numberCount=0;
                    if(result!=null)for(int row=20;row<documentHeight-40;row+=67) {
                        using var actual=Imaging.Crop(result,new Rectangle(0,row,chosenWidth,32));using var expected=Imaging.Crop(golden,new Rectangle(0,row,chosenWidth,32));
                        everyNumber&=Equal(actual,expected);numberCount++;
                    }
                    check(everyNumber&&numberCount>60,"every independently numbered chat band appears once, in order, across all twelve screens");
                    using(var tail=Imaging.Crop(result!,new Rectangle(0,documentHeight-1,chosenWidth,1)))
                    using(var goldenTail=Imaging.Crop(golden,new Rectangle(0,documentHeight-1,chosenWidth,1)))
                        check(Equal(tail,goldenTail),"the final single physical pixel is present after the long queue and transient-frame recovery");
                    check(controller.ImagesReleased&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!controller.RangeProbeActive&&!overlay.IsMouseCaptured&&overlay.TakeDragResult()==null,"bounded completion releases the queue, timers, frames and native mouse capture and transfers the image only once");
                }catch(Exception ex){error=ex;}
                finally{if(overlay.IsVisible)overlay.Close();completed=true;}
            };
            begin.Start();overlay.ShowDialog();if(!completed)PumpUntil(()=>completed,48000);
            if(error!=null)throw new InvalidOperationException("Native bounded long-chat acceptance failed",error);
        }finally {
            begin?.Stop();repaint?.Stop();overlay?.Close();controller?.Dispose();source.Close();result?.Dispose();
            if(folder!=null&&trace.Count>0){Directory.CreateDirectory(folder);File.WriteAllLines(Path.Combine(folder,"bounded-chat-native-dispatch.txt"),trace);}
        }
    }

    private static Bitmap Document(int width,int height,int column)
    {
        var bitmap=new Bitmap(width,height);using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.FromArgb(43,43,43));
        using var font=new Font("Segoe UI",14,GraphicsUnit.Pixel);using var small=new Font("Consolas",11,GraphicsUnit.Pixel);
        using var ink=new SolidBrush(Color.FromArgb(241,241,241));using var dim=new SolidBrush(Color.FromArgb(177,177,177));
        using var line=new Pen(Color.FromArgb(67,67,67));using var bubble=new SolidBrush(Color.FromArgb(48,105,188));
        for(int y=20,n=0;y<height-40;y+=67,n++) {
            if(n%3==1)graphics.FillRectangle(bubble,column+4,y-3,290,50);
            graphics.DrawString($"Chat {n:0000} / source {n*7919+37}",font,ink,column+10,y);
            graphics.DrawString($"keep order {n:0000} token {n*6151+17}",small,dim,column+10,y+21);
            graphics.DrawLine(line,column+5,y+52,column+294,y+52);
            // The two tiny source markers are part of the golden document, not
            // a stitched-image checksum or a value generated from the result.
            using var marker=new SolidBrush(Color.FromArgb(100+(n*29)%150,100+(n*47)%150,100+(n*71)%150));
            graphics.FillRectangle(marker,column+2,y+8,3,19);graphics.FillRectangle(marker,column+296,y+26,3,13);
        }
        using var final=new SolidBrush(Color.FromArgb(17,231,93));graphics.FillRectangle(final,0,height-1,width,1);
        return bitmap;
    }
    private static void SaveOverlay(CaptureOverlay overlay,string? folder)
    {
        if(folder==null)return;Directory.CreateDirectory(folder);overlay.UpdateLayout();var root=(W.FrameworkElement)overlay.Content;
        var rendered=new ImagingW.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,Media.PixelFormats.Pbgra32);rendered.Render(root);
        var encoder=new ImagingW.PngBitmapEncoder();encoder.Frames.Add(ImagingW.BitmapFrame.Create(rendered));using var stream=File.Create(Path.Combine(folder,"bounded-chat-overlay.png"));encoder.Save(stream);
    }
    private static bool Equal(Bitmap actual,Bitmap expected)=>actual.Size==expected.Size&&Imaging.Png(actual).SequenceEqual(Imaging.Png(expected));
    private static void PumpUntil(Func<bool> condition,int milliseconds)
    {
        var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<milliseconds)PumpFor(20);
        if(!condition())throw new TimeoutException("Bounded long-chat acceptance did not settle.");
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
}
