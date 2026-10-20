using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QingCapture;

internal static class UiAcceptance
{
    public static List<string> Run(Action<string>? progress=null)
    {
        var app=Application.Current as App??new App();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Ui.Resources(app);var lines=new List<string>();
        void Check(bool ok,string text) { progress?.Invoke("check: "+text);if(!ok)throw new Exception("FAIL: "+text);lines.Add("PASS: "+text); }
        var palette=new[]{Ui.Primary,Ui.Accent,Ui.Ink,Ui.Muted,Ui.Canvas,Ui.Surface,Ui.Field,Ui.Border,Ui.Success,Ui.Danger};
        Check(palette.Cast<SolidColorBrush>().All(b=>!(b.Color.G>b.Color.R*1.08&&b.Color.G>b.Color.B*1.08)),"UI palette has no green-dominant semantic color");
        Check(Contrast(Ui.Ink,Ui.Surface)>=4.5&&Contrast(Ui.Muted,Ui.Surface)>=4.5&&Contrast(Ui.Muted,Ui.Canvas)>=4.5&&Contrast(Ui.OnWarm,Ui.Warm)>=4.5&&Contrast(Ui.OnPrimary,Ui.Primary)>=4.5,"body, secondary, warm badge and primary-button text meet 4.5:1 contrast");
        Check(Ui.HomeCanvas is LinearGradientBrush background&&background.GradientStops.Count>=3&&background.GradientStops.All(stop=>stop.Color.A==255&&!(stop.Color.G>stop.Color.R*1.08&&stop.Color.G>stop.Color.B*1.08)),"compact home keeps a continuous non-green warm-fog background");
        var darkBackdrop=Ui.Brush("#121827");var glassSurface=Composite(Ui.HomeGlass,darkBackdrop);
        Check(((SolidColorBrush)Ui.HomeGlass).Color.A<255&&Contrast(Ui.Ink,glassSurface)>=4.5&&Contrast(Ui.Muted,glassSurface)>=4.5,"translucent home glass preserves primary and secondary text contrast over dark backdrops");
        int clicks=0;var button=Ui.Button("测试按钮",()=>clicks++,true,"copy");
        var checkbox=new CheckBox { Content="测试开关" };
        var input=new TextBox { Text="输入焦点" };
        var choice=new ComboBox { ItemsSource=new[]{"细","粗"},SelectedIndex=0 };
        var expandedInput=new TextBox { Text="展开后的快捷键字段" };
        var expander=new Expander { Header="测试展开内容",Content=expandedInput };
        int expanderLoadedCount=0;expander.Loaded+=(_,_)=>expanderLoadedCount++;
        var busy=new ActivityBar();var panel=new StackPanel { Margin=new Thickness(16) };
        foreach(var control in new UIElement[]{button,checkbox,input,choice,expander,busy})panel.Children.Add(control);
        // A real, non-activating host gives controls their actual Loaded/IsVisible
        // lifecycle. Ignore the physical pointer; only these routed-event probes run.
        var host=new Window { Title=AppBrand.Title("界面状态回归"),Width=340,Height=400,ShowActivated=false,ShowInTaskbar=false,IsHitTestVisible=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Content=panel };
        var reducedBefore=UiMotion.ReducedForTest;
        try {
            void Flush() {
                host.UpdateLayout();var frame=new DispatcherFrame();
                host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
                Dispatcher.PushFrame(frame);host.UpdateLayout();
            }
            void Enter()=>button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseEnterEvent});
            host.Show();Flush();
            Check(host.IsVisible&&button.IsVisible&&button.IsLoaded&&expander.IsLoaded,"UI behavior probes use genuinely loaded visible controls in a non-activating host");
            Check(AutomationProperties.GetName(button)=="测试按钮"&&button.Content is StackPanel,"icon buttons keep explicit accessible text names");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(clicks==1,"styled button retains click behavior");
            Ui.Select(button,true);Check(ReferenceEquals(button.Background,Ui.Soft)&&ReferenceEquals(button.BorderBrush,Ui.Primary),"selected tool gets a persistent visual state");
            Ui.Select(button,false);Check(ReferenceEquals(button.Background,Ui.Surface),"switching tools clears the previous selection state");
            var focusStyle=(Style)app.Resources["KeyboardFocusVisual"];
            var focusProbe=new Control { Style=focusStyle };focusProbe.ApplyTemplate();
            Check(focusProbe.Template.LoadContent() is Border outline&&outline.Child==null&&outline.BorderBrush!=null&&outline.BorderThickness.Left>0&&!outline.IsHitTestVisible,"keyboard focus style contains one solid non-interactive outline instead of system dashes");
            bool NoFocusTrigger(Control control)=>control.Template.Triggers.OfType<Trigger>().All(trigger=>trigger.Property!=UIElement.IsKeyboardFocusedProperty&&trigger.Property!=UIElement.IsKeyboardFocusWithinProperty);
            Check(ReferenceEquals(button.FocusVisualStyle,focusStyle)&&button.Template.FindName("focusRing",button)==null&&NoFocusTrigger(button)&&button.Focusable&&KeyboardNavigation.GetIsTabStop(button),"buttons keep keyboard navigation and one keyboard-only focus style without a mouse-focus outer ring");
            Check(ReferenceEquals(checkbox.FocusVisualStyle,focusStyle)&&checkbox.Template.FindName("focus",checkbox)==null&&NoFocusTrigger(checkbox)&&checkbox.Focusable&&KeyboardNavigation.GetIsTabStop(checkbox),"switches keep keyboard navigation without a second focus ring");
            Check(input.FocusVisualStyle==null&&choice.FocusVisualStyle==null&&input.Template.Triggers.OfType<Trigger>().Any(trigger=>trigger.Property==UIElement.IsKeyboardFocusedProperty)&&choice.Template.Triggers.OfType<Trigger>().Any(trigger=>trigger.Property==UIElement.IsKeyboardFocusWithinProperty),"text and choice fields retain their own editing-focus border without duplicate system focus visuals");
            var header=Descendants<ToggleButton>(expander).Single(toggle=>ReferenceEquals(toggle.TemplatedParent,expander));
            bool hasExpanderStyle=app.Resources.Contains(typeof(Expander));
            var expanderStyleSource=DependencyPropertyHelper.GetValueSource(expander,FrameworkElement.StyleProperty);
            var headerLocalFocus=header.ReadLocalValue(Control.FocusVisualStyleProperty);
            Check(header.IsLoaded&&ReferenceEquals(header.FocusVisualStyle,focusStyle)&&header.Focusable&&KeyboardNavigation.GetIsTabStop(header),$"loaded Expander header uses the application focus style and stays focusable and tabbable (header.IsLoaded={header.IsLoaded}, sharedFocusStyle={ReferenceEquals(header.FocusVisualStyle,focusStyle)}, header.Focusable={header.Focusable}, header.IsTabStop={KeyboardNavigation.GetIsTabStop(header)}, expander.Focusable={expander.Focusable}, expander.IsTabStop={KeyboardNavigation.GetIsTabStop(expander)}, expander.LoadedCount={expanderLoadedCount}, implicitExpanderStyleExists={hasExpanderStyle}, sharedExpanderStyle={hasExpanderStyle&&ReferenceEquals(expander.Style,app.Resources[typeof(Expander)])}, header.LocalFocusStyle={headerLocalFocus}, localFocusIsAppStyle={ReferenceEquals(headerLocalFocus,focusStyle)}, expander.StyleSource={expanderStyleSource.BaseValueSource}, expander.StyleIsExpression={expanderStyleSource.IsExpression}, expander.LocalStyle={expander.ReadLocalValue(FrameworkElement.StyleProperty)})");
            var togglePeer=new ToggleButtonAutomationPeer(header);
            var toggleProvider=togglePeer.GetPattern(PatternInterface.Toggle) as IToggleProvider??throw new InvalidOperationException("Expander header lost its toggle pattern");
            toggleProvider.Toggle();Flush();Check(expander.IsExpanded&&header.IsChecked==true&&expandedInput.IsVisible,"Expander header toggle still reveals its bound shortcut content");
            toggleProvider.Toggle();Flush();Check(!expander.IsExpanded&&header.IsChecked==false&&!expandedInput.IsVisible,"Expander header toggle still collapses its bound shortcut content");
            var group=(TransformGroup)button.RenderTransform;var scale=(ScaleTransform)group.Children[0];var move=(TranslateTransform)group.Children[1];
            var wash=(Border)button.Template.FindName("hoverWash",button);
            bool Resting()=>!move.HasAnimatedProperties&&!scale.HasAnimatedProperties&&!wash.HasAnimatedProperties&&move.Y==0&&scale.ScaleX==1&&scale.ScaleY==1&&wash.Opacity==0;
            UiMotion.ReducedForTest=true;Enter();
            Check(!move.HasAnimatedProperties&&!scale.HasAnimatedProperties&&!wash.HasAnimatedProperties&&move.Y==0&&scale.ScaleX==1&&scale.ScaleY==1,"reduced motion removes hover displacement and clocks");
            busy.SetActive(true);Flush();var bar=(Border)busy.Child;
            Check(!((TranslateTransform)bar.RenderTransform).HasAnimatedProperties&&busy.IsVisible,"reduced motion retains a static visible busy indicator");
            UiMotion.ReducedForTest=false;Enter();
            Check(SystemParameters.ClientAreaAnimation?move.HasAnimatedProperties:move.Y==0,"hover motion starts when enabled and follows the Windows animation preference");
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseLeaveEvent});
            Check(SystemParameters.ClientAreaAnimation?move.HasAnimatedProperties:move.Y==0,"pointer leave schedules a return to the resting geometry");
            busy.SetActive(true);busy.SetActive(false);Check(!((TranslateTransform)bar.RenderTransform).HasAnimatedProperties&&busy.Visibility==Visibility.Collapsed,"finishing work removes the indeterminate animation clock");
            Enter();button.IsEnabled=false;Check(Resting(),"disabling a hovered button immediately clears movement and hover feedback");
            Enter();Check(Resting(),"late pointer events cannot restart feedback on a disabled button");button.IsEnabled=true;
            Enter();host.Hide();Check(!button.IsVisible&&Resting(),"hiding a hovered window resets its button without relying on Unloaded");
            Enter();button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.LostMouseCaptureEvent});
            Check(Resting(),"late enter, mouse-up and lost-capture events cannot restart feedback while hidden");
            host.Show();Flush();Check(button.IsVisible&&Resting(),"showing the window again leaves no stale hover wash, movement or animation clocks");
            Enter();panel.Children.Remove(button);Flush();Check(!button.IsLoaded&&Resting(),"actual control removal cleans up movement and hover feedback");
        } finally { busy.SetActive(false);host.Close();UiMotion.ReducedForTest=reducedBefore; }
        var dialog=new TranslationEditorDialog(null,new[]{new OcrBlock { Number=1,Original="Hello",Translation="你好" },new OcrBlock { Number=2,Original="world",Translation="世界" }});
        Check(dialog.CancelButton.IsCancel,"translation dialog exposes Escape cancellation");
        Check(dialog.Fields.Count==1&&AutomationProperties.GetName(dialog.Fields[0])=="完整译文"&&AutomationProperties.GetHelpText(dialog.Fields[0])=="Hello world"&&dialog.Translations[0]=="你好 世界","translation dialog uses one accessible field for the complete unnumbered text");
        dialog.Fields[0].Text="测试修改";Check(dialog.Translations[0]=="测试修改"&&KeyboardNavigation.GetTabNavigation(dialog)==KeyboardNavigationMode.Cycle,"translation dialog preserves edits and cycles keyboard focus");dialog.Close();
        return lines;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i);if(child is T typed)yield return typed;foreach(var nested in Descendants<T>(child))yield return nested; }
    }
    private static double Contrast(Brush a,Brush b)
    {
        double L(Brush brush) { var c=((SolidColorBrush)brush).Color;double S(byte v){double x=v/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}return .2126*S(c.R)+.7152*S(c.G)+.0722*S(c.B); }
        double x=L(a),y=L(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
    private static Brush Composite(Brush foreground,Brush background)
    {
        var a=((SolidColorBrush)foreground).Color;var b=((SolidColorBrush)background).Color;double alpha=a.A/255d;
        return new SolidColorBrush(Color.FromRgb((byte)Math.Round(a.R*alpha+b.R*(1-alpha)),(byte)Math.Round(a.G*alpha+b.G*(1-alpha)),(byte)Math.Round(a.B*alpha+b.B*(1-alpha))));
    }
}
