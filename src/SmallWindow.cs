using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace QingCapture;

internal static class SmallWindow
{
    public static void Configure(Window window,double captionHeight)
    {
        window.WindowStyle=WindowStyle.None;
        window.ResizeMode=ResizeMode.CanMinimize;
        WindowChrome.SetWindowChrome(window,new WindowChrome { CaptionHeight=captionHeight,ResizeBorderThickness=new Thickness(0),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(9),UseAeroCaptionButtons=false });
        window.SourceInitialized+=(_,_)=> {
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(BlockMaximize);
            var work=SystemParameters.WorkArea;
            window.MinWidth=Math.Min(window.MinWidth,Math.Max(320,work.Width-32));
            window.MinHeight=Math.Min(window.MinHeight,Math.Max(240,work.Height-32));
            window.Width=Math.Min(window.Width,Math.Max(320,work.Width-32));
            window.Height=Math.Min(window.Height,Math.Max(240,work.Height-32));
        };
        window.StateChanged+=(_,_)=> { if(window.WindowState==WindowState.Maximized) window.WindowState=WindowState.Normal; };
    }
    private static IntPtr BlockMaximize(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if((message==0x0112 && (wParam.ToInt64()&0xfff0)==0xf030) || (message==0x00a3 && wParam.ToInt64()==2)) handled=true;
        return IntPtr.Zero;
    }
    public static Grid Titlebar(Window window)
    {
        var bar=new Grid { Name="EditorTitlebar",Height=36,Background=Ui.Surface };
        var title=Ui.Text(window.Title,12,Ui.Muted); title.Margin=new Thickness(18,0,0,0);title.VerticalAlignment=VerticalAlignment.Center;bar.Children.Add(title);
        var controls=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,8,0) };
        foreach(var (name,glyph,action) in new (string,string,Action)[]{("最小化","−",()=>SystemCommands.MinimizeWindow(window)),("关闭","×",()=>window.Close())}) {
            var button=new Button { Style=(Style)Application.Current.Resources["HomeChromeButton"],Content=glyph,FontFamily=new FontFamily("Segoe UI Symbol"),FontSize=17,ToolTip=name };
            AutomationProperties.SetName(button,name);WindowChrome.SetIsHitTestVisibleInChrome(button,true);button.Click+=(_,_)=>action();controls.Children.Add(button);
        }
        bar.Children.Add(controls);return bar;
    }
}
