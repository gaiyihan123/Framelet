using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QingCapture;

internal static class CompactToolbar
{
    public static Button Button(string icon,string label,Action action,bool primary=false)
    {
        var button=Ui.Button(label,action,primary);
        button.Content=icon=="close"?Symbol("×"):Ui.Icon(icon,18);button.Width=button.Height=32;
        button.MinWidth=button.MinHeight=32;button.Margin=new Thickness(1);
        button.Padding=new Thickness(6);button.ToolTip=label;
        if(!primary) { button.Background=Brushes.Transparent;button.BorderBrush=Brushes.Transparent; }
        return button;
    }
    public static void Select(Button button,bool selected)
    {
        button.Background=selected?Ui.Soft:Brushes.Transparent;
        button.Foreground=selected?Ui.Primary:Ui.Ink;
        button.BorderBrush=selected?Ui.Primary:Brushes.Transparent;
        AutomationProperties.SetHelpText(button,selected?"当前选中工具":"点击切换工具");
    }
    public static Border Separator()=>new() {
        Width=1,Height=20,Margin=new Thickness(5,0,5,0),Background=Ui.Border,
        VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false
    };
    public static Border Frame(UIElement child)=>new() {
        Background=Ui.Surface,BorderBrush=Ui.Border,BorderThickness=new Thickness(1),
        CornerRadius=new CornerRadius(10),Padding=new Thickness(5),Child=child,
        UseLayoutRounding=true,SnapsToDevicePixels=true
    };
    public static Popup Popup(Button anchor,UIElement child)
    {
        var popup=new Popup {
            PlacementTarget=anchor,Placement=PlacementMode.Top,VerticalOffset=-7,
            AllowsTransparency=true,StaysOpen=false,Child=Frame(child)
        };
        KeyboardNavigation.SetTabNavigation(child,KeyboardNavigationMode.Cycle);
        popup.Opened+=(_,_)=>child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        popup.Closed+=(_,_)=> { if(Window.GetWindow(anchor)?.IsActive==true)anchor.Focus(); };
        child.PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) { e.Handled=true;popup.IsOpen=false; } };
        anchor.Click+=(_,_)=>popup.IsOpen=!popup.IsOpen;
        return popup;
    }
    public static Button Glyph(string glyph,string label,Action action)
    {
        var button=Button("view",label,action);
        button.Content=Symbol(glyph);
        return button;
    }
    private static TextBlock Symbol(string glyph)=>new() { Text=glyph,FontFamily=new FontFamily("Segoe UI Symbol"),FontSize=18,VerticalAlignment=VerticalAlignment.Center };
    public static void Label(Button button,string label)
    {
        button.ToolTip=label;AutomationProperties.SetName(button,label);
    }
}
