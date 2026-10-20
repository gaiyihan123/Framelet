using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

// Pure synthetic concurrency tests: no Python, native model or application UI.
internal static class ModelIdleLifetimeAcceptance
{
    private static readonly TimeSpan IdleDelay=TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Bound=TimeSpan.FromSeconds(2);
    public static List<string> Run()=>RunAsync().GetAwaiter().GetResult();

    private static async Task<List<string>> RunAsync()
    {
        var lines=new List<string>();
        void Check(bool ok,string text) {
            if(!ok)throw new InvalidOperationException("FAIL: "+text);
            lines.Add("PASS: "+text);
        }
        await LeaseAccounting(Check).ConfigureAwait(false);
        await StaleGenerationAndGate(Check).ConfigureAwait(false);
        await DisposedCallback(Check).ConfigureAwait(false);
        await CallbackFailure(Check).ConfigureAwait(false);
        return lines;
    }
    private static TaskCompletionSource<bool> Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task)=>task.WaitAsync(Bound);

    private static async Task LeaseAccounting(Action<bool,string> check)
    {
        using var gate=new SemaphoreSlim(1,1);
        var released=Signal();var nativeEntered=Signal();var nativeFinish=Signal();
        int cleanup=0,failures=0;
        // Isolate the lease-only check so it cannot arm a cleanup timer for
        // the subsequent active/queued-native scenario between setup steps.
        using(var accounting=new ModelIdleLifetime(IdleDelay,_=>Task.CompletedTask,_=>Interlocked.Increment(ref failures))) {
            var first=accounting.BeginRequest();
            var second=accounting.BeginRequest();
            check(accounting.PendingRequests==2,"idle lifetime counts every outstanding request lease");
            first.Dispose();first.Dispose();
            check(accounting.PendingRequests==1,"disposing a request lease twice decrements exactly once");
            second.Dispose();second.Dispose();
            check(accounting.PendingRequests==0,"the final idempotent lease completion balances request accounting");
        }
        ModelIdleLifetime? lifetime=null;
        using(lifetime=new ModelIdleLifetime(IdleDelay,async expected=> {
            await gate.WaitAsync().ConfigureAwait(false);
            bool didCleanup=false;
            try {
                if(lifetime!.IsExpired(expected)) {
                    Interlocked.Increment(ref cleanup);didCleanup=true;
                }
            } finally {gate.Release();if(didCleanup)released.TrySetResult(true);}
        },_=>Interlocked.Increment(ref failures))) {
            async Task NativeRequest()
            {
                using var lease=lifetime.BeginRequest();
                await gate.WaitAsync().ConfigureAwait(false);
                try {nativeEntered.TrySetResult(true);await nativeFinish.Task.ConfigureAwait(false);}
                finally {gate.Release();}
            }
            async Task QueuedRequest(CancellationToken cancellation)
            {
                using var lease=lifetime.BeginRequest();
                await gate.WaitAsync(cancellation).ConfigureAwait(false);
                try {cancellation.ThrowIfCancellationRequested();}
                finally {gate.Release();}
            }
            Task native=NativeRequest();
            using var queuedStop=new CancellationTokenSource();
            Task queued=QueuedRequest(queuedStop.Token);
            try {
                await Await(nativeEntered.Task).ConfigureAwait(false);
                check(lifetime.PendingRequests==2,"queued and active native work both keep the model lifetime busy");
                queuedStop.Cancel();
                bool cancelled=false;
                try {await Await(queued).ConfigureAwait(false);}
                catch(OperationCanceledException) {cancelled=true;}
                check(cancelled&&lifetime.PendingRequests==1,"cancelling queued work releases only its own lease, not active native work");
                using var uiStop=new CancellationTokenSource();
                Task uiWait=native.WaitAsync(uiStop.Token);uiStop.Cancel();
                bool uiCancelled=false;
                try {await Await(uiWait).ConfigureAwait(false);}
                catch(OperationCanceledException) {uiCancelled=true;}
                check(uiCancelled&&!native.IsCompleted&&lifetime.PendingRequests==1,"cancelling the UI wait does not end the still-running native request lease");
                await Task.Delay(TimeSpan.FromMilliseconds(120)).ConfigureAwait(false);
                check(Volatile.Read(ref cleanup)==0,"idle timer never releases while an actual native request remains active");
                nativeFinish.TrySetResult(true);
                await Await(native).ConfigureAwait(false);
                await Await(released.Task).ConfigureAwait(false);
                check(lifetime.PendingRequests==0&&Volatile.Read(ref cleanup)==1,"idle cleanup starts after the last real request completes, not after a cancelled UI wait");
                check(Volatile.Read(ref failures)==0,"request-accounting and active-work test has no timer callback failures");
            } finally {
                nativeFinish.TrySetResult(true);queuedStop.Cancel();
                await Await(native).ConfigureAwait(false);
                try {await Await(queued).ConfigureAwait(false);}catch(OperationCanceledException) { }
            }
        }
    }

    private static async Task StaleGenerationAndGate(Action<bool,string> check)
    {
        using var gate=new SemaphoreSlim(1,1);
        await gate.WaitAsync().ConfigureAwait(false);
        bool gateHeld=true;
        var firstCallback=Signal();var firstFinished=Signal();var freshCleanup=Signal();
        long oldGeneration=0;int callbacks=0,cleanup=0,failures=0;
        ModelIdleLifetime? lifetime=null;
        using(lifetime=new ModelIdleLifetime(IdleDelay,async expected=> {
            bool first=Interlocked.Increment(ref callbacks)==1;
            if(first){Interlocked.Exchange(ref oldGeneration,expected);firstCallback.TrySetResult(true);}
            await gate.WaitAsync().ConfigureAwait(false);
            bool didCleanup=false;
            try {
                // This order matches production: gate first, expiry check next.
                if(lifetime!.IsExpired(expected)) {
                    Interlocked.Increment(ref cleanup);didCleanup=true;
                }
            } finally {gate.Release();if(first)firstFinished.TrySetResult(true);if(didCleanup)freshCleanup.TrySetResult(true);}
        },_=>Interlocked.Increment(ref failures))) {
            IDisposable? renewed=null;
            try {
                lifetime.BeginRequest().Dispose();
                await Await(firstCallback.Task).ConfigureAwait(false);
                check(Volatile.Read(ref cleanup)==0,"idle release waits on the same gate that protects native inference");
                renewed=lifetime.BeginRequest();
                check(lifetime.Generation!=Interlocked.Read(ref oldGeneration)&&!lifetime.IsExpired(Interlocked.Read(ref oldGeneration)),"new request generation invalidates a timer callback already waiting for the gate");
                gate.Release();gateHeld=false;
                await Await(firstFinished.Task).ConfigureAwait(false);
                check(Volatile.Read(ref cleanup)==0,"stale callback rechecks expiry after taking the gate and leaves the new model untouched");
                renewed.Dispose();renewed=null;
                await Await(freshCleanup.Task).ConfigureAwait(false);
                check(Volatile.Read(ref cleanup)==1,"the renewed request receives its own later idle cleanup");
                check(Volatile.Read(ref failures)==0,"stale-generation and serialized-release test has no timer callback failures");
            } finally {
                renewed?.Dispose();
                if(gateHeld)gate.Release();
                if(firstCallback.Task.IsCompleted)await Await(firstFinished.Task).ConfigureAwait(false);
            }
        }
    }

    private static async Task DisposedCallback(Action<bool,string> check)
    {
        using var gate=new SemaphoreSlim(1,1);
        await gate.WaitAsync().ConfigureAwait(false);
        bool gateHeld=true;
        var entered=Signal();var finished=Signal();
        int callbacks=0,cleanup=0,failures=0;
        ModelIdleLifetime? lifetime=null;
        using(lifetime=new ModelIdleLifetime(IdleDelay,async expected=> {
            Interlocked.Increment(ref callbacks);entered.TrySetResult(true);
            await gate.WaitAsync().ConfigureAwait(false);
            try {if(lifetime!.IsExpired(expected))Interlocked.Increment(ref cleanup);}
            finally {gate.Release();finished.TrySetResult(true);}
        },_=>Interlocked.Increment(ref failures))) {
            try {
                lifetime.BeginRequest().Dispose();
                await Await(entered.Task).ConfigureAwait(false);
                lifetime.Dispose();lifetime.Dispose();
                check(!lifetime.IsExpired(lifetime.Generation),"disposing the lifetime invalidates outstanding cleanup callbacks");
                bool rejected=false;
                try {lifetime.BeginRequest().Dispose();}
                catch(ObjectDisposedException) {rejected=true;}
                check(rejected,"a disposed lifetime refuses new request leases");
                gate.Release();gateHeld=false;
                await Await(finished.Task).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(120)).ConfigureAwait(false);
                check(Volatile.Read(ref cleanup)==0&&Volatile.Read(ref callbacks)==1,"a callback queued before disposal does not release models or schedule more cleanup after disposal");
                check(Volatile.Read(ref failures)==0,"disposed-lifetime test has no timer callback failures");
            } finally {
                lifetime.Dispose();
                if(gateHeld)gate.Release();
                if(entered.Task.IsCompleted)await Await(finished.Task).ConfigureAwait(false);
            }
        }
    }

    private static async Task CallbackFailure(Action<bool,string> check)
    {
        var observed=Signal();int failures=0;
        using var lifetime=new ModelIdleLifetime(IdleDelay,_=>Task.FromException(new InvalidOperationException("synthetic release failure")),ex=> {
            if(ex.Message=="synthetic release failure")Interlocked.Increment(ref failures);
            observed.TrySetResult(true);
        });
        lifetime.BeginRequest().Dispose();
        await Await(observed.Task).ConfigureAwait(false);
        check(Volatile.Read(ref failures)==1,"a cleanup failure is reported through the failure callback instead of being silently lost");
    }
}
