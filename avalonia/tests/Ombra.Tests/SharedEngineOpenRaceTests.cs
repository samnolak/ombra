using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class SharedEngineOpenRaceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private sealed class StopBarrierClock : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _thread;
        private int _armed;

        public Task Entered => _entered.Task;
        public void ParkNextReadOnThisThread()
        {
            _thread = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _armed, 1);
        }

        public void Release() => _release.Set();

        public override DateTimeOffset GetUtcNow()
        {
            if (Environment.CurrentManagedThreadId == _thread && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _entered.TrySetResult();
                if (!_release.Wait(Deadline)) throw new TimeoutException("The test did not release the coordinated-close barrier.");
            }
            return TimeProvider.System.GetUtcNow();
        }

        public void Dispose() => _release.Dispose();
    }

    [Fact]
    public async Task A_chat_opened_during_restart_never_attaches_to_the_old_engine_generation()
    {
        using var clock = new StopBarrierClock();
        var factory = TestProcesses.FakeFactory("normal", TestProcesses.TempDir("restart-open-sessions"));
        var first = new SessionController(factory, new LaunchRequest(TestProcesses.TempDir("restart-open-first")), clock: clock);
        await using var host = new SessionHost(first);
        await first.StartAsync();
        var observer = host.Open(new LaunchRequest(TestProcesses.TempDir("restart-open-observer")));
        await observer.StartAsync();
        var oldPid = first.ProcessId;
        var oldObserver = observer.Connection!;

        // StopAsync reads the clock only after RestartEngineAsync has captured its chat set and checked idleness.
        // Park that exact thread before the first logical close. Existing RPC traffic on the observer remains live.
        var restarting = Task.Run(() =>
        {
            clock.ParkNextReadOnThisThread();
            return host.RestartEngineAsync();
        });
        SessionController? late = null;
        Task? startingLate = null;
        try
        {
            await clock.Entered.WaitAsync(Deadline);
            late = host.Open(new LaunchRequest(TestProcesses.TempDir("restart-open-late")));
            startingLate = late.StartAsync();

            // Both requests use the same ordered root writer. This response fences any ungated engine_open above;
            // unlike a delay, it proves whether the old host accepted that late chat while restart was parked.
            var inspected = await oldObserver.RequestAsync("test_engine_session_keys").WaitAsync(Deadline);
            Assert.True(inspected.Success);
            Assert.Equal(2, inspected.Data!.Value.GetProperty("sessionKeys").GetArrayLength());
            Assert.False(startingLate.IsCompleted);
            Assert.Equal(SessionPhase.NotStarted, late.Snapshot().Phase);
            Assert.Null(late.Connection);
        }
        finally
        {
            clock.Release();
            await restarting.WaitAsync(Deadline);
            if (startingLate is not null) await startingLate.WaitAsync(Deadline);
        }

        Assert.NotNull(late);
        Assert.Equal(SessionPhase.Ready, late.Snapshot().Phase);
        Assert.Equal(first.ProcessId, late.ProcessId);
        Assert.NotEqual(oldPid, late.ProcessId);
        Assert.True((await late.ProbeAsync()).Success);
        Assert.DoesNotContain(late.Snapshot().DebugTail, entry => entry.Type == "error");
    }
}
