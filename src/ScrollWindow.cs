using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using D=System.Drawing;
using Forms=System.Windows.Forms;

namespace QingCapture;

internal sealed class ScrollWindow : Window
{
    private readonly D.Rectangle area;
    private readonly Action<D.Bitmap> finished;
    private readonly Func<D.Rectangle,D.Bitmap> capture;
    private readonly WheelCaptureGate gate;
    private readonly TextBlock summary=Ui.Text("长截图",12);
    private readonly TextBlock status=Ui.Text("准备开始…",11,Ui.Muted);
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(45) };
    private readonly Button finishButton;
    private MouseWheelObserver? observer;
    private ScrollCaptureFrame? selectionFrame;
    private readonly MouseWheelSample? firstWheel;
    private readonly Action<MouseWheelSample>? forwardInitialWheel;
    private readonly HeldDragScrollGuard? initialDrag;
    private readonly bool fromHeldSelection;
    private D.Bitmap? composite,last;
    private bool busy,closed,finishing,paused;
    private int count=1;

    public ScrollWindow(D.Rectangle bounds,Action<D.Bitmap> completed) : this(bounds,completed,Imaging.Capture) { }
    internal ScrollWindow(D.Rectangle bounds,Action<D.Bitmap> completed,Func<D.Rectangle,D.Bitmap> captureFrame,D.Bitmap? initialFrame=null,MouseWheelSample? initialWheel=null,Action<MouseWheelSample>? forwardInitialWheel=null,HeldDragScrollGuard? initialDrag=null,bool fromHeldSelection=false)
    {
        area=bounds;finished=completed;capture=captureFrame;gate=new WheelCaptureGate(bounds,fromHeldSelection);this.initialDrag=initialDrag;this.fromHeldSelection=fromHeldSelection;
        firstWheel=initialWheel;this.forwardInitialWheel=forwardInitialWheel;
        if(initialFrame!=null){if(initialFrame.Size!=bounds.Size)throw new ArgumentException("Initial long screenshot size differs from its selection.");last=new D.Bitmap(initialFrame);composite=new D.Bitmap(initialFrame);}
        Title=AppBrand.Title("滚轮长截图");Width=284;Height=58;ResizeMode=ResizeMode.NoResize;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowActivated=false;ShowInTaskbar=false;
        var row=new Grid { Margin=new Thickness(8,6,6,6) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var text=new StackPanel { VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,8,0) };
        summary.Margin=new Thickness(0,0,0,2);summary.FontWeight=FontWeights.SemiBold;
        summary.TextWrapping=status.TextWrapping=TextWrapping.NoWrap;
        summary.TextTrimming=status.TextTrimming=TextTrimming.CharacterEllipsis;
        status.Margin=new Thickness(0);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);
        text.Children.Add(summary);text.Children.Add(status);row.Children.Add(text);
        finishButton=IconButton("完成","M 4,12 L 9,17 20,6",Finish,true,"完成长截图（Enter）");finishButton.IsEnabled=composite!=null;
        Grid.SetColumn(finishButton,1);row.Children.Add(finishButton);
        var cancel=IconButton("取消","M 5,5 L 19,19 M 19,5 L 5,19",Close,false,"取消（Esc）");
        Grid.SetColumn(cancel,2);row.Children.Add(cancel);
        Content=new Border { Background=Ui.Surface,BorderBrush=Ui.Border,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),Child=row };
        SourceInitialized+=(_,_)=> {
            var hwnd=new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(hwnd,-20,new IntPtr(Native.GetWindowLongPtr(hwnd,-20).ToInt64()|0x08000000));
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _,int message,IntPtr w,IntPtr l,ref bool handled)=> {if(message==0x21){handled=true;return new IntPtr(3);}return IntPtr.Zero;});
        };
        Loaded+=async(_,_)=>await Start();
        timer.Tick+=async(_,_)=>await Tick();
        PreviewKeyDown+=(_,e)=> {
            if(e.Key==Key.Escape) { e.Handled=true;Close(); }
            else if(e.Key==Key.Enter&&!(Keyboard.FocusedElement is Button button&&AutomationProperties.GetName(button)=="取消")) { e.Handled=true;Finish(); }
        };
        Closed+=(_,_)=> {
            closed=true;timer.Stop();gate.Dispose();observer?.Dispose();observer=null;
            initialDrag?.Dispose();
            selectionFrame?.Close();selectionFrame=null;
            if(!busy)DisposeImages();
        };
    }

    internal bool ObserverAttached=>observer?.IsInstalled==true;
    internal bool SamplingActive=>busy||timer.IsEnabled;
    internal int CaptureCount { get; private set; }
    internal int FrameCount=>count;
    internal string StatusText=>status.Text;
    internal bool ImagesReleased=>composite==null&&last==null;
    internal bool SelectionFrameVisible=>selectionFrame?.IsVisible==true;
    internal D.Rectangle SelectionFrameBounds=>selectionFrame?.SelectionBounds??D.Rectangle.Empty;
    internal bool SelectionFrameReleased=>selectionFrame==null;
    internal void FinishForAcceptance()=>Finish();

    private static Button IconButton(string name,string geometry,Action action,bool primary,string help)
    {
        var button=Ui.Button(name,action,primary);
        button.Width=button.Height=32;button.MinHeight=32;button.Padding=new Thickness(0);button.Margin=new Thickness(2,0,2,0);button.ToolTip=help;
        button.Content=new System.Windows.Shapes.Path { Data=Geometry.Parse(geometry),Width=16,Height=16,Stretch=Stretch.Uniform,Stroke=primary?Ui.OnPrimary:Ui.Ink,StrokeThickness=1.8,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,IsHitTestVisible=false };
        AutomationProperties.SetHelpText(button,help);return button;
    }

    private async Task Start()
    {
        try {
            if(area.Width<30||area.Height<80)throw new InvalidOperationException("长截图选区至少需要 30 × 80 像素。");
            var hwnd=new WindowInteropHelper(this).Handle;var dpi=VisualTreeHelper.GetDpi(this);
            int pw=(int)Math.Ceiling(ActualWidth*dpi.DpiScaleX),ph=(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY);
            var spot=ToolbarPosition(area,pw,ph,Forms.Screen.AllScreens.Select(s=>s.WorkingArea));
            if(spot.IsEmpty)throw new InvalidOperationException("选区外没有空间放置长截图工具条。请留出约 284 × 58 的区域。");
            if(!Native.SetWindowPos(hwnd,new IntPtr(-1),spot.X,spot.Y,pw,ph,0x10))throw new InvalidOperationException("无法把长截图工具条放到选区外。");
            Native.SetWindowDisplayAffinity(hwnd,0x11);
            if(closed)return;
            // A move onto a monitor with another DPI may resize the HWND. Check
            // its final physical bounds before taking the first screenshot.
            if(!Native.GetWindowRect(hwnd,out var actual))throw new InvalidOperationException("无法确认长截图工具条的位置。");
            var screens=Forms.Screen.AllScreens.Select(s=>s.WorkingArea).ToArray();
            if(actual.Bounds.IntersectsWith(area)||!screens.Any(s=>s.Contains(actual.Bounds))) {
                spot=ToolbarPosition(area,actual.Bounds.Width,actual.Bounds.Height,screens);
                if(spot.IsEmpty||!Native.SetWindowPos(hwnd,new IntPtr(-1),spot.X,spot.Y,actual.Bounds.Width,actual.Bounds.Height,0x10))
                    throw new InvalidOperationException("选区外没有空间放置当前缩放尺寸的工具条。");
                if(closed)return;
                if(!Native.GetWindowRect(hwnd,out actual)||actual.Bounds.IntersectsWith(area))throw new InvalidOperationException("工具条仍位于选区内，请缩小选区。");
            }
            // Resolve the real target before adding the transparent blue ring.
            var firstPosition=firstWheel is MouseWheelSample first?(fromHeldSelection?HeldDragScrollGuard.ClampToSelection(first.Position,area):first.Position):D.Point.Empty;
            var target=firstWheel.HasValue?Native.WindowFromPoint(new Native.Point {X=firstPosition.X,Y=firstPosition.Y}):IntPtr.Zero;
            if(last==null){last=CaptureFrame();composite=new D.Bitmap(last);}
            finishButton.IsEnabled=true;
            selectionFrame=new ScrollCaptureFrame(area);selectionFrame.Show();
            observer=new MouseWheelObserver(sample=>QueueWheel(sample),()=>Dispatcher.BeginInvoke(new Action(()=> {if(!closed)Close();})),
                ex=>Dispatcher.BeginInvoke(new Action(()=>Pause(ex))),()=>Dispatcher.BeginInvoke(new Action(Finish)));
            initialDrag?.AttachTarget(target,ex=>Dispatcher.BeginInvoke(new Action(()=>Pause(ex))));
            Update("蓝框内滚动 · 自动追加长图");
            if(firstWheel is MouseWheelSample initial) {
                var delivery=initial with {Position=firstPosition};
                if(forwardInitialWheel!=null)forwardInitialWheel(delivery);
                else if(!Native.ForwardWheel(target,delivery))throw new InvalidOperationException("无法把首次滚轮交给原窗口，请在蓝框内继续滚动。");
                ObserveWheel(initial);
                initialDrag?.FlushPending(ObserveWheel);
            }
            // Hook and first-message handoff happen before yielding: fast subsequent
            // wheel events cannot fall into a startup delay without observation.
            await Task.CompletedTask;
        } catch(Exception ex) {
            if(closed)return;
            Pause(ex);
            if(composite==null)MessageBox.Show("长截图未开始："+ex.Message,AppBrand.Name);
        }
    }

    internal static D.Rectangle ToolbarPosition(D.Rectangle selection,int width,int height,IEnumerable<D.Rectangle> workingAreas)
    {
        foreach(var screen in workingAreas.OrderByDescending(s=> { var r=D.Rectangle.Intersect(s,selection);return (long)r.Width*r.Height; })) {
            int x=Math.Clamp(selection.Left,screen.Left,Math.Max(screen.Left,screen.Right-width));
            int y=Math.Clamp(selection.Top,screen.Top,Math.Max(screen.Top,screen.Bottom-height));
            var candidates=new[] {
                new D.Rectangle(x,selection.Bottom+8,width,height),new D.Rectangle(x,selection.Top-height-8,width,height),
                new D.Rectangle(selection.Right+8,y,width,height),new D.Rectangle(selection.Left-width-8,y,width,height),
                new D.Rectangle(screen.Left,screen.Top,width,height),new D.Rectangle(screen.Right-width,screen.Top,width,height),
                new D.Rectangle(screen.Left,screen.Bottom-height,width,height),new D.Rectangle(screen.Right-width,screen.Bottom-height,width,height)
            };
            foreach(var spot in candidates)if(screen.Contains(spot)&&!spot.IntersectsWith(selection))return spot;
        }
        return D.Rectangle.Empty;
    }

    private D.Bitmap CaptureFrame() { var bitmap=capture(area);CaptureCount++;return bitmap; }
    private void QueueWheel(MouseWheelSample sample)
    {
        // Low-level hooks run on the installing UI thread. Record the tiny gate
        // transition now so a following Finish click cannot overtake this event;
        // defer all UI updates, captures and matching to the dispatcher.
        if(closed||finishing||paused||last==null||composite==null||!gate.Record(sample,Environment.TickCount64))return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(ScheduleWheel));
    }
    internal void ObserveWheel(MouseWheelSample sample)
    {
        if(closed||finishing||paused||last==null||composite==null||!gate.Record(sample,Environment.TickCount64))return;
        ScheduleWheel();
    }
    private void ScheduleWheel()
    {
        if(closed||finishing||paused||!gate.Pending)return;
        Update(gate.Direction==WheelCaptureDirection.Down?"滚动中 · 自动截图…":"向上回退 · 暂不追加");
        if(!busy)timer.Start();
    }
    private void Update(string message)
    {
        if(composite!=null)summary.Text=$"{composite.Width} × {composite.Height} · {count} 帧";
        status.Text=message;status.ToolTip=message;AutomationProperties.SetHelpText(status,message);
    }
    private void Pause(Exception error)
    {
        if(closed)return;
        paused=true;timer.Stop();gate.Clear();observer?.Dispose();observer=null;
        initialDrag?.DetachTarget();
        Update((composite==null?"未开始：":"已暂停，可完成已有图：")+error.Message);
    }

    private async Task Tick()
    {
        timer.Stop();
        if(busy||closed||paused||last==null||composite==null)return;
        if(!gate.TakeDue(Environment.TickCount64,out long revision,out var direction)) {if(gate.Pending)timer.Start();return;}
        busy=true;D.Bitmap? candidate=null;
        try {
            candidate=CaptureFrame();long deadline=Environment.TickCount64+2400;
            while(!closed) {
                await Task.Delay(WheelCaptureGate.StableMilliseconds);
                if(closed)return;
                using var next=CaptureFrame();
                var stable=await Task.Run(()=>Stitcher.Match(candidate,next));
                if(closed)return;
                if(stable.State!="unchanged") {
                    // Do not throw away every intermediate frame while the user
                    // keeps turning the wheel. A reliable overlap is sufficient
                    // to keep this rendered frame, even before scrolling stops.
                    if(direction==WheelCaptureDirection.Down) {
                        var intermediate=await Task.Run(()=>Stitcher.Match(last,candidate));
                        if(closed)return;
                        if(intermediate.State=="append") {
                            await AppendFrame(candidate,intermediate);if(closed)return;
                            deadline=Environment.TickCount64+2400;Update("已自动追加 · 继续滚动");
                        }
                    }
                    candidate.Dispose();candidate=new D.Bitmap(next);
                    if(Environment.TickCount64>=deadline) {Update("画面未稳定 · 停稳后再滚动");return;}
                    continue;
                }
                var match=await Task.Run(()=>Stitcher.Match(last,next));
                if(closed)return;
                if(direction==WheelCaptureDirection.Up) {Update(match.State=="unchanged"?"已回到上一段 · 可向下滚动":"向上回退不追加 · 滚回上一段后继续");return;}
                if(match.State=="unchanged") {Update("没有新内容 · 继续滚动或完成");return;}
                if(match.State!="append") {Update("滚动跨度过大或内容重复 · 回滚一点后继续");return;}
                await AppendFrame(next,match);if(closed)return;
                Update("已自动追加 · 继续滚动或点 ✓ 完成");return;
            }
        } catch(Exception ex) {Pause(ex);}
        finally {candidate?.Dispose();busy=false;if(closed)DisposeImages();else if(gate.Pending&&!paused)timer.Start();}
    }
    private async Task AppendFrame(D.Bitmap frame,StitchMatch match)
    {
        var merged=await Task.Run(()=>Stitcher.Append(composite!,frame,match));
        if(closed){merged.Dispose();return;}
        composite!.Dispose();composite=merged;last!.Dispose();last=new D.Bitmap(frame);count++;
    }

    private void Finish()
    {
        if(finishing||closed||composite==null)return;finishing=true;finishButton.IsEnabled=false;
        // Routed-event probes and accessibility invocations can arrive without
        // a DispatcherSynchronizationContext even on the STA thread. Begin the
        // async flush from a dispatcher operation so every continuation owns UI.
        Dispatcher.BeginInvoke(new Action(async()=>await FinishPending()));
    }
    private async Task FinishPending()
    {
        try {
            while(!closed&&(busy||gate.Pending)) {
                if(!busy)await Tick();
                if(busy||gate.Pending)await Task.Delay(40);
            }
            if(closed)return;
            timer.Stop();
            using var result=new D.Bitmap(composite!);finished(result);Close();
        } catch(Exception ex) {if(!closed) {finishing=false;finishButton.IsEnabled=true;Pause(ex);}}
    }
    private void DisposeImages() {composite?.Dispose();last?.Dispose();composite=last=null;}
}
