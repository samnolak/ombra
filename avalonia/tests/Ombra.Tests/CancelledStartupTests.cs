using System.Reflection;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class CancelledStartupTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Cancelled_startup_pump_cannot_fault_or_replay_into_an_immediate_retry()
    {
        using var clock = new PausedPumpClock();
        using var cancellation = new CancellationTokenSource();
        var firstLaunch = TestProcesses.Fake("history-live-start") with
        {
            Environment = new Dictionary<string, string?>
            {
                ["FAKE_SESSION_DIR"] = TestProcesses.TempDir("cancelled-startup"),
            },
        };
        // Only logical-session arguments change: both attempts belong to the same immutable root engine.
        var replacementLaunch = firstLaunch with { SessionArguments = ["normal"] };
        var launches = 0;
        var controller = new SessionController(
            _ => Interlocked.Increment(ref launches) == 1 ? firstLaunch : replacementLaunch,
            new LaunchRequest(), readyTimeout: Wait, clock: clock);
        Task? oldPump = null;
        try
        {
            var starting = controller.StartAsync(cancellation.Token);
            var held = await TestProcesses.Eventually(controller.Snapshot,
                snapshot => snapshot.DebugTail.Any(entry => entry.Type == "history_catalog_held"),
                Wait, "startup waits at the history catalog barrier after creating its pump");
            Assert.False(starting.IsCompleted);
            Assert.Equal(SessionPhase.Running, held.Phase);
            Assert.Equal("new streaming user", Assert.Single(held.Items.OfType<UserItem>()).Text);
            var oldConnection = Assert.IsType<Ombra.Rpc.RpcConnection>(controller.Connection);
            var processId = controller.ProcessId;
            Assert.NotNull(processId);

            // Observe the actual pump, without replacing it or adding a production synchronization hook.
            var pumpField = typeof(SessionController).GetField("_pump", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(pumpField);
            oldPump = Assert.IsAssignableFrom<Task>(pumpField.GetValue(controller));

            // The catalog request is still held, so startup cannot complete. With that stream now quiet,
            // the next clock read is PumpAsync's pre-lock timestamp for history_finish_start's message_end.
            // Park there: cancellation can still take the state lock and dispose the logical connection,
            // while the old message_end and agent_end cannot be folded until the test releases them.
            clock.Arm();
            var finishOldRun = oldConnection.RequestAsync("history_finish_start", timeout: Wait);
            await clock.Parked.WaitAsync(Wait);
            Assert.True((await finishOldRun.WaitAsync(Wait)).Success);
            Assert.False(oldPump.IsCompleted);

            cancellation.Cancel();
            var retry = StartPrivateReplacementAsync(controller);
            try
            {
                using var watchdog = new CancellationTokenSource();
                var deadline = Task.Delay(Wait, watchdog.Token);
                var completed = await Task.WhenAny(retry, deadline);
                watchdog.Cancel();
                if (completed == retry)
                {
                    // On the regression path the replacement is fully Ready, with its own completed turn,
                    // before the old pump is allowed to resume. No sleep arranges that ordering.
                    await retry;
                    Assert.False(oldPump.IsCompleted);
                }
                else
                {
                    // Correct cleanup may await the parked pump while retaining the lifecycle semaphore.
                    // Only that path needs the watchdog to release it. Never turn a slow, already-cancelled
                    // startup/retry into a passing test by releasing the old frames before replacement.
                    Assert.False(starting.IsCompleted,
                        "Cancelled startup released its lifecycle before the old pump drained, but retry did not reach Ready while the pump was parked.");
                }
            }
            finally
            {
                clock.Release();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(Wait));
            var replacement = await retry.WaitAsync(Wait);
            await oldPump.WaitAsync(Wait);

            // Check after the captured old pump really finished, not merely after the new startup returned.
            var afterDrain = controller.Snapshot();
            Assert.Equal(SessionPhase.Ready, afterDrain.Phase);
            Assert.Null(afterDrain.LastError);
            Assert.Equal(replacement.Items, afterDrain.Items);
            Assert.Equal("replacement private input", Assert.Single(afterDrain.Items.OfType<UserItem>()).Text);
            Assert.DoesNotContain(afterDrain.Items.OfType<AssistantItem>(), item => item.Text == "streaming response");
            Assert.Equal(replacement.MessagesCompleted, afterDrain.MessagesCompleted);
            Assert.Equal(2, afterDrain.MessagesCompleted); // One drained old message and one replacement message.
            Assert.Equal("fake/model", afterDrain.Model);
            Assert.NotSame(oldConnection, controller.Connection);
            Assert.NotNull(oldConnection.CloseReason);
            Assert.Null(controller.Connection!.CloseReason);
            Assert.Equal(processId, controller.ProcessId);
            Assert.Equal(2, Volatile.Read(ref launches));
        }
        finally
        {
            // Release before disposal even when a barrier/assertion fails; neither pump nor lifecycle may leak.
            clock.Release();
            cancellation.Cancel();
            await controller.DisposeAsync().AsTask().WaitAsync(Wait);
            if (oldPump is not null) await oldPump.WaitAsync(Wait);
        }
    }

    private static async Task<SessionSnapshot> StartPrivateReplacementAsync(SessionController controller)
    {
        // The caller bounds this operation both before and after releasing the old pump. A separate startup
        // deadline here would race the watchdog when correct cleanup deliberately waits for that release.
        await controller.StartAsync();
        var ready = controller.Snapshot();
        Assert.Equal(SessionPhase.Ready, ready.Phase);
        Assert.Empty(ready.Items);
        Assert.True(await controller.PromptAsync("replacement private input", []).WaitAsync(Wait));
        return await TestProcesses.Eventually(controller.Snapshot,
            // This diagnostic counter survives transcript resets. Correct cleanup has already counted the old
            // message before Ready; only the replacement's increment belongs to the new turn.
            snapshot => snapshot.Phase == SessionPhase.Ready && snapshot.MessagesCompleted == ready.MessagesCompleted + 1,
            Wait, "replacement has its own completed private turn");
    }

    private sealed class PausedPumpClock : TimeProvider, IDisposable
    {
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(false);
        private int _armed;

        public Task Parked => _parked.Task;
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public void Release() => _release.Set();

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _parked.TrySetResult();
                if (!_release.Wait(Wait + Wait))
                    throw new TimeoutException("The test did not release the cancelled startup pump.");
            }
            return TimeProvider.System.GetUtcNow();
        }

        public void Dispose() => _release.Dispose();
    }
}
