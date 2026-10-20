using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace QingCapture;

internal sealed class HomeWindow : Window
{
    public HomeWindow()
    {
        Ui.Setup(this,AppBrand.Name,350,197200d/350);Background=Ui.HomeCanvas;MinWidth=320;MinHeight=520;
        SmallWindow.Configure(this,40);
        var root=new Grid { Background=Ui.HomeCanvas };root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(40) });root.RowDefinitions.Add(new RowDefinition());
        var titlebar=new Grid { Name="HomeTitlebar",Background=Ui.Brush("#E8EBF3") };root.Children.Add(titlebar);
        var titleMark=new StackPanel { Name="HomeBrandLockup",Orientation=Orientation.Horizontal,Margin=new Thickness(24,0,0,0),VerticalAlignment=VerticalAlignment.Center };
        var brandMark=Ui.BrandMark();brandMark.Name="HomeBrandMark";titleMark.Children.Add(brandMark);
        titleMark.Children.Add(new TextBlock { Name="HomeBrandName",Text=AppBrand.Name,FontFamily=new FontFamily("Segoe UI"),FontSize=14,FontWeight=FontWeights.SemiBold,Foreground=Ui.Brush("#414960"),Margin=new Thickness(9,0,0,0),VerticalAlignment=VerticalAlignment.Center });titlebar.Children.Add(titleMark);
        var controls=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0) };
        controls.Children.Add(ChromeButton("最小化","−",()=>SystemCommands.MinimizeWindow(this)));controls.Children.Add(ChromeButton("关闭","×",Close));titlebar.Children.Add(controls);
        var dividerBrush=new LinearGradientBrush(new GradientStopCollection {
            new(Color.FromArgb(0,174,185,206),0),new(Color.FromRgb(174,185,206),.16),
            new(Color.FromRgb(174,185,206),.84),new(Color.FromArgb(0,174,185,206),1)
        },new Point(0,0),new Point(1,0));dividerBrush.Freeze();
        titlebar.Children.Add(new Border { Name="HomeTitlebarDivider",Height=1.5,Margin=new Thickness(24,0,24,0),VerticalAlignment=VerticalAlignment.Bottom,Background=dividerBrush,IsHitTestVisible=false,SnapsToDevicePixels=true });
        var content=new Grid { Margin=new Thickness(24,10,24,16) };content.RowDefinitions.Add(new RowDefinition());content.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });content.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var cards=new[]{CaptureCard("截图","crop",()=>App.Instance.StartCapture("自动",returnHomeOnCancel:true)),CaptureCard("长截屏","scroll",()=>App.Instance.StartCapture("区域",CaptureFollowUp.Scroll,returnHomeOnCancel:true))};
        cards[0].ToolTip="自动识别窗口，点击截取；拖动选择区域，Enter 截取全屏";
        var actions=new Grid { Margin=new Thickness(-4,0,-4,10) };for(int i=0;i<cards.Length;i++)actions.RowDefinitions.Add(new RowDefinition());
        for(int i=0;i<cards.Length;i++){Grid.SetRow(cards[i],i);actions.Children.Add(cards[i]);}content.Children.Add(actions);
        var utilities=new DockPanel { Margin=new Thickness(0,0,0,8) };
        var settings=Ui.Button("设置",()=>new SettingsWindow(this).ShowDialog(),icon:"settings");settings.Name="SettingsEntry";settings.Margin=new Thickness(0);DockPanel.SetDock(settings,Dock.Right);utilities.Children.Add(settings);
        var clipboard=Ui.Button("剪贴板贴图",App.Instance.PinClipboard,icon:"pin");clipboard.HorizontalAlignment=HorizontalAlignment.Left;clipboard.Margin=new Thickness(0);clipboard.ToolTip="将剪贴板中的图片置顶在屏幕上";utilities.Children.Add(clipboard);Grid.SetRow(utilities,1);content.Children.Add(utilities);
        var footer=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right };var hide=Ui.Button("收起",Hide);hide.ToolTip="收起到系统托盘";hide.Margin=new Thickness(0);footer.Children.Add(hide);
        var exit=Ui.Button("退出",App.Instance.ExitApp);exit.Margin=new Thickness(8,0,0,0);exit.Background=Brushes.Transparent;exit.BorderBrush=Brushes.Transparent;footer.Children.Add(exit);Grid.SetRow(footer,2);content.Children.Add(footer);
        var scroller=new ScrollViewer { Name="HomeScroll",Content=content,Background=Brushes.Transparent,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalContentAlignment=VerticalAlignment.Stretch };Grid.SetRow(scroller,1);root.Children.Add(scroller);Content=root;
        Loaded+=(_,_)=>UiMotion.Reveal(content);Closing+=(_,e)=> { if(!App.Instance.Exiting){e.Cancel=true;Hide();} };
    }
    private Button ChromeButton(string name,string glyph,Action action)
    {
        var button=new Button { Style=(Style)Application.Current.Resources["HomeChromeButton"],Content=glyph,FontFamily=new FontFamily("Segoe UI Symbol"),FontSize=17,ToolTip=name };
        AutomationProperties.SetName(button,name);WindowChrome.SetIsHitTestVisibleInChrome(button,true);button.Click+=(_,_)=>action();return button;
    }
    private static Button CaptureCard(string title,string icon,Action action)
    {
        var b=Ui.Button(title,action,true);b.Name="CaptureCard";b.Padding=new Thickness(20,12,20,12);b.MinHeight=80;b.HorizontalContentAlignment=HorizontalAlignment.Stretch;b.Margin=new Thickness(4);b.Background=b.BorderBrush=Ui.Brush("#626FA6");
        var p=new Grid();p.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });p.ColumnDefinitions.Add(new ColumnDefinition());var mark=Ui.Icon(icon,28,Ui.OnPrimary);p.Children.Add(mark);
        var titleText=Ui.Text(title,18,Ui.OnPrimary);titleText.FontWeight=FontWeights.SemiBold;titleText.Margin=new Thickness(16,0,0,0);titleText.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(titleText,1);p.Children.Add(titleText);b.Content=p;return b;
    }
}
