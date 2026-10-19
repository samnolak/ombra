using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class SharedEngineSettingsRaceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    // FakeOmp records PI_* variables from the actual process, not from the requested launch spec.
    private const string GenerationVariable = "PI_OMBRA_TEST_SETTINGS_GENERATION";

    [AvaloniaFact]
    public async Task New_chat_during_pending_settings_restart_never_faults_and_opens_in_latest_engine()
    {
        var fixture = new SettingsFixture();
        var vm = fixture.Vm;
        var busy = fixture.Session;
        LaunchGate? newLaunch = null;
        try
        {
            await busy.StartAsync().WaitAsync(Wait);
            vm.Apply(busy.Snapshot());
            var originalPid = busy.ProcessId;
            Assert.NotNull(originalPid);
            Assert.Equal("0", Assert.Single(fixture.Starts()).Generation);

            // As in SessionHostTests: a locally busy chat with no streaming timer that could end the run for us.
            busy.Mutate(state => state.SetPhase(SessionPhase.Running, DateTimeOffset.UtcNow));
            vm.Apply(busy.Snapshot());
            fixture.SaveGeneration("1");
            Assert.False(await vm.RestartEnginesAsync().WaitAsync(Wait));
            Assert.Equal(1, vm.PendingEngineRestarts);
            Assert.Equal(originalPid, busy.ProcessId);

            newLaunch = fixture.HoldNextLaunch();
            var opening = vm.NewChatCommand.ExecuteAsync(null);
            await Until(() => vm.Session != busy && vm.PendingEngineRestarts == 2,
                "the new chat is registered behind the pending settings restart");
            var opened = vm.Session;
            AssertHealthy(opened);
            Assert.Equal(SessionPhase.NotStarted, opened.Snapshot().Phase);
            Assert.Null(opened.Connection);
            Assert.False(opening.IsCompleted);
            Assert.False(newLaunch.Entered.Task.IsCompleted);
            Assert.Single(fixture.Requested);
            Assert.Equal(SessionPhase.Running, busy.Snapshot().Phase);
            Assert.Equal(originalPid, busy.ProcessId);
            Assert.Equal(2, vm.PendingEngineRestarts);

            // Admission waits before evaluating the factory: a waiting chat stays idle and cannot attach to the
            // old generation. The first replacement launch can proceed as soon as the busy peer becomes idle.
            newLaunch.Release.TrySetResult();
            busy.Mutate(state => state.SetPhase(SessionPhase.Ready, DateTimeOffset.UtcNow));
            vm.Apply(opened.Snapshot());
            await Until(() =>
            {
                AssertHealthy(busy);
                AssertHealthy(opened);
                return opening.IsCompleted && vm.PendingEngineRestarts == 0
                    && busy.Snapshot().Phase == SessionPhase.Ready && opened.Snapshot().Phase == SessionPhase.Ready
                    && busy.ProcessId != originalPid && busy.ProcessId == opened.ProcessId;
            }, "both chats opened in the replacement engine without a configuration fault");
            await opening.WaitAsync(Wait);

            Assert.Equal("1", fixture.Store.Load().Environment[GenerationVariable]);
            Assert.Contains(fixture.Requested, spec => spec.Environment![GenerationVariable] == "1");
            var latest = fixture.Starts().Where(start => start.Pid == opened.ProcessId).ToArray();
            Assert.Equal(2, latest.Length);
            Assert.All(latest, start => Assert.Equal("1", start.Generation));
            Assert.Equal(2, fixture.Starts().Select(start => start.Pid).Distinct().Count());
            Assert.False(opened.Snapshot().StartFailed);
        }
        finally
        {
            newLaunch?.Release.TrySetResult();
            await vm.DisposeAsync().AsTask().WaitAsync(Wait);
        }
    }

    [AvaloniaFact]
    public async Task Settings_saved_during_restart_apply_latest_generation_without_another_snapshot()
    {
        var fixture = new SettingsFixture();
        var vm = fixture.Vm;
        var session = fixture.Session;
        var continuations = new RestartContinuations();
        LaunchGate? firstLaunch = null;
        try
        {
            // Deliberately use the direct Apply pattern from DraftRecoveryTests, not OnWindowOpened:
            // no background UI pump can accidentally rescue a lost pending restart with a later snapshot.
            await session.StartAsync().WaitAsync(Wait);
            vm.Apply(session.Snapshot());
            var originalPid = session.ProcessId;
            Assert.Equal("0", Assert.Single(fixture.Starts()).Generation);

            fixture.SaveGeneration("1");
            firstLaunch = fixture.HoldNextLaunch();
            var restarting = continuations.Invoke(vm.RestartEnginesAsync);
            await Until(() => firstLaunch.Entered.Task.IsCompleted, "the first restart captured generation 1",
                () => continuations.RunOne());
            var captured = await firstLaunch.Entered.Task.WaitAsync(Wait);
            Assert.Equal("1", captured.Environment![GenerationVariable]);
            Assert.False(restarting.IsCompleted);

            // The first factory has captured its immutable spec and is still held when the next save arrives.
            fixture.SaveGeneration("2");
            Assert.False(await vm.RestartEnginesAsync().WaitAsync(Wait));
            Assert.Equal(1, vm.PendingEngineRestarts);
            firstLaunch.Release.TrySetResult();

            // Startup runs off-context. Do not release the host/VM completion continuation until Ready has
            // already been applied on the UI thread while _engineRestarting is still true.
            await Until(() => session.Snapshot().Phase == SessionPhase.Ready && session.ProcessId != originalPid,
                "generation 1 ready before its restart completion",
                () => continuations.RunOne(() => session.Snapshot().Phase == SessionPhase.Ready));
            Assert.False(restarting.IsCompleted);
            var intermediatePid = session.ProcessId;
            Assert.NotNull(intermediatePid);
            Assert.Equal("1", fixture.Starts().Single(start => start.Pid == intermediatePid).Generation);
            var applies = vm.UiApplies;
            vm.Apply(session.Snapshot());
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.UiApplies > applies);
            Assert.Equal(SessionPhase.Ready, vm.Phase);
            Assert.Equal(1, vm.PendingEngineRestarts);
            Assert.False(restarting.IsCompleted);

            continuations.Release();
            await restarting.WaitAsync(Wait);
            // No Apply, new user action, or synthesized state change below: completion must drain pending work.
            await Until(() =>
            {
                AssertHealthy(session);
                return vm.PendingEngineRestarts == 0 && session.Snapshot().Phase == SessionPhase.Ready
                    && session.ProcessId != originalPid && session.ProcessId != intermediatePid;
            }, "the second settings generation applied without a later snapshot");

            var starts = fixture.Starts();
            Assert.Equal(new[] { "0", "1", "2" }, starts.Select(start => start.Generation));
            Assert.Equal(3, starts.Select(start => start.Pid).Distinct().Count());
            Assert.Equal(session.ProcessId, starts[^1].Pid);
            Assert.Equal("2", fixture.Store.Load().Environment[GenerationVariable]);
            Assert.Equal(new[] { "0", "1", "2" },
                fixture.Requested.Select(spec => spec.Environment![GenerationVariable]));
        }
        finally
        {
            firstLaunch?.Release.TrySetResult();
            continuations.Release();
            await vm.DisposeAsync().AsTask().WaitAsync(Wait);
        }
    }

    private static void AssertHealthy(SessionController session)
    {
        var snapshot = session.Snapshot();
        Assert.True(snapshot.Phase != SessionPhase.Faulted, snapshot.LastError);
        Assert.False(snapshot.StartFailed);
    }

    private static async Task Until(Func<bool> condition, string what, Action? advance = null)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            advance?.Invoke();
            await Task.Delay(10);
        }
    }

    private sealed class LaunchGate
    {
        public TaskCompletionSource<OmpLaunchSpec> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RecordedStart(int Pid, string Generation);

    private sealed class SettingsFixture
    {
        private readonly string _root = TestProcesses.TempDir("shared-engine-settings-race");
        private readonly string _launchLog;
        private LaunchGate? _nextLaunch;

        public SettingsFixture()
        {
            _launchLog = Path.Combine(_root, "launches.jsonl");
            Store = new ClientSettingsStore(Path.Combine(_root, "ombra.local.json"));
            SaveGeneration("0");
            Session = new SessionController(LaunchAsync, new LaunchRequest(_root, ApprovalMode: "write"), readyTimeout: Wait);
            Vm = new MainViewModel(Session, new AppArgs(), settings: Store);
        }

        public ClientSettingsStore Store { get; }
        public SessionController Session { get; }
        public MainViewModel Vm { get; }
        public ConcurrentQueue<OmpLaunchSpec> Requested { get; } = new();

        public void SaveGeneration(string generation) => Store.Update(options => options with
        {
            Environment = new Dictionary<string, string?>
            {
                ["FAKE_SESSION_DIR"] = _root,
                ["FAKE_OMP_LAUNCH_LOG"] = _launchLog,
                [GenerationVariable] = generation,
            },
        });

        public LaunchGate HoldNextLaunch()
        {
            var gate = new LaunchGate();
            Assert.Null(Interlocked.CompareExchange(ref _nextLaunch, gate, null));
            return gate;
        }

        private async Task<OmpLaunchSpec> LaunchAsync(LaunchRequest request, CancellationToken ct)
        {
            // Mirrors the app's dynamic settings-backed factory, including the immutable environment snapshot.
            var spec = TestProcesses.FakeFactory("normal", _root)(request) with
            {
                Environment = Store.Load().Environment.ToDictionary(pair => pair.Key, pair => pair.Value),
            };
            Requested.Enqueue(spec);
            if (Interlocked.Exchange(ref _nextLaunch, null) is { } gate)
            {
                gate.Entered.TrySetResult(spec);
                await gate.Release.Task.WaitAsync(Wait, ct).ConfigureAwait(false);
            }
            return spec;
        }

        public RecordedStart[] Starts() => File.Exists(_launchLog)
            ? File.ReadAllLines(_launchLog).Select(line =>
            {
                var start = JsonNode.Parse(line)!;
                return new RecordedStart(start["pid"]!.GetValue<int>(), start["env"]![GenerationVariable]!.GetValue<string>());
            }).ToArray()
            : [];
    }

    /// <summary>Holds only the restart's captured continuations; Avalonia's real dispatcher remains runnable.</summary>
    private sealed class RestartContinuations : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private bool _released;

        public Task<bool> Invoke(Func<Task<bool>> start)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return start(); }
            finally { SetSynchronizationContext(previous); }
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_pending)
            {
                if (!_released)
                {
                    _pending.Enqueue((callback, state));
                    return;
                }
            }
            Dispatcher.UIThread.Post(() => Execute(callback, state));
        }

        public void RunOne(Func<bool>? hold = null)
        {
            (SendOrPostCallback Callback, object? State) next;
            lock (_pending)
            {
                // Check after observing the queued continuation: its preceding Ready mutation is then visible.
                if (_pending.Count == 0 || hold?.Invoke() == true) return;
                next = _pending.Dequeue();
            }
            Execute(next.Callback, next.State);
        }

        public void Release()
        {
            lock (_pending)
            {
                _released = true;
                while (_pending.TryDequeue(out var next))
                {
                    var callback = next;
                    Dispatcher.UIThread.Post(() => Execute(callback.Callback, callback.State));
                }
            }
        }

        private void Execute(SendOrPostCallback callback, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        }
    }
}
