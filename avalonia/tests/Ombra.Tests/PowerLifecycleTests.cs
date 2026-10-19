using System.Diagnostics;
using System.Text.Json;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class PowerLifecycleTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    public void Displayed_run_thinking_tool_shell_and_subagent_durations_ignore_wall_clock_changes(int hours)
    {
        var clock = new ManualClock();
        var state = new ConversationState(clock) { SessionFile = "timing.jsonl" };
        var started = clock.GetUtcNow();
        state.SetPhase(SessionPhase.Running, started);
        state.Apply(Frame("""{"type":"tool_execution_start","toolCallId":"t","toolName":"read","args":{}}"""), started);
        var shell = state.AddShellCommand("echo timing", started);
        state.Apply(Frame("""{"type":"subagent_lifecycle","payload":{"id":"a","status":"started","agent":"task"}}"""), started);
        var assistant = new AssistantItem(1, "", "considering", true, null, null);
        var row = new AssistantRowViewModel(assistant, clock);
        var run = state.Snapshot();
        clock.Advance(TimeSpan.FromSeconds(65));
        clock.MoveUtc(TimeSpan.FromHours(hours));

        Assert.Equal(Loc.F("{0}m {1:00}s", 1, 5), MainViewModel.Elapsed(run));
        Assert.Equal(Loc.F("Thinking… {0}s", 65), row.ThinkingLabel);
        Assert.Equal(started, run.RunStartedAt);
        state.FinishShellCommand(shell, "ok", 0, false, clock.GetUtcNow());
        state.Apply(Frame("""{"type":"tool_execution_end","toolCallId":"t","toolName":"read","result":{"content":[]}}"""), clock.GetUtcNow());
        state.Apply(Frame("""{"type":"subagent_lifecycle","payload":{"id":"a","status":"completed","agent":"task"}}"""), clock.GetUtcNow());
        state.EndRun(false, clock.GetUtcNow());
        row.Update(assistant with { Text = "answer", Streaming = false });
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(Loc.F("Thought for {0}s", 65), row.ThinkingLabel);
        var done = state.Snapshot();
        Assert.All(done.Items.OfType<ToolItem>(), tool => Assert.Equal(TimeSpan.FromSeconds(65), tool.Took));
        Assert.Equal(TimeSpan.FromSeconds(65), Assert.Single(done.Items.OfType<TurnEndItem>()).Took);
        var tasks = new TasksViewModel();
        tasks.ApplySubagents(done.Subagents);
        Assert.Equal(Loc.F("{0}m {1:00}s", 1, 5), Assert.Single(tasks.Subagents).ElapsedText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86400000)]
    public void Subagent_progress_cannot_replace_live_monotonic_elapsed_with_a_remote_clock_jump(long remoteMilliseconds)
    {
        var clock = new ManualClock();
        var state = new ConversationState(clock);
        state.Apply(Frame("""{"type":"subagent_progress","payload":{"progress":{"id":"a","status":"running","durationMs":5000}}}"""), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(4));
        state.Apply(Frame(JsonSerializer.Serialize(new
        {
            type = "subagent_progress", payload = new { progress = new { id = "a", status = "running", durationMs = remoteMilliseconds } },
        })), clock.GetUtcNow());
        var tasks = new TasksViewModel();
        tasks.ApplySubagents(state.Snapshot().Subagents);
        Assert.Equal(Loc.F("{0}s", 9), Assert.Single(tasks.Subagents).ElapsedText);
        clock.Advance(TimeSpan.FromSeconds(1));
        tasks.Subagents[0].Tick();
        Assert.Equal(Loc.F("{0}s", 10), tasks.Subagents[0].ElapsedText);
    }

    [Fact]
    public async Task Resume_reconciles_background_chat_deduplicates_and_never_opens_read_only_history_or_resends_input()
    {
        var root = TestProcesses.TempDir("wake-host");
        var first = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(root));
        await using var host = new SessionHost(first);
        await first.StartAsync();
        var background = host.Open(new LaunchRequest(root));
        await background.StartAsync();
        var saved = Path.Combine(root, "readonly.jsonl");
        await File.WriteAllTextAsync(saved, JsonSerializer.Serialize(new
        {
            type = "session", version = 3, id = "readonly", cwd = root, timestamp = "2026-01-01T00:00:00Z",
        }) + "\n");
        var readOnly = host.Open(new LaunchRequest(root, saved) { ReadOnly = true });
        await readOnly.StartAsync();
        var readVersion = readOnly.Snapshot().Version;
        host.Activate(first);
        background.Mutate(s =>
        {
            s.SetPhase(SessionPhase.Running, DateTimeOffset.UtcNow);
            s.Enqueue(QueueKind.FollowUp, "recover manually", []);
            s.RecoverQueue();
            s.Enqueue(QueueKind.DeferredFollowUp, "do not auto-send after wake", []);
        });
        await Control(first, w => w.WriteBoolean("hold", true));
        await Control(background, w => w.WriteBoolean("hold", true));
        host.NotifySuspending();
        var resume = host.ReconcileAfterResumeAsync();
        Assert.Same(resume, host.ReconcileAfterResumeAsync());
        await Held(first);
        await Held(background);
        await Release(first);
        await Release(background);
        await resume.WaitAsync(Wait);
        Assert.Equal(SessionPhase.Ready, background.Snapshot().Phase);
        Assert.Equal(2, background.Snapshot().Queued.Count);
        Assert.All(background.Snapshot().Queued, queued => Assert.True(queued.Recovered));
        Assert.Equal(readVersion, readOnly.Snapshot().Version);
        Assert.True(readOnly.Snapshot().IsReadOnly);
        Assert.Null(readOnly.Connection);
        Assert.Same(resume, host.ReconcileAfterResumeAsync());
        foreach (var chat in new[] { first, background })
        {
            var counts = await Control(chat);
            Assert.Equal(2, counts.GetProperty("stateCalls").GetInt32());
            Assert.Equal(0, counts.GetProperty("inputCalls").GetInt32());
            Assert.Equal(0, counts.GetProperty("abortCalls").GetInt32());
        }
    }

    [Fact]
    public async Task Idle_chat_with_retained_engine_queue_stays_ready_after_wake()
    {
        await using var chat = new SessionController(TestProcesses.Fake("queue-recovery"));
        await chat.StartAsync();
        await chat.Connection!.RequestAsync("follow_up", w => w.WriteString("message", "held after stop"));
        var before = await chat.Connection.GetStateAsync();
        Assert.False(before.IsStreaming);
        Assert.Equal(1, before.QueuedMessageCount);
        Assert.Equal(SessionPhase.Ready, chat.Snapshot().Phase);

        await chat.ReconcileAfterResumeAsync();

        Assert.Equal(SessionPhase.Ready, chat.Snapshot().Phase);
        var after = await chat.Connection.GetStateAsync();
        Assert.Equal(1, after.QueuedMessageCount);
        Assert.False(after.IsStreaming);
        var calls = await Control(chat);
        Assert.Equal(1, calls.GetProperty("inputCalls").GetInt32());
        Assert.Equal(0, calls.GetProperty("abortCalls").GetInt32());
    }

    [Fact]
    public async Task Running_authoritative_state_stays_running_and_no_frame_watchdog_reports_failure_without_stopping()
    {
        var clock = new ManualClock();
        await using var chat = new SessionController(TestProcesses.Fake("normal"), clock: clock)
        { WakeQuietPeriod = TimeSpan.FromSeconds(30), WakeResponseTimeout = TimeSpan.FromSeconds(15) };
        await chat.StartAsync();
        chat.Mutate(s => s.SetPhase(SessionPhase.Running, clock.GetUtcNow()));
        await Control(chat, w => w.WriteBoolean("streaming", true));
        await chat.ReconcileAfterResumeAsync();
        Assert.Equal(SessionPhase.Running, chat.Snapshot().Phase);
        var initial = (await Control(chat)).GetProperty("stateCalls").GetInt32();
        await Control(chat, w => w.WriteBoolean("silent", true));
        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitControl(chat, counts => counts.GetProperty("stateCalls").GetInt32() > initial);
        clock.MoveUtc(TimeSpan.FromDays(-10));
        clock.Advance(TimeSpan.FromSeconds(15));
        var failed = await TestProcesses.Eventually(chat.Snapshot, s => s.DebugTail.Any(e => e.Type == "wake_error"), Wait, "actionable wake failure");
        Assert.Equal(SessionPhase.Running, failed.Phase);
        Assert.Contains(failed.Items.OfType<NoticeItem>(), n => n.Level == NoticeLevel.Error);
        var counts = await Control(chat);
        Assert.Equal(0, counts.GetProperty("inputCalls").GetInt32());
        Assert.Equal(0, counts.GetProperty("abortCalls").GetInt32());
        await Control(chat, w => w.WriteBoolean("streaming", false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_wake_state_cannot_cross_new_run_or_logical_session(bool changeSession)
    {
        await using var chat = new SessionController(TestProcesses.Fake("normal"));
        await chat.StartAsync();
        await Control(chat, w => w.WriteBoolean("hold", true));
        var refresh = chat.ReconcileAfterResumeAsync();
        await Held(chat);
        if (changeSession)
        {
            // A switch uses the same connection, but replaces its logical transcript epoch.
            await Control(chat, w => w.WriteBoolean("hold", false));
            await chat.NewSessionAsync();
            chat.Mutate(s => s.Model = "new/model");
        }
        else chat.Mutate(s => { s.BeginRun(DateTimeOffset.UtcNow); s.Model = "new/model"; });
        await Release(chat);
        await refresh.WaitAsync(Wait);
        Assert.Equal("new/model", chat.Snapshot().Model);
        Assert.DoesNotContain(chat.Snapshot().DebugTail, e => e.Type == "wake");
        if (!changeSession) Assert.Equal(SessionPhase.Running, chat.Snapshot().Phase);
    }

    [Fact]
    public async Task Delayed_wake_state_cannot_cross_engine_configuration_restart()
    {
        var chat = new SessionController(TestProcesses.Fake("normal"));
        await using var host = new SessionHost(chat);
        await chat.StartAsync();
        var connection = chat.Connection;
        await Control(chat, w => w.WriteBoolean("hold", true));
        var refresh = host.ReconcileAfterResumeAsync();
        await Held(chat);
        await host.RestartEngineAsync();
        await refresh.WaitAsync(Wait);
        Assert.NotSame(connection, chat.Connection);
        Assert.Equal(SessionPhase.Ready, chat.Snapshot().Phase);
        Assert.DoesNotContain(chat.Snapshot().DebugTail, e => e.Type is "wake" or "wake_error");
    }

    [Fact]
    public async Task Suspend_and_disposal_cancel_pending_reconciliation_without_late_application()
    {
        var chat = new SessionController(TestProcesses.Fake("normal"));
        await using var host = new SessionHost(chat);
        await chat.StartAsync();
        await Control(chat, w => w.WriteBoolean("hold", true));
        var resume = host.ReconcileAfterResumeAsync();
        await Held(chat);
        host.NotifySuspending();
        await resume.WaitAsync(Wait);
        await Release(chat);
        Assert.DoesNotContain(chat.Snapshot().DebugTail, e => e.Type is "wake" or "wake_error");
        await Control(chat, w => w.WriteBoolean("hold", true));
        var closingResume = host.ReconcileAfterResumeAsync();
        await Held(chat);
        await host.DisposeAsync();
        await closingResume.WaitAsync(Wait);
        var version = chat.Snapshot().Version;
        await host.ReconcileAfterResumeAsync();
        await chat.ReconcileAfterResumeAsync();
        Assert.Equal(version, chat.Snapshot().Version);
        Assert.DoesNotContain(chat.Snapshot().DebugTail, e => e.Type is "wake" or "wake_error");
    }

    private static RpcFrame Frame(string json)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement.Clone();
        return new RpcFrame(element.GetProperty("type").GetString()!, null, element, Stopwatch.GetTimestamp(), json.Length + 1);
    }

    private static async Task<JsonElement> Control(SessionController chat, Action<Utf8JsonWriter>? fields = null) =>
        (await chat.Connection!.RequestAsync("test_wake_control", fields)).Data!.Value;

    private static Task Release(SessionController chat) => Control(chat, w => { w.WriteBoolean("hold", false); w.WriteBoolean("release", true); });

    private static Task Held(SessionController chat) => WaitControl(chat, counts => counts.GetProperty("held").GetBoolean());

    private static async Task WaitControl(SessionController chat, Func<JsonElement, bool> ready)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready(await Control(chat)))
        {
            if (Stopwatch.GetElapsedTime(started) > Wait) throw new TimeoutException("The fake engine did not reach the wake barrier.");
            await Task.Delay(10);
        }
    }

    internal sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ClockTimer> _timers = [];
        private long _ticks;
        private DateTimeOffset _utc = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
        public void MoveUtc(TimeSpan amount) { lock (_gate) _utc += amount; }
        public void Advance(TimeSpan elapsed)
        {
            List<ClockTimer> ready;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                _utc += elapsed;
                ready = _timers.Where(t => t.Due <= _ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period < 0 ? long.MaxValue : _ticks + timer.Period;
            }
            foreach (var timer in ready) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        private sealed class ClockTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due = long.MaxValue;
            public long Period = -1;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (_disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                    Period = period == Timeout.InfiniteTimeSpan ? -1 : period.Ticks;
                    return true;
                }
            }
            public void Fire() { lock (owner._gate) if (_disposed) return; callback(state); }
            public void Dispose() { lock (owner._gate) { _disposed = true; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
