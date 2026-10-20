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
using System.Windows.Media;
using Microsoft.Win32;
using D=System.Drawing;

namespace QingCapture;

internal sealed class EditorWindow : Window
{
    private readonly ImageDocument document;
    private readonly D.Rectangle? screenArea;
    private readonly bool autoCopy;
    private readonly Image image=new() { Name="EditorPreviewImage",Stretch=Stretch.Fill,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,UseLayoutRounding=true,SnapsToDevicePixels=true };
    private readonly Canvas canvas=new() { Name="EditorPreviewCanvas",Background=Brushes.Transparent,UseLayoutRounding=true,SnapsToDevicePixels=true };
    private readonly ScrollViewer viewer=new() { Name="EditorPreviewScroll",HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,UseLayoutRounding=true,SnapsToDevicePixels=true };
    private readonly TextBlock status=Ui.Text("",11,Ui.Muted);
    private readonly List<Button> mutating=new();
    private readonly List<(string name,Button button)> tools=new();
    private readonly List<Popup> popups=new();
    private readonly ActivityBar activity=new();
    private readonly ComboBox colors,widths;
    private readonly Button undo,redo,translate,toggle,editTranslation,cancel,complete;
    private readonly System.Windows.Shapes.Rectangle preview=new() { Stroke=Ui.Plum,StrokeThickness=2,StrokeDashArray=new DoubleCollection{3,2},IsHitTestVisible=false,Visibility=Visibility.Collapsed };
    private D.Bitmap? translated;
    private List<OcrBlock> blocks=new();
    private bool showTranslation,closed,finishing,loadedCopy;
    private CancellationTokenSource? copying;
    private CancellationTokenSource? cts;
    private D.Point? start;
    private double zoom=1;
    private string tool="查看";
    private D.Color color=D.Color.FromArgb(201,85,103);
    private float stroke=3;
    private D.Bitmap Display=>showTranslation&&translated!=null?translated:document.Current;
    public EditorWindow(D.Bitmap bitmap,D.Rectangle? area,bool autoCopy=true)
    {
        Imaging.CheckSize(bitmap.Width,bitmap.Height);document=new(bitmap);screenArea=area;this.autoCopy=autoCopy;
        Ui.Setup(this,AppBrand.Title("编辑"),Math.Min(880,SystemParameters.WorkArea.Width-32),Math.Min(620,SystemParameters.WorkArea.Height-32));MinWidth=620;MinHeight=400;
        UseLayoutRounding=true;SnapsToDevicePixels=true;SmallWindow.Configure(this,28,allowMaximize:true);
        var root=new DockPanel { UseLayoutRounding=true,SnapsToDevicePixels=true };
        var titlebar=SmallWindow.Titlebar(this);titlebar.Height=28;
        foreach(var title in titlebar.Children.OfType<TextBlock>())title.Visibility=Visibility.Collapsed;
        foreach(var button in titlebar.Children.OfType<StackPanel>().SelectMany(panel=>panel.Children.OfType<Button>())) { button.Height=24;button.Width=32;button.MinHeight=24; }
        DockPanel.SetDock(titlebar,Dock.Top);root.Children.Add(titlebar);

        var bottom=new StackPanel { Background=Ui.Surface };
        DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);bottom.Children.Add(activity);
        var row=new StackPanel { Name="EditorCompactToolbar",Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(5,5,5,1) };
        bottom.Children.Add(row);
        foreach(var (name,icon) in new[]{("查看","view"),("箭头","arrow"),("框线","box"),("文字","text"),("马赛克","mosaic")}) {
            var button=CompactToolbar.Button(icon,name,()=>SelectTool(name));row.Children.Add(button);mutating.Add(button);tools.Add((name,button));CompactToolbar.Select(button,name==tool);
        }
        colors=new ComboBox { Width=102,Margin=new Thickness(0,4,0,10),ItemsSource=new[]{"玫瑰红","雾蓝","杏橙","石墨"},SelectedIndex=0,ToolTip="标注颜色" };AutomationProperties.SetName(colors,"标注颜色");
        colors.SelectionChanged+=(_,_)=> { if(colors.SelectedIndex>=0)color=new[]{D.Color.FromArgb(201,85,103),D.Color.FromArgb(82,102,206),D.Color.FromArgb(185,111,75),D.Color.FromArgb(41,50,72)}[colors.SelectedIndex]; };
        widths=new ComboBox { Width=102,Margin=new Thickness(0,4,0,0),ItemsSource=new[]{"细","中","粗"},SelectedIndex=1,ToolTip="线宽 / 字号" };AutomationProperties.SetName(widths,"线宽 / 字号");
        widths.SelectionChanged+=(_,_)=> { if(widths.SelectedIndex>=0)stroke=new[]{2f,3f,5f}[widths.SelectedIndex]; };
        var stylePanel=new StackPanel { Margin=new Thickness(9) };stylePanel.Children.Add(Ui.Text("颜色",11,Ui.Muted));stylePanel.Children.Add(colors);stylePanel.Children.Add(Ui.Text("线宽 / 字号",11,Ui.Muted));stylePanel.Children.Add(widths);
        var style=CompactToolbar.Button("settings","颜色 / 线宽",()=>{});mutating.Add(style);row.Children.Add(style);popups.Add(CompactToolbar.Popup(style,stylePanel));
        row.Children.Add(CompactToolbar.Separator());
        undo=CompactToolbar.Button("undo","撤销",()=>ChangeHistory(false));redo=CompactToolbar.Button("redo","重做",()=>ChangeHistory(true));row.Children.Add(undo);row.Children.Add(redo);mutating.Add(undo);mutating.Add(redo);
        row.Children.Add(CompactToolbar.Separator());
        translate=CompactToolbar.Button("translate","离线翻译",async()=>await Translate());translate.ToolTip="离线翻译 · 英语 → 简体中文";row.Children.Add(translate);
        cancel=CompactToolbar.Glyph("×","取消翻译",()=>cts?.Cancel());row.Children.Add(cancel);
        toggle=CompactToolbar.Button("window","查看原图",()=> { if(cts!=null||finishing)return;showTranslation=!showTranslation;Refresh();Fit();SynchronizeCopy(); });row.Children.Add(toggle);
        editTranslation=CompactToolbar.Button("text","修改译文",EditTranslation);row.Children.Add(editTranslation);
        row.Children.Add(CompactToolbar.Separator());
        row.Children.Add(CompactToolbar.Button("pin","贴图",()=>new PinWindow(Display).Show()));row.Children.Add(CompactToolbar.Button("save","保存",Save));
        complete=CompactToolbar.Button("copy","完成 · 复制",async()=>await CopyAsync(true),true);row.Children.Add(complete);
        var more=CompactToolbar.Button("menu","更多",()=>{});row.Children.Add(more);
        var morePanel=new StackPanel { Margin=new Thickness(3) };
        morePanel.Children.Add(Ui.Button("复制图片",async()=>await CopyAsync()));morePanel.Children.Add(Ui.Button("菜单",()=>App.Instance.ShowMenu(this),icon:"menu"));
        var longButton=Ui.Button("长截图",StartScroll,icon:"scroll");longButton.IsEnabled=area.HasValue&&area.Value.Height>=80;longButton.ToolTip=area.HasValue?"恢复原窗口并滚动所选区域":"区域截图后可使用";morePanel.Children.Add(longButton);if(longButton.IsEnabled)mutating.Add(longButton);
        var morePopup=CompactToolbar.Popup(more,morePanel);popups.Add(morePopup);
        foreach(var button in morePanel.Children.OfType<Button>())button.Click+=(_,_)=>morePopup.IsOpen=false;

