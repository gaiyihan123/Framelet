using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace QingCapture;

// In-process WPF component tests: measure, arrange, inspect bounds, and render
// production controls. These do not replace physical multi-monitor testing.
internal static class LayoutAcceptance
{
    public static List<string> Run(string folder,Action<string>? progress=null)
    {
        var app=Application.Current as App ?? new App(); Ui.Resources(app);
        var lines=new List<string>();
        if(AppIdentity.Current()!=AppIdentity.Id)throw new InvalidOperationException("Process does not have its own taskbar identity");
        lines.Add("PASS: explicit Framelet.Desktop taskbar identity is set before any window is created");
        var home=new HomeWindow();
        CheckBrandIcon(home,lines);
        Check((FrameworkElement)home.Content,320,520,"home-minimum",folder,lines,progress);
        CheckCompactHome(home,"home-minimum",true,lines,progress);
        Check((FrameworkElement)home.Content,350,197200d/350,"home-default",folder,lines,progress);
        CheckCompactHome(home,"home-default",true,lines,progress);
        var cards=Descendants<Button>((FrameworkElement)home.Content).Where(button=>button.Name=="CaptureCard").ToArray();
        if(cards.Length!=2||!cards.Select(AutomationProperties.GetName).SequenceEqual(new[]{"截图","长截屏"})||cards.Any(card=>card.ActualHeight<80||card.Background.ToString()!="#FF626FA6"||card.BorderBrush.ToString()!="#FF626FA6"||Descendants<TextBlock>(card).Count()!=1||Descendants<TextBlock>(card).Any(text=>text.Foreground.ToString()!=Ui.OnPrimary.ToString())))
            throw new InvalidOperationException("Capture entry cards do not share the same color and label treatment");
        var cardBounds=cards.Select(card=>card.TransformToAncestor((FrameworkElement)home.Content).TransformBounds(new Rect(new Point(),card.RenderSize))).ToArray();
        if(cardBounds.Any(bounds=>Math.Abs(bounds.Left-cardBounds[0].Left)>.5||Math.Abs(bounds.Width-cardBounds[0].Width)>.5||Math.Abs(bounds.Height-cardBounds[0].Height)>.5)||cardBounds.Zip(cardBounds.Skip(1)).Any(pair=>pair.First.Bottom>pair.Second.Top+.5))
            throw new InvalidOperationException("Capture entry cards do not form an aligned, non-overlapping vertical stack");
        lines.Add("PASS: unified capture and long capture form two aligned mist-blue-violet home cards; three separate capture entries are removed");
        var settings=new SettingsWindow(null);
        Check((FrameworkElement)settings.Content,520,360,"settings-shortcuts-minimum",folder,lines,progress);
        CheckShortcutFields(settings,520,360,lines);
        Check((FrameworkElement)settings.Content,600,440,"settings-shortcuts-default",folder,lines,progress);
        CheckShortcutFields(settings,600,440,lines);
        using var fixture=SelfTests.Fixture();
        var editor=new EditorWindow(fixture,null,autoCopy:false);
        Check((FrameworkElement)editor.Content,620,400,"editor-minimum",folder,lines,progress);
        Check((FrameworkElement)editor.Content,880,620,"editor-default",folder,lines,progress);
        lines.AddRange(ImageQualityAcceptance.Run(home,editor,folder));
        var scroll=new ScrollWindow(new System.Drawing.Rectangle(0,0,800,600),_=>{});
        var root=(FrameworkElement)scroll.Content;
        var status=Descendants<TextBlock>(root).Single(t=>t.Text=="准备开始…");
        status.Text="暂停追加：无法可靠匹配。请缓慢滚回上一段，或完成已有长图。";
        status.ToolTip=status.Text;
        Check(root,284,58,"scroll-error",folder,lines,progress);
        var dialog=new TranslationEditorDialog(null,new[]{new OcrBlock { Number=1,Original="Review the original text before applying your translation.",Translation="应用修改前，请核对原文的日期、数字和条件。" },new OcrBlock { Number=2,Original="The price increases from $120 to $150 next month.",Translation="下个月价格将从120美元增加到150美元。" }});
        Check((FrameworkElement)dialog.Content,404,262,"translation-dialog-minimum",folder,lines,progress);
        settings.Close();editor.Close(); scroll.Close();dialog.Close();
        return lines;
    }
    private static void CheckBrandIcon(HomeWindow home,List<string> lines)
    {
        using var file=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"icon.ico"));
        var frames=BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames;
        int[] sizes={16,20,24,32,40,48,64,96,128,256};
        if(!frames.Select(frame=>frame.PixelWidth).SequenceEqual(sizes)||frames.Any(frame=>frame.PixelWidth!=frame.PixelHeight))
            throw new InvalidOperationException("Brand ICO is missing an explicit square size");
        foreach(var frame in frames) {
            int size=frame.PixelWidth;var pixels=new byte[size*size*4];
            new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0).CopyPixels(pixels,size*4,0);
            // The new F top bar reaches higher than the former Q; sample the
            // clear top padding rather than the glyph's antialiased edge.
            int tile=((int)(size*.12)*size+size/2)*4;
            if(pixels[3]!=0||pixels[tile+3]!=255||Math.Abs(pixels[tile]-166)>2||Math.Abs(pixels[tile+1]-111)>2||Math.Abs(pixels[tile+2]-98)>2)
                throw new InvalidOperationException($"Brand {size}px frame lost transparency or the home-card mist-blue-violet palette");
            int light=0;
            for(int i=0;i<pixels.Length;i+=4) {
                if(pixels[i+3]<240)continue;
                if(pixels[i+2]>pixels[i+1]+4||pixels[i+1]>pixels[i]+4)
                    throw new InvalidOperationException($"Brand {size}px frame retains an old warm/purple accent");
                if(pixels[i+2]>230&&pixels[i+1]>230&&pixels[i]>230)light++;
            }
            if(light<size*size*.08)throw new InvalidOperationException($"Brand {size}px frame does not retain the light capture mark");
        }
        lines.Add("PASS: brand ICO contains 10 transparent 16-256px frames with the home-card palette and a legible light mark");
        var titleMark=Descendants<System.Windows.Shapes.Path>((FrameworkElement)home.Content).Single(mark=>mark.Name=="HomeBrandMark");
        var titleName=Descendants<TextBlock>((FrameworkElement)home.Content).Single(text=>text.Name=="HomeBrandName");
        if(titleMark.Width!=20||titleMark.Height!=20||titleMark.IsHitTestVisible||titleMark.Fill.ToString()!="#FF626FA6"||titleMark.Data.ToString()!=Geometry.Parse(Ui.BrandMarkData).ToString()||!ReferenceEquals(home.Icon,Ui.BrandFrame(32)))
            throw new InvalidOperationException("Home titlebar does not use the shared Framelet vector mark or window icon");
        if(titleName.Text!="Framelet"||titleName.FontFamily.Source!="Segoe UI"||titleName.FontSize!=14||titleName.FontWeight!=FontWeights.SemiBold||titleName.Margin.Left!=9||titleName.Foreground.ToString()!="#FF414960")
            throw new InvalidOperationException("Home titlebar wordmark has lost the requested English typography or spacing");
        lines.Add("PASS: home titlebar uses a crisp untiled vector F capture mark and Segoe UI Framelet wordmark; window uses the shared application ICO");
    }
    private static void CheckShortcutFields(SettingsWindow settings,double width,double height,List<string> lines)
    {
        var root=(FrameworkElement)settings.Content;
        if(settings.ShortcutFields.Count!=6||Descendants<Expander>(root).Any()||Descendants<TextBlock>(root).Any(text=>text.Text is "更多快捷键" or "打开主界面"))
            throw new InvalidOperationException("Settings must expose six bindings directly without the removed expander or home shortcut");
        var scroller=Descendants<ScrollViewer>(root).Single(viewer=>viewer.Name=="SettingsScroll");
        if(width>=600&&height>=440&&scroller.ExtentHeight>scroller.ViewportHeight+.5)
            throw new InvalidOperationException($"Default settings layout requires scrolling for the six direct shortcut fields: extent={scroller.ExtentHeight}, viewport={scroller.ViewportHeight}");
        var titlebar=Descendants<Grid>(root).SingleOrDefault(child=>child.Name=="SettingsTitlebar");
        var chrome=WindowChrome.GetWindowChrome(settings);
        if(titlebar==null||titlebar.RenderSize.Height<39||titlebar.RenderSize.Width<root.RenderSize.Width-.5||settings.WindowStyle!=WindowStyle.None||settings.ResizeMode!=ResizeMode.CanMinimize||chrome?.CaptionHeight!=40)
            throw new InvalidOperationException("Settings window does not retain the compact titlebar");
        var chromeButtons=Descendants<Button>(titlebar).ToArray();
        if(chromeButtons.Length!=2||!chromeButtons.Select(AutomationProperties.GetName).SequenceEqual(new[]{"最小化","关闭"})||chromeButtons.Any(button=>!WindowChrome.GetIsHitTestVisibleInChrome(button)))
            throw new InvalidOperationException("Settings window does not expose only accessible minimize and close controls");
        foreach(var field in settings.ShortcutFields.Values) {
            field.BringIntoView();root.UpdateLayout();
            var bounds=field.TransformToAncestor(root).TransformBounds(new Rect(new Point(),field.RenderSize));
            if(bounds.Width<140||bounds.Height<38||bounds.Left<0||bounds.Right>width+.5||bounds.Top<0||bounds.Bottom>height+.5)
                throw new InvalidOperationException("Shortcut field cannot be reached without clipping: "+AutomationProperties.GetName(field));
        }
        foreach(var button in new[]{settings.CancelButton,settings.SaveButton}) {
            var bounds=button.TransformToAncestor(root).TransformBounds(new Rect(new Point(),button.RenderSize));
            if(bounds.Width<40||bounds.Height<32||bounds.Left<0||bounds.Right>width+.5||bounds.Top<0||bounds.Bottom>height+.5)
                throw new InvalidOperationException("Settings save or cancel action is obscured when scrolling shortcut fields");
        }
        lines.Add($"PASS: settings retains two titlebar controls; six direct shortcut fields and save/cancel remain reachable at {width}x{height}, without an expander or home shortcut");
    }
    private static void CheckCompactHome(HomeWindow home,string name,bool requireNoScrolling,List<string> lines,Action<string>? progress)
    {
        progress?.Invoke(name+": verify compact single-column layout");
        var root=(FrameworkElement)home.Content;
        var grid=(Grid)root;
        var chrome=WindowChrome.GetWindowChrome(home);
        var titlebar=grid.Children.OfType<Grid>().SingleOrDefault(child=>child.Name=="HomeTitlebar");
        if(home.Height<=home.Width||Math.Abs(home.Width*home.Height-680*290)>.5)
            throw new InvalidOperationException(name+": portrait home window does not preserve the previous overall area");
        if(grid.ColumnDefinitions.Count!=0||grid.RowDefinitions.Count!=2||Descendants<FrameworkElement>(root).Any(element=>element.Name=="HomeRail"||element.Name.StartsWith("Rail",StringComparison.Ordinal)))
            throw new InvalidOperationException(name+": removed sidebar still reserves layout space");
        if(home.WindowStyle!=WindowStyle.None||chrome==null||chrome.CaptionHeight!=40||titlebar==null||titlebar.RenderSize.Height<39||titlebar.RenderSize.Width<root.RenderSize.Width-.5)
            throw new InvalidOperationException(name+": compact titlebar does not fill the window width");
        var chromeButtons=Descendants<Button>(titlebar).ToArray();
        if(chromeButtons.Length!=2||!chromeButtons.Select(AutomationProperties.GetName).SequenceEqual(new[]{"最小化","关闭"})||home.ResizeMode!=ResizeMode.CanMinimize||chromeButtons.Any(button=>!WindowChrome.GetIsHitTestVisibleInChrome(button)))
            throw new InvalidOperationException(name+": titlebar controls are not accessible and clickable inside the drag region");
        if(!Descendants<TextBlock>(titlebar).Any(text=>text.Text=="Framelet"))
            throw new InvalidOperationException(name+": compact window title is missing");
        if(Descendants<FrameworkElement>(root).Any(element=>element.Name=="QuickMenu")||Descendants<ShortcutBox>(root).Any()||Descendants<TextBlock>(root).Any(text=>new[]{"快捷菜单","开始截图","本地 · 离线","英 → 中 · 离线翻译","快捷键与偏好"}.Contains(text.Text)))
            throw new InvalidOperationException(name+": removed labels, quick menu or inline settings remain on the home window");
        var entry=Descendants<Button>(root).Single(button=>button.Name=="SettingsEntry"&&AutomationProperties.GetName(button)=="设置");
        var clipboard=Descendants<Button>(root).Single(button=>AutomationProperties.GetName(button)=="剪贴板贴图");
        var bounds=entry.TransformToAncestor(root).TransformBounds(new Rect(new Point(),entry.RenderSize));
        var clipboardBounds=clipboard.TransformToAncestor(root).TransformBounds(new Rect(new Point(),clipboard.RenderSize));
        if(bounds.Width<60||bounds.Height<32||bounds.Bottom>root.RenderSize.Height+.5||bounds.Top<0||bounds.Left<0||bounds.Right>root.RenderSize.Width+.5||Math.Abs((bounds.Top+bounds.Bottom)/2-(clipboardBounds.Top+clipboardBounds.Bottom)/2)>1||bounds.IntersectsWith(clipboardBounds))
            throw new InvalidOperationException(name+": settings entry is clipped or does not share the clipboard action row");
        var scroller=grid.Children.OfType<ScrollViewer>().Single();
        if(requireNoScrolling&&scroller.ExtentHeight>scroller.ViewportHeight+.5)
            throw new InvalidOperationException(name+": default compact content requires vertical scrolling");
        var footerButtons=Descendants<Button>(root).Where(button=>new[]{"收起","退出"}.Contains(AutomationProperties.GetName(button))).ToArray();
        var footerBottom=footerButtons.Max(button=>button.TransformToAncestor(root).TransformBounds(new Rect(new Point(),button.RenderSize)).Bottom);
        if(root.RenderSize.Height-footerBottom>24||root.RenderSize.Height-footerBottom<10)
            throw new InvalidOperationException(name+": bottom controls leave an unbalanced blank area");
        lines.Add($"PASS: {name} preserves the 197200-square-DIP area in portrait proportions; removes the requested labels, quick menu and inline settings; retains two titlebar controls and a reachable settings entry beside clipboard"+(requireNoScrolling?", with no default scrolling":""));
    }
    private static void Check(FrameworkElement root,double width,double height,string name,string folder,List<string> lines,Action<string>? progress=null)
    {
        progress?.Invoke(name+": measure and arrange");
        root.Measure(new Size(width,height)); root.Arrange(new Rect(0,0,width,height)); root.UpdateLayout();
        int count=0;
        foreach(var button in Descendants<Button>(root).Where(b=>b.Visibility==Visibility.Visible && (b.Content is string || AutomationProperties.GetName(b).Length>0))) {
            var bounds=button.TransformToAncestor(root).TransformBounds(new Rect(new Point(),button.RenderSize));
            if(bounds.Top<0||bounds.Bottom>height) { button.BringIntoView(); root.UpdateLayout(); bounds=button.TransformToAncestor(root).TransformBounds(new Rect(new Point(),button.RenderSize)); }
            if(bounds.Left< -0.5 || bounds.Top< -0.5 || bounds.Right>width+0.5 || bounds.Bottom>height+0.5 || bounds.Height<24)
                throw new InvalidOperationException($"{name}: button {button.Content} clipped: {bounds} in {width}x{height}");
            count++;
        }
        if(count==0) throw new InvalidOperationException(name+": no visible buttons");
        progress?.Invoke(name+": controls within bounds; render sizes");
        foreach(var viewer in Descendants<ScrollViewer>(root)) viewer.ScrollToTop(); root.UpdateLayout();
        foreach(double scale in new[]{1d,1.25,1.5,2d}) {
            progress?.Invoke(name+$": render {scale:P0}");
            var target=new RenderTargetBitmap((int)Math.Ceiling(width*scale),(int)Math.Ceiling(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            // The component root is transparent; include the production Window background.
            var backdrop=new DrawingVisual(); using(var dc=backdrop.RenderOpen()) dc.DrawRectangle(Ui.Canvas,null,new Rect(0,0,width,height));
            target.Render(backdrop); target.Render(root); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using var file=File.Create(Path.Combine(folder,$"layout-{name}-{(int)(scale*100)}.png")); encoder.Save(file);progress?.Invoke(name+$": saved {scale:P0}");
        }
        lines.Add($"PASS: WPF {name}: {count} visible buttons within {width}x{height}, rendered at 100/125/150/200%");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i); if(child is T t) yield return t; foreach(var nested in Descendants<T>(child)) yield return nested; }
    }
}
