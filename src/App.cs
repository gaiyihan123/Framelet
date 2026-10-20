using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace QingCapture;

internal record Shortcut(uint Modifiers=0,uint Key=0,string Label="未设置");
internal sealed class Settings
{
    public Shortcut Capture { get; set; }=new();
    public Shortcut Pin { get; set; }=new();
    public Shortcut WindowCapture { get; set; }=new();
    public Shortcut FullscreenCapture { get; set; }=new();
    public Shortcut ScrollCapture { get; set; }=new();
    public Shortcut TranslateCapture { get; set; }=new();
    public bool Startup { get; set; }
    public bool Configured { get; set; }
    public bool ReduceMotion { get; set; }
}

internal sealed class App : Application
{
    public static App Instance => (App)Current;
    public static string DataPath { get; private set; }=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QingCapture");
    public Settings Settings { get; private set; }=new();
    public readonly TranslationClient Translator=new();
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private HwndSource? hotkeys;
    private HotkeyRegistry? hotkeyRegistry;
    private HomeWindow? home;
    private bool capturing;
    private int captureGeneration;
    private Mutex? mutex;
    public bool Exiting { get; private set; }
    private static bool acceptanceMode;
    private ClipboardImages.ClipboardSnapshotLease? testClipboard;
    private string? testClipboardReport;
    [STAThread] public static void Main(string[] args)
    {
        AppIdentity.Initialize();
        acceptanceMode=args.Contains("--ui-self-test")||args.Contains("--self-test")||args.Contains("--display-test");
        int di=Array.IndexOf(args,"--data-dir"); if(di>=0 && di+1<args.Length) DataPath=Path.GetFullPath(args[di+1]);
        if(args.Contains("--translation-regression-test")) { Environment.ExitCode=SelfTests.RunTranslationRegression(args); return; }
        if(args.Contains("--translation-memory-test")) { Environment.ExitCode=TranslationMemoryAcceptance.Run(args); return; }
        if(args.Contains("--ui-self-test")) { Environment.ExitCode=SelfTests.RunUi(args); return; }
        if(args.Contains("--clipboard-roundtrip-test")) { Environment.ExitCode=SelfTests.RunClipboard(args); return; }
        if(args.Contains("--clipboard-lifetime-test")) { Environment.ExitCode=SelfTests.RunClipboardLifetime(args); return; }
        if(args.Contains("--clipboard-lifetime-writer")) {
            try {ClipboardAcceptance.WriteLifetimeFixtureAsync().GetAwaiter().GetResult();Environment.ExitCode=0;}
            catch(Exception ex) {Console.Error.WriteLine(ex);Environment.ExitCode=1;}
            return;
        }
        if(args.Contains("--self-test")) { Environment.ExitCode=SelfTests.Run(args); return; }
        if(args.Contains("--quality-audit")) { Environment.ExitCode=OfflineQualityAudit.Run(args); return; }
        if(args.Contains("--display-test")) { Environment.ExitCode=LiveDisplayAcceptance.Run(args); return; }
        var app=new App(); app.Run();
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode=ShutdownMode.OnExplicitShutdown;
        // Component hosts pump the WPF dispatcher but must never start a second
        // tray instance, load/write user settings or display a mutex warning.
        if(acceptanceMode) {tray=new Forms.NotifyIcon {Text=AppBrand.TrayTooltip(Settings,0),Visible=false};return;}
        Ui.Resources(this);
        Directory.CreateDirectory(DataPath);
        mutex=new Mutex(true,"Local\\QingCapture-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DataPath))).Substring(0,16),out bool first);
        if(!first) { MessageBox.Show(AppBrand.Name+" 已在运行，请从系统托盘打开。",AppBrand.Name); Shutdown(); return; }
        DispatcherUnhandledException+=(_,ex)=> { ex.Handled=true; MessageBox.Show("操作未完成："+ex.Exception.Message,AppBrand.Name); };
        try { if(File.Exists(Path.Combine(DataPath,"settings.json"))) Settings=JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(DataPath,"settings.json")))??new(); }
        catch { Settings=new(); }
        hotkeys=new HwndSource(new HwndSourceParameters("QingCapture.Hotkeys") { Width=0,Height=0,WindowStyle=0 }); hotkeys.AddHook(Hook);
        hotkeyRegistry=new HotkeyRegistry(hotkeys.Handle);
        var menu=trayMenu=new Forms.ContextMenuStrip();menu.Items.Add("截图",null,(_,_)=>Dispatcher.Invoke(()=>StartCapture("自动")));
        menu.Items.Add("长截屏",null,(_,_)=>Dispatcher.Invoke(()=>StartCapture("区域",CaptureFollowUp.Scroll)));
        menu.Items.Add("剪贴板贴图",null,(_,_)=>Dispatcher.Invoke(PinClipboard)); menu.Items.Add("设置 / 主界面",null,(_,_)=>Dispatcher.Invoke(ShowHome));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出 "+AppBrand.Name,null,(_,_)=>Dispatcher.Invoke(ExitApp));
        tray=new Forms.NotifyIcon { Text=AppBrand.TrayTooltip(Settings,0),Icon=new System.Drawing.Icon(AppIdentity.ShellIconPath,Forms.SystemInformation.SmallIconSize),ContextMenuStrip=menu,Visible=true };
        tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(ShowHome);
        RefreshTrayTooltip();
        var error=Register(Settings);
        if(error!=null) MessageBox.Show(error,"快捷键冲突");
        if(!e.Args.Contains("--background") || !Settings.Configured) ShowHome();
        if(e.Args.Contains("--test-clipboard-preserve")) {
            int reportIndex=Array.IndexOf(e.Args,"--report-dir");testClipboardReport=reportIndex>=0&&reportIndex+1<e.Args.Length?Path.Combine(e.Args[reportIndex+1],"clipboard-restored.txt"):Path.Combine(DataPath,"clipboard-restored.txt");
            try {testClipboard=ClipboardImages.PreserveForTestAsync().GetAwaiter().GetResult();}
            catch(Exception ex) {Directory.CreateDirectory(Path.GetDirectoryName(testClipboardReport)!);File.WriteAllText(testClipboardReport,"NOT STARTED: "+ex.Message);Shutdown();return;}
        }
        if(e.Args.Contains("--test-fixture")) {
            var fixture=new FixtureWindow();
            if(e.Args.Contains("--test-wheel"))fixture.Loaded+=(_,_)=> {
                var handle=new WindowInteropHelper(fixture).Handle;Native.GetWindowRect(handle,out var rect);
                var area=new System.Drawing.Rectangle(rect.Left+70,rect.Top+110,rect.Right-rect.Left-160,rect.Bottom-rect.Top-190);
                var scroll=new ScrollWindow(area,b=> {new EditorWindow(b,null).Show();fixture.Title=AppBrand.Title("滚轮验收完成");});fixture.AttachCapture(scroll);scroll.Show();
            };
            fixture.Show();
        }
        if(e.Args.Contains("--test-editor")) { using var fixture=SelfTests.OfflineQualityFixture(); new EditorWindow(fixture,null).Show(); }
    }
    private IntPtr Hook(IntPtr h,int msg,IntPtr w,IntPtr l,ref bool handled)
    {
        if(msg==0x0312) {
            handled=true;
            if(Keyboard.FocusedElement is ShortcutBox field) { long value=l.ToInt64();field.SetShortcut((uint)value&15,(uint)(value>>16)&0xFFFF); }
            else HotkeyBindings.Execute((HotkeyAction)w.ToInt32(),(mode,next)=>StartCapture(mode,next),PinClipboard);
        }
        return IntPtr.Zero;
    }
    private string? Register(Settings settings)
    {
        return hotkeyRegistry?.Apply(settings)??HotkeyBindings.Validate(settings);
    }
    public string? SaveSettings(Settings next)
    {
        string? err=Register(next); if(err!=null) return err;
        try {
            string file=Path.Combine(DataPath,"settings.json"),temp=file+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(next,new JsonSerializerOptions{WriteIndented=true}));
            using var key=Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
            if(next.Startup) key.SetValue("QingCapture",'"'+Environment.ProcessPath+"\" --background"); else key.DeleteValue("QingCapture",false);
            File.Move(temp,file,true); Settings=next;RefreshTrayTooltip(); return null;
        } catch(Exception ex) { Register(Settings); return "设置未保存："+ex.Message; }
    }
    internal string? TrayTooltipText=>tray?.Text;
    internal void RefreshTrayTooltip()
    {
        if(tray==null||Exiting)return;
        tray.Text=AppBrand.TrayTooltip(Settings,Windows.OfType<PinWindow>().Count(window=>window.IsVisible));
    }
    internal void ReportClipboardFailure(string message)
    {
        if(!Exiting&&tray!=null)tray.ShowBalloonTip(5000,AppBrand.Name,"截图未能复制，请重新截图或复制："+message,Forms.ToolTipIcon.Warning);
    }
    public void ShowHome() { if(home==null) { home=new HomeWindow(); home.Closed+=(_,_)=>home=null; } home.Show(); if(home.WindowState==WindowState.Minimized) home.WindowState=WindowState.Normal; home.Activate(); }
    public void ShowMenu(Window owner)
    {
        owner.Activate(); Native.GetCursorPos(out var point); trayMenu?.Show(point.X,point.Y);
    }
    public async void StartCapture(string mode,CaptureFollowUp followUp=CaptureFollowUp.None,bool returnHomeOnCancel=false)
    {
        if(capturing) return; capturing=true;
        int generation=++captureGeneration;
        void RestoreHome() { if(returnHomeOnCancel&&!Exiting&&generation==captureGeneration)ShowHome(); }
        try {
            home?.Hide(); await Task.Delay(250);
            var screen=Forms.SystemInformation.VirtualScreen;
            using var snap=Imaging.Capture(screen);
            if(mode=="全屏"&&followUp==CaptureFollowUp.None) { new ScreenEditorWindow(snap,screen,screen).ShowDialog(); return; }
            var overlay=new CaptureOverlay(snap,screen,mode,followUp);
            try {
            bool? result=overlay.ShowDialog();
            if(result==true) {
                using var extended=overlay.TakeDragResult();
                if(extended!=null){new EditorWindow(extended,null).Show();if(overlay.DragError!=null)MessageBox.Show("已保留可确认的截图内容："+overlay.DragError,AppBrand.Name);return;}
                var r=overlay.Selection;
                if(overlay.ScrollRequested) {
                    bool completed=false;
                    Action<System.Drawing.Bitmap> finish=b=> {new EditorWindow(b,null).Show();completed=true;};
                    var scroll=CreateDragScroll(overlay,snap,screen,finish);
                    if(returnHomeOnCancel)scroll.Closed+=(_,_)=> { if(!completed)RestoreHome(); };
                    try {scroll.Show();}catch {scroll.Close();throw;}
                }
                else if(followUp is CaptureFollowUp.None or CaptureFollowUp.Translate or CaptureFollowUp.Scroll) {
                    CreateScreenEditor(snap,screen,r,followUp).ShowDialog();
                }
                else {
                    using var selected=Imaging.Crop(snap,new System.Drawing.Rectangle(r.X-screen.X,r.Y-screen.Y,r.Width,r.Height));
                    var editor=new EditorWindow(selected,r);
                    editor.Show();
                }
            } else RestoreHome();
            } finally {overlay.ReleaseUnusedDragGuard();}
        } catch(Exception ex) { MessageBox.Show("截图未完成："+ex.Message,AppBrand.Name);RestoreHome(); }
        finally { capturing=false; }
    }
    internal static ScrollWindow CreateDragScroll(CaptureOverlay overlay,System.Drawing.Bitmap snapshot,System.Drawing.Rectangle desktop,Action<System.Drawing.Bitmap> completed,Func<System.Drawing.Rectangle,System.Drawing.Bitmap>? capture=null,Action<MouseWheelSample>? forward=null)
    {
        if(!overlay.ScrollRequested)throw new InvalidOperationException("No held-drag scroll handoff was requested.");
        var selected=overlay.Selection;
        using var seed=Imaging.Crop(snapshot,new System.Drawing.Rectangle(selected.X-desktop.X,selected.Y-desktop.Y,selected.Width,selected.Height));
        var guard=overlay.TakeDragGuard();
        try {return new ScrollWindow(selected,completed,capture??Imaging.Capture,seed,overlay.InitialWheel,forward,guard,true);}
        catch {guard?.Dispose();throw;}
    }
    internal static ScreenEditorWindow CreateScreenEditor(System.Drawing.Bitmap snapshot,System.Drawing.Rectangle desktop,System.Drawing.Rectangle selection,CaptureFollowUp followUp,bool autoCopy=true,TranslationClient? client=null,Func<System.Drawing.Bitmap,IProgress<string>,CancellationToken,Task<List<OcrBlock>>>? read=null)
    {
        var editor=new ScreenEditorWindow(snapshot,desktop,selection,autoCopy);
        if(followUp==CaptureFollowUp.Translate)editor.Loaded+=async(_,_)=>await editor.Translate(client,read);
        return editor;
    }
    public void PinClipboard()
    {
        try { var src=Clipboard.GetImage(); if(src==null) { MessageBox.Show("剪贴板里没有图片，请先复制一张图片。",AppBrand.Name); return; } using var b=Imaging.FromSource(src); new PinWindow(b).Show(); }
        catch(Exception ex) { MessageBox.Show("读取剪贴板失败："+ex.Message,AppBrand.Name); }
    }
    public void ExitApp() { Exiting=true; Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        if(testClipboard!=null) {
            try {bool restored=testClipboard.RestoreAsync().GetAwaiter().GetResult();Directory.CreateDirectory(Path.GetDirectoryName(testClipboardReport!)!);File.WriteAllText(testClipboardReport!,restored?"PASS: original clipboard restored after local GUI paste verification.":"UNCHANGED: external clipboard update detected; preserved the new content.");}
            catch(Exception ex) {File.WriteAllText(testClipboardReport!,"FAIL: "+ex.Message);}
            finally {testClipboard.Dispose();testClipboard=null;}
        }
        Translator.Dispose();
        hotkeyRegistry?.Dispose(); hotkeys?.Dispose();
        if(tray!=null) { tray.Visible=false; tray.Dispose(); } mutex?.Dispose(); base.OnExit(e);
    }
}


