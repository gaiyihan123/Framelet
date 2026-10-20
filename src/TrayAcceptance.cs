using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace QingCapture;

internal static class TrayAcceptance
{
    public static List<string> Run()
    {
        var lines=new List<string>();
        void Check(bool ok,string text) { if(!ok)throw new Exception("FAIL: "+text);lines.Add("PASS: "+text); }
        Check(AppBrand.Name=="Framelet"&&AppBrand.TrayTooltip(new Settings(),0)==$"Framelet {AppBrand.Version}\n截屏快捷键: 未设置\n贴图快捷键: 未设置\n贴图: 0","Framelet tray tooltip uses four reference-style lines with current version and unassigned shortcuts");
        var configured=new Settings { Capture=new Shortcut(3,65,"stale label"),Pin=new Shortcut(4,0x2C,"wrong label") };
        string text=AppBrand.TrayTooltip(configured,2);
        Check(text.Contains("截屏快捷键: Ctrl + Alt + A")&&text.Contains("贴图快捷键: Shift + Print Screen")&&text.EndsWith("贴图: 2")&&!text.Contains("stale"),"tray shortcuts are rebuilt from actual keys and ignore obsolete stored labels");
        var longest=new Settings { Capture=new Shortcut(15,247),Pin=new Shortcut(15,248) };
        string bounded=AppBrand.TrayTooltip(longest,int.MaxValue);
        using(var probe=new Forms.NotifyIcon { Text=bounded })
            Check(bounded.Length<=127&&bounded.Split('\n').Length==4&&bounded.EndsWith("贴图: 2147483647")&&probe.Text==bounded,"longest valid shortcut names fit the native tray limit without losing any information line");
        var app=App.Instance;var capture=app.Settings.Capture;var pin=app.Settings.Pin;
        PinWindow? window=null;
        void Flush() {
            var frame=new DispatcherFrame();app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
        }
        try {
            app.Settings.Capture=configured.Capture;app.Settings.Pin=configured.Pin;app.RefreshTrayTooltip();
            Check(app.TrayTooltipText==AppBrand.TrayTooltip(app.Settings,0),"refresh writes updated current shortcuts into the actual NotifyIcon tooltip");
            using var fixture=new System.Drawing.Bitmap(16,16);window=new PinWindow(fixture);window.Show();Flush();
            Check(app.TrayTooltipText?.EndsWith("贴图: 1")==true,"opening a visible pin updates the tray count");
            window.Hide();Flush();Check(app.TrayTooltipText?.EndsWith("贴图: 0")==true,"hiding a pin updates the tray count");
            window.Show();Flush();Check(app.TrayTooltipText?.EndsWith("贴图: 1")==true,"showing the pin again restores the tray count");
            window.Close();window=null;Flush();Check(app.TrayTooltipText?.EndsWith("贴图: 0")==true,"closing the pin removes it from the tray count");
        } finally { window?.Close();app.Settings.Capture=capture;app.Settings.Pin=pin;app.RefreshTrayTooltip(); }
        return lines;
    }
}
