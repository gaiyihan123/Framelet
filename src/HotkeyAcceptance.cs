using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace QingCapture;

internal static class HotkeyAcceptance
{
    public static List<string> Run()
    {
        var lines=new List<string>();
        void Check(bool ok,string name) { if(!ok) throw new Exception("FAIL: "+name);lines.Add("PASS: "+name); }
        var legacy=JsonSerializer.Deserialize<Settings>("""{"Capture":{"Modifiers":3,"Key":65,"Label":"Ctrl + Alt + A"},"Pin":{"Modifiers":3,"Key":66,"Label":"Ctrl + Alt + B"},"Startup":true,"Configured":true,"ReduceMotion":true}""")!;
        Check(legacy.Capture.Key==65&&legacy.Pin.Key==66&&HotkeyBindings.All.Skip(2).All(b=>b.Read(legacy).Key==0)&&legacy.Startup&&legacy.ReduceMotion,"legacy settings keep existing bindings and leave additional modes unassigned");
        var settings=new Settings();
        for(int i=0;i<HotkeyBindings.All.Length;i++) HotkeyBindings.All[i].Write(settings,new Shortcut(7,(uint)(0x7C+i),HotkeyBindings.Label(7,(uint)(0x7C+i))));
        var restored=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Check(HotkeyBindings.All.Length==6&&HotkeyBindings.All.All(b=>b.Read(restored)==b.Read(settings)),"all six mode bindings survive settings serialization");
        var legacyModes=JsonNode.Parse(JsonSerializer.Serialize(settings))!.AsObject();
        bool removedHomeIgnored=true;
        foreach(var removed in new[]{new Shortcut(7,0x82,"Ctrl + Alt + Shift + F19"),settings.Capture,new Shortcut(16,65,"invalid")}) {
            legacyModes["ShowHome"]=JsonSerializer.SerializeToNode(removed);
            var migrated=JsonSerializer.Deserialize<Settings>(legacyModes.ToJsonString())!;
            removedHomeIgnored&=HotkeyBindings.Validate(migrated)==null&&HotkeyBindings.All.All(b=>b.Read(migrated)==b.Read(settings))&&!JsonSerializer.Serialize(migrated).Contains("\"ShowHome\"",StringComparison.Ordinal);
        }
        Check(removedHomeIgnored,"valid, duplicate and invalid obsolete home bindings are ignored while all six remaining bindings survive migration");
        restored.TranslateCapture=restored.Capture;
        Check(HotkeyBindings.Validate(restored)?.Contains("截图后离线翻译")==true,"duplicates across remaining modes are rejected by action name");
        restored.TranslateCapture=new Shortcut(0,65,"A");Check(HotkeyBindings.Validate(restored)!=null,"unmodified letter bindings are rejected without hijacking typing");
        restored.TranslateCapture=new Shortcut(0,0x2C,"Print Screen");Check(HotkeyBindings.Validate(restored)==null,"Print Screen and function keys can be bound without modifiers");
        var dispatch=new List<string>();
        foreach(var binding in HotkeyBindings.All) HotkeyBindings.Execute(binding.Action,(mode,next)=>dispatch.Add(mode+":"+next),()=>dispatch.Add("pin"));
        HotkeyBindings.Execute((HotkeyAction)7,(mode,next)=>dispatch.Add("removed-home"),()=>dispatch.Add("removed-home"));
        Check(dispatch.SequenceEqual(new[]{"自动:None","pin","窗口:None","全屏:None","区域:Scroll","区域:Translate"}),"six hotkeys dispatch unified capture, legacy direct modes and follow-ups; removed home action remains inactive");
        using var host=new HwndSource(new HwndSourceParameters("QingCapture.HotkeyAcceptance") { Width=0,Height=0 });
        using var registry=new HotkeyRegistry(host.Handle);
        Check(registry.Apply(settings)==null,"Windows registers all six global bindings together");
        using var contender=new HwndSource(new HwndSourceParameters("QingCapture.HotkeyContender") { Width=0,Height=0 });
        Check(HotkeyBindings.All.All(b=>!Native.RegisterHotKey(contender.Handle,99,b.Read(settings).Modifiers|0x4000,b.Read(settings).Key)),"each configured global binding is reserved by the application");
        const uint occupied=0x87;
        if(!Native.RegisterHotKey(contender.Handle,100,0x4007,occupied)) throw new Exception("Hotkey test requires available Ctrl+Alt+Shift+F24");
        try {
            var conflict=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;conflict.WindowCapture=new Shortcut(7,occupied,"Ctrl + Alt + Shift + F24");
            Check(registry.Apply(conflict)?.Contains("窗口截图")==true,"external hotkey conflict names the affected mode");
            Check(HotkeyBindings.All.All(b=>!Native.RegisterHotKey(contender.Handle,99,b.Read(settings).Modifiers|0x4000,b.Read(settings).Key)),"failed binding update restores all previously working shortcuts");
            Check(registry.Apply(new Settings())==null&&Native.RegisterHotKey(contender.Handle,99,settings.Capture.Modifiers|0x4000,settings.Capture.Key),"clearing a binding releases its Windows registration");
        } finally { Native.UnregisterHotKey(contender.Handle,99);Native.UnregisterHotKey(contender.Handle,100); }
        var app=Application.Current as App??new App();Ui.Resources(app);
        var field=new ShortcutBox(new Shortcut(3,65,"Ctrl + Alt + A"));
        void Press(Key key) { field.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,host,0,key) { RoutedEvent=Keyboard.PreviewKeyDownEvent }); }
        Press(Key.A);Check(field.Value.Key==65&&field.Value.Modifiers==3,"invalid key entry preserves the previous shortcut");
        Press(Key.F13);Check(field.Value.Key==0x7C&&field.Value.Modifiers==0,"shortcut input records function keys directly");
        Press(Key.Escape);Check(field.Value.Key==0x7C,"Escape leaves the stored shortcut intact");
        Press(Key.Back);Check(field.Value.Key==0&&field.Text=="未设置","Backspace clears a shortcut binding");
        field.SetShortcut(3,65);Check(field.Value.Modifiers==3&&field.Text=="Ctrl + Alt + A","an already registered hotkey can be recorded while its field has focus");
        string unchanged=JsonSerializer.Serialize(app.Settings);
        var dialog=new SettingsWindow(null);
        Check(dialog.ShortcutFields.Count==6&&JsonSerializer.Serialize(app.Settings)==unchanged,"settings exposes six shortcut fields directly without changing stored preferences");
        foreach(var binding in HotkeyBindings.All) dialog.ShortcutFields[binding.Action].SetShortcut(binding.Read(settings).Modifiers,binding.Read(settings).Key);
        var edited=dialog.ReadSettings();
        bool closed=false;dialog.Closed+=(_,_)=>closed=true;
        dialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(HotkeyBindings.All.All(b=>b.Read(edited)==b.Read(settings))&&closed&&JsonSerializer.Serialize(app.Settings)==unchanged,"settings reads all six edited bindings; cancelling discards edits and closes the window");
        return lines;
    }
}
