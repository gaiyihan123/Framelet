using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace QingCapture;

internal sealed class TranslationEditorDialog : Window
{
    internal readonly List<TextBox> Fields=new();
    internal readonly Button CancelButton;
    public IReadOnlyList<string> Translations=>Fields.Select(input=>input.Text).ToArray();
    public TranslationEditorDialog(Window? owner,IReadOnlyList<OcrBlock> blocks)
    {
        Owner=owner;MinWidth=420;MinHeight=300;Ui.Setup(this,"修改译文",600,600);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);
        var root=new DockPanel { Margin=new Thickness(18) };var list=new StackPanel();
        string original=TranslationText.Original(blocks);
        var source=Ui.Text(original,12,Ui.Muted);source.TextWrapping=TextWrapping.Wrap;list.Children.Add(source);
        var field=new TextBox { Text=TranslationText.SingleParagraph(string.Join(" ",blocks.Select(block=>block.Translation))),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=160,Margin=new Thickness(0,8,0,16) };
        AutomationProperties.SetName(field,"完整译文");AutomationProperties.SetHelpText(field,original);Fields.Add(field);list.Children.Add(field);
        var buttons=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0) };
        CancelButton=Ui.Button("取消",()=>DialogResult=false);CancelButton.IsCancel=true;
        buttons.Children.Add(CancelButton);buttons.Children.Add(Ui.Button("应用修改",()=>DialogResult=true,true));
        DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);root.Children.Add(new ScrollViewer { Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto });Content=root;
        Loaded+=(_,_)=> { if(Fields.Count>0)Fields[0].Focus();else CancelButton.Focus(); };
    }
}