        var foot=new DockPanel { Margin=new Thickness(11,0,8,5),LastChildFill=true };
        var zooms=new StackPanel { Orientation=Orientation.Horizontal };
        zooms.Children.Add(CompactToolbar.Glyph("−","缩小",()=>SetZoom(zoom/1.25)));
        zooms.Children.Add(CompactToolbar.Button("crop","适应",Fit));
        var native=CompactToolbar.Button("view","1:1",()=>SetZoom(1));native.Content=new TextBlock { Text="1:1",FontSize=11,VerticalAlignment=VerticalAlignment.Center };zooms.Children.Add(native);
        zooms.Children.Add(CompactToolbar.Glyph("+","放大",()=>SetZoom(zoom*1.25)));
        DockPanel.SetDock(zooms,Dock.Right);foot.Children.Add(zooms);
        status.VerticalAlignment=VerticalAlignment.Center;status.TextWrapping=TextWrapping.NoWrap;status.TextTrimming=TextTrimming.CharacterEllipsis;status.Margin=new Thickness(0);AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);foot.Children.Add(status);bottom.Children.Add(foot);

        canvas.Children.Add(image);canvas.Children.Add(preview);viewer.Content=canvas;viewer.Padding=new Thickness(24);viewer.Background=Ui.Brush("#E8ECF4");root.Children.Add(viewer);Content=root;
        Loaded+=async(_,_)=> { Fit();if(!loadedCopy) { loadedCopy=true;if(autoCopy)await CopyAsync(); } };
        DpiChanged+=(_,_)=>Refresh();
        image.MouseLeftButtonDown+=(_,e)=> {
            if(tool=="查看"||cts!=null||showTranslation||finishing)return;
            start=Pixel(e.GetPosition(image));e.Handled=true;
            if(tool=="文字") {
                var point=start.Value;start=null;var text=Ui.Prompt(this,"添加文字");
                if(!closed&&!string.IsNullOrWhiteSpace(text))Commit(b=>Annotation.Draw(b,"文字",point,point,text,color,stroke));
                return;
            }
            if(!image.CaptureMouse())start=null;
        };
        image.MouseMove+=(_,e)=> {
            if(start==null)return;var end=Pixel(e.GetPosition(image));var rect=Imaging.Rect(start.Value,end);
            var shown=PixelPreview.Selection(new Rect(rect.X,rect.Y,rect.Width,rect.Height),new Size(document.Current.Width,document.Current.Height),image.RenderSize);
            preview.Visibility=Visibility.Visible;Canvas.SetLeft(preview,shown.X);Canvas.SetTop(preview,shown.Y);preview.Width=shown.Width;preview.Height=shown.Height;
        };
        image.MouseLeftButtonUp+=(_,e)=> {
            if(start==null)return;var point=start.Value;var end=Pixel(e.GetPosition(image));CancelDrawing();e.Handled=true;
            if(point!=end)Commit(b=>Annotation.Draw(b,tool,point,end,"",color,stroke));
        };
        image.LostMouseCapture+=(_,_)=>CancelDrawing();Deactivated+=(_,_)=>CancelDrawing();
        viewer.PreviewMouseWheel+=(_,e)=> { if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetZoom(zoom*(e.Delta>0?1.15:1/1.15));e.Handled=true; } };
        PreviewKeyDown+=async(_,e)=> {
            if(popups.Any(popup=>popup.IsOpen)){if(e.Key==Key.Escape){e.Handled=true;foreach(var popup in popups)popup.IsOpen=false;}return;}
            if(e.Key==Key.Escape) {
                e.Handled=true;
                if(popups.Any(popup=>popup.IsOpen)) { foreach(var popup in popups)popup.IsOpen=false; }
                else if(start!=null)CancelDrawing();else if(cts!=null)cts.Cancel();else Close();
                return;
            }
            if(Keyboard.Modifiers==ModifierKeys.Control) {
                if(e.Key==Key.C) { e.Handled=true;await CopyAsync(); }
                else if(e.Key==Key.S) { e.Handled=true;Save(); }
                else if(cts==null&&!showTranslation&&!finishing&&e.Key==Key.Z) { e.Handled=true;ChangeHistory(false); }
                else if(cts==null&&!showTranslation&&!finishing&&e.Key==Key.Y) { e.Handled=true;ChangeHistory(true); }
            }
        };
        Closed+=(_,_)=> { closed=true;CancelDrawing();foreach(var popup in popups)popup.IsOpen=false;cts?.Cancel();translated?.Dispose();document.Dispose(); };
        Refresh();
    }
    internal D.Point Pixel(Point point)
    {
        var mapped=PixelPreview.Pixel(point,new Size(document.Current.Width,document.Current.Height),image.RenderSize);
        return new(Math.Clamp((int)mapped.X,0,document.Current.Width-1),Math.Clamp((int)mapped.Y,0,document.Current.Height-1));
    }
    private void SelectTool(string name)
    {
        CancelDrawing();tool=name;foreach(var entry in tools)CompactToolbar.Select(entry.button,entry.name==name);
        status.Text=name=="查看"?"滚动查看 · Ctrl + 滚轮缩放":"在图片上"+(name=="文字"?"点击添加文字":"拖动绘制"+name);
    }
    private void CancelDrawing() { start=null;preview.Visibility=Visibility.Collapsed;if(image.IsMouseCaptured)image.ReleaseMouseCapture(); }
    private void Commit(Action<D.Bitmap> draw) { if(closed)return;document.Apply(draw);InvalidateTranslation();Refresh();SynchronizeCopy(); }
    private void ChangeHistory(bool forward)
    {
        if(closed||cts!=null||showTranslation||finishing||(forward?!document.CanRedo:!document.CanUndo))return;
        CancelDrawing();if(forward)document.Redo();else document.Undo();InvalidateTranslation();Refresh();SynchronizeCopy();
    }
    private void InvalidateTranslation() { translated?.Dispose();translated=null;blocks.Clear();showTranslation=false; }
    private void Refresh()
    {
        if(closed)return;image.Source=Imaging.Source(Display);var size=PixelPreview.Size(Display.Width,Display.Height,zoom,VisualTreeHelper.GetDpi(image));image.Width=canvas.Width=size.Width;image.Height=canvas.Height=size.Height;RenderOptions.SetBitmapScalingMode(image,PixelPreview.Sampling(zoom));
        foreach(var button in mutating)button.IsEnabled=cts==null&&!showTranslation&&!finishing;
        colors.IsEnabled=widths.IsEnabled=cts==null&&!showTranslation&&!finishing;activity.SetActive(cts!=null);
        undo.IsEnabled=cts==null&&!showTranslation&&!finishing&&document.CanUndo;redo.IsEnabled=cts==null&&!showTranslation&&!finishing&&document.CanRedo;
        translate.IsEnabled=cts==null&&!finishing;cancel.Visibility=cts==null?Visibility.Collapsed:Visibility.Visible;
        toggle.Visibility=editTranslation.Visibility=translated==null?Visibility.Collapsed:Visibility.Visible;toggle.IsEnabled=editTranslation.IsEnabled=cts==null&&!finishing;
        CompactToolbar.Label(toggle,showTranslation?"查看原图":"查看对照图");complete.IsEnabled=cts==null&&!finishing;
        status.Text=$"{Display.Width} × {Display.Height} 像素 · {PixelPreview.ZoomLabel(zoom)}"+(showTranslation?" · 中英对照":"");
    }
    internal void SetZoom(double value) { if(closed)return;zoom=Math.Clamp(value,0.001,4);Refresh(); }
    private void Fit() { if(!closed)SetZoom(PixelPreview.Fit(Display.Width,Display.Height,viewer.ActualWidth-65,viewer.ActualHeight-65,VisualTreeHelper.GetDpi(image))); }
    private void SynchronizeCopy() { if(autoCopy&&!closed)_=CopyAsync(); }
    private async Task<bool> CopyAsync(bool closeAfter=false)
    {
        if(closed||finishing||(closeAfter&&cts!=null))return false;
        copying?.Cancel();var current=new CancellationTokenSource();copying=current;
        if(closeAfter) { finishing=true;CancelDrawing();Refresh(); }
        try {
            // This snapshot survives document replacement and window disposal while clipboard retries are pending.
            using var snapshot=new D.Bitmap(Display);await ClipboardImages.CopyAsync(snapshot,current.Token);
            if(closed)return true;
            if(ReferenceEquals(copying,current)&&cts==null) { status.Text="已复制，可直接粘贴。";UiMotion.Reveal(status); }
            if(closeAfter)Close();return true;
        } catch(OperationCanceledException) { return false; }
        catch(Exception ex) { if(ReferenceEquals(copying,current)) { if(!closed){status.Text="复制失败，请重试："+ex.Message;status.ToolTip=status.Text;UiMotion.Reveal(status);}else (Application.Current as App)?.ReportClipboardFailure(ex.Message); }return false; }
        finally {
            if(ReferenceEquals(copying,current))copying=null;current.Dispose();
            if(closeAfter&&!closed) { finishing=false;foreach(var button in mutating)button.IsEnabled=cts==null&&!showTranslation;undo.IsEnabled=cts==null&&!showTranslation&&document.CanUndo;redo.IsEnabled=cts==null&&!showTranslation&&document.CanRedo;translate.IsEnabled=cts==null;toggle.IsEnabled=editTranslation.IsEnabled=cts==null;colors.IsEnabled=widths.IsEnabled=cts==null&&!showTranslation;complete.IsEnabled=cts==null; }
        }
    }
    private void Save()
    {
        if(closed)return;var dialog=new SaveFileDialog { Filter="PNG 图片|*.png|JPEG 图片|*.jpg",FileName=AppBrand.Name+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+(showTranslation?"_中英对照":""),DefaultExt=".png" };
        if(dialog.ShowDialog(this)==true&&!closed) { try { Imaging.Save(Display,dialog.FileName,dialog.FilterIndex==2);status.Text="图片已保存。"; }catch(Exception ex) { status.Text="保存失败："+ex.Message; } }
    }
    private void StartScroll()
    {
        if(screenArea==null||closed)return;Hide();var scroll=new ScrollWindow(screenArea.Value,bitmap=> { if(!closed)Close();new EditorWindow(bitmap,null,autoCopy).Show(); });scroll.Closed+=(_,_)=> { if(!closed)Show(); };scroll.Show();
    }
    private static OcrBlock CloneBlock(OcrBlock block)=>new() { Number=block.Number,Bounds=block.Bounds,Original=block.Original,Translation=block.Translation,Error=block.Error };
    internal async Task Translate()
    {
        if(cts!=null||closed||finishing)return;CancelDrawing();var operation=new CancellationTokenSource();cts=operation;var token=operation.Token;Refresh();
        var progress=new Progress<string>(value=> { if(!closed&&ReferenceEquals(cts,operation))status.Text=value; });string message="";bool publish=false;
        using var source=new D.Bitmap(document.Current);var working=blocks.Select(CloneBlock).ToList();
        try {
            if(working.Count==0)working=TranslationText.MergeForTranslation(await LocalOcr.Read(source,progress,token));
            if(working.Count==0) { message="未识别到英语，请放大原页面后重新截图。";return; }
            await App.Instance.Translator.Fill(working,progress,token);token.ThrowIfCancellationRequested();
            if(closed)return;var result=TranslationLayout.Compose(source,working);
            blocks=working;translated?.Dispose();translated=result;showTranslation=true;publish=true;
            int failed=blocks.Count(block=>block.Translation.Length==0);
            message=failed==0?"离线翻译完成，可修改译文或直接粘贴对照图。":"译文尚未完整，点击离线翻译重试。";
        } catch(OperationCanceledException) { message="翻译已取消，原有译文已保留。"; }
        catch(Exception ex) { message="翻译未完成："+ex.Message; }
        finally {
            operation.Dispose();if(ReferenceEquals(cts,operation))cts=null;
            if(!closed) { Refresh();if(publish)Fit();status.Text=message;UiMotion.Reveal(status);if(publish)SynchronizeCopy(); }
        }
    }
    private void EditTranslation()
    {
        if(closed||cts!=null||finishing)return;var dialog=new TranslationEditorDialog(this,blocks);
        if(dialog.ShowDialog()==true&&!closed) {
            string edited=TranslationText.SingleParagraph(dialog.Translations[0]);
            var working=edited.Length==0?TranslationText.MergeForTranslation(blocks):new List<OcrBlock> { new() { Number=1,Original=TranslationText.Original(blocks),Translation=edited } };
            var result=TranslationLayout.Compose(document.Current,working);blocks=working;translated?.Dispose();translated=result;showTranslation=true;Refresh();Fit();SynchronizeCopy();
        }
        if(!closed)editTranslation.Focus();
    }
}

