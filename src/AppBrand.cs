using System;

namespace QingCapture;

internal static class AppBrand
{
    public const string Name="Framelet";
    internal const string ShellIconFile="framelet-f-62545da8.ico";
    public static string Version=>typeof(AppBrand).Assembly.GetName().Version?.ToString(3)??"1.3.0";
    public static string Title(string section)=>Name+" · "+section;
    public static string TrayTooltip(Settings settings,int pinCount)
    {
        string Key(Shortcut? shortcut)=>shortcut==null?"未设置":HotkeyBindings.Label(shortcut.Modifiers,shortcut.Key);
        string capture=Key(settings.Capture),pin=Key(settings.Pin);
        string Compose(string a,string b)=>$"{Name} {Version}\n截屏快捷键: {a}\n贴图快捷键: {b}\n贴图: {Math.Max(0,pinCount)}";
        string result=Compose(capture,pin);
        if(result.Length<=127)return result;
        capture=capture.Replace(" + ","+");pin=pin.Replace(" + ","+");result=Compose(capture,pin);
        if(result.Length<=127)return result;
        int budget=127-Compose("","").Length;
        string Shorten(string value,int length)=>value.Length<=length?value:value[..(length-1)]+"…";
        return Compose(Shorten(capture,budget/2),Shorten(pin,budget-budget/2));
    }
}
