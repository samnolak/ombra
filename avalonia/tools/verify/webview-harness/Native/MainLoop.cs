using System.Collections.Concurrent;

namespace OmpGui.WebViewHarness.Native;

/// <summary>
/// The process's AppKit main thread without any UI: <c>NSApplication</c> with activation policy <i>prohibited</i>
/// (no Dock icon, no menu bar, never activated), pumped by hand. Async continuations come back to the main thread
/// through <see cref="Context"/>; WebKit's delegate callbacks and <c>dispatch_async</c> on the main queue run inside
/// the pump.
/// </summary>
internal sealed class MainLoop
{
    private const long NSApplicationActivationPolicyProhibited = 2;
    private const ulong NSEventMaskAny = ulong.MaxValue;

    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly nint _app;
    private readonly nint _defaultMode;

    public MainLoop()
    {
        ObjC.LoadFrameworks();
        _app = ObjC.Send(ObjC.Cls("NSApplication"), "sharedApplication");
        // Before anything can show: no Dock icon, no menu bar, cannot become active.
        ObjC.SendVoidLong(_app, "setActivationPolicy:", NSApplicationActivationPolicyProhibited);
        _defaultMode = ObjC.Constant(ObjC.Foundation, "NSDefaultRunLoopMode");
        Context = new MainThreadContext(this);
    }

    public SynchronizationContext Context { get; }

    public nint App => _app;

    public long ActivationPolicy => ObjC.SendLong(_app, "activationPolicy");

    public bool IsActive => ObjC.SendBool(_app, "isActive");

    /// <summary>Runs the loop on the main thread until <paramref name="task"/> ends or <paramref name="timeout"/> passes.</summary>
    public bool RunUntil(Task task, TimeSpan timeout)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("The main loop must run on the thread that created it.");
        var deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) return false;
            Pump(TimeSpan.FromMilliseconds(10));
        }
        return true;
    }

    /// <summary>One turn: queued continuations, then AppKit events and the run loop for up to <paramref name="wait"/>.</summary>
    private void Pump(TimeSpan wait)
    {
        var pool = ObjC.PoolPush();
        try
        {
            while (_queue.TryDequeue(out var item)) item.Callback(item.State);
            var ev = NextEvent(DateWithInterval(wait.TotalSeconds));
            if (ev != 0) ObjC.SendVoid(_app, "sendEvent:", ev);
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    private static unsafe nint DateWithInterval(double seconds) =>
        ((delegate* unmanaged<nint, nint, double, nint>)ObjC.MsgSend)(ObjC.Cls("NSDate"), ObjC.Sel("dateWithTimeIntervalSinceNow:"), seconds);

    private unsafe nint NextEvent(nint until) =>
        ((delegate* unmanaged<nint, nint, ulong, nint, nint, byte, nint>)ObjC.MsgSend)(
            _app, ObjC.Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"), NSEventMaskAny, until, _defaultMode, 1);

    private sealed class MainThreadContext(MainLoop loop) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => loop._queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == loop._threadId)
            {
                d(state);
                return;
            }
            using var done = new ManualResetEventSlim();
            loop._queue.Enqueue((s => { try { d(s); } finally { done.Set(); } }, state));
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