internal sealed class PinWindow : Window
{
    private readonly D.Bitmap bitmap;
    private CancellationTokenSource? copying;
    private readonly Button copy;
    private bool closed;
    public PinWindow(D.Bitmap source)
    {
        bitmap=new D.Bitmap(source);Title=AppBrand.Title("贴图");Topmost=true;ShowInTaskbar=false;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResizeWithGrip;
        var imageSource=Imaging.Source(bitmap);double ratio=(double)bitmap.Height/bitmap.Width;Width=Math.Clamp(bitmap.Width,160,700);Height=Math.Clamp(Width*ratio,80,700);
        var root=new DockPanel();var toolbar=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Background=Ui.Surface };DockPanel.SetDock(toolbar,Dock.Bottom);root.Children.Add(toolbar);
        copy=CompactToolbar.Button("copy","复制",async()=>await CopyAsync());toolbar.Children.Add(copy);toolbar.Children.Add(CompactToolbar.Glyph("−","缩小",()=>Scale(.8)));toolbar.Children.Add(CompactToolbar.Glyph("+","放大",()=>Scale(1.25)));toolbar.Children.Add(CompactToolbar.Glyph("×","关闭",Close));
        var image=new Image { Source=imageSource,Stretch=Stretch.Uniform };var scroll=new ScrollViewer { Content=image,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };root.Children.Add(scroll);Content=new Border { BorderBrush=Ui.Plum,BorderThickness=new Thickness(1),Child=root };
        image.MouseLeftButtonDown+=(_,e)=> { if(e.ClickCount==2)Close();else DragMove(); };image.MouseWheel+=(_,e)=> { Scale(e.Delta>0?1.15:1/1.15);e.Handled=true; };
        KeyDown+=async(_,e)=> { if(e.Key==Key.Escape)Close();else if(e.Key==Key.C&&Keyboard.Modifiers==ModifierKeys.Control) { e.Handled=true;await CopyAsync(); } };
        IsVisibleChanged+=(_,_)=> (Application.Current as App)?.RefreshTrayTooltip();
        Closed+=(_,_)=> { closed=true;bitmap.Dispose();(Application.Current as App)?.RefreshTrayTooltip(); };
    }
    private async Task CopyAsync()
    {
        if(closed)return;copying?.Cancel();var current=new CancellationTokenSource();copying=current;copy.IsEnabled=false;
        try { using var snapshot=new D.Bitmap(bitmap);await ClipboardImages.CopyAsync(snapshot,current.Token);if(!closed&&ReferenceEquals(copying,current))CompactToolbar.Label(copy,"已复制，可直接粘贴"); }
        catch(OperationCanceledException) {}
        catch(Exception ex) { if(ReferenceEquals(copying,current)){if(!closed)MessageBox.Show(this,"复制失败，请重试："+ex.Message,AppBrand.Name);else (Application.Current as App)?.ReportClipboardFailure(ex.Message);} }
        finally { if(ReferenceEquals(copying,current)) { copying=null;if(!closed)copy.IsEnabled=true; }current.Dispose(); }
    }
    private void Scale(double factor) { Width=Math.Clamp(Width*factor,160,3000);Height=Math.Clamp(Height*factor,80,2200); }
}
