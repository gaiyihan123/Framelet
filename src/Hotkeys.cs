using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace QingCapture;

internal enum HotkeyAction { Region=1, Pin=2, Window=3, Fullscreen=4, Scroll=5, Translate=6 }
internal enum CaptureFollowUp { None, Scroll, Translate }
internal sealed record HotkeyDefinition(HotkeyAction Action,string Name,string Help,Func<Settings,Shortcut> Read,Action<Settings,Shortcut> Write);

internal static class HotkeyBindings
{
    public static readonly HotkeyDefinition[] All={
        new(HotkeyAction.Region,"截图","自动识别窗口，也可拖动选择区域",s=>s.Capture,(s,k)=>s.Capture=k),
        new(HotkeyAction.Pin,"剪贴板贴图","把剪贴板图片置顶",s=>s.Pin,(s,k)=>s.Pin=k),
        new(HotkeyAction.Window,"窗口截图","点击要截取的窗口",s=>s.WindowCapture,(s,k)=>s.WindowCapture=k),
        new(HotkeyAction.Fullscreen,"全屏截图","直接截取整个屏幕",s=>s.FullscreenCapture,(s,k)=>s.FullscreenCapture=k),
        new(HotkeyAction.Scroll,"拖动长截图","先定宽度，按住左键下拉，也可滚轮辅助",s=>s.ScrollCapture,(s,k)=>s.ScrollCapture=k),
        new(HotkeyAction.Translate,"截图后离线翻译","框选后自动识别并翻译",s=>s.TranslateCapture,(s,k)=>s.TranslateCapture=k)
    };
    public static string? Validate(Settings settings)
    {
        var used=new Dictionary<(uint,uint),string>();
        foreach(var binding in All) {
            var key=binding.Read(settings);
            if(key==null) return $"{binding.Name}的快捷键无效，请重新设置。";
            if(key.Key==0) continue;
            if(key.Key>254 || (key.Modifiers&~15u)!=0 || (key.Modifiers==0 && key.Key!=0x2C && (key.Key<0x70||key.Key>0x87)))
                return $"{binding.Name}请使用组合键、F1–F24 或 Print Screen。";
            if(!used.TryAdd((key.Modifiers,key.Key),binding.Name)) return $"{used[(key.Modifiers,key.Key)]}和{binding.Name}不能使用相同快捷键。";
        }
        return null;
    }
    public static string Label(uint modifiers,uint key)
    {
        if(key==0) return "未设置";
        string name=key==0x2C?"Print Screen":KeyInterop.KeyFromVirtualKey((int)key).ToString();
        return ((modifiers&2)!=0?"Ctrl + ":"")+((modifiers&1)!=0?"Alt + ":"")+((modifiers&4)!=0?"Shift + ":"")+((modifiers&8)!=0?"Win + ":"")+name;
    }
    public static void Execute(HotkeyAction action,Action<string,CaptureFollowUp> capture,Action pin)
    {
        switch(action) {
            case HotkeyAction.Region: capture("自动",CaptureFollowUp.None); break;
            case HotkeyAction.Window: capture("窗口",CaptureFollowUp.None); break;
            case HotkeyAction.Fullscreen: capture("全屏",CaptureFollowUp.None); break;
            case HotkeyAction.Scroll: capture("区域",CaptureFollowUp.Scroll); break;
            case HotkeyAction.Translate: capture("区域",CaptureFollowUp.Translate); break;
            case HotkeyAction.Pin: pin(); break;
        }
    }
}

// A failed update restores the previously registered bindings.
internal sealed class HotkeyRegistry : IDisposable
{
    private readonly IntPtr handle;
    private (HotkeyDefinition binding,Shortcut key)[] active=Array.Empty<(HotkeyDefinition,Shortcut)>();
    public HotkeyRegistry(IntPtr handle) { this.handle=handle; }
    private void Clear() { foreach(var entry in active) Native.UnregisterHotKey(handle,(int)entry.binding.Action); }
    public string? Apply(Settings settings)
    {
        var validation=HotkeyBindings.Validate(settings); if(validation!=null) return validation;
        var previous=active; Clear();
        var next=HotkeyBindings.All.Select(binding=>(binding,key:binding.Read(settings))).Where(entry=>entry.key.Key!=0).ToArray();
        var registered=new List<(HotkeyDefinition binding,Shortcut key)>();
        foreach(var entry in next) {
            if(!Native.RegisterHotKey(handle,(int)entry.binding.Action,entry.key.Modifiers|0x4000,entry.key.Key)) {
                foreach(var applied in registered) Native.UnregisterHotKey(handle,(int)applied.binding.Action);
                active=previous.Where(old=>Native.RegisterHotKey(handle,(int)old.binding.Action,old.key.Modifiers|0x4000,old.key.Key)).ToArray();
                return $"{entry.binding.Name}的 {HotkeyBindings.Label(entry.key.Modifiers,entry.key.Key)} 无法绑定，可能已被其他软件占用。请换一组按键。"+(active.Length!=previous.Length?" 部分原快捷键无法恢复，请重新保存设置。":"");
            }
            registered.Add(entry);
        }
        active=next; return null;
    }
    public void Dispose() { Clear(); active=Array.Empty<(HotkeyDefinition,Shortcut)>(); }
}
