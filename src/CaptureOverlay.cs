using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D=System.Drawing;
using Forms=System.Windows.Forms;

namespace QingCapture;

internal sealed class CaptureOverlay : Window
{
    private readonly D.Rectangle screen;
    private readonly CaptureSelection gesture;
    private readonly CaptureFollowUp followUp;
    private readonly Canvas canvas=new();
    private readonly System.Windows.Shapes.Rectangle border=new() { Stroke=Ui.Primary,StrokeThickness=2,Fill=new SolidColorBrush(Color.FromArgb(24,82,102,206)),IsHitTestVisible=false };
    private readonly TextBlock label=Ui.Text("",13,Ui.OnPlum);
    private readonly Border plate;
    private readonly CaptureLoupe loupe;
    private readonly Action<string> copyColor;
    private readonly BitmapSource snapshotSource;
    private DragWidthSelection? dragWidth;
    private DragCaptureController? dragCapture;
    private D.Bitmap? dragResult;
    private bool finishingDrag,finishReady,cancelDragPending,closing,cancelling,closed,pointerReleased;
    private readonly long startedAt=Environment.TickCount64;
    private long finishStartedAt;
    private string? dragStartError;
    internal string? DragDiagnostics {get;private set;}
    private HeldDragScrollGuard? dragScrollGuard;
    private DragWheelRouter? wheelRouter;
    internal int NativeWheelCount=>wheelRouter?.NativeWheelCount??0;
    internal bool WheelHookReleased=>wheelRouter==null||wheelRouter.HooksReleased;
    private bool completed;
    public D.Rectangle Selection=>dragCapture?.VisibleSelection??(dragWidth?.Locked==true?dragWidth.Selection:gesture.Selection);
    internal DragCaptureController? DragCapture=>dragCapture;
    internal string? DragError=>dragCapture?.Error??dragCapture?.CompletionWarning??dragStartError;
    internal D.Bitmap? TakeDragResult(){var result=dragResult;dragResult=null;return result;}
    internal MouseWheelSample? InitialWheel {get;private set;}
    internal bool ScrollRequested=>InitialWheel.HasValue;
    internal bool Dragging=>gesture.IsPressed&&gesture.IsDragging;
    internal CaptureLoupe Loupe=>loupe;
    public CaptureOverlay(D.Bitmap bitmap,D.Rectangle bounds,string captureMode,CaptureFollowUp followUp=CaptureFollowUp.None,Action<string>? copyColor=null)
    {
        this.copyColor=copyColor??(text=>Clipboard.SetText(text));
        screen=bounds;this.followUp=followUp;gesture=new CaptureSelection(bounds,Native.Windows(),captureMode!="区域");Title=AppBrand.Title("截图");
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;ShowInTaskbar=false;Topmost=true;Cursor=Cursors.Cross;
        snapshotSource=Imaging.Source(bitmap);Background=new ImageBrush(snapshotSource) { Stretch=Stretch.Fill };Content=canvas;loupe=new CaptureLoupe(snapshotSource);
        var controls=new StackPanel();label.Margin=new Thickness(0);controls.Children.Add(label);
        var buttons=new WrapPanel { Margin=new Thickness(-5,6,-5,-3) };
        if(followUp==CaptureFollowUp.None)buttons.Children.Add(Ui.Button("全屏截图",SelectFullScreen,icon:"screen"));
        buttons.Children.Add(Ui.Button("取消",CancelSelection));controls.Children.Add(buttons);
        plate=new Border { Name="CaptureControls",Background=new SolidColorBrush(Color.FromArgb(245,41,50,72)),Padding=new Thickness(16,10,16,10),CornerRadius=new CornerRadius(12),Child=controls };
        canvas.Children.Add(border);canvas.Children.Add(plate);canvas.Children.Add(loupe);Canvas.SetLeft(plate,24);Canvas.SetTop(plate,24);
        AutomationProperties.SetName(label,"截图操作提示");
        SourceInitialized+=(_,_)=> { var h=new WindowInteropHelper(this).Handle; Native.SetWindowPos(h,new IntPtr(-1),bounds.X,bounds.Y,bounds.Width,bounds.Height,0x40); };
        Loaded+=(_,_)=> {
            var h=new WindowInteropHelper(this).Handle;Native.SetWindowPos(h,new IntPtr(-1),bounds.X,bounds.Y,bounds.Width,bounds.Height,0x40);UpdateLayout();
            Native.GetCursorPos(out var cursor);var monitor=Forms.Screen.FromPoint(new D.Point(cursor.X,cursor.Y)).Bounds;
            var position=ControlPosition(bounds,monitor,new Size(ActualWidth,ActualHeight));Canvas.SetLeft(plate,position.X);Canvas.SetTop(plate,position.Y);
            double available=monitor.Width*ActualWidth/screen.Width-48;plate.MaxWidth=Math.Max(120,available);label.MaxWidth=Math.Max(88,available-32);
            var point=PointFromScreen(new Point(cursor.X,cursor.Y));gesture.Hover(point,new Size(ActualWidth,ActualHeight));ShowSelection();UpdateLoupe(point);Activate();Focus();
        };
        PreviewKeyDown+=(_,e)=> {
            if(HandleColorKey(e.Key,Keyboard.Modifiers,e.IsRepeat)){e.Handled=true;return;}
            if(e.Key==Key.Escape){e.Handled=true;CancelSelection();}
            else if(e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.None) {
                if(dragCapture!=null){e.Handled=true;_=FinishDrag();return;}
                if(Keyboard.FocusedElement is Button focused&&IsControl(focused)){e.Handled=true;focused.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
                else if(followUp==CaptureFollowUp.None){e.Handled=true;SelectFullScreen();}
            }
        };
        MouseRightButtonDown+=(_,e)=> {e.Handled=true;CancelSelection();};
        MouseLeftButtonDown+=(_,e)=> {
            if(IsControl(e.OriginalSource as DependencyObject))return;
            BeginPointer(e.GetPosition(canvas));e.Handled=true;
        };
        MouseMove+=(_,e)=> {
            if(completed)return;
            if(!gesture.IsPressed&&IsControl(e.OriginalSource as DependencyObject)){loupe.HideSample();return;}
            MovePointer(e.GetPosition(canvas));
        };
        MouseLeftButtonUp+=(_,e)=> {
            pointerReleased=true;
            if(cancelDragPending){e.Handled=true;cancelDragPending=false;gesture.CommitDrag();DialogResult=false;return;}
            if(finishReady){e.Handled=true;CommitDragResult();return;}
            if(!gesture.IsPressed)return;EndPointer(e.GetPosition(canvas));e.Handled=true;
        };
        PreviewMouseWheel+=(_,e)=> {
            if((!gesture.IsPressed&&IsControl(e.OriginalSource as DependencyObject))||(Keyboard.Modifiers&(ModifierKeys.Alt|ModifierKeys.Windows))!=ModifierKeys.None)return;
            var physical=CaptureLoupe.MapPixel(e.GetPosition(canvas),new Size(ActualWidth,ActualHeight),screen);
            var sample=new MouseWheelSample(physical,e.Delta,unchecked((uint)e.Timestamp),ControlPressed:Keyboard.Modifiers.HasFlag(ModifierKeys.Control),ShiftPressed:Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            if(HandleDragWheel(sample,Keyboard.Modifiers)||(followUp!=CaptureFollowUp.Scroll&&BeginScrollFromWheel(sample)))e.Handled=true;
        };
        LostMouseCapture+=(_,_)=> {if(closed||closing||cancelling)return;if(dragCapture!=null&&!completed){CancelSelection();return;}if(gesture.IsPressed){gesture.CancelGesture();ShowSelection();}};
        MouseLeave+=(_,_)=>loupe.HideSample();
        Deactivated+=(_,_)=> {loupe.HideSample();if(closed||closing||cancelling)return;if(dragCapture!=null&&!completed){CancelSelection();return;}if(!completed){gesture.CancelGesture();ShowSelection();}if(IsMouseCaptured)ReleaseMouseCapture();};
        Closing+=(_,_)=>closing=true;
        Closed+=(_,_)=> {ProtectDragRelease();closed=true;wheelRouter?.Dispose();dragCapture?.Dispose();if(!completed){dragResult?.Dispose();dragResult=null;}loupe.HideSample();gesture.CancelGesture(clearSelection:!completed);if(IsMouseCaptured)ReleaseMouseCapture();if(!ScrollRequested)ReleaseUnusedDragGuard();SaveScrollDiagnostics();};
        ShowSelection();
    }
    internal static D.Rectangle MapSelection(Point a,Point b,double width,double height,D.Rectangle physical)
    {
        if(width<=0||height<=0)return D.Rectangle.Empty;
        int x1=(int)Math.Round(Math.Min(a.X,b.X)/width*physical.Width),x2=(int)Math.Round(Math.Max(a.X,b.X)/width*physical.Width);
        int y1=(int)Math.Round(Math.Min(a.Y,b.Y)/height*physical.Height),y2=(int)Math.Round(Math.Max(a.Y,b.Y)/height*physical.Height);
        return D.Rectangle.Intersect(physical,D.Rectangle.FromLTRB(physical.X+x1,physical.Y+y1,physical.X+x2,physical.Y+y2));
    }
    internal static Point ControlPosition(D.Rectangle desktop,D.Rectangle monitor,Size surface)=>new((monitor.Left-desktop.Left)*surface.Width/desktop.Width+24,(monitor.Top-desktop.Top)*surface.Height/desktop.Height+24);
    internal void BeginPointer(Point point)
    {
        if(completed)return;
        pointerReleased=false;
        if(followUp==CaptureFollowUp.Scroll)dragWidth=new DragWidthSelection(CaptureLoupe.MapPixel(point,new Size(ActualWidth,ActualHeight),screen));
        gesture.Begin(point,new Size(ActualWidth,ActualHeight));if(!CaptureMouse())gesture.CancelGesture();ShowSelection();UpdateLoupe(point);
    }
    internal void MovePointer(Point point)
    {
        if(completed||finishingDrag||cancelDragPending)return;
        var physical=CaptureLoupe.MapPixel(point,new Size(ActualWidth,ActualHeight),screen);
        if(dragCapture!=null){dragCapture.UpdatePointer(physical);ShowSelection();return;}
        gesture.Move(point,new Size(ActualWidth,ActualHeight),SystemParameters.MinimumHorizontalDragDistance,SystemParameters.MinimumVerticalDragDistance);
        if(followUp==CaptureFollowUp.Scroll&&gesture.IsPressed&&dragWidth!=null) {
            dragWidth.Move(physical);
            if(dragWidth.Locked)BeginDragCapture(physical);
        }
        ShowSelection();if(dragCapture==null)UpdateLoupe(point);
    }
    private void BeginDragCapture(D.Point pointer,D.Rectangle? manualSelection=null)
    {
        if(dragCapture!=null||dragWidth==null)return;
        try {
            var chosen=D.Rectangle.Intersect(screen,manualSelection??dragWidth.Selection);
            var hwnd=new WindowInteropHelper(this).Handle;
            var delivery=new Native.Point {X=chosen.Left+chosen.Width/2,Y=chosen.Top+Math.Min(40,chosen.Height/2)};
            var required=new D.Rectangle(chosen.Left,chosen.Top,chosen.Width,Math.Max(240,chosen.Height));
            var target=Native.WindowBelow(delivery,hwnd,required);if(target==IntPtr.Zero)throw new InvalidOperationException("请在可滚动窗口的内容区域选取宽度。");
            var root=Native.GetAncestor(target,2);if(root==IntPtr.Zero)root=target;
            if(!Native.GetClientRect(root,out var client))throw new InvalidOperationException("无法读取原窗口范围。");
            var origin=new Native.Point();if(!Native.ClientToScreen(root,ref origin))throw new InvalidOperationException("无法读取原窗口坐标。");
            var monitor=Forms.Screen.FromPoint(new D.Point(delivery.X,delivery.Y)).WorkingArea;
            int bottom=Math.Min(screen.Bottom,Math.Min(monitor.Bottom,origin.Y+client.Bottom));
            var viewport=manualSelection.HasValue?chosen:new D.Rectangle(chosen.Left,chosen.Top,chosen.Width,bottom-chosen.Top);
            DragDiagnostics=$"chosen={chosen}, root={root}, rootClient={origin.X},{origin.Y},{client.Right},{client.Bottom}, monitor={monitor}, viewport={viewport}";
            if(viewport.Height<240)throw new InvalidOperationException("请从页面较靠上的内容开始向下拖动。");
            // The initial top edge can lie over a toolbar/header. Send automatic
            // scrolling inside the page body and resolve its current child on
            // every attempt, since virtualized controls can replace their HWND.
            delivery.Y=viewport.Top+viewport.Height*2/3;
            var bodyTarget=Native.WindowBelow(delivery,hwnd,viewport);
            if(bodyTarget!=IntPtr.Zero&&Native.GetAncestor(bodyTarget,2)==root)target=bodyTarget;
            var contentBounds=ScrollContentBounds.ForTarget(target,root,chosen);
            if(contentBounds.HasValue&&!manualSelection.HasValue) {
                viewport=D.Rectangle.Intersect(viewport,contentBounds.Value);
                if(viewport.Width!=chosen.Width||viewport.Height<240)throw new InvalidOperationException("请在较高的可滚动内容区域内选取宽度。");
                delivery.Y=viewport.Top+viewport.Height*2/3;
            }
            bool delivered=false;
            using var seed=Imaging.FromSource(new CroppedBitmap(snapshotSource,new Int32Rect(viewport.X-screen.X,viewport.Y-screen.Y,viewport.Width,viewport.Height)));
            if(!Native.SetWindowDisplayAffinity(hwnd,0x11))throw new InvalidOperationException("当前系统无法隔离截图浮层，请使用滚轮长截图。");
            bool Deliver(int delta) {
                delivered=false;
                var currentViewport=dragCapture?.Viewport??viewport;
                delivery.Y=currentViewport.Top+currentViewport.Height*2/3;
                var current=Native.WindowBelow(delivery,hwnd,currentViewport);
                var currentRoot=current==IntPtr.Zero?IntPtr.Zero:Native.GetAncestor(current,2);
                if(currentRoot==IntPtr.Zero)currentRoot=current;
                if(current==IntPtr.Zero||currentRoot!=root)return false;
                if(contentBounds.HasValue&&ScrollContentBounds.ForTarget(current,root,chosen)!=contentBounds)throw new InvalidOperationException("原窗口的滚动区域发生变化，请松手保留已截内容后重新截图。");
                target=current;
                var sample=new MouseWheelSample(new D.Point(delivery.X,delivery.Y),delta,unchecked((uint)Environment.TickCount));
                return delivered=Native.ForwardWheelUnderOverlay(hwnd,target,sample);
            }
            wheelRouter??=new DragWheelRouter(()=>!closed&&!closing&&!completed&&gesture.IsPressed,
                sample=>HandleDragWheel(sample),error=>{dragStartError=error.Message;ShowSelection();});
            dragCapture=new DragCaptureController(viewport,seed,Imaging.Capture,()=>Deliver(-120),()=>delivered?Native.ReadVerticalScrollEnd(target):null,Deliver);
            if(manualSelection.HasValue)dragCapture.FreezeSelection(chosen);
            dragCapture.Changed+=()=>{if(!closed&&!cancelDragPending)ShowSelection();};
            // A fully transparent layered window lets native wheel routing hit
            // the source directly, bypassing our capture queue. Nonzero alpha
            // keeps input here; display affinity excludes it from image samples.
            Background=new SolidColorBrush(Color.FromArgb(1,0,0,0));border.Fill=null;loupe.HideSample();dragStartError=null;
            dragCapture.UpdatePointer(pointer);
        }catch(Exception ex){dragStartError=ex.Message;}
    }
    private async System.Threading.Tasks.Task FinishDrag()
    {
        wheelRouter?.Flush();
        if(finishingDrag||dragCapture==null||closed)return;finishingDrag=true;finishStartedAt=Environment.TickCount64;ShowSelection();
        try {
            var image=await dragCapture.Finish();
            if(closed||cancelDragPending){image.Dispose();return;}
            dragResult=image;
            if(!pointerReleased&&Mouse.LeftButton==MouseButtonState.Pressed&&IsMouseCaptured){finishReady=true;label.Text="截图已收齐，松开左键完成。";return;}
            ProtectDragRelease();CommitDragResult();
        }catch(OperationCanceledException){if(!closed&&!cancelDragPending)CancelSelection();}
        catch(Exception ex){finishingDrag=false;dragStartError="长截图未完成："+ex.Message;CancelSelection();}
    }
    private void CommitDragResult()
    {
        finishReady=false;completed=true;gesture.CommitDrag();if(IsMouseCaptured)ReleaseMouseCapture();DialogResult=true;
    }
    private void SaveScrollDiagnostics()
    {
        if(followUp!=CaptureFollowUp.Scroll)return;
        var selected=Selection;var controller=dragCapture;
        ScrollSessionDiagnostics.Save(new {
            time=DateTimeOffset.Now,build=typeof(CaptureOverlay).Module.ModuleVersionId,
            elapsedMs=Environment.TickCount64-startedAt,
            finishMs=finishStartedAt==0?0:Environment.TickCount64-finishStartedAt,
            width=selected.Width,height=selected.Height,completed,pointerReleased,
            nativeWheelCount=NativeWheelCount,mouseCaptureReleased=!IsMouseCaptured,wheelHookReleased=WheelHookReleased,
            controller?.DispatchCount,controller?.RejectedDispatchCount,controller?.MaximumPendingWheelCount,
            controller?.AbandonedWheelCount,controller?.CaptureCount,controller?.FrameCount,controller?.ScrollOffset,
            controller?.NeedsOverlap,controller?.Rewound,controller?.AtBottom,controller?.FinishBudgetExpired,
            controller?.LastMatch,controller?.InputWarning,controller?.CompletionWarning,error=DragError
        });
    }
    private void UpdateLoupe(Point point)
    {
        if(completed||dragCapture!=null||ActualWidth<=0||ActualHeight<=0||point.X<0||point.Y<0||point.X>=ActualWidth||point.Y>=ActualHeight){loupe.HideSample();return;}
        var physical=CaptureLoupe.MapPixel(point,new Size(ActualWidth,ActualHeight),screen);
        if(!Forms.Screen.AllScreens.Any(monitor=>monitor.Bounds.Contains(physical))){loupe.HideSample();return;}
        var monitorBounds=D.Rectangle.Intersect(screen,Forms.Screen.FromPoint(physical).Bounds);
        if(monitorBounds.IsEmpty){loupe.HideSample();return;}
        loupe.UpdateSample(physical,screen);loupe.Width=222;loupe.MaxHeight=double.PositiveInfinity;loupe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));plate.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        var monitor=new Rect((monitorBounds.X-screen.X)*ActualWidth/screen.Width,(monitorBounds.Y-screen.Y)*ActualHeight/screen.Height,monitorBounds.Width*ActualWidth/screen.Width,monitorBounds.Height*ActualHeight/screen.Height);
        var controls=new Rect(new Point(Canvas.GetLeft(plate),Canvas.GetTop(plate)),plate.DesiredSize);
        var card=CaptureLoupe.CardBounds(point,monitor,loupe.DesiredSize,controls);Canvas.SetLeft(loupe,card.X);Canvas.SetTop(loupe,card.Y);loupe.Width=card.Width;loupe.MaxHeight=card.Height;
    }
    internal bool HandleColorKey(Key key,ModifierKeys modifiers=ModifierKeys.None,bool repeat=false)
    {
        if(completed||dragCapture!=null||!loupe.HasSample||loupe.Visibility!=Visibility.Visible)return false;
        if((key is Key.LeftShift or Key.RightShift)&&(modifiers&~ModifierKeys.Shift)==ModifierKeys.None){if(!repeat)loupe.ToggleFormat();return true;}
        if(key!=Key.C||modifiers!=ModifierKeys.None)return false;
        try{copyColor(loupe.ColorText);loupe.ShowCopyResult("颜色值已复制");}catch{loupe.ShowCopyResult("复制失败，请再按 C 重试");}return true;
    }
    internal void EndPointer(Point point)
    {
        pointerReleased=true;
        wheelRouter?.Flush();
        if(dragCapture!=null){if(cancelDragPending){cancelDragPending=false;DialogResult=false;}else {dragCapture.UpdatePointer(CaptureLoupe.MapPixel(point,new Size(ActualWidth,ActualHeight),screen));_=FinishDrag();}return;}
        if(!gesture.IsPressed)return;
        bool ready=gesture.Finish(point,new Size(ActualWidth,ActualHeight),SystemParameters.MinimumHorizontalDragDistance,SystemParameters.MinimumVerticalDragDistance);
        if(ready)completed=true;
        ReleaseMouseCapture();if(ready)DialogResult=true;else ShowSelection();
    }
    internal bool HandleDragWheel(MouseWheelSample sample,ModifierKeys modifiers=ModifierKeys.None)
    {
        if(followUp!=CaptureFollowUp.Scroll||sample.Delta==0||sample.ControlPressed||sample.ShiftPressed||modifiers!=ModifierKeys.None)return false;
        if(closed||closing||completed||!gesture.IsPressed)return false;
        if(finishingDrag||cancelDragPending)return true;
        var selected=Selection;
        if(selected.Width<30||selected.Height<240) {
            label.Text="请保持左键，再向下拉出至少240像素高的正文框，然后滚轮采集；原框不会自动扩大。";
            return true;
        }
        if(dragCapture==null) {
            if(dragWidth==null||!dragWidth.LockCurrent())return true;
            BeginDragCapture(sample.Position,selected);
        }
        if(dragCapture==null){ShowSelection();return true;}
        if(!dragCapture.FreezeSelection(selected)){label.Text="请框选至少240像素高的正文区域，滚轮采集会保留原框。";return true;}
        if(!dragCapture.QueueWheel(sample.Delta)&&dragCapture.Error==null&&!dragCapture.NeedsOverlap)label.Text="滚动正在处理中，请稍停后继续 · 松手完成";
        else ShowSelection();
        return true;
    }
    internal bool BeginScrollFromWheel(MouseWheelSample sample)
    {
        if(completed||dragCapture!=null||!gesture.IsPressed||!gesture.IsDragging||followUp==CaptureFollowUp.Translate||sample.Delta>=0||sample.ControlPressed||sample.ShiftPressed)return false;
        var selected=Selection;if(selected.Width<30||selected.Height<80)return false;
        var dpi=VisualTreeHelper.GetDpi(this);
        if(ScrollWindow.ToolbarPosition(selected,(int)Math.Ceiling(284*dpi.DpiScaleX),(int)Math.Ceiling(58*dpi.DpiScaleY),Forms.Screen.AllScreens.Select(monitor=>monitor.WorkingArea)).IsEmpty){label.Text="请给选区外留出工具条空间，再按住左键滚动。";return false;}
        // Guard the physical drag before releasing native capture. The original
        // application must receive scrolling, not this selection drag's MOVE/UP.
        try {dragScrollGuard=HeldDragScrollGuard.TryCreate(selected);}
        catch(Exception ex){label.Text="暂时无法开始长截图："+ex.Message;return false;}
        gesture.CommitDrag();completed=true;InitialWheel=sample;loupe.HideSample();
        if(IsMouseCaptured)ReleaseMouseCapture();DialogResult=true;return true;
    }
    internal HeldDragScrollGuard? TakeDragGuard(){var guard=dragScrollGuard;dragScrollGuard=null;return guard;}
    internal void ReleaseUnusedDragGuard(){dragScrollGuard?.Dispose();dragScrollGuard=null;}
    private bool IsControl(DependencyObject? source)
    {
        while(source!=null) {if(ReferenceEquals(source,plate))return true;source=source is Visual?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source);}
        return false;
    }
    private void CancelSelection()
    {
        if(closed||closing||cancelling)return;cancelling=true;
        try {
            dragCapture?.Dispose();
            if(dragCapture!=null&&!pointerReleased&&IsMouseCaptured&&Mouse.LeftButton==MouseButtonState.Pressed) {
                cancelDragPending=true;border.Visibility=plate.Visibility=Visibility.Collapsed;loupe.HideSample();Background=new SolidColorBrush(Color.FromArgb(1,0,0,0));Cursor=Cursors.Arrow;return;
            }
            ProtectDragRelease();gesture.CancelGesture();if(IsMouseCaptured)ReleaseMouseCapture();if(!closed&&!closing)DialogResult=false;
        }finally{cancelling=false;}
    }
    private void ProtectDragRelease()
    {
        if(dragCapture==null||pointerReleased||Mouse.LeftButton!=MouseButtonState.Pressed)return;
        try{var guard=HeldDragScrollGuard.TryCreate(Selection);guard?.Dispose();}catch{/* Closing must still release the window; no source mouse-down was sent. */}
    }
    private void SelectFullScreen() {gesture.FullScreen();completed=true;if(IsMouseCaptured)ReleaseMouseCapture();DialogResult=true;}
    private void ShowSelection()
    {
        border.Visibility=Selection.Width>=4&&Selection.Height>=4?Visibility.Visible:Visibility.Collapsed;
        Canvas.SetLeft(border,(Selection.X-screen.X)*ActualWidth/screen.Width); Canvas.SetTop(border,(Selection.Y-screen.Y)*ActualHeight/screen.Height);
        border.Width=Math.Max(0,Selection.Width*ActualWidth/screen.Width); border.Height=Math.Max(0,Selection.Height*ActualHeight/screen.Height);
        string action=finishingDrag?"正在收尾 · 已停止继续滚动，请稍候":dragCapture!=null?(dragCapture.Error??dragCapture.InputWarning??(dragCapture.Recovering?"等待页面内容恢复 · 暂停滚动采样":dragCapture.NeedsOverlap?"画面尚未衔接 · 已暂停下滚 · 向上滚一点可恢复":dragCapture.Rewound?"向上回退不追加 · 向下滚回后继续 · 松手完成":dragCapture.AtBottom?"已到页底 · 也可用滚轮继续 · 松手完成":dragCapture.WaitingForScroll?"原页面暂未出现新内容 · 可重试滚轮或松手完成":dragCapture.ManualWheelMode?"范围已锁定 · 滚轮采集 · 松手完成":"宽度已锁定 · 向下拉或滚轮辅助 · 松手完成")):followUp==CaptureFollowUp.Scroll?"先框选正文宽高，左键按住下拉或滚轮采集":gesture.IsDragging&&followUp!=CaptureFollowUp.Translate?"按住左键不松手 · 向下滚轮截长图":followUp==CaptureFollowUp.Translate?"拖动框选翻译区域":gesture.IsDragging?"松开完成截图":gesture.Automatic?"点击窗口 · 拖动框选 · Enter 全屏":"拖动框选 · Enter 全屏";
        label.Text=dragStartError??((border.Visibility==Visibility.Visible?$"{Selection.Width} × {dragCapture?.OutputHeight??Selection.Height} 像素  ·  ":"")+action+" · Esc 取消");
    }
}

