using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using D=System.Drawing;
using W=System.Windows;
using Interop=System.Windows.Interop;

namespace QingCapture;

// A real Chromium page supplies its own wheel/scroll telemetry. This fixture
// never changes its scroll position or intercepts its native wheel messages.
// The test driver opens the ready.json URL and supplies the final real wheel.
internal static class BrowserWheelRouteAcceptance
{
    private const string PageTitle="Framelet Chromium Wheel Route Probe 20261020";
    private const string OverlayTitle="Framelet Chromium Wheel Overlay Probe 20261020";
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int maximum);

    internal static List<string> Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var lines=new List<string>();
        void Check(bool value,string label){if(!value)throw new InvalidOperationException("FAIL: "+label);lines.Add("PASS: "+label);}
        using var server=new ProbeServer();
        WriteJson(folder,"ready.json",new {url=server.Url,phase="open-page",pageTitle=PageTitle});
        WriteJson(folder,"phase.json",new {phase="open-page",url=server.Url,pageTitle=PageTitle});
        CaptureOverlay? overlay=null;DragCaptureController? controller=null;D.Bitmap? result=null;
        DispatcherTimer? begin=null;Exception? error=null;bool completed=false;
        try {
            PumpUntil(()=>server.Started,240000,"The local browser probe was not opened and started within four minutes.");
            var browser=FindProbeWindow();
            Check(browser!=IntPtr.Zero,"only the uniquely titled local Chromium probe window is selected");
            Check(Native.GetClientRect(browser,out var client),"the probe browser exposes native client bounds");
            var origin=new Native.Point();Check(Native.ClientToScreen(browser,ref origin),"the probe client origin maps to physical screen coordinates");
            Check(client.Right>=620&&client.Bottom>=650,"the probe has room for a 400 by 430 content selection below browser chrome");
            var desktop=System.Windows.Forms.SystemInformation.VirtualScreen;
            using var snapshot=Imaging.Capture(desktop);
            overlay=new CaptureOverlay(snapshot,desktop,"区域",CaptureFollowUp.Scroll,_=>{}) {Title=OverlayTitle};
            begin=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(120)};
            begin.Tick+=(_,_)=> {
                begin.Stop();
                try {
                    var handle=new Interop.WindowInteropHelper(overlay).Handle;
                    W.Point Local(int x,int y)=>overlay.PointFromScreen(new W.Point(x+.25,y+.25));
                    int left=origin.X+100,top=origin.Y+160;
                    var selected=new D.Rectangle(left,top,400,430);
                    overlay.BeginPointer(Local(left,top));
                    overlay.MovePointer(Local(left+400,top+329));
                    overlay.MovePointer(Local(left+400,top+429));
                    controller=overlay.DragCapture;
                    Check(controller!=null&&overlay.Selection==selected&&overlay.IsMouseCaptured,"a real loaded overlay locks the chosen width and retains native mouse capture before any wheel is sent");
                    Check(controller!.FreezeSelection(selected)&&controller.Viewport==selected&&!controller.AutoScrolling,"the exact four selected edges are frozen without queuing or dispatching a wheel");
                    PumpFor(120);
                    var point=new Native.Point {X=selected.Left+selected.Width/2,Y=selected.Top+selected.Height/2};
                    var target=Native.WindowBelow(point,handle,selected);
                    var root=target==IntPtr.Zero?IntPtr.Zero:Native.GetAncestor(target,2);if(root==IntPtr.Zero)root=target;
                    Check(target!=IntPtr.Zero&&root==browser,"the production source lookup resolves only the known Chromium probe below the overlay");
                    Check(Native.WindowFromPoint(point)==handle,"the nonzero-alpha production overlay is the native hit target during the route comparison");
                    var baseline=server.Snapshot;
                    var sample=new MouseWheelSample(new D.Point(point.X,point.Y),-120,unchecked((uint)Environment.TickCount));
                    Check(Native.ForwardWheel(target,sample,activate:false),"the old direct PostMessage wheel path reports successful native delivery");
                    PumpFor(650);
                    var negative=server.Snapshot;
                    WriteJson(folder,"old-route.json",new {baseline,after=negative,selected=Rect(selected)});
                    Check(negative.Count==baseline.Count&&negative.Y==baseline.Y,"negative control: the old wheel path does not produce a Chromium DOM wheel or scroll while the opaque input overlay covers it");
                    Check(overlay.HandleDragWheel(sample),"the production manual-wheel handler accepts the next wheel into its serialized queue");
                    PumpUntil(()=>server.Snapshot.Count>negative.Count&&server.Snapshot.Y>negative.Y&&controller.ScrollOffset>0&&controller.PendingWheelCount==0&&!controller.SamplingActive,20000,"The new production route did not scroll the real Chromium page and append its content.");
                    var routed=server.Snapshot;
                    Check(controller.Error==null&&!controller.NeedsOverlap&&routed.Count>negative.Count&&routed.Y>negative.Y,"the new native route reaches Chromium default wheel scrolling without a source message hook or script-driven scrolling");
                    Check(overlay.Selection==selected&&controller.Viewport==selected&&controller.VisibleSelection==selected&&overlay.IsMouseCaptured,"the new route preserves all four selected edges and native mouse capture");
                    var local=overlay.PointFromScreen(new W.Point(point.X,point.Y));
                    int beforeOffset=controller.ScrollOffset,beforeCount=routed.Count;
                    WriteJson(folder,"phase.json",new {phase="await-physical-wheel",overlayTitle=OverlayTitle,overlayHandle=handle.ToInt64(),selection=Rect(selected),localPoint=new {x=local.X,y=local.Y},physicalPoint=new {x=point.X,y=point.Y},beforeCount,beforeOffset,dom=routed});
                    PumpUntil(()=>server.Snapshot.Count>beforeCount&&controller.ScrollOffset>beforeOffset&&controller.PendingWheelCount==0&&!controller.SamplingActive,240000,"A real wheel was not routed through the retained-capture overlay into the Chromium page within four minutes.");
                    var physical=server.Snapshot;
                    Check(physical.Count>beforeCount&&physical.Y>routed.Y&&controller.ScrollOffset>beforeOffset,"an external native wheel entering the overlay reaches the production queue, scrolls Chromium, and appends newly revealed content");
                    Check(overlay.Selection==selected&&controller.Viewport==selected&&overlay.IsMouseCaptured,"external wheel input does not expand the selection or release its capture");
                    WriteJson(folder,"phase.json",new {phase="finishing",dom=physical,offset=controller.ScrollOffset});
                    overlay.EndPointer(Local(selected.Right,selected.Bottom-1));
                    PumpUntil(()=>!overlay.IsVisible,20000,"The browser wheel capture did not finish.");
                    result=overlay.TakeDragResult();
                    Check(result!=null&&result.Width==selected.Width&&result.Height>selected.Height&&controller.Error==null&&controller.CompletionWarning==null,"the real Chromium route completes as a nonempty longer screenshot with no partial-completion warning");
                    Imaging.Save(result!,Path.Combine(folder,"browser-wheel-route-result.png"));
                    Check(controller.ImagesReleased&&controller.PendingWheelCount==0&&!controller.SamplingActive&&!overlay.IsMouseCaptured,"browser-route completion releases its images, queue, timer, and mouse capture");
                    WriteJson(folder,"phase.json",new {phase="completed",dom=physical,offset=controller.ScrollOffset,result=new {width=result!.Width,height=result.Height}});
                }catch(Exception ex){error=ex;WriteJson(folder,"phase.json",new {phase="failed",error=ex.ToString(),dom=server.Snapshot});}
                finally{if(overlay.IsVisible)overlay.Close();completed=true;}
            };
            begin.Start();overlay.ShowDialog();
            if(!completed)throw new InvalidOperationException("The browser probe overlay closed before its checks completed.");
            if(error!=null)throw new InvalidOperationException("Real Chromium wheel route acceptance failed",error);
            return lines;
        }finally {
            begin?.Stop();if(overlay?.IsVisible==true)overlay.Close();controller?.Dispose();result?.Dispose();
            File.WriteAllLines(Path.Combine(folder,"browser-wheel-route-checks.txt"),lines);
        }
    }

    private static object Rect(D.Rectangle rectangle)=>new {x=rectangle.X,y=rectangle.Y,width=rectangle.Width,height=rectangle.Height};
    private static void WriteJson(string folder,string name,object value)
    {
        string path=Path.Combine(folder,name),temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(value,new JsonSerializerOptions {WriteIndented=true}));File.Move(temporary,path,true);
    }
    private static IntPtr FindProbeWindow()
    {
        IntPtr result=IntPtr.Zero;
        Native.EnumWindows((window,_)=> {
            if(!Native.IsWindowVisible(window)||Native.IsIconic(window))return true;
            var title=new StringBuilder(1024);GetWindowText(window,title,title.Capacity);
            if(!title.ToString().StartsWith(PageTitle,StringComparison.Ordinal))return true;
            result=window;return false;
        },IntPtr.Zero);
        return result;
    }
    private static void PumpUntil(Func<bool> condition,int milliseconds,string timeout)
    {
        var elapsed=Stopwatch.StartNew();while(!condition()&&elapsed.ElapsedMilliseconds<milliseconds)PumpFor(25);
        if(!condition())throw new TimeoutException(timeout);
    }
    private static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }

    private sealed class ProbeServer : IDisposable
    {
        private readonly HttpListener listener=new();
        private readonly object gate=new();
        private readonly Task serving;
        private bool started;private int count;private double y;
        internal string Url {get;}
        internal bool Started {get{lock(gate)return started;}}
        internal ProbeState Snapshot {get{lock(gate)return new(count,y);}}
        internal ProbeServer()
        {
            int port;var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
            Url=$"http://127.0.0.1:{port}/";listener.Prefixes.Add(Url);listener.Start();serving=Task.Run(Serve);
        }
        private async Task Serve()
        {
            while(listener.IsListening) {
                HttpListenerContext context;
                try{context=await listener.GetContextAsync();}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}
                try {
                    var request=context.Request;string path=request.Url?.AbsolutePath??"/";
                    string text="ok";string type="text/plain; charset=utf-8";
                    if(path=="/"){text=Page;type="text/html; charset=utf-8";}
                    else if(path=="/start"){lock(gate)started=true;}
                    else if(path=="/state") {
                        if(int.TryParse(request.QueryString["count"],NumberStyles.Integer,CultureInfo.InvariantCulture,out int incoming)&&double.TryParse(request.QueryString["y"],NumberStyles.Float,CultureInfo.InvariantCulture,out double scrollY))
                            lock(gate)if(incoming>=count){count=incoming;y=scrollY;}
                    }else context.Response.StatusCode=404;
                    byte[] body=Encoding.UTF8.GetBytes(text);context.Response.ContentType=type;context.Response.ContentLength64=body.Length;context.Response.Headers["Cache-Control"]="no-store";
                    await context.Response.OutputStream.WriteAsync(body);
                }catch(HttpListenerException){}catch(IOException){}
                finally{context.Response.Close();}
            }
        }
        public void Dispose(){listener.Stop();listener.Close();try{serving.GetAwaiter().GetResult();}catch(HttpListenerException){}catch(ObjectDisposedException){}}
    }
    private sealed record ProbeState(int Count,double Y);
    private const string Page="""
<!doctype html><html><head><meta charset="utf-8"><title>Framelet Chromium Wheel Route Probe 20261020</title>
<style>*{box-sizing:border-box}html{background:#262a32;color:#f2f3f8;font:18px Arial,sans-serif;scroll-behavior:auto}body{margin:0;padding:105px 32px 900px}header{position:fixed;inset:0 0 auto;height:84px;padding:18px 32px;background:#17375b;z-index:2}button{font:inherit;margin-right:25px;padding:8px 18px}.row{height:79px;max-width:1050px;border-bottom:1px solid #70788b;padding:13px 20px}.row:nth-child(3n){background:#31496a}.row:nth-child(5n){border-left:6px solid #e1b75b}small{display:block;margin-top:8px;color:#c8d0e0}</style></head>
<body><header><button id="start">Start route test</button><span id="stats">wheel 0 / y 0</span></header><main id="content"></main>
<script>let count=0;const content=document.querySelector('#content');for(let n=0;n<220;n++){const e=document.createElement('section');e.className='row';e.innerHTML=`Row ${String(n).padStart(4,'0')} — Framelet real Chromium content ${n*7919+37}<small>Independent line ${n*6151+17}: fixed rectangle, continuous native wheel route</small>`;content.append(e)}
function report(){document.querySelector('#stats').textContent=`wheel ${count} / y ${window.scrollY}`;fetch(`/state?count=${count}&y=${window.scrollY}`,{cache:'no-store'}).catch(()=>{})}window.addEventListener('wheel',()=>{count++;report()},{passive:true});window.addEventListener('scroll',report,{passive:true});document.querySelector('#start').addEventListener('click',()=>{fetch('/start',{cache:'no-store'}).catch(()=>{});document.querySelector('#start').textContent='Running — use overlay when prompted';report()});report();</script></body></html>
""";
}
