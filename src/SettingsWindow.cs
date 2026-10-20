using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QingCapture;

internal sealed class SettingsWindow : Window
{
    internal readonly Dictionary<HotkeyAction,ShortcutBox> ShortcutFields=new();
    internal readonly Button CancelButton,SaveButton;
    private readonly CheckBox startup=new() { Content="开机启动" },motion=new() { Content="减少界面动画" };
    internal Settings ReadSettings()
    {
        var next=new Settings { Startup=startup.IsChecked==true,Configured=true,ReduceMotion=motion.IsChecked==true };
        foreach(var binding in HotkeyBindings.All)binding.Write(next,ShortcutFields[binding.Action].Value);
        return next;
    }
    public SettingsWindow(Window? owner)
    {
        Ui.Setup(this,AppBrand.Title("设置"),600,440);Owner=owner;WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner;MinWidth=520;MinHeight=360;Background=Ui.HomeCanvas;
        SmallWindow.Configure(this,40);KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);
        var root=new Grid { Background=Ui.HomeCanvas };root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(40) });root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var titlebar=SmallWindow.Titlebar(this);titlebar.Name="SettingsTitlebar";titlebar.Height=40;titlebar.Background=Brushes.Transparent;root.Children.Add(titlebar);
        var content=new StackPanel { Margin=new Thickness(20,10,20,10) };
        var title=Ui.Text("快捷键与偏好",17);title.FontWeight=FontWeights.SemiBold;title.Margin=new Thickness(0,0,0,4);content.Children.Add(title);
        var hint=Ui.Text("点击输入框后按键绑定，Backspace 清除。",11,Ui.Muted);hint.Margin=new Thickness(0,0,0,6);content.Children.Add(hint);
        Grid ShortcutGrid(HotkeyDefinition[] bindings)
        {
            var grid=new Grid { Margin=new Thickness(0,4,0,0) };grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());
            for(int i=0;i<bindings.Length;i++) {
                if(i%2==0)grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
                var binding=bindings[i];var field=new ShortcutBox(binding.Read(App.Instance.Settings)) { Padding=new Thickness(12,7,12,7) };ShortcutFields.Add(binding.Action,field);
                AutomationProperties.SetName(field,binding.Name+"快捷键");AutomationProperties.SetHelpText(field,binding.Help+"；点击后按键绑定，Backspace 清除");field.ToolTip=binding.Help+"\n点击后按键绑定，Backspace 清除";
                var group=new StackPanel { Margin=new Thickness(i%2==0?0:8,0,i%2==0?8:0,8) };var label=Ui.Text(binding.Name,12);label.Margin=new Thickness(0,0,0,5);group.Children.Add(label);group.Children.Add(field);Grid.SetColumn(group,i%2);Grid.SetRow(group,i/2);grid.Children.Add(group);
            }
            return grid;
        }
        content.Children.Add(ShortcutGrid(HotkeyBindings.All));
        var switches=new WrapPanel { Margin=new Thickness(0,4,0,0) };startup.IsChecked=App.Instance.Settings.Startup;motion.IsChecked=App.Instance.Settings.ReduceMotion;switches.Children.Add(startup);switches.Children.Add(motion);content.Children.Add(switches);
        var scroller=new ScrollViewer { Name="SettingsScroll",Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=Brushes.Transparent };Grid.SetRow(scroller,1);root.Children.Add(scroller);
        var status=Ui.Text("",11,Ui.Danger);status.Margin=new Thickness(0,0,12,0);status.VerticalAlignment=VerticalAlignment.Center;AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);
        var footer=new DockPanel { Margin=new Thickness(20,10,20,16) };var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        CancelButton=Ui.Button("取消",Close);CancelButton.Margin=new Thickness(0,0,8,0);SaveButton=Ui.Button("保存设置",()=> {var error=App.Instance.SaveSettings(ReadSettings());if(error==null)Close();else {status.Text=error;UiMotion.Reveal(status);} },true);SaveButton.Margin=new Thickness(0);
        buttons.Children.Add(CancelButton);buttons.Children.Add(SaveButton);DockPanel.SetDock(buttons,Dock.Right);footer.Children.Add(buttons);footer.Children.Add(status);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        Loaded+=(_,_)=> {CancelButton.Focus();UiMotion.Reveal(content);};PreviewKeyDown+=(_,e)=> {if(e.Key==Key.Escape){e.Handled=true;Close();}};
    }
}
