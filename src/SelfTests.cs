using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Point=System.Drawing.Point;

namespace QingCapture;

internal static class SelfTests
{
    private static readonly List<string> lines=new();
    private static void Assert(bool ok,string name) { if(!ok) throw new Exception("FAIL: "+name); lines.Add("PASS: "+name); }
    public static int RunTranslationRegression(string[] args)
    {
        string folder=args.SkipWhile(x=>x!="--report-dir").Skip(1).FirstOrDefault()??Path.Combine(Environment.CurrentDirectory,"translation-regression-results");
        Directory.CreateDirectory(folder);
        try { File.WriteAllLines(Path.Combine(folder,"tests.txt"),TranslationRegressionAcceptance.Run());return 0; }
        catch(Exception ex) { File.WriteAllText(Path.Combine(folder,"tests.txt"),ex.ToString());return 1; }
    }
    public static int RunUi(string[] args)
    {
        string folder=args.SkipWhile(x=>x!="--report-dir").Skip(1).FirstOrDefault()??Path.Combine(Environment.CurrentDirectory,"ui-test-results"); Directory.CreateDirectory(folder);
        void Mark(string stage)=>File.AppendAllText(Path.Combine(folder,"progress.txt"),stage+Environment.NewLine);
        try {
            if(args.Contains("--recorded-chat-scroll")) {
                string path=args.SkipWhile(x=>x!="--image-path").Skip(1).FirstOrDefault()??throw new ArgumentException("--image-path is required");
                lines.Clear();lines.AddRange(RecordedChatScrollAcceptance.Run(path,folder));
                File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
            }
            if(args.Contains("--scroll-stability-only")) {
                lines.Clear();lines.AddRange(SparseScrollAcceptance.Run());lines.AddRange(ReleaseCaptureAcceptance.Run());lines.AddRange(ScrollRecoveryAcceptance.Run());
                File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
            }
            if(args.Contains("--desktop-wheel-route")) {
                lines.Clear();lines.AddRange(DesktopWheelRouteAcceptance.Run(folder,args.Contains("--wait-physical-wheel")));
                File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
            }
            if(args.Contains("--browser-wheel-route")) {
                lines.Clear();lines.AddRange(BrowserWheelRouteAcceptance.Run(folder));
                File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
            }
            if(args.Contains("--dispatch-failure-only")) {
                lines.Clear();lines.AddRange(DispatchFailureAcceptance.Run());
                File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
            }
            if(args.Contains("--bounded-scroll-only")) {
                lines.Clear();lines.AddRange(DragWheelAcceptance.Run(folder));lines.AddRange(ScrollRecoveryAcceptance.Run());lines.AddRange(DispatchFailureAcceptance.Run());lines.AddRange(BoundedScrollAcceptance.Run(folder));
                File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
            }
            if(args.Contains("--drag-wheel-only")) {
                lines.Clear();lines.AddRange(DragWheelAcceptance.Run(folder));
                File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
            }
            if(args.Contains("--native-capture-only")) {
                lines.Clear();
                void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
                DragCaptureAcceptance.RunLoadedOverlay(Check,folder);FixedOverlayAcceptance.RunNativeDarkOverlay(Check,folder);FixedOverlayAcceptance.RunNativeDarkOverlay(Check,folder,true);
                File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
            }
            lines.Clear();Mark("UI behavior acceptance started");lines.AddRange(ModelIdleLifetimeAcceptance.Run());lines.AddRange(UiAcceptance.Run(Mark));lines.AddRange(HotkeyAcceptance.Run());lines.AddRange(TrayAcceptance.Run());lines.AddRange(CaptureAcceptance.Run(folder));lines.AddRange(ClipboardAcceptance.Run());lines.AddRange(WheelCaptureAcceptance.Run());lines.AddRange(DragCaptureAcceptance.Run(folder));lines.AddRange(FixedOverlayAcceptance.Run(folder));lines.AddRange(DragWheelAcceptance.Run(folder));lines.AddRange(ScrollRecoveryAcceptance.Run());lines.AddRange(DispatchFailureAcceptance.Run());lines.AddRange(SparseScrollAcceptance.Run());lines.AddRange(ReleaseCaptureAcceptance.Run());lines.AddRange(BoundedScrollAcceptance.Run(folder));lines.AddRange(DesktopWheelRouteAcceptance.Run(folder));lines.AddRange(ScreenEditorAcceptance.Run(folder));Mark("UI behavior acceptance passed");lines.AddRange(LayoutAcceptance.Run(folder,Mark));Mark("WPF layout and image rendering passed");
            File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 0;
        } catch(Exception ex) { lines.Add(ex.ToString());File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines);return 1; }
    }
    public static int RunClipboard(string[] args)
    {
        string folder=args.SkipWhile(x=>x!="--report-dir").Skip(1).FirstOrDefault()??Path.Combine(Environment.CurrentDirectory,"clipboard-test-results");Directory.CreateDirectory(folder);
        try {File.WriteAllLines(Path.Combine(folder,"tests.txt"),ClipboardAcceptance.RunRoundTripAsync().GetAwaiter().GetResult());return 0;}
        catch(Exception ex) {File.WriteAllText(Path.Combine(folder,"tests.txt"),ex.ToString());return 1;}
    }
    public static int RunClipboardLifetime(string[] args)
    {
        string folder=args.SkipWhile(x=>x!="--report-dir").Skip(1).FirstOrDefault()??Path.Combine(Environment.CurrentDirectory,"clipboard-lifetime-results");Directory.CreateDirectory(folder);
        try {File.WriteAllLines(Path.Combine(folder,"tests.txt"),ClipboardAcceptance.RunCrossProcessLifetimeAsync(Environment.ProcessPath!,folder).GetAwaiter().GetResult());return 0;}
        catch(Exception ex) {File.WriteAllText(Path.Combine(folder,"tests.txt"),ex.ToString());return 1;}
    }
    public static Bitmap Fixture(int width=800,int height=2200)
    {
        var b=new Bitmap(width,height); using var g=Graphics.FromImage(b); g.Clear(Color.White);
        using var title=new Font("Arial",28,GraphicsUnit.Pixel); using var font=new Font("Arial",20,GraphicsUnit.Pixel);
        g.DrawString("Screenshot translation test",title,Brushes.Black,30,25);
        var random=new Random(427);
        for(int y=90,n=1;y<height-30;y+=53,n++) {
            g.DrawString($"{n:00}. Hello world. This is paragraph {n}.",font,Brushes.Black,30,y);
            using var color=new SolidBrush(Color.FromArgb(random.Next(40,220),random.Next(40,220),random.Next(40,220))); g.FillRectangle(color,width-90,y,random.Next(20,65),random.Next(12,30));
        }
        return b;
    }
    public static Bitmap OfflineQualityFixture()
    {
        var b=new Bitmap(1200,780); using var g=Graphics.FromImage(b); g.Clear(Color.White);
        using var title=new Font("Arial",30,GraphicsUnit.Pixel); using var line=new Font("Arial",22,GraphicsUnit.Pixel);
        g.DrawString("Offline translation review",title,Brushes.Black,38,28);
        string[] samples={
            "The meeting was moved from Monday to Friday at 3 PM.",
            "Changes made before 5 p.m. today will be reviewed tomorrow.",
            "Reduce the total by 15%, not to 15%.",
            "The refund will not be issued until 30 days after the returned item is received.",
            "The trial lasts for two weeks after installation.",
            "The price increases from $120 to $150 next month."
        };
        for(int i=0;i<samples.Length;i++) g.DrawString(samples[i],line,Brushes.Black,38,104+i*76);
        return b;
    }
    public static int Run(string[] args)
    {
        string folder=args.SkipWhile(x=>x!="--report-dir").Skip(1).FirstOrDefault()??Path.Combine(Environment.CurrentDirectory,"test-results"); Directory.CreateDirectory(folder);
        try {
            using var fixture=Fixture(); fixture.Save(Path.Combine(folder,"fixture.png"));
            using var first=Imaging.Crop(fixture,new Rectangle(0,0,800,650)); using var next=Imaging.Crop(fixture,new Rectangle(0,240,800,650));
            Assert(Stitcher.Match(first,first).State=="unchanged","stationary frame is not appended");
            var match=Stitcher.Match(first,next); Assert(match.State=="append"&&match.Shift==240,$"scroll match 240px (actual {match})");
            using var merged=Stitcher.Append(first,next,match); Assert(merged.Height==890,"stitched height");
            using var expected=Imaging.Crop(fixture,new Rectangle(0,0,800,890)); Assert(Imaging.Png(merged).SequenceEqual(Imaging.Png(expected)),"stitched output exactly equals source pixels"); merged.Save(Path.Combine(folder,"stitched.png"));
            using var unrelated=new Bitmap(800,650); using(var g=Graphics.FromImage(unrelated)) g.Clear(Color.DarkRed); Assert(Stitcher.Match(first,unrelated).State is "mismatch" or "ambiguous","unmatched / large jump rejected");
            using var fixedA=new Bitmap(first); using var fixedB=new Bitmap(next);
            foreach(var b in new[]{fixedA,fixedB}) { using var g=Graphics.FromImage(b); g.FillRectangle(Brushes.Navy,0,0,800,45); g.FillRectangle(Brushes.Gray,0,605,800,45); }
            var fixedMatch=Stitcher.Match(fixedA,fixedB); Assert(fixedMatch.State=="append"&&fixedMatch.Shift==240&&fixedMatch.Footer>=45,"fixed header/footer detected");
            using var fixedMerged=Stitcher.Append(fixedA,fixedB,fixedMatch); Assert(fixedMerged.GetPixel(20,650).ToArgb()!=Color.Gray.ToArgb(),"old footer removed at seam");
            using var repeated=new Bitmap(800,650); using(var g=Graphics.FromImage(repeated)) { g.Clear(Color.White); for(int y=0;y<650;y+=30) g.FillRectangle(Brushes.Black,20,y,400,10); }
            using var repeatShift=new Bitmap(800,650); using(var g=Graphics.FromImage(repeatShift)) { g.Clear(Color.White); for(int y=-10;y<650;y+=30) g.FillRectangle(Brushes.Black,20,y,400,10); }
            Assert(Stitcher.Match(repeated,repeatShift).State=="ambiguous","repeating patterns rejected");
            using var browserBefore=new Bitmap(Path.Combine(AppContext.BaseDirectory,"testdata","browser-before.png"));
            using var browserAfter=new Bitmap(Path.Combine(AppContext.BaseDirectory,"testdata","browser-after.png"));
            var browserMatch=Stitcher.Match(browserBefore,browserAfter);
            Assert(browserMatch.State=="append"&&browserMatch.Shift==240,$"real Chrome repeated-paragraph regression (actual {browserMatch})");
            using var browserMerged=Stitcher.Append(browserBefore,browserAfter,browserMatch);
            Assert(browserMerged.Height==961,"real Chrome stitched image height");
            using var browserPrefix=Imaging.Crop(browserMerged,new Rectangle(0,0,browserBefore.Width,browserBefore.Height-browserMatch.Footer));
            using var expectedPrefix=Imaging.Crop(browserBefore,new Rectangle(0,0,browserBefore.Width,browserBefore.Height-browserMatch.Footer));
            Assert(EqualPixels(browserPrefix,expectedPrefix),"real Chrome original pixels retained regardless of image DPI metadata");
            using var browserTranslation=TranslationLayout.Compose(browserBefore,new List<OcrBlock>{new(){Number=1,Original="Test",Translation="测试",Bounds=new Rectangle(10,10,100,30)}});
            using var translatedPrefix=Imaging.Crop(browserTranslation,new Rectangle(0,0,browserBefore.Width,browserBefore.Height));
            Assert(EqualPixels(browserBefore,translatedPrefix),"translation keeps imported screenshot pixels at different DPI");
            browserMerged.Save(Path.Combine(folder,"browser-regression-stitched.png"));
            using var doc=new ImageDocument(first); var original=Imaging.Png(doc.Current); doc.Apply(b=>Annotation.Draw(b,"框线",new Point(20,20),new Point(180,80),"",Color.Red,3)); Assert(!original.SequenceEqual(Imaging.Png(doc.Current)),"annotation modifies pixels"); doc.Undo(); Assert(original.SequenceEqual(Imaging.Png(doc.Current)),"undo restores original"); doc.Redo(); Assert(!original.SequenceEqual(Imaging.Png(doc.Current)),"redo restores annotation");
            doc.Apply(b=>Annotation.Draw(b,"箭头",new Point(20,120),new Point(280,180),"",Color.Teal,3)); doc.Apply(b=>Annotation.Draw(b,"文字",new Point(25,220),new Point(25,220),"中文标注",Color.Red,3)); doc.Apply(b=>Annotation.Draw(b,"马赛克",new Point(25,270),new Point(380,315),"",Color.Red,3)); doc.Current.Save(Path.Combine(folder,"annotations.png"));
            var mapped=CaptureOverlay.MapSelection(new System.Windows.Point(100,100),new System.Windows.Point(500,300),1920,1080,new Rectangle(-1920,0,2880,1620)); Assert(mapped==new Rectangle(-1770,150,600,300),"150% DPI + negative monitor coordinate mapping");
            foreach(double dpi in new[]{1d,1.25,1.5,2d}) {
                var mappedDpi=CaptureOverlay.MapSelection(new System.Windows.Point(320,240),new System.Windows.Point(80,40),1280,720,new Rectangle(-1280,0,(int)(1280*dpi),(int)(720*dpi)));
                Assert(mappedDpi==new Rectangle(-1280+(int)(80*dpi),(int)(40*dpi),(int)(240*dpi),(int)(200*dpi)), $"reverse selection at {dpi:P0} DPI");
            }
            var blocks=LocalOcr.Read(first,new Progress<string>(),CancellationToken.None).GetAwaiter().GetResult(); Assert(blocks.Any(b=>b.Original.Contains("Hello",StringComparison.OrdinalIgnoreCase)),"real bundled OCR recognizes English");
            lines.AddRange(RuntimeDependencyTest.AssertAppLocalAfterOcr(Path.Combine(folder,"runtime-dependencies.txt")));
            foreach(var block in blocks) block.Translation="你好，世界。这是一段可编辑的简体中文译文。";
            using var translated=TranslationLayout.Compose(first,blocks); Assert(translated.Width>first.Width&&translated.Height>=first.Height,"translation column grows without shrinking source"); translated.Save(Path.Combine(folder,"translated.png"));
            using var preserved=Imaging.Crop(translated,new Rectangle(0,0,first.Width,first.Height)); Assert(Imaging.Png(preserved).SequenceEqual(Imaging.Png(first)),"translation preserves every source pixel");
            using var continuous=TranslationLayout.Compose(first,new[]{new OcrBlock { Number=99,Bounds=new Rectangle(0,2000,200,30),Translation=TranslationText.Translated(blocks) }});
            Assert(EqualPixels(translated,continuous),"translation layout is one paragraph independent of OCR numbering and vertical gaps");
            Assert(blocks.Any(b=>b.Original.Contains("Hello world. This is paragraph")),"OCR nearby fragments merged into readable lines");
            var longBlocks=LocalOcr.Read(fixture,new Progress<string>(),CancellationToken.None).GetAwaiter().GetResult(); Assert(longBlocks.Any(b=>b.Bounds.Y>1700),"long-image OCR reaches later strips");
            Imaging.Save(translated,Path.Combine(folder,"translated.jpg"),true); using var jpg=new Bitmap(Path.Combine(folder,"translated.jpg")); Assert(jpg.Size==translated.Size,"JPEG export dimensions");
            using var preCancelled=new CancellationTokenSource(); preCancelled.Cancel(); bool cancelled=false; try { LocalOcr.Read(first,new Progress<string>(),preCancelled.Token).GetAwaiter().GetResult(); } catch(OperationCanceledException) { cancelled=true; } Assert(cancelled,"OCR cancellation");
            lines.AddRange(OfflineTranslationAcceptance.Run(folder));
            lines.AddRange(ModelIdleLifetimeAcceptance.Run());
            bool sizeRejected=false; try { Imaging.CheckSize(10000,10000); } catch(InvalidOperationException) { sizeRejected=true; } Assert(sizeRejected,"oversized image rejected before allocation");
            using var editedTranslation=TranslationLayout.Compose(first,new List<OcrBlock>{new(){Number=1,Bounds=new Rectangle(20,20,200,30),Original="Hello",Translation=new string('中',1200)},new(){Number=2,Bounds=new Rectangle(20,60,200,30),Original="World",Translation="第二段译文"}}); Assert(editedTranslation.Height>first.Height,"long edited translation expands canvas without clipping");
            var h=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("QingCapture.Test") { Width=0,Height=0 });
            Assert(Native.RegisterHotKey(h.Handle,55,0x4003,0x7B),"register unused Ctrl+Alt+F12 hotkey"); Assert(!Native.RegisterHotKey(h.Handle,56,0x4003,0x7B),"duplicate hotkey reports conflict"); Native.UnregisterHotKey(h.Handle,55); h.Dispose();
            if(args.Contains("--browser-translation-test")) {
                // Bundled synthetic browser fixture; the real local model, no remote provider.
                var browserBlocks=LocalOcr.Read(browserMerged,new Progress<string>(),CancellationToken.None).GetAwaiter().GetResult();
                Assert(browserBlocks.Count>0&&browserBlocks.Any(b=>b.Bounds.Y>750),"stitched browser OCR reaches the fourth paragraph");
                string browserOriginal=TranslationText.Original(browserBlocks);browserBlocks=TranslationText.MergeForTranslation(browserBlocks);
                Assert(browserBlocks.Count==1&&browserBlocks[0].Original==browserOriginal,"stitched browser OCR is translated as complete text without line breaks");
                using var browserTranslator=new TranslationClient();
                browserTranslator.Fill(browserBlocks,new Progress<string>(),CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(folder,"browser-translation.json"),System.Text.Json.JsonSerializer.Serialize(browserBlocks,new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
                Assert(browserBlocks.All(b=>b.Translation.Length>0&&b.Error.Length==0),"all stitched browser OCR lines translated by bundled offline models");
                Assert(browserBlocks.Any(b=>b.Translation.Any(c=>c>='\u4e00'&&c<='\u9fff')),"stitched browser translation contains Chinese text");
                using var browserResult=TranslationLayout.Compose(browserMerged,browserBlocks);
                using var retained=Imaging.Crop(browserResult,new Rectangle(0,0,browserMerged.Width,browserMerged.Height));
                Assert(EqualPixels(retained,browserMerged),"live translated browser image preserves every original pixel");
                Imaging.Save(browserResult,Path.Combine(folder,"browser-translated.png"));
                Imaging.Save(browserResult,Path.Combine(folder,"browser-translated.jpg"),true);
                using var browserPng=new Bitmap(Path.Combine(folder,"browser-translated.png"));
                using var browserJpg=new Bitmap(Path.Combine(folder,"browser-translated.jpg"));
                Assert(EqualPixels(browserPng,browserResult)&&browserJpg.Size==browserResult.Size,"live browser translation PNG pixels and JPEG dimensions round-trip");
            }
            lines.AddRange(UiAcceptance.Run());
            lines.AddRange(HotkeyAcceptance.Run());
            lines.AddRange(LayoutAcceptance.Run(folder));
            File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines); return 0;
        } catch(Exception ex) { lines.Add(ex.ToString()); File.WriteAllLines(Path.Combine(folder,"tests.txt"),lines); return 1; }
    }
    private static bool EqualPixels(Bitmap a,Bitmap b)
    {
        if(a.Size!=b.Size)return false;
        for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())return false;
        return true;
    }
}

internal sealed class FixtureWindow : Window
{
    private readonly Button finishCapture;
    internal void AttachCapture(ScrollWindow scroll) {finishCapture.Visibility=Visibility.Visible;finishCapture.Click+=(_,_)=>scroll.FinishForAcceptance();}
    public FixtureWindow()
    {
        Ui.Setup(this,AppBrand.Title("英文滚动内容测试页"),950,780); Left=30; Top=30;
        var panel=new StackPanel { Margin=new Thickness(25) }; panel.Children.Add(Ui.Text("Screenshot translation test",26));
        for(int i=1;i<=60;i++) { panel.Children.Add(Ui.Text($"{i:00}. Hello world. This is paragraph {i}.",20)); panel.Children.Add(Ui.Text($"Example {i*173}: Capture the visible area, then scroll slowly.",14,Ui.Muted)); }
        var root=new DockPanel();finishCapture=Ui.Button("完成长截图验收",()=>{});finishCapture.Visibility=Visibility.Collapsed;DockPanel.SetDock(finishCapture,Dock.Bottom);root.Children.Add(finishCapture);
        root.Children.Add(new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Visible });Content=root;
    }
}
