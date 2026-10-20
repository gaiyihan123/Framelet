using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace QingCapture;

// Explicit opt-in fixture: replay an existing user-supplied chat image as a
// document. Existing defects in that image remain pixels in the source; this
// verifies scrolling and stitching, not repair of the historical screenshot.
// The external source is read only and is never bundled with the application.
internal static class RecordedChatScrollAcceptance
{
    internal static List<string> Run(string imagePath,string folder)
    {
        const int documentHeight=1240,viewportHeight=430;
        var checks=new List<string>();var trace=new List<string>();
        Directory.CreateDirectory(folder);
        void Check(bool value,string label)
        {
            checks.Add((value?"PASS: ":"FAIL: ")+label);
            if(!value)throw new InvalidOperationException("FAIL: "+label);
        }
        byte[] sourceHash=SHA256.HashData(File.ReadAllBytes(imagePath));
        try {
            using var source=new Bitmap(imagePath);
            Check(source.Width==977&&source.Height>=documentHeight,"the explicitly supplied recorded chat has its original 977px width and at least 1240px of body above the composer");
            foreach(int width in new[]{source.Width,300}) {
                int sourceX=(source.Width-width)/2;
                using var document=source.Clone(new Rectangle(sourceX,0,width,documentHeight),PixelFormat.Format32bppArgb);
                Replay(document,width==source.Width?"full":"center",folder,viewportHeight,Check,trace);
            }
            Check(sourceHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(imagePath))),"recorded-source pixels are preserved on disk; any old screenshot artifacts remain part of the test document");
            return checks;
        } finally {
            File.WriteAllLines(Path.Combine(folder,"recorded-chat-scroll-checks.txt"),checks);
            File.WriteAllLines(Path.Combine(folder,"recorded-chat-scroll-trace.txt"),trace);
        }
    }

    private static void Replay(Bitmap document,string name,string folder,int height,Action<bool,string> check,List<string> trace)
    {
        int maximum=document.Height-height,offset=0,dispatches=0,step=0;
        bool bounded=true;
        var viewport=new Rectangle(-1100,80,document.Width,height);
        Bitmap Capture()=>document.Clone(new Rectangle(0,offset,document.Width,height),PixelFormat.Format32bppArgb);
        using var initial=Capture();
        using var controller=new DragCaptureController(viewport,initial,area=> {
            bounded&=area==viewport;return Capture();
        },()=>throw new InvalidOperationException("The recorded-chat fixture must stay in manual wheel mode."),()=>offset==maximum,delta=> {
            dispatches++;offset=Math.Clamp(offset-delta,0,maximum);return true;
        });
        check(controller.FreezeSelection(viewport),$"recorded {name} chat freezes the original {document.Width} by {height} viewport");
        int expected=0;
        while(expected<maximum) {
            step++;int distance=Math.Min(120,maximum-expected);expected+=distance;
            check(controller.QueueWheel(-distance),$"recorded {name} chat accepts step {step} ({distance}px) in the production wheel queue");
            var clock=Stopwatch.StartNew();
            PumpUntil(()=>controller.PendingWheelCount==0&&!controller.SamplingActive,15000);
            trace.Add($"{name} step={step} requested={distance} expected={expected} source={offset} confirmed={controller.ScrollOffset} frames={controller.FrameCount} captures={controller.CaptureCount} elapsedMs={clock.ElapsedMilliseconds} pending={controller.PendingWheelCount} overlap={controller.NeedsOverlap} match={controller.LastMatch} error={controller.Error??"<none>"}");
            check(controller.ScrollOffset==expected&&offset==expected&&!controller.NeedsOverlap&&controller.Error==null&&controller.FrameCount==step+1,
                $"recorded {name} chat step {step} confirms exactly {expected}px of real antialiased Chinese content");
            check(bounded&&controller.VisibleSelection==viewport&&controller.Viewport==viewport,
                $"recorded {name} chat step {step} keeps the viewport fixed and excludes every source row at or below 1240");
        }
        check(controller.PendingWheelCount==0&&!controller.SamplingActive&&dispatches==step&&offset==maximum,
            $"recorded {name} chat reaches the last {maximum%120}px tail and has no queued input before release");
        Task<Bitmap> finish=controller.Finish();
        PumpUntil(()=>finish.IsCompleted,15000);
        using var result=finish.GetAwaiter().GetResult();
        result.Save(Path.Combine(folder,$"recorded-chat-{name}-result.png"),ImageFormat.Png);
        check(result.Size==document.Size&&Stitcher.SamePixels(result,document)&&controller.CompletionWarning==null,
            $"recorded {name} chat output equals all {document.Width} by {document.Height} source pixels exactly once, including existing source artifacts");
        check(controller.ImagesReleased&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!controller.RangeProbeActive,
            $"recorded {name} chat release clears its images, queue, and both production timers");
    }

    private static void PumpUntil(Func<bool> condition,int milliseconds)
    {
        var clock=Stopwatch.StartNew();
        while(!condition()&&clock.ElapsedMilliseconds<milliseconds) {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};
            timer.Start();Dispatcher.PushFrame(frame);
        }
        if(!condition())throw new TimeoutException("The recorded-chat production wheel queue did not settle.");
    }
}