// Mouse intent uses WPF units; only the final selection is mapped to physical
// pixels. Once dragged, returning to the starting point cannot become a click.
internal sealed class CaptureSelection
{
    private readonly D.Rectangle screen;
    private readonly IReadOnlyList<D.Rectangle> windows;
    private Point? start;
    private D.Rectangle pressedWindow;
    public bool Automatic {get;}
    public bool IsPressed=>start.HasValue;
    public bool IsDragging {get;private set;}
    public D.Rectangle Selection {get;private set;}
    public CaptureSelection(D.Rectangle screen,IReadOnlyList<D.Rectangle> windows,bool automatic) {this.screen=screen;this.windows=windows;Automatic=automatic;}
    public void Hover(Point point,Size surface)
    {
        if(!Automatic||surface.Width<=0||surface.Height<=0){Selection=D.Rectangle.Empty;return;}
        var physical=new D.Point(screen.X+(int)Math.Floor(point.X*screen.Width/surface.Width),screen.Y+(int)Math.Floor(point.Y*screen.Height/surface.Height));
        Selection=D.Rectangle.Intersect(screen,windows.FirstOrDefault(rect=>rect.Width>=4&&rect.Height>=4&&rect.Contains(physical)));
    }
    public void Begin(Point point,Size surface) {Hover(point,surface);pressedWindow=Selection;start=point;IsDragging=false;}
    public void Move(Point point,Size surface,double thresholdX,double thresholdY)
    {
        if(start is not Point origin){Hover(point,surface);return;}
        if(Math.Abs(point.X-origin.X)>=thresholdX||Math.Abs(point.Y-origin.Y)>=thresholdY)IsDragging=true;
        Selection=IsDragging?CaptureOverlay.MapSelection(origin,point,surface.Width,surface.Height,screen):pressedWindow;
    }
    public bool Finish(Point point,Size surface,double thresholdX,double thresholdY)
    {
        if(!IsPressed)return false;Move(point,surface,thresholdX,thresholdY);
        bool valid=(IsDragging||Automatic)&&Selection.Width>=4&&Selection.Height>=4;
        start=null;IsDragging=false;if(!valid)Hover(point,surface);return valid;
    }
    public void CancelGesture(bool clearSelection=true) {start=null;IsDragging=false;if(clearSelection)Selection=D.Rectangle.Empty;pressedWindow=D.Rectangle.Empty;}
    public void CommitDrag(){start=null;IsDragging=false;pressedWindow=D.Rectangle.Empty;}
    public void FullScreen() {CancelGesture();Selection=screen;}
}
