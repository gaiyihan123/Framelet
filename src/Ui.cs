using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace QingCapture;

internal static class Ui
{
    public static SolidColorBrush Brush(string hex) { var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static readonly Brush Primary=Brush("#5266CE"), Accent=Brush("#B96F4B"), Ink=Brush("#293248"), Muted=Brush("#626B7D"), OnWarm=Brush("#965134");
    public static readonly Brush Canvas=Brush("#F3F4F8"), Surface=Brush("#FCFBF8"), Field=Brush("#F5F6FA"), Border=Brush("#DCE0EC");
    public static readonly Brush Soft=Brush("#E9EDFC"), Warm=Brush("#F4E6DA"), Rail=Brush("#293248"), OnPrimary=Brush("#FAFBFF");
    public static readonly Brush Success=Brush("#5266CE"), Danger=Brush("#AA4757");
    public static readonly Brush HomeCanvas=CreateHomeCanvas();
    public static readonly Brush HomeGlass=Brush("#F0FCFBF8"), HomeGlassBorder=Brush("#B8FFFFFF");
    // Compatibility aliases for the capture and annotation surfaces.
    public static Brush Plum=>Primary;
    public static Brush Gold=>Accent;
    public static Brush OnPlum=>OnPrimary;

    private static Brush CreateHomeCanvas()
    {
        var brush=new LinearGradientBrush(new GradientStopCollection {
            new(Color.FromRgb(237,241,249),0),new(Color.FromRgb(241,241,247),.54),new(Color.FromRgb(244,236,230),1)
        },new Point(0,0),new Point(1,1)); brush.Freeze(); return brush;
    }
    public static void Resources(Application app)
    {
        app.Resources=(ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="clr-namespace:QingCapture;assembly=QingCapture">
          <Style x:Key="KeyboardFocusVisual" TargetType="Control">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Control">
              <Border x:Name="outline" BorderBrush="#5266CE" BorderThickness="1.5" CornerRadius="10" Margin="-2" Visibility="Collapsed" IsHitTestVisible="False"/>
              <ControlTemplate.Triggers><DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType={x:Type Adorner}}, Path=AdornedElement.(local:KeyboardFocusCue.Navigation)}" Value="True"><Setter TargetName="outline" Property="Visibility" Value="Visible"/></DataTrigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="{x:Static SystemParameters.FocusVisualStyleKey}" TargetType="Control" BasedOn="{StaticResource KeyboardFocusVisual}"/>
          <Style TargetType="Window"><Setter Property="FontFamily" Value="Microsoft YaHei UI"/><Setter Property="FontSize" Value="13"/><Setter Property="Background" Value="#F3F4F8"/><Setter Property="Foreground" Value="#293248"/><Setter Property="TextOptions.TextFormattingMode" Value="Display"/><Setter Property="UseLayoutRounding" Value="True"/></Style>
          <Style TargetType="Button">
            <Setter Property="FocusVisualStyle" Value="{StaticResource KeyboardFocusVisual}"/>
            <Setter Property="Padding" Value="14,9"/><Setter Property="Margin" Value="3"/><Setter Property="Background" Value="#FCFBF8"/><Setter Property="Foreground" Value="#293248"/><Setter Property="BorderBrush" Value="#DCE0EC"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Cursor" Value="Hand"/><Setter Property="MinHeight" Value="38"/><Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Grid x:Name="frame">
                <Border x:Name="plate" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="10"/>
                <Border x:Name="hoverWash" Background="#16FFFFFF" CornerRadius="10" Opacity="0" IsHitTestVisible="False"/>
                <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" RecognizesAccessKey="True"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="plate" Property="BorderBrush" Value="#8899E4"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="frame" Property="Opacity" Value="0.42"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="Expander">
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Expander">
              <StackPanel>
                <ToggleButton x:Name="HeaderSite" Content="{TemplateBinding Header}" ContentTemplate="{TemplateBinding HeaderTemplate}" Foreground="{TemplateBinding Foreground}" FocusVisualStyle="{StaticResource KeyboardFocusVisual}" IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" HorizontalContentAlignment="Left" Background="Transparent" BorderThickness="0" Padding="0,2">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                    <DockPanel Background="Transparent">
                      <Grid Width="20" Height="20" Margin="0,0,4,0">
                        <Ellipse Stroke="#697184" StrokeThickness="1" Margin="1"/>
                        <Path x:Name="chevron" Data="M 6,8 L 10,12 14,8" Stroke="#697184" StrokeThickness="1.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" RenderTransformOrigin=".5,.5"/>
                      </Grid>
                      <ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </DockPanel>
                    <ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="chevron" Property="RenderTransform"><Setter.Value><RotateTransform Angle="180"/></Setter.Value></Setter></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".45"/></Trigger></ControlTemplate.Triggers>
                  </ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter x:Name="ExpandSite" Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" Visibility="Collapsed"/>
              </StackPanel>
              <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="ExpandSite" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="HomeChromeKeyboardFocus" TargetType="Control">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Control"><Border x:Name="outline" BorderBrush="#5266CE" BorderThickness="0,0,0,1" Margin="10,5" Visibility="Collapsed"/><ControlTemplate.Triggers><DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType={x:Type Adorner}}, Path=AdornedElement.(local:KeyboardFocusCue.Navigation)}" Value="True"><Setter TargetName="outline" Property="Visibility" Value="Visible"/></DataTrigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="HomeChromeButton" TargetType="Button">
            <Setter Property="Width" Value="44"/><Setter Property="Height" Value="34"/><Setter Property="Margin" Value="0"/><Setter Property="Padding" Value="0"/><Setter Property="Background" Value="Transparent"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Foreground" Value="#48516A"/><Setter Property="Cursor" Value="Arrow"/>
            <Setter Property="FocusVisualStyle" Value="{StaticResource HomeChromeKeyboardFocus}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Grid><Border x:Name="plate" Background="{TemplateBinding Background}" CornerRadius="8"/><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Grid><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="plate" Property="Background" Value="#DDE1EF"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="plate" Property="Background" Value="#C9D0E8"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Padding" Value="12,9"/><Setter Property="Background" Value="#F5F6FA"/><Setter Property="Foreground" Value="#293248"/><Setter Property="BorderBrush" Value="#DCE0EC"/><Setter Property="BorderThickness" Value="1"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="SelectionBrush" Value="#5266CE"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox"><Border x:Name="plate" CornerRadius="9" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"><ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/></Border><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="plate" Property="BorderBrush" Value="#5266CE"/><Setter TargetName="plate" Property="BorderThickness" Value="2"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="CheckBox">
            <Setter Property="FocusVisualStyle" Value="{StaticResource KeyboardFocusVisual}"/>
            <Setter Property="Foreground" Value="#626B7D"/><Setter Property="Cursor" Value="Hand"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="Margin" Value="0,5,16,5"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><StackPanel Orientation="Horizontal"><Grid Width="34" Height="20" VerticalAlignment="Center"><Border x:Name="track" CornerRadius="10" Background="#CDD3E4"/><Border x:Name="thumb" Width="14" Height="14" CornerRadius="7" Background="#FCFBF8" HorizontalAlignment="Left" Margin="3,0"/></Grid><ContentPresenter Margin="9,0,0,0" VerticalAlignment="Center"/></StackPanel><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="track" Property="Background" Value="#5266CE"/><Setter TargetName="thumb" Property="HorizontalAlignment" Value="Right"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBoxItem"><Setter Property="Padding" Value="10,7"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="item" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="item" Property="Background" Value="#E9EDFC"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="#5266CE"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ComboBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Background" Value="#F5F6FA"/><Setter Property="Foreground" Value="#293248"/><Setter Property="BorderBrush" Value="#DCE0EC"/><Setter Property="MinHeight" Value="38"/><Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
              <Border x:Name="plate" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="9"/>
              <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" Margin="10,5,23,5" VerticalAlignment="Center" IsHitTestVisible="False"/>
              <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" ClickMode="Press" Background="Transparent"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"><Path Data="M 0,0 L 4,4 8,0" Stroke="#697184" StrokeThickness="1.4" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,9,0"/></Border></ControlTemplate></ToggleButton.Template></ToggleButton>
              <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False"><Border Background="#FCFBF8" BorderBrush="#DCE0EC" BorderThickness="1" CornerRadius="9" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}"><ScrollViewer CanContentScroll="True"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border></Popup>
            </Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="plate" Property="BorderBrush" Value="#5266CE"/><Setter TargetName="plate" Property="BorderThickness" Value="2"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ToolTip"><Setter Property="Background" Value="#293248"/><Setter Property="Foreground" Value="#FAFBFF"/><Setter Property="Padding" Value="10,7"/><Setter Property="BorderThickness" Value="0"/></Style>
        </ResourceDictionary>
        """);
        KeyboardFocusCue.Initialize();
    }

    public static Button Button(string label,Action action,bool primary=false,string? icon=null)
    {
        var b=new Button { Content=icon==null?label:ButtonContent(label,icon) };
        AutomationProperties.SetName(b,label);
        if(primary) { b.Background=Primary; b.Foreground=OnPrimary; b.BorderBrush=Primary; }
        b.Click+=(_,_)=>action(); UiMotion.Attach(b); return b;
    }
    public static StackPanel ButtonContent(string label,string icon)
    {
        var p=new StackPanel { Orientation=Orientation.Horizontal }; p.Children.Add(Icon(icon,16));
        p.Children.Add(new TextBlock { Text=label,Margin=new Thickness(7,0,0,0),VerticalAlignment=VerticalAlignment.Center }); return p;
    }
    public static void Select(Button button,bool selected)
    {
        button.Background=selected?Soft:Surface; button.Foreground=selected?Primary:Ink; button.BorderBrush=selected?Primary:Border;
        AutomationProperties.SetHelpText(button,selected?"当前选中工具":"点击切换工具");
    }
    public static TextBlock Text(string text,double size=13,Brush? brush=null)=>new() { Text=text,FontSize=size,Foreground=brush??Ink,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,4) };
    public static Border Panel(UIElement child,Thickness? padding=null)=>new() { Background=Surface,BorderBrush=Border,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(16),Padding=padding??new Thickness(20),Child=child };
    public static void Setup(Window w,string title,double width,double height)
    {
        AppIdentity.Configure(w);
        w.Title=title; w.Width=width; w.Height=height; w.Background=Canvas; w.Foreground=Ink; w.WindowStartupLocation=WindowStartupLocation.CenterScreen;
        w.Icon=BrandFrame(32);
    }
    private static readonly Lazy<BitmapFrame[]> brandFrames=new(()=> {
        using var file=File.OpenRead(System.IO.Path.Combine(AppContext.BaseDirectory,"icon.ico"));
        var frames=BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames.OrderBy(frame=>frame.PixelWidth).ToArray();
        foreach(var frame in frames)frame.Freeze();
        return frames;
    });
    internal static BitmapFrame BrandFrame(double physicalPixels)=>brandFrames.Value.FirstOrDefault(frame=>frame.PixelWidth>=Math.Ceiling(physicalPixels))??brandFrames.Value[^1];
    internal const string BrandMarkData="M7.5 25.5V8Q7.5 6.5 9 6.5H25.5V9.5H10.5V14.5H20.5V17.5H10.5V25.5Z M22.5 16.5H25.5V24Q25.5 25.5 24 25.5H16.5V22.5H22.5Z";
    public static System.Windows.Shapes.Path BrandMark(double size=20)=>new() {
        Data=Geometry.Parse(BrandMarkData),Width=size,Height=size,Stretch=Stretch.Uniform,Fill=Brush("#626FA6"),
        IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center,SnapsToDevicePixels=true
    };
    public static Image BrandIcon(Window owner,double size=19)
    {
        var image=new Image { Width=size,Height=size,Stretch=Stretch.Uniform,IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        void Refresh()=>image.Source=BrandFrame(size*VisualTreeHelper.GetDpi(owner).DpiScaleX);
        Refresh();owner.Loaded+=(_,_)=>Refresh();owner.DpiChanged+=(_,_)=>Refresh();
        return image;
    }
    private static readonly Dictionary<string,string> Icons=new() {
        ["crop"]="M 8,3 L 3,3 3,8 M 16,3 L 21,3 21,8 M 21,16 L 21,21 16,21 M 8,21 L 3,21 3,16 M 8,8 L 16,8 16,16 8,16 Z",
        ["window"]="M 3,4 L 21,4 21,20 3,20 Z M 3,9 L 21,9 M 6,6.5 L 7,6.5",
        ["screen"]="M 3,4 L 21,4 21,17 3,17 Z M 12,17 L 12,21 M 7,21 L 17,21",
        ["pin"]="M 9,3 L 18,3 17,10 20,13 12,13 9,10 Z M 12,13 L 5,21",
        ["copy"]="M 8,8 L 21,8 21,21 8,21 Z M 16,8 L 16,3 3,3 3,16 8,16",
        ["save"]="M 4,3 L 17,3 21,7 21,21 3,21 3,3 Z M 7,3 L 7,9 16,9 16,3 M 7,21 L 7,14 17,14 17,21",
        ["arrow"]="M 4,20 L 20,4 M 9,4 L 20,4 20,15",
        ["box"]="M 3,4 L 21,4 21,20 3,20 Z",
        ["text"]="M 4,4 L 20,4 M 12,4 L 12,21 M 8,21 L 16,21",
        ["mosaic"]="M 3,3 L 10,3 10,10 3,10 Z M 14,3 L 21,3 21,10 14,10 Z M 3,14 L 10,14 10,21 3,21 Z M 14,14 L 21,14 21,21 14,21 Z",
        ["view"]="M 4,3 L 4,20 9,15 13,22 17,20 13,13 20,12 Z",
        ["undo"]="M 8,4 L 3,9 8,14 M 3,9 L 14,9 C 23,9 23,21 14,21",
        ["redo"]="M 16,4 L 21,9 16,14 M 21,9 L 10,9 C 1,9 1,21 10,21",
        ["translate"]="M 2,5 L 14,5 M 8,2 L 8,5 M 5,5 C 5,11 9,14 14,16 M 12,5 C 12,12 8,15 3,17 M 13,22 L 18,10 23,22 M 15,18 L 21,18",
        ["scroll"]="M 5,3 L 19,3 19,21 5,21 Z M 12,7 L 12,17 M 8,13 L 12,17 16,13",
        ["shield"]="M 12,2 L 21,6 20,15 Q 17,20 12,22 Q 7,20 4,15 L 3,6 Z M 7,12 L 11,16 17,9",
        ["menu"]="M 4,6 L 20,6 M 4,12 L 20,12 M 4,18 L 20,18",
        ["settings"]="M 12,3 L 14,3 15,6 18,7 21,6 22,9 20,11 20,14 22,16 20,19 17,18 14,20 14,22 10,22 10,20 7,18 4,19 2,16 4,14 4,11 2,9 3,6 6,7 9,6 10,3 Z M 16,12 A 4,4 0 1 1 8,12 A 4,4 0 1 1 16,12",
        ["spark"]="M 12,2 L 15,9 22,12 15,15 12,22 9,15 2,12 9,9 Z"
    };
    public static System.Windows.Shapes.Path Icon(string name,double size=22,Brush? brush=null)
    {
        var p=new System.Windows.Shapes.Path { Data=Geometry.Parse(Icons.TryGetValue(name,out var value)?value:Icons["crop"]),Width=size,Height=size,Stretch=Stretch.Uniform,StrokeThickness=1.6,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center };
        if(brush!=null)p.Stroke=brush; else p.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground") { RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(Button),1) });
        return p;
    }
    public static string? Prompt(Window owner,string title,string initial="")
    {
        var w=new Window { Owner=owner,ResizeMode=ResizeMode.CanResize,MinWidth=360,MinHeight=220 }; Setup(w,title,500,280); w.WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new DockPanel { Margin=new Thickness(22) }; var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0) };
        var input=new TextBox { Text=initial,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        AutomationProperties.SetName(input,title);
        buttons.Children.Add(Button("取消",()=>w.DialogResult=false)); var ok=Button("确定",()=>w.DialogResult=true,true); ok.IsDefault=true; buttons.Children.Add(ok); DockPanel.SetDock(buttons,Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(input); w.Content=panel;
        w.Loaded+=(_,_)=> { input.Focus(); input.SelectAll(); }; w.PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) w.DialogResult=false; };
        return w.ShowDialog()==true?input.Text:null;
    }
}

public static class KeyboardFocusCue
{
    public static readonly DependencyProperty NavigationProperty=DependencyProperty.RegisterAttached("Navigation",typeof(bool),typeof(KeyboardFocusCue),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetNavigation(DependencyObject target)=>(bool)target.GetValue(NavigationProperty);
    public static void SetNavigation(DependencyObject target,bool value)=>target.SetValue(NavigationProperty,value);
    private static bool initialized;
    internal static void Initialize()
    {
        if(initialized)return;initialized=true;
        EventManager.RegisterClassHandler(typeof(Window),Keyboard.PreviewKeyDownEvent,new KeyEventHandler((sender,e)=> {
            if(e.Key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
                SetNavigation((Window)sender,true);
        }),true);
        EventManager.RegisterClassHandler(typeof(Window),Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler((sender,_)=>SetNavigation((Window)sender,false)),true);
        EventManager.RegisterClassHandler(typeof(Window),Stylus.PreviewStylusDownEvent,new StylusDownEventHandler((sender,_)=>SetNavigation((Window)sender,false)),true);
        EventManager.RegisterClassHandler(typeof(Window),UIElement.PreviewTouchDownEvent,new EventHandler<TouchEventArgs>((sender,_)=> { if(sender is Window window)SetNavigation(window,false); }),true);
    }
}

internal static class UiMotion
{
    internal static bool? ReducedForTest;
    public static bool Enabled=>!(ReducedForTest??((Application.Current as App)?.Settings.ReduceMotion??false))&&SystemParameters.ClientAreaAnimation;
    private static void To(Animatable target,DependencyProperty property,double value,int ms)
    {
        if(!Enabled) { target.BeginAnimation(property,null); target.SetValue(property,value); return; }
        target.BeginAnimation(property,new DoubleAnimation(value,TimeSpan.FromMilliseconds(ms)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut } },HandoffBehavior.SnapshotAndReplace);
    }
    public static void Attach(Button button)
    {
        var scale=new ScaleTransform(1,1); var move=new TranslateTransform(); var group=new TransformGroup(); group.Children.Add(scale); group.Children.Add(move);
        button.RenderTransform=group; button.RenderTransformOrigin=new Point(.5,.5);
        void Reset() {
            move.BeginAnimation(TranslateTransform.YProperty,null);scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);
            move.Y=0;scale.ScaleX=scale.ScaleY=1;
            if(button.Template?.FindName("hoverWash",button) is Border wash){wash.BeginAnimation(UIElement.OpacityProperty,null);wash.Opacity=0;}
        }
        void Update(bool hovered,bool pressed) {
            if(!button.IsVisible||!button.IsEnabled){Reset();return;}
            To(move,TranslateTransform.YProperty,Enabled&&hovered&&!pressed?-1.5:0,150);
            To(scale,ScaleTransform.ScaleXProperty,Enabled&&pressed?.975:1,90); To(scale,ScaleTransform.ScaleYProperty,Enabled&&pressed?.975:1,90);
            if(button.Template?.FindName("hoverWash",button) is Border wash) {
                wash.BeginAnimation(UIElement.OpacityProperty,Enabled?new DoubleAnimation(hovered?1:0,TimeSpan.FromMilliseconds(150)):null); wash.Opacity=hovered?1:0;
            }
        }
        button.MouseEnter+=(_,_)=>Update(true,button.IsPressed); button.MouseLeave+=(_,_)=>Update(false,false);
        button.PreviewMouseLeftButtonDown+=(_,_)=>Update(true,true); button.PreviewMouseLeftButtonUp+=(_,_)=>Update(button.IsMouseOver,false);
        button.LostMouseCapture+=(_,_)=>Update(button.IsMouseOver,false);
        button.IsEnabledChanged+=(_,_)=> { if(!button.IsEnabled)Reset(); };
        button.IsVisibleChanged+=(_,_)=>Reset();
        button.Unloaded+=(_,_)=>Reset();
    }
    public static void Reveal(FrameworkElement element)
    {
        if(!Enabled) { element.BeginAnimation(UIElement.OpacityProperty,null); element.Opacity=1; return; }
        element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(.65,1,TimeSpan.FromMilliseconds(220)) { FillBehavior=FillBehavior.Stop });
    }
}

internal sealed class ActivityBar : Border
{
    private readonly Border segment=new() { Background=Ui.Primary,Width=100,HorizontalAlignment=HorizontalAlignment.Left,CornerRadius=new CornerRadius(2) };
    private readonly TranslateTransform move=new();
    public ActivityBar() { Height=3; Background=Ui.Soft; ClipToBounds=true; Visibility=Visibility.Collapsed; segment.RenderTransform=move; Child=segment; Unloaded+=(_,_)=>move.BeginAnimation(TranslateTransform.XProperty,null); }
    public void SetActive(bool active)
    {
        Visibility=active?Visibility.Visible:Visibility.Collapsed; move.BeginAnimation(TranslateTransform.XProperty,null); move.X=0;
        segment.Width=UiMotion.Enabled?100:Math.Max(100,ActualWidth);
        if(active&&UiMotion.Enabled)move.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(-100,Math.Max(200,ActualWidth),TimeSpan.FromMilliseconds(1300)) { RepeatBehavior=RepeatBehavior.Forever });
    }
}
