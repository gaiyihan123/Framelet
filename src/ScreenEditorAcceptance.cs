using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using D=System.Drawing;

namespace QingCapture;

internal static class ScreenEditorAcceptance
{
    internal static List<string> Run(string folder)
    {
        var app=Application.Current as App??new App();Ui.Resources(app);var lines=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);lines.Add("PASS: "+name);}
        var desktop=new D.Rectangle(-300,-200,1000,800);var selection=new D.Rectangle(-180,-100,445,391);
        using var fixture=new D.Bitmap(desktop.Width,desktop.Height);
        using(var g=D.Graphics.FromImage(fixture)){g.Clear(D.Color.FromArgb(244,246,251));for(int y=0;y<800;y+=17){using var ink=new D.SolidBrush(D.Color.FromArgb(y%256,(y*7)%256,(y*13)%256));g.FillRectangle(ink,0,y,1000,7);}}
        var editor=new ScreenEditorWindow(fixture,desktop,selection,autoCopy:false);
        try {
            var root=(FrameworkElement)editor.Content;root.Measure(new Size(800,640));root.Arrange(new Rect(0,0,800,640));root.UpdateLayout();
            editor.RefreshDocument();root.UpdateLayout();
            Check(Descendants<Image>(root).Single().Source!=null,"component preview renders the real frozen bitmap before evidence capture");
            Check(editor.Physical(new Point(96,80))==new D.Point(-180,-100),"frozen editor maps WPF coordinates into physical negative desktop pixels");
            using var original=Imaging.Crop(fixture,new D.Rectangle(120,100,445,391));var bytes=Imaging.Png(original);
            using(var exported=editor.Export())Check(bytes.SequenceEqual(Imaging.Png(exported)),"selection export excludes every toolbar, mask, outline and handle; original pixels remain lossless");
            Check(Descendants<System.Windows.Shapes.Ellipse>(root).Count()==8,"screen editor has eight resize handles");
            var toolbar=Descendants<Border>(root).Single(x=>x.Name=="SelectionToolbar");
            var buttons=Descendants<Button>(toolbar).ToArray();
            Check(buttons.Length==14&&buttons.All(b=>b.Width==32&&b.Height==32&&AutomationProperties.GetName(b).Length>0),"one compact 14-icon toolbar preserves annotations, history, scroll, translation, pin, save and finish");
            Check(!Descendants<TextBlock>(root).Any(t=>t.Text=="截图编辑"||t.Text=="标注 / 翻译 / 分享"),"screen editor removes the old heading and two text-button rows");
            foreach(double scale in new[]{1d,1.25,1.5,2d}) {
                var target=new RenderTargetBitmap((int)(800*scale),(int)(640*scale),96*scale,96*scale,PixelFormats.Pbgra32);target.Render(root);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(target));using var file=File.Create(Path.Combine(folder,$"screen-editor-{scale*100:0}.png"));encoder.Save(file);
            }
            lines.Add("PASS: production frozen selection editor rendered at 100/125/150/200% component scale");
            editor.SelectTool("框线");editor.CommitAnnotation(new D.Point(-170,-90),new D.Point(100,160),"");
            using(var annotated=editor.Export())Check(!bytes.SequenceEqual(Imaging.Png(annotated)),"screen-space annotation commits to selection-local original pixels");
            editor.History(false);using(var undone=editor.Export())Check(bytes.SequenceEqual(Imaging.Png(undone)),"frozen editor undo restores exact original selection pixels");
            editor.History(true);using(var redone=editor.Export())Check(!bytes.SequenceEqual(Imaging.Png(redone)),"frozen editor redo restores the annotation");
            using(var outside=Imaging.Crop(fixture,new D.Rectangle(0,0,50,50))) {
                // Annotation operations are restricted to the selected patch.
                editor.SelectTool("马赛克");editor.CommitAnnotation(new D.Point(-1000,-1000),new D.Point(2000,2000),"");
                editor.History(false);editor.History(false);
                using var restored=editor.Export();Check(bytes.SequenceEqual(Imaging.Png(restored)),"out-of-selection drawing is clamped and consecutive undo is reversible");
            }
        } finally {editor.Close();}
        var bounds=new D.Rectangle(-1920,-600,3840,1680);var r=new D.Rectangle(-1000,-200,500,300);
        Check(ScreenEditorWindow.TransformSelection(r,bounds,8,-9999,9999)==new D.Rectangle(-1920,780,500,300),"selection move clamps to physical desktop boundaries");
        int[] left={0,6,7},top={0,1,2},right={2,3,4},bottom={4,5,6};
        for(int i=0;i<8;i++) {
            var resized=ScreenEditorWindow.TransformSelection(r,bounds,i,30,40);
            Check(resized.Left==r.Left+(left.Contains(i)?30:0)&&resized.Top==r.Top+(top.Contains(i)?40:0)&&resized.Right==r.Right+(right.Contains(i)?30:0)&&resized.Bottom==r.Bottom+(bottom.Contains(i)?40:0),$"resize handle {i} adjusts only its intended edges");
            var limited=ScreenEditorWindow.TransformSelection(r,bounds,i,9999,-9999);
            Check(bounds.Contains(limited)&&limited.Width>=4&&limited.Height>=4,$"resize handle {i} enforces minimum dimensions and screen bounds");
        }
        foreach(var selected in new[]{new Rect(120,120,255,201),new Rect(5,530,40,65),new Rect(780,5,20,20),new Rect(0,0,800,600)}) {
            var monitor=new Rect(0,0,800,600);var size=new Size(520,64);var at=ScreenEditorWindow.ToolbarPosition(selected,monitor,size);
            Check(monitor.Contains(new Rect(at,size)),"selection toolbar remains on the actual monitor for edge/fullscreen selections");
        }
        Check(ScreenEditorWindow.ToolbarPosition(new Rect(-1700,-400,100,50),new Rect(-1920,-600,1920,1080),new Size(520,64)).X<0,"toolbar respects the selected negative-coordinate monitor instead of desktop gaps");
        RunLoaded(Check,folder);
        RunInlineTranslation(Check,folder);
        RunScrollHandoff(Check,folder);
        return lines;
    }
    private static void RunScrollHandoff(Action<bool,string> check,string folder)
    {
        var viewer=new ScrollViewer {CanContentScroll=false,VerticalScrollBarVisibility=ScrollBarVisibility.Visible};
        var rows=new StackPanel {Margin=new Thickness(18)};
        for(int i=0;i<100;i++)rows.Children.Add(Ui.Text($"File {i:0000} — reference {i*713+19} — 2026/10/{i%28+1:00}",18));
        viewer.Content=rows;var source=new Window {Title="Framelet scroll handoff acceptance",Content=viewer,Width=850,Height=650,Left=40,Top=40};
        int wheelMessages=0;ScrollWindow? session=null;D.Bitmap? result=null;ScreenEditorWindow? editor=null;
        try {
            source.Show();source.Activate();PumpForScroll(100);source.UpdateLayout();
            var sourceHwnd=new System.Windows.Interop.WindowInteropHelper(source).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(sourceHwnd)!.AddHook((IntPtr _,int message,IntPtr w,IntPtr l,ref bool handled)=> {
                if(message==MouseWheelObserver.MouseWheelMessage) {
                    wheelMessages++;int delta=unchecked((short)(w.ToInt64()>>16));
                    viewer.ScrollToVerticalOffset(Math.Max(0,viewer.VerticalOffset-delta/120d*56));handled=true;
                }
                return IntPtr.Zero;
            });
            var topLeft=viewer.PointToScreen(new Point(25,25));var bottomRight=viewer.PointToScreen(new Point(Math.Min(625,viewer.ActualWidth-30),Math.Min(475,viewer.ActualHeight-30)));
            var selected=D.Rectangle.FromLTRB((int)topLeft.X,(int)topLeft.Y,(int)bottomRight.X,(int)bottomRight.Y);
            var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
            PumpForScroll(200);
            using var snapshot=Imaging.Capture(desktop);using var original=Imaging.Crop(snapshot,new D.Rectangle(selected.X-desktop.X,selected.Y-desktop.Y,selected.Width,selected.Height));
            Imaging.Save(original,Path.Combine(folder,"live-scroll-first.png"));
            var sample=new MouseWheelSample(new D.Point(selected.Left+40,selected.Top+40),-120,1);
            editor=new ScreenEditorWindow(snapshot,desktop,selected,autoCopy:false);
            editor.ScrollFactory=(area,initial,wheel)=>session=new ScrollWindow(area,b=>result=new D.Bitmap(b),area=> {var image=Imaging.Capture(area);Imaging.Save(image,Path.Combine(folder,"live-scroll-current.png"));return image;},initial,wheel);
            editor.Show();PumpForScroll(100);
            check(!editor.BeginScrollFromWheel(sample with {ControlPressed=true})&&!editor.BeginScrollFromWheel(sample with {ShiftPressed=true})&&!editor.BeginScrollFromWheel(sample with {Position=new D.Point(selected.Left-5,selected.Top)}),"Ctrl zoom, Shift and out-of-selection wheel gestures do not enter live long capture");
            check(editor.BeginScrollFromWheel(sample),"turning the wheel in a frozen selection enters live long capture without pressing another button");
            WaitForScroll(()=>session?.ObserverAttached==true&&wheelMessages==1,4000);
            check(session!=null&&session.SelectionFrameVisible&&session.SelectionFrameBounds==selected&&!editor.IsVisible,"wheel handoff replaces the frozen image with the same live blue selection frame");
            WaitForScroll(()=>viewer.VerticalOffset==56,2000);
            PumpForScroll(300);
            check(wheelMessages==1&&viewer.VerticalOffset==56,"the first consumed wheel reaches the original native window exactly once");
            // The toolbar and frame must not steal the original application's focus.
            check((Native.GetWindowLongPtr(new System.Windows.Interop.WindowInteropHelper(session!).Handle,-20).ToInt64()&0x08000000)!=0,"live long capture controls are nonactivating so further natural wheel input reaches the source");
            using(var shown=Imaging.Capture(desktop))Imaging.Save(shown,Path.Combine(folder,"live-scroll-blue-frame.png"));
            session!.FinishForAcceptance();WaitForScroll(()=>!session.IsVisible,4000);
            check(result!=null&&result.Height>original.Height,$"the first wheel automatically appends the newly revealed source content (height={result?.Height}, initial={original.Height}, frames={session.FrameCount}, status={session.StatusText}, captures={session.CaptureCount})");
            using(var prefix=Imaging.Crop(result!,new D.Rectangle(0,0,original.Width,original.Height)))check(Imaging.Png(prefix).SequenceEqual(Imaging.Png(original)),"wheel handoff preserves the exact frozen top before the source scrolls");
            check(session.SelectionFrameReleased&&session.ImagesReleased&&!session.ObserverAttached,"finishing live handoff releases the blue frame, images and hooks");
        }finally {editor?.Close();session?.Close();source.Close();result?.Dispose();}
    }
    private static void WaitForScroll(Func<bool> condition,int milliseconds)
    {
        var deadline=DateTime.UtcNow.AddMilliseconds(milliseconds);while(!condition()&&DateTime.UtcNow<deadline)PumpForScroll(20);
        if(!condition())throw new TimeoutException("Live scroll handoff did not settle.");
    }
    private static void PumpForScroll(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=> {timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    private static void RunInlineTranslation(Action<bool,string> check,string folder)
    {
        var monitor=new Rect(0,0,1200,800);var toolbar=new Rect(100,650,520,64);
        var center=new Rect(100,100,400,300);var card=ScreenEditorWindow.TranslationPanelBounds(center,monitor,toolbar);
        check(card.Left>center.Right&&!card.IntersectsWith(center),"inline translation prefers the area beside the original selection");
        var shortSelection=new Rect(40,100,400,160);var shortToolbar=new Rect(8,272,520,64);var shortCard=ScreenEditorWindow.TranslationPanelBounds(shortSelection,monitor,shortToolbar);
        check(shortCard.Left>shortSelection.Right&&!shortCard.IntersectsWith(shortToolbar),"short screenshot translation stays beside the image by moving above the toolbar");
        var right=new Rect(900,100,250,300);var leftCard=ScreenEditorWindow.TranslationPanelBounds(right,monitor,toolbar);
        check(leftCard.Right<right.Left,"inline translation switches to the left at the screen edge");
        foreach(var selected in new[]{new Rect(0,0,1200,800),new Rect(5,5,40,40),new Rect(1150,750,45,45),new Rect(200,100,900,650)}) {
            var placed=ScreenEditorWindow.TranslationPanelBounds(selected,monitor,toolbar);
            check(monitor.Contains(placed)&&!placed.IntersectsWith(toolbar),"edge and fullscreen translation cards remain on screen and clear of the toolbar");
        }
        var negative=new Rect(-1920,-600,1920,1080);
        check(negative.Contains(ScreenEditorWindow.TranslationPanelBounds(new Rect(-1700,-400,400,300),negative,new Rect(-1700,100,520,64))),"translation placement preserves negative-coordinate monitor bounds");
        var compact=new Rect(0,0,683,384);var compactToolbar=new Rect(150,8,520,64);var compactCard=ScreenEditorWindow.TranslationPanelBounds(compact,compact,compactToolbar);
        check(compact.Contains(compactCard)&&compactCard.Height>=200&&!compactCard.IntersectsWith(compactToolbar),"fullscreen translation fits a small screen at 200% DPI without collapsing to one pixel");

        var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
        var selectedArea=new D.Rectangle(desktop.Left+40,desktop.Top+100,Math.Min(500,desktop.Width-80),Math.Min(260,desktop.Height-140));
        using var fixture=new D.Bitmap(desktop.Width,desktop.Height);
        using(var g=D.Graphics.FromImage(fixture)) {g.Clear(D.Color.FromArgb(240,242,248));using var font=new D.Font("Segoe UI",22,D.GraphicsUnit.Pixel);g.DrawString("This download must finish before\nyou close the window.",font,D.Brushes.Black,new D.RectangleF(selectedArea.X-desktop.X+14,selectedArea.Y-desktop.Y+20,selectedArea.Width-28,selectedArea.Height-40));}
        using var expected=Imaging.Crop(fixture,new D.Rectangle(selectedArea.X-desktop.X,selectedArea.Y-desktop.Y,selectedArea.Width,selectedArea.Height));var originalBytes=Imaging.Png(expected);
        string received="";const string result="下载完成前，请勿关闭此窗口。";
        using var client=new TranslationClient((text,ct)=> {received=text;return Task.FromResult(result);});
        // Exercise the very same factory/Loaded route used by the translation shortcut.
        var editor=App.CreateScreenEditor(fixture,desktop,selectedArea,CaptureFollowUp.Translate,autoCopy:false,client:client);
        Exception? error=null;var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};
        timer.Tick+=async(_,_)=> {
            timer.Stop();
            try {
                await Until(()=>editor.TranslationPanel.Text.Text==result);
                var panel=editor.TranslationPanel;var root=(FrameworkElement)editor.Content;
                check(editor.IsVisible&&editor.Selection==selectedArea&&!Application.Current.Windows.OfType<EditorWindow>().Any(w=>w.IsVisible),"translation shortcut keeps the same frozen editor and opens no translation window");
                check(received.Contains("download",StringComparison.OrdinalIgnoreCase)&&received.Contains("close",StringComparison.OrdinalIgnoreCase)&&!received.Contains('\n'),"inline translation combines real OCR lines into a complete sentence");
                using(var raw=editor.Export())check(Imaging.Png(raw).SequenceEqual(originalBytes),"opening inline translation preserves every original selection pixel");
                using(var output=editor.ExportResult())using(var prefix=Imaging.Crop(output,new D.Rectangle(0,0,expected.Width,expected.Height)))check(output.Width>expected.Width&&Imaging.Png(prefix).SequenceEqual(originalBytes),"inline translation image export includes continuous text but no overlay controls");
                editor.UpdateLayout();
                foreach(double scale in new[]{1d,1.25,1.5,2d}) {
                    var rendered=new RenderTargetBitmap((int)(root.ActualWidth*scale),(int)(root.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);rendered.Render(root);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using var file=File.Create(Path.Combine(folder,$"inline-translation-{scale*100:0}.png"));encoder.Save(file);
                }
                check(panel.ActualWidth>=220&&panel.Text.ActualHeight>40,"loaded inline translation has a readable, scrollable text area");
                using(var pendingCopy=new CancellationTokenSource()) {
                    var copyField=typeof(ScreenEditorWindow).GetField("copying",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;copyField.SetValue(editor,pendingCopy);
                    ExecutedRoutedEventHandler blockCopy=(_,e)=> {if(e.Command==ApplicationCommands.Copy)e.Handled=true;};panel.Text.AddHandler(CommandManager.PreviewExecutedEvent,blockCopy,true);
                    try {panel.Text.SelectAll();ApplicationCommands.Copy.Execute(null,panel.Text);check(pendingCopy.IsCancellationRequested,"inline text copying cancels an older queued image publication without touching the test clipboard");}
                    finally {panel.Text.RemoveHandler(CommandManager.PreviewExecutedEvent,blockCopy);copyField.SetValue(editor,null);}
                }
                Button Action(string name)=>Descendants<Button>(panel).Single(b=>AutomationProperties.GetName(b)==name);
                Action("修改译文").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));panel.Text.Text="第一句。\n\n第二句。";panel.Text.Focus();
                var source=PresentationSource.FromVisual(editor);
                var enter=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};editor.RaiseEvent(enter);
                check(!enter.Handled&&editor.IsVisible&&editor.Selection==selectedArea,"Enter in inline text is not intercepted as screenshot completion");
                var rightClick=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Right){RoutedEvent=UIElement.MouseRightButtonDownEvent};panel.Text.RaiseEvent(rightClick);
                check(editor.IsVisible&&editor.Selection==selectedArea,"right-clicking inline text does not dismiss or alter the screenshot");
                Action("应用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(panel.Text.Text=="第一句。 第二句。"&&!panel.Editing,"inline text edits apply as one continuous paragraph without a dialog");
                using(var raw=editor.Export())check(Imaging.Png(raw).SequenceEqual(originalBytes),"inline text edits leave the frozen document untouched");
                editor.SelectTool("框线");editor.CommitAnnotation(new D.Point(selectedArea.Left+20,selectedArea.Top+20),new D.Point(selectedArea.Left+100,selectedArea.Top+80),"");
                check(panel.Visibility==Visibility.Collapsed,"annotation invalidates the previous inline translation");editor.History(false);
                Task<List<OcrBlock>> Read(string text)=>Task.FromResult(new List<OcrBlock>{new(){Original=text}});
                var oldRead=new TaskCompletionSource<List<OcrBlock>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var old=editor.Translate(client,(bitmap,progress,ct)=>oldRead.Task);
                Action("收起译文").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(editor.IsVisible&&panel.Visibility==Visibility.Collapsed,"collapsing translation retains the original frozen screen");
                using var latest=new TranslationClient((text,ct)=>Task.FromResult("最新译文。"));
                await editor.Translate(latest,(bitmap,progress,ct)=>Read("The latest complete source sentence."));
                oldRead.SetResult(new List<OcrBlock>{new(){Original="An old source sentence."}});await old;
                check(panel.Text.Text=="最新译文。"&&panel.Visibility==Visibility.Visible,"a cancelled older OCR result cannot overwrite a newer inline translation");
                // Selection changes must cancel stale work as well as remove cached results.
                editor.SelectTool("查看");
                editor.BeginPointer(editor.PointFromScreen(new Point(selectedArea.Left+150,selectedArea.Top+130)));
                editor.MovePointer(editor.PointFromScreen(new Point(selectedArea.Left+170,selectedArea.Top+150)));
                editor.EndPointer(editor.PointFromScreen(new Point(selectedArea.Left+170,selectedArea.Top+150)));
                check(panel.Visibility==Visibility.Collapsed&&editor.Selection!=selectedArea,"moving the selection invalidates its old translation");
                var closingRead=new TaskCompletionSource<List<OcrBlock>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var closing=editor.Translate(client,(bitmap,progress,ct)=>closingRead.Task);editor.Close();closingRead.SetResult(new List<OcrBlock>{new(){Original="A late sentence after close."}});await closing;
                check(!editor.IsVisible,"closing during OCR safely discards late results without opening another window");
            }catch(Exception ex){error=ex;}
            finally{if(editor.IsVisible)editor.Close();finished.SetResult();}
        };
        timer.Start();editor.ShowDialog();timer.Stop();
        if(!finished.Task.IsCompleted) {
            var frame=new DispatcherFrame();var settle=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)};var deadline=DateTime.UtcNow.AddSeconds(15);
            settle.Tick+=(_,_)=> {if(finished.Task.IsCompleted||DateTime.UtcNow>deadline)frame.Continue=false;};settle.Start();Dispatcher.PushFrame(frame);settle.Stop();
            if(!finished.Task.IsCompleted)throw new TimeoutException("Closing translation acceptance did not settle");
        }
        if(error!=null)throw new InvalidOperationException("Loaded inline translation acceptance failed",error);
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(!condition()){if(DateTime.UtcNow>deadline)throw new TimeoutException("Inline translation did not settle");await Task.Delay(20);}
    }
    private static void RunLoaded(Action<bool,string> check,string folder)
    {
        var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
        using var fixture=new D.Bitmap(desktop.Width,desktop.Height);using(var g=D.Graphics.FromImage(fixture))g.Clear(D.Color.FromArgb(240,242,248));
        foreach(string path in new[]{"move","resize","annotation","lost","popup"}) {
            Native.GetCursorPos(out var physical);int x=Math.Clamp(physical.X-150,desktop.Left,desktop.Right-300),y=Math.Clamp(physical.Y-100,desktop.Top,desktop.Bottom-200);
            var selected=new D.Rectangle(x,y,300,200);
            if(path=="resize") {x=Math.Clamp(physical.X,desktop.Left,desktop.Right-300);y=Math.Clamp(physical.Y,desktop.Top,desktop.Bottom-200);selected=new D.Rectangle(x,y,300,200);}
            var editor=new ScreenEditorWindow(fixture,desktop,selected,autoCopy:false);var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};Exception? error=null;
            timer.Tick+=(_,_)=> {
                timer.Stop();
                try {
                    if(path=="popup") {
                        Descendants<Button>((DependencyObject)editor.Content).Single(b=>AutomationProperties.GetName(b)=="标注颜色和线宽").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        editor.UpdateLayout();check(editor.OptionsOpen,"loaded frozen editor opens its annotation options popup");
                        editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor),0,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                        check(editor.IsVisible&&editor.OptionsOpen,"Enter in options does not publish or close the screenshot");
                        editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                        check(editor.IsVisible&&!editor.OptionsOpen,"Escape closes only the options popup and retains the frozen editor");return;
                    }
                    Native.GetCursorPos(out var cursor);var origin=editor.PointFromScreen(new Point(cursor.X,cursor.Y));
                    if(path=="resize")origin=editor.PointFromScreen(new Point(selected.Left,selected.Top));
                    if(path=="annotation")editor.SelectTool("框线");
                    editor.BeginPointer(origin);bool captured=editor.IsMouseCaptured;
                    var end=new Point(origin.X+40,origin.Y+30);
                    if(path=="lost") {editor.MovePointer(end);editor.ReleaseMouseCapture();editor.EndPointer(end);check(editor.Selection==selected,"loaded frozen editor discards unfinished movement after real native mouse-capture loss");}
                    else {
                        // Begin/Move/End are the exact methods wired to real mouse events.
                        editor.MovePointer(end);editor.EndPointer(end);
                        if(path=="annotation") {using var output=editor.Export();using var expected=Imaging.Crop(fixture,new D.Rectangle(selected.X-desktop.X,selected.Y-desktop.Y,selected.Width,selected.Height));check(captured&&editor.Selection==selected&&!Imaging.Png(output).SequenceEqual(Imaging.Png(expected)),"loaded frozen editor annotation drag modifies pixels without moving the selection");}
                        else {var a=editor.Physical(origin);var b=editor.Physical(end);var expected=ScreenEditorWindow.TransformSelection(selected,desktop,path=="resize"?0:8,b.X-a.X,b.Y-a.Y);check(captured&&editor.Selection==expected,$"loaded frozen editor {path} handler preserves its physical selection after mouse release");}
                    }
                    if(path=="move") {
                        editor.UpdateLayout();var frame=new DispatcherFrame();var settle=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(120)};settle.Tick+=(_,_)=>{settle.Stop();frame.Continue=false;};settle.Start();Dispatcher.PushFrame(frame);
                        using var shown=Imaging.Capture(desktop);Imaging.Save(shown,Path.Combine(folder,"loaded-screen-editor-desktop.png"));var sample=editor.Selection;var center=shown.GetPixel(sample.X-desktop.X+sample.Width/2,sample.Y-desktop.Y+sample.Height/2);
                        check(center.ToArgb()==D.Color.FromArgb(240,242,248).ToArgb(),$"actual loaded screen editor displays unscaled original selection pixels on the current desktop (visible={editor.IsVisible}, active={editor.IsActive}, selection={sample}, pixel={center})");
                    }
                }catch(Exception ex){error=ex;}
                finally{editor.Close();}
            };
            timer.Start();editor.ShowDialog();timer.Stop();if(error!=null)throw new InvalidOperationException("Loaded screen editor pointer acceptance failed: "+path,error);
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T t)yield return t;foreach(var item in Descendants<T>(child))yield return item;}}
}
