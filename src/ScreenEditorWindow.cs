using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using D=System.Drawing;
using Forms=System.Windows.Forms;
using Shape=System.Windows.Shapes;

namespace QingCapture;

// A frozen desktop stays in physical-pixel space. Selection and decoration are
// separate from the document, so no handle, mask or toolbar can enter an export.
internal sealed class ScreenEditorWindow : Window
{
    private readonly D.Rectangle desktop;
    private readonly ImageDocument document;
    private readonly Canvas surface=new() { Name="ScreenEditorSurface",UseLayoutRounding=true,SnapsToDevicePixels=true,Background=Brushes.Transparent };
    private readonly Image picture=new() { Stretch=Stretch.Fill,IsHitTestVisible=false,UseLayoutRounding=true,SnapsToDevicePixels=true };
    private readonly Shape.Rectangle[] masks=new Shape.Rectangle[4];
    private readonly Shape.Rectangle outline=new() { Stroke=Ui.Primary,StrokeThickness=1,IsHitTestVisible=false };
    private readonly Shape.Ellipse[] handles=new Shape.Ellipse[8];
    private readonly Shape.Rectangle preview=new() { Stroke=Ui.Plum,StrokeThickness=2,IsHitTestVisible=false,Visibility=Visibility.Collapsed };
    private readonly Border toolbar;
    private readonly Border dimensions;
    private readonly Popup optionsPopup;
    private readonly TextBlock sizeText=Ui.Text("",12,Ui.OnPrimary);
    private readonly TextBlock status=Ui.Text("",11,Ui.Muted);
    private readonly List<(string tool,Button button)> tools=new();
    private readonly Button undo,redo,translate;
    private readonly InlineTranslationPanel translationPanel;
    private List<OcrBlock> translationBlocks=new();
    private CancellationTokenSource? translationStop;
    private int translationGeneration;
    private readonly bool autoCopy;
    private bool closed,dialogOpen;
    private CancellationTokenSource? copying;
    private string tool="查看";
    private D.Color color=D.Color.FromArgb(201,85,103);
    private float stroke=3;
    private Point? pointer;
    private D.Point startPixel;
    private D.Rectangle originalSelection;
    private int action=-1; // 0..7 resize; 8 move; 9 annotation; 10 replace selection
    internal D.Rectangle Selection {get;private set;}
    internal bool OptionsOpen=>optionsPopup.IsOpen;
    internal InlineTranslationPanel TranslationPanel=>translationPanel;
    internal Func<D.Rectangle,D.Bitmap,MouseWheelSample?,ScrollWindow>? ScrollFactory {get;set;}
    internal ScreenEditorWindow(D.Bitmap snapshot,D.Rectangle desktop,D.Rectangle selection,bool autoCopy=true)
    {
        this.desktop=desktop;this.autoCopy=autoCopy;
        if(snapshot.Width!=desktop.Width||snapshot.Height!=desktop.Height)throw new ArgumentException("Desktop pixels and capture bounds must agree.");
        Selection=D.Rectangle.Intersect(desktop,selection);
        if(Selection.Width<4||Selection.Height<4)throw new ArgumentException("Invalid screenshot selection.");
        document=new(snapshot);Title=AppBrand.Title("截图编辑");Icon=Ui.BrandFrame(32);
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;UseLayoutRounding=true;SnapsToDevicePixels=true;
        Content=surface;Background=Ui.Canvas;surface.Children.Add(picture);RenderOptions.SetBitmapScalingMode(picture,BitmapScalingMode.NearestNeighbor);
        for(int i=0;i<masks.Length;i++) { masks[i]=new Shape.Rectangle {Fill=new SolidColorBrush(Color.FromArgb(115,18,22,34)),IsHitTestVisible=false};surface.Children.Add(masks[i]); }
        surface.Children.Add(preview);surface.Children.Add(outline);
        for(int i=0;i<handles.Length;i++) { handles[i]=new Shape.Ellipse { Width=8,Height=8,Stroke=Ui.OnPrimary,StrokeThickness=1.2,Fill=Ui.Primary,IsHitTestVisible=false };surface.Children.Add(handles[i]); }
        dimensions=new Border { Background=Ui.Primary,CornerRadius=new CornerRadius(4),Padding=new Thickness(8,4,8,4),Child=sizeText,IsHitTestVisible=false };surface.Children.Add(dimensions);
        var row=new StackPanel { Orientation=Orientation.Horizontal };
        foreach(var (name,icon) in new[]{("查看","view"),("框线","box"),("箭头","arrow"),("文字","text"),("马赛克","mosaic")}) {
            var button=CompactToolbar.Button(icon,name,()=>SelectTool(name));tools.Add((name,button));row.Children.Add(button);
        }
        var options=CompactToolbar.Button("settings","标注颜色和线宽",()=>{});row.Children.Add(options);
        var panel=new StackPanel { Margin=new Thickness(10) };
        panel.Children.Add(Ui.Text("标注颜色",11,Ui.Muted));
        var colors=new ComboBox { Width=150,ItemsSource=new[]{"玫瑰红","雾蓝","杏橙","石墨"},SelectedIndex=0,Margin=new Thickness(0,4,0,10) };
        AutomationProperties.SetName(colors,"标注颜色");colors.SelectionChanged+=(_,_)=>color=new[]{D.Color.FromArgb(201,85,103),D.Color.FromArgb(82,102,206),D.Color.FromArgb(185,111,75),D.Color.FromArgb(41,50,72)}[colors.SelectedIndex];panel.Children.Add(colors);
        panel.Children.Add(Ui.Text("线宽 / 字号",11,Ui.Muted));
        var widths=new ComboBox {Width=150,ItemsSource=new[]{"细","中","粗"},SelectedIndex=1,Margin=new Thickness(0,4,0,0)};AutomationProperties.SetName(widths,"线宽 / 字号");widths.SelectionChanged+=(_,_)=>stroke=new[]{2f,3f,5f}[widths.SelectedIndex];panel.Children.Add(widths);optionsPopup=CompactToolbar.Popup(options,panel);
        optionsPopup.Closed+=(_,_)=>ScheduleDeactivationClose();
        row.Children.Add(CompactToolbar.Separator());
        undo=CompactToolbar.Button("undo","撤销 · Ctrl+Z",()=>History(false));redo=CompactToolbar.Button("redo","重做 · Ctrl+Y",()=>History(true));row.Children.Add(undo);row.Children.Add(redo);
        row.Children.Add(CompactToolbar.Separator());
        row.Children.Add(CompactToolbar.Button("scroll","拖动长截图",StartScroll));
        translate=CompactToolbar.Button("translate","离线翻译",async()=>await Translate());row.Children.Add(translate);
        row.Children.Add(CompactToolbar.Button("pin","贴图",()=>{using var output=ExportResult();new PinWindow(output).Show();Close();}));
        row.Children.Add(CompactToolbar.Button("save","保存 · Ctrl+S",Save));
        row.Children.Add(CompactToolbar.Button("close","关闭 · Esc",Close));
        row.Children.Add(CompactToolbar.Button("copy","完成并复制 · Enter",async()=>await PublishAsync(true),true));
        var body=new StackPanel();body.Children.Add(row);status.Margin=new Thickness(6,1,6,3);status.MaxWidth=520;status.TextTrimming=TextTrimming.CharacterEllipsis;AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);body.Children.Add(status);
        toolbar=CompactToolbar.Frame(body);toolbar.Name="SelectionToolbar";surface.Children.Add(toolbar);
        translationPanel=new InlineTranslationPanel(HideTranslation,CancelTranslation,ApplyTranslation,CopyTranslation);surface.Children.Add(translationPanel);
        translationPanel.Text.ContextMenuOpening+=(_,_)=>dialogOpen=true;
        translationPanel.Text.ContextMenuClosing+=(_,_)=> {dialogOpen=false;ScheduleDeactivationClose();};
        translationPanel.Text.AddHandler(CommandManager.PreviewExecutedEvent,new ExecutedRoutedEventHandler((_,e)=> {if(e.Command==ApplicationCommands.Copy||e.Command==ApplicationCommands.Cut)copying?.Cancel();}),true);
        SourceInitialized+=(_,_)=>PlaceWindow();
        Loaded+=async(_,_)=> {PlaceWindow();RefreshDocument();UpdateLayout();ArrangeSelection();Activate();Focus();if(autoCopy)await PublishAsync();};
        SizeChanged+=(_,_)=>ArrangeSelection();
        DpiChanged+=(_,_)=>ArrangeSelection();
        MouseLeftButtonDown+=(_,e)=> {if(InToolbar(e.OriginalSource as DependencyObject))return;BeginPointer(e.GetPosition(surface));e.Handled=true;};
        MouseMove+=(_,e)=>MovePointer(e.GetPosition(surface));
        MouseLeftButtonUp+=(_,e)=> {if(pointer.HasValue){EndPointer(e.GetPosition(surface));e.Handled=true;}};
        PreviewMouseWheel+=(_,e)=> {
            if(InToolbar(e.OriginalSource as DependencyObject))return;
            var physical=Physical(e.GetPosition(surface));
            if(BeginScrollFromWheel(new MouseWheelSample(physical,e.Delta,unchecked((uint)e.Timestamp),ControlPressed:Keyboard.Modifiers.HasFlag(ModifierKeys.Control),ShiftPressed:Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))))e.Handled=true;
        };
        LostMouseCapture+=(_,_)=>AbortPointer();
        MouseRightButtonDown+=(_,e)=> {if(InToolbar(e.OriginalSource as DependencyObject))return;e.Handled=true;Close();};
        PreviewKeyDown+=async(_,e)=> {
            if(optionsPopup.IsOpen){if(e.Key==Key.Escape){e.Handled=true;optionsPopup.IsOpen=false;}return;}
            if(e.Key==Key.Escape){e.Handled=true;if(pointer.HasValue)AbortPointer();else if(translationPanel.Visibility==Visibility.Visible)HideTranslation();else Close();}
            else if(translationPanel.IsKeyboardFocusWithin&&Keyboard.FocusedElement is TextBox&&e.Key!=Key.S)return;
            else if(e.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.None&&Keyboard.FocusedElement is not Button){e.Handled=true;await PublishAsync(true);}
            else if(Keyboard.Modifiers==ModifierKeys.Control) {
                if(e.Key==Key.C){e.Handled=true;await PublishAsync();}else if(e.Key==Key.S){e.Handled=true;Save();}
                else if(e.Key==Key.Z){e.Handled=true;History(false);}else if(e.Key==Key.Y){e.Handled=true;History(true);}
            }
        };
        // Switching to the destination app dismisses only the overlay. A queued
        // snapshot copy remains alive; closing never erases the clipboard.
        Deactivated+=(_,_)=> {AbortPointer();ScheduleDeactivationClose();};
        Closed+=(_,_)=> {closed=true;translationGeneration++;translationStop?.Cancel();translationStop=null;optionsPopup.IsOpen=false;AbortPointer();document.Dispose();};
        SelectTool("查看");RefreshDocument();
    }
    private void PlaceWindow() => Native.SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),desktop.X,desktop.Y,desktop.Width,desktop.Height,0x40);
    private void ScheduleDeactivationClose()=>Dispatcher.BeginInvoke(new Action(()=> {if(!closed&&!dialogOpen&&!optionsPopup.IsOpen&&!IsActive)Close();}));
    private Size SurfaceSize=>new(Math.Max(1,surface.ActualWidth),Math.Max(1,surface.ActualHeight));
    internal D.Point Physical(Point p)=>new(desktop.X+(int)Math.Round(p.X*desktop.Width/SurfaceSize.Width),desktop.Y+(int)Math.Round(p.Y*desktop.Height/SurfaceSize.Height));
    private Rect Local(D.Rectangle rect)=>new((rect.X-desktop.X)*SurfaceSize.Width/desktop.Width,(rect.Y-desktop.Y)*SurfaceSize.Height/desktop.Height,rect.Width*SurfaceSize.Width/desktop.Width,rect.Height*SurfaceSize.Height/desktop.Height);
    internal void RefreshDocument() {if(closed)return;picture.Source=Imaging.Source(document.Current);picture.Width=SurfaceSize.Width;picture.Height=SurfaceSize.Height;undo.IsEnabled=document.CanUndo;redo.IsEnabled=document.CanRedo;ArrangeSelection();}
    internal void SelectTool(string name) {AbortPointer();tool=name;foreach(var item in tools)CompactToolbar.Select(item.button,item.tool==name);Cursor=name=="查看"?Cursors.SizeAll:Cursors.Cross;}
    private static void Position(FrameworkElement element,Rect rect) {Canvas.SetLeft(element,rect.X);Canvas.SetTop(element,rect.Y);element.Width=Math.Max(0,rect.Width);element.Height=Math.Max(0,rect.Height);}
    private void ArrangeSelection()
    {
        if(closed||surface.ActualWidth<=0||surface.ActualHeight<=0)return;
        picture.Width=surface.ActualWidth;picture.Height=surface.ActualHeight;var r=Local(Selection);
        Position(masks[0],new Rect(0,0,surface.ActualWidth,r.Top));Position(masks[1],new Rect(0,r.Bottom,surface.ActualWidth,Math.Max(0,surface.ActualHeight-r.Bottom)));
        Position(masks[2],new Rect(0,r.Top,r.Left,r.Height));Position(masks[3],new Rect(r.Right,r.Top,Math.Max(0,surface.ActualWidth-r.Right),r.Height));Position(outline,r);
        var points=HandlePoints(r);for(int i=0;i<handles.Length;i++){Canvas.SetLeft(handles[i],points[i].X-4);Canvas.SetTop(handles[i],points[i].Y-4);}
        sizeText.Text=$"{Selection.Width} × {Selection.Height} px";dimensions.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        var monitor=Forms.Screen.FromRectangle(Selection).Bounds;var m=Local(D.Rectangle.Intersect(desktop,monitor));
        Canvas.SetLeft(dimensions,Math.Clamp(r.Left,m.Left,Math.Max(m.Left,m.Right-dimensions.DesiredSize.Width)));
        Canvas.SetTop(dimensions,r.Top-dimensions.DesiredSize.Height-7>=m.Top?r.Top-dimensions.DesiredSize.Height-7:Math.Min(m.Bottom-dimensions.DesiredSize.Height,r.Top+8));
        toolbar.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        var position=ToolbarPosition(r,m,toolbar.DesiredSize);Canvas.SetLeft(toolbar,position.X);Canvas.SetTop(toolbar,position.Y);
        if(translationPanel.Visibility==Visibility.Visible) {
            var card=TranslationPanelBounds(r,m,new Rect(position,toolbar.DesiredSize));Position(translationPanel,card);
        }
    }
    internal static Rect TranslationPanelBounds(Rect selection,Rect monitor,Rect toolbar)
    {
        var available=new Rect(monitor.Left+8,monitor.Top+8,Math.Max(1,monitor.Width-16),Math.Max(1,monitor.Height-16));
        double height=Math.Min(available.Height,Math.Clamp(selection.Height,240,480));
        foreach(double preferred in new[]{360d,280d,220d}) {
            double width=Math.Min(preferred,available.Width);
            double x=Math.Clamp(selection.Left,available.Left,available.Right-width),y=Math.Clamp(selection.Top,available.Top,available.Bottom-height);
            var candidates=new[]{new Rect(selection.Right+12,y,width,height),new Rect(selection.Left-width-12,y,width,height),new Rect(x,Math.Max(selection.Bottom+12,toolbar.Bottom+8),width,height),new Rect(x,Math.Min(selection.Top-height-12,toolbar.Top-height-8),width,height)};
            for(int i=0;i<candidates.Length;i++) {
                var card=candidates[i];
                if(i<2&&card.IntersectsWith(toolbar))card.Y=toolbar.Top-card.Height-8;
                if(available.Contains(card)&&!card.IntersectsWith(selection)&&!card.IntersectsWith(toolbar))return card;
            }
        }
        // A fullscreen selection has no outside area. Keep a compact, dismissible
        // card on that same screen, with scrolling for long text.
        double fallbackWidth=Math.Min(360,available.Width),fallbackHeight=Math.Min(320,available.Height);
        var fallback=new Rect(available.Right-fallbackWidth,available.Top,fallbackWidth,fallbackHeight);
        if(fallback.IntersectsWith(toolbar)) {
            double below=Math.Max(available.Top,toolbar.Bottom+8),above=Math.Min(available.Bottom,toolbar.Top-8);
            double belowSpace=Math.Max(0,available.Bottom-below),aboveSpace=Math.Max(0,above-available.Top);
            if(belowSpace>=aboveSpace){fallback.Y=below;fallback.Height=Math.Max(1,Math.Min(fallbackHeight,belowSpace));}
            else {fallback.Height=Math.Max(1,Math.Min(fallbackHeight,aboveSpace));fallback.Y=above-fallback.Height;}
        }
        return fallback;
    }
    internal static Point ToolbarPosition(Rect selection,Rect monitor,Size toolbar)
    {
        double x=Math.Clamp(selection.Right-toolbar.Width,monitor.Left+8,Math.Max(monitor.Left+8,monitor.Right-toolbar.Width-8));
        double y=selection.Bottom+12;
        if(y+toolbar.Height>monitor.Bottom-8)y=selection.Top-toolbar.Height-12;
        return new Point(x,Math.Clamp(y,monitor.Top+8,Math.Max(monitor.Top+8,monitor.Bottom-toolbar.Height-8)));
    }
    private static Point[] HandlePoints(Rect r)=>new[]{new Point(r.Left,r.Top),new Point((r.Left+r.Right)/2,r.Top),new Point(r.Right,r.Top),new Point(r.Right,(r.Top+r.Bottom)/2),new Point(r.Right,r.Bottom),new Point((r.Left+r.Right)/2,r.Bottom),new Point(r.Left,r.Bottom),new Point(r.Left,(r.Top+r.Bottom)/2)};
    private bool InToolbar(DependencyObject? source) {while(source!=null){if(ReferenceEquals(source,toolbar)||ReferenceEquals(source,translationPanel))return true;source=source is Visual?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source);}return false;}
    internal void BeginPointer(Point p)
    {
        if(closed||dialogOpen)return;var r=Local(Selection);var points=HandlePoints(r);action=-1;
        if(tool=="查看"||!r.Contains(p))for(int i=0;i<points.Length;i++)if(Math.Abs(p.X-points[i].X)<=8&&Math.Abs(p.Y-points[i].Y)<=8){action=i;break;}
        if(action<0)action=r.Contains(p)?(tool=="查看"?8:9):10;
        originalSelection=Selection;startPixel=Physical(p);pointer=p;
        if(action==9&&tool=="文字") {
            pointer=null;dialogOpen=true;string? text;
            try{text=Ui.Prompt(this,"添加文字");}finally{dialogOpen=false;}
            if(!string.IsNullOrWhiteSpace(text)){CommitAnnotation(startPixel,startPixel,text);}
            return;
        }
        if(!CaptureMouse())AbortPointer();
    }
    internal void MovePointer(Point p)
    {
        if(pointer is not Point origin)return;var end=Physical(p);
        if(action==9) {var rr=Local(D.Rectangle.Intersect(Selection,Imaging.Rect(startPixel,end)));Position(preview,rr);preview.Visibility=Visibility.Visible;return;}
        Selection=action==10?CaptureOverlay.MapSelection(origin,p,SurfaceSize.Width,SurfaceSize.Height,desktop):TransformSelection(originalSelection,desktop,action,end.X-startPixel.X,end.Y-startPixel.Y);
        ArrangeSelection();
    }
    internal void EndPointer(Point p)
    {
        if(!pointer.HasValue)return;MovePointer(p);var end=Physical(p);int committed=action;pointer=null;ReleaseMouseCapture();preview.Visibility=Visibility.Collapsed;
        if(Selection.Width<4||Selection.Height<4){Selection=originalSelection;ArrangeSelection();return;}
        if(committed==9){if(startPixel!=end)CommitAnnotation(startPixel,end,"");}
        else {if(Selection!=originalSelection)InvalidateTranslation();if(autoCopy)_=PublishAsync();}
    }
    private void AbortPointer() {if(!pointer.HasValue)return;pointer=null;if(action!=9)Selection=originalSelection;preview.Visibility=Visibility.Collapsed;if(IsMouseCaptured)ReleaseMouseCapture();ArrangeSelection();}
    internal static D.Rectangle TransformSelection(D.Rectangle r,D.Rectangle bounds,int action,int dx,int dy)
    {
        if(action==8)return new D.Rectangle(Math.Clamp(r.X+dx,bounds.Left,bounds.Right-r.Width),Math.Clamp(r.Y+dy,bounds.Top,bounds.Bottom-r.Height),r.Width,r.Height);
        int left=r.Left,top=r.Top,right=r.Right,bottom=r.Bottom;
        if(action is 0 or 6 or 7)left=Math.Clamp(left+dx,bounds.Left,right-4);
        if(action is 0 or 1 or 2)top=Math.Clamp(top+dy,bounds.Top,bottom-4);
        if(action is 2 or 3 or 4)right=Math.Clamp(right+dx,left+4,bounds.Right);
        if(action is 4 or 5 or 6)bottom=Math.Clamp(bottom+dy,top+4,bounds.Bottom);
        return D.Rectangle.FromLTRB(left,top,right,bottom);
    }
    internal D.Bitmap Export()=>Imaging.Crop(document.Current,new D.Rectangle(Selection.X-desktop.X,Selection.Y-desktop.Y,Selection.Width,Selection.Height));
    internal D.Bitmap ExportResult() {using var original=Export();return translationPanel.Visibility==Visibility.Visible&&translationBlocks.Count>0?TranslationLayout.Compose(original,translationBlocks):new D.Bitmap(original);}
    internal void CommitAnnotation(D.Point a,D.Point b,string text)
    {
        InvalidateTranslation();
        var selection=Selection;var local=new D.Rectangle(selection.X-desktop.X,selection.Y-desktop.Y,selection.Width,selection.Height);
        a=new D.Point(Math.Clamp(a.X-selection.X,0,selection.Width-1),Math.Clamp(a.Y-selection.Y,0,selection.Height-1));b=new D.Point(Math.Clamp(b.X-selection.X,0,selection.Width-1),Math.Clamp(b.Y-selection.Y,0,selection.Height-1));
        document.Apply(full=>{using var patch=Imaging.Crop(full,local);Annotation.Draw(patch,tool,a,b,text,color,stroke);using var g=D.Graphics.FromImage(full);g.CompositingMode=D.Drawing2D.CompositingMode.SourceCopy;g.DrawImageUnscaled(patch,local.Location);});
        RefreshDocument();if(autoCopy)_=PublishAsync();
    }
    internal void History(bool forward) {AbortPointer();if(forward?document.CanRedo:document.CanUndo){InvalidateTranslation();if(forward)document.Redo();else document.Undo();}RefreshDocument();if(autoCopy)_=PublishAsync();}
    private async Task PublishAsync(bool finish=false)
    {
        if(closed)return;AbortPointer();copying?.Cancel();var current=new CancellationTokenSource();copying=current;
        try {using var output=ExportResult();status.Text="正在复制…";await ClipboardImages.CopyAsync(output,current.Token);if(!closed&&ReferenceEquals(copying,current)){status.Text="已复制 · Enter 完成 / Alt+Tab 后直接粘贴";if(finish)Close();}}
        catch(OperationCanceledException) { }
        catch(Exception ex) {if(ReferenceEquals(copying,current)){if(!closed){status.Text="复制失败，请重试："+ex.Message;status.ToolTip=status.Text;}else (Application.Current as App)?.ReportClipboardFailure(ex.Message);}}
        finally {if(ReferenceEquals(copying,current))copying=null;current.Dispose();}
    }
    private void Save()
    {
        AbortPointer();
        dialogOpen=true;
        try {var dialog=new SaveFileDialog {Filter="PNG 图片|*.png|JPEG 图片|*.jpg",DefaultExt=".png",FileName=AppBrand.Name+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")};if(dialog.ShowDialog(this)==true){using var output=ExportResult();Imaging.Save(output,dialog.FileName,dialog.FilterIndex==2);status.Text="图片已保存";}}
        catch(Exception ex){status.Text="保存失败："+ex.Message;}
        finally {dialogOpen=false;}
    }
    private void CancelTranslation()=>translationStop?.Cancel();
    private void HideTranslation() {translationGeneration++;translationStop?.Cancel();translationStop=null;translate.IsEnabled=true;translationPanel.Visibility=Visibility.Collapsed;status.Text="已收起译文";if(autoCopy&&!closed)_=PublishAsync();}
    private void InvalidateTranslation() {translationGeneration++;translationStop?.Cancel();translationStop=null;translationBlocks.Clear();translationPanel.Clear();translate.IsEnabled=true;}
    private static OcrBlock CloneBlock(OcrBlock block)=>new() {Number=block.Number,Bounds=block.Bounds,Original=block.Original,Translation=block.Translation,Error=block.Error};
    internal async Task Translate(TranslationClient? client=null,Func<D.Bitmap,IProgress<string>,CancellationToken,Task<List<OcrBlock>>>? read=null)
    {
        if(closed||translationStop!=null)return;AbortPointer();
        var operation=new CancellationTokenSource();translationStop=operation;int generation=++translationGeneration;var selected=Selection;var token=operation.Token;
        bool Current()=>!closed&&generation==translationGeneration&&ReferenceEquals(translationStop,operation)&&Selection==selected;
        translate.IsEnabled=false;translationPanel.Visibility=Visibility.Visible;translationPanel.SetBusy(true,"正在识别并离线翻译，可取消…");ArrangeSelection();
        var progress=new Progress<string>(value=> {if(Current())translationPanel.SetMessage(value);});
        using var source=Export();var working=translationBlocks.Select(CloneBlock).ToList();string message="";
        try {
            if(working.Count==0)working=TranslationText.MergeForTranslation(await (read??LocalOcr.Read)(source,progress,token));
            token.ThrowIfCancellationRequested();if(!Current())return;
            if(working.Count==0){message="未识别到英语，请调整选区后重试。";return;}
            await (client??App.Instance.Translator).Fill(working,progress,token);token.ThrowIfCancellationRequested();if(!Current())return;
            translationBlocks=working;int failed=working.Count(block=>block.Translation.Length==0);
            message=failed==0?"翻译完成 · 可直接选中文字复制":"译文尚未完整，点击翻译重试。";
            translationPanel.SetResult(TranslationText.Translated(working),message);ArrangeSelection();
            if(autoCopy)_=PublishAsync();
        }catch(OperationCanceledException){message="翻译已取消，可重新点击翻译。";}
        catch(Exception ex){message="翻译未完成："+ex.Message;}
        finally {
            if(Current()){translationStop=null;translate.IsEnabled=true;translationPanel.SetBusy(false,message);}
            else if(!closed&&ReferenceEquals(translationStop,operation)){translationStop=null;InvalidateTranslation();}
            operation.Dispose();
        }
    }
    private void ApplyTranslation(string value)
    {
        if(closed||translationStop!=null)return;string edited=TranslationText.SingleParagraph(value);
        translationBlocks=edited.Length==0?TranslationText.MergeForTranslation(translationBlocks):new List<OcrBlock>{new(){Number=1,Original=TranslationText.Original(translationBlocks),Translation=edited}};
        translationPanel.SetResult(edited,"译文已更新");if(autoCopy)_=PublishAsync();
    }
    private void CopyTranslation()
    {
        copying?.Cancel();
        try {Clipboard.SetText(translationPanel.Text.Text);translationPanel.SetMessage("译文已复制");}
        catch(Exception ex){translationPanel.SetMessage("复制失败："+ex.Message);}
    }
    internal bool BeginScrollFromWheel(MouseWheelSample sample)
    {
        if(closed||pointer.HasValue||dialogOpen||optionsPopup.IsOpen||tool!="查看"||translationPanel.Visibility==Visibility.Visible||sample.Delta>=0||sample.ControlPressed||sample.ShiftPressed||!Selection.Contains(sample.Position)||Selection.Width<30||Selection.Height<80)return false;
        return StartScroll(sample);
    }
    private void StartScroll()
    {
        Close();Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(()=>App.Instance.StartCapture("区域",CaptureFollowUp.Scroll)));
    }
    private bool StartScroll(MouseWheelSample? firstWheel)
    {
        var area=Selection;if(area.Width<30||area.Height<80){status.Text="长截图选区至少 30 × 80 像素";return false;}
        var dpi=VisualTreeHelper.GetDpi(this);
        if(ScrollWindow.ToolbarPosition(area,(int)Math.Ceiling(284*dpi.DpiScaleX),(int)Math.Ceiling(58*dpi.DpiScaleY),Forms.Screen.AllScreens.Select(screen=>screen.WorkingArea)).IsEmpty){status.Text="请给蓝框外留出工具条空间，再滚动截长图。";return false;}
        // Clone the top before removing the frozen HWND. The first physical wheel
        // was delivered here; the live session forwards just that message once.
        using var initial=Export();
        var scroll=ScrollFactory?.Invoke(area,initial,firstWheel)??new ScrollWindow(area,b=>new EditorWindow(b,null,autoCopy).Show(),Imaging.Capture,initial,firstWheel);
        Close();scroll.Show();return true;
    }
}