internal sealed class ShortcutBox : TextBox
{
    public Shortcut Value { get; private set; }
    internal void SetShortcut(uint modifiers,uint key) { string label=HotkeyBindings.Label(modifiers,key); Value=new Shortcut(modifiers,key,label);Text=label; }
    public ShortcutBox(Shortcut key)
    {
        Style=(Style)Application.Current.FindResource(typeof(TextBox)); Value=key; Text=HotkeyBindings.Label(key.Modifiers,key.Key); IsReadOnly=true; MinWidth=140; MinHeight=40; ToolTip="点击后按下组合键、F1–F24 或 Print Screen；Backspace 清除";
        GotKeyboardFocus+=(_,_)=> { Text="请按快捷键…"; SelectAll(); };
        LostKeyboardFocus+=(_,_)=>Text=HotkeyBindings.Label(Value.Modifiers,Value.Key);
        PreviewKeyDown+=(_,e)=> {
            Key k=e.Key==Key.System?e.SystemKey:e.Key;
            if(k==Key.Tab) return;
            e.Handled=true;
            if(k==Key.Escape) { Text=HotkeyBindings.Label(Value.Modifiers,Value.Key); return; }
            if(k==Key.Back) { Value=new(); Text=Value.Label; return; }
            if(k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var m=Keyboard.Modifiers;
            if(m==ModifierKeys.None && k!=Key.PrintScreen && (k<Key.F1 || k>Key.F24)) { Text="组合键 / F键 / Print Screen"; return; }
            uint mods=0; if(m.HasFlag(ModifierKeys.Alt)) mods|=1; if(m.HasFlag(ModifierKeys.Control)) mods|=2; if(m.HasFlag(ModifierKeys.Shift)) mods|=4; if(m.HasFlag(ModifierKeys.Windows)) mods|=8;
            SetShortcut(mods,(uint)KeyInterop.VirtualKeyFromKey(k));
        };
    }
}
internal static class Bits { public static bool HasFlag(this uint x,uint mask)=>(x&mask)!=0; }
