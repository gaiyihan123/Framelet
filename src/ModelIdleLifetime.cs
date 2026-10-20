using System;
using System.Threading;
using System.Threading.Tasks;

namespace QingCapture;

// Counts the real queued/native work, not the UI's cancellable wait. A stale
// timer must recheck its generation after obtaining the inference gate.
internal sealed class ModelIdleLifetime : IDisposable
{
    private readonly object sync=new();
    private readonly Timer timer;
    private readonly TimeSpan delay;
    private readonly Func<long,Task> release;
    private readonly Action<Exception> failure;
    private int requests;
    private long generation,lastCompleted;
    private bool disposed;
    internal ModelIdleLifetime(TimeSpan delay,Func<long,Task> release,Action<Exception> failure)
    {
        this.delay=delay;this.release=release;this.failure=failure;
        timer=new Timer(OnTimer,null,Timeout.InfiniteTimeSpan,Timeout.InfiniteTimeSpan);
    }
    internal IDisposable BeginRequest()
    {
        lock(sync){ObjectDisposedException.ThrowIf(disposed,this);requests++;generation++;timer.Change(Timeout.InfiniteTimeSpan,Timeout.InfiniteTimeSpan);}
        return new Request(this);
    }
    private void Complete()
    {
        lock(sync){if(disposed)return;if(--requests<0)throw new InvalidOperationException("Unbalanced model lifetime");generation++;if(requests==0){lastCompleted=Environment.TickCount64;timer.Change(delay,Timeout.InfiniteTimeSpan);}}
    }
    internal bool IsExpired(long expectedGeneration)
    {
        lock(sync)return !disposed&&requests==0&&generation==expectedGeneration&&Environment.TickCount64-lastCompleted>=delay.TotalMilliseconds;
    }
    internal long Generation {get {lock(sync)return generation;}}
    internal int PendingRequests {get {lock(sync)return requests;}}
    private void OnTimer(object? state)
    {
        long current;
        lock(sync){
            if(disposed||requests!=0)return;
            double remaining=delay.TotalMilliseconds-(Environment.TickCount64-lastCompleted);
            if(remaining>0){timer.Change(TimeSpan.FromMilliseconds(Math.Max(1,remaining)),Timeout.InfiniteTimeSpan);return;}
            current=generation;
        }
        _=Release(current);
    }
    private async Task Release(long expected)
    {
        try{await release(expected).ConfigureAwait(false);}catch(Exception ex){failure(ex);}
    }
    public void Dispose(){lock(sync){if(disposed)return;disposed=true;timer.Dispose();}}
    private sealed class Request : IDisposable
    {
        private ModelIdleLifetime? owner;
        internal Request(ModelIdleLifetime owner){this.owner=owner;}
        public void Dispose()=>Interlocked.Exchange(ref owner,null)?.Complete();
    }
}
