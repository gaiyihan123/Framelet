using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace QingCapture;

// Lives inside the frozen screenshot canvas, never in another window or popup.
internal sealed class InlineTranslationPanel : Border
{
    internal readonly TextBox Text=new() { Name="InlineTranslationText",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0),FontSize=15,VerticalContentAlignment=VerticalAlignment.Top };
    private readonly TextBlock status=Ui.Text("",11,Ui.Muted);
    private readonly Button edit,apply,discard,cancel,copy;
    private string committed="";
    private bool busy,hasResult;
    internal bool Editing=>!Text.IsReadOnly;
    internal InlineTranslationPanel(Action hide,Action cancelTranslation,Action<string> commit,Action copyText)
    {
        Name="InlineTranslationPanel";Background=Ui.Surface;BorderBrush=Ui.Border;BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(10);Padding=new Thickness(14);Visibility=Visibility.Collapsed;
        var root=new DockPanel();
        var header=new DockPanel { Margin=new Thickness(0,0,0,12) };
        var close=CompactToolbar.Glyph("×","收起译文",hide);DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var title=Ui.Text("英语 → 简体中文",13,Ui.Primary);title.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(title);DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var footer=new StackPanel();status.TextWrapping=TextWrapping.Wrap;status.Margin=new Thickness(0,10,0,8);footer.Children.Add(status);
        var actions=new WrapPanel();
        edit=Ui.Button("修改译文",()=> {Text.IsReadOnly=false;UpdateButtons();Text.Focus();Text.CaretIndex=Text.Text.Length;});
        apply=Ui.Button("应用",()=>commit(Text.Text),true);
        discard=Ui.Button("取消修改",()=> {Text.Text=committed;Text.IsReadOnly=true;UpdateButtons();});
        cancel=Ui.Button("取消翻译",cancelTranslation);copy=Ui.Button("复制译文",copyText);
        foreach(var button in new[]{edit,apply,discard,cancel,copy}) {button.MinHeight=28;button.Height=28;button.Padding=new Thickness(8,3,8,3);button.FontSize=12;button.Margin=new Thickness(0,0,6,0);actions.Children.Add(button);}
        footer.Children.Add(actions);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);root.Children.Add(Text);Child=root;
        AutomationProperties.SetName(Text,"完整译文");UpdateButtons();
    }
    internal void SetBusy(bool value,string message) {busy=value;Text.IsReadOnly=true;Text.Text=committed;status.Text=message;UpdateButtons();}
    internal void SetResult(string value,string message) {committed=value;hasResult=value.Length>0;Text.Text=value;Text.IsReadOnly=true;busy=false;status.Text=message;UpdateButtons();}
    internal void SetMessage(string message)=>status.Text=message;
    internal void Clear() {committed="";hasResult=false;SetBusy(false,"");Visibility=Visibility.Collapsed;}
    private void UpdateButtons()
    {
        edit.Visibility=copy.Visibility=hasResult&&!Editing&&!busy?Visibility.Visible:Visibility.Collapsed;
        apply.Visibility=discard.Visibility=Editing?Visibility.Visible:Visibility.Collapsed;
        cancel.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
    }
}
