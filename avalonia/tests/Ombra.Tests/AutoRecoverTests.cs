using Ombra.ClientCore;
using Ombra.Rpc;
using ManualClock = Ombra.Tests.PowerLifecycleTests.ManualClock;

namespace Ombra.Tests;

/// <summary>Automatic restart after an unexpected engine exit (PARITY_ROADMAP O4), on an injected clock: nothing
/// restarts before the backoff has elapsed on that clock, so every countdown below is deterministic.</summary>
public sealed class AutoRecoverTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    /// <summary>A short limit keeps the limit test small: 0.5, 1 and 2 s.</summary>
    private static readonly AutoRecoverPolicy Policy = new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(30), 3, TimeSpan.FromMinutes(2));

    internal sealed class Fixture
    {
        private readonly Dictionary<string, string?> _environment;
        public string Root { get; }
        public ManualClock Clock { get; } = new();

        public Fixture(string name)
        {
            Root = TestProcesses.TempDir(name);
            _environment = new Dictionary<string, string?>
            {
                ["FAKE_SESSION_DIR"] = Root,
                ["FAKE_OMP_ENGINE_LOG"] = Path.Combine(Root, "engines.jsonl"),
                ["FAKE_OMP_PROMPT_LOG"] = Path.Combine(Root, "inputs.log"),
                ["FAKE_OMP_CRASH_ON_START"] = Path.Combine(Root, "crash-on-start"),
                ["FAKE_OMP_HANG_ON_START"] = Path.Combine(Root, "hang-on-start"),
            };
        }

        public OmpLaunchSpec Launch(LaunchRequest request)
        {
            var spec = TestProcesses.Fake("multi");
            return spec with
            {
                SessionArguments = [.. spec.SessionArguments,
                    .. (request.ResumeSessionFile is { } resume ? new[] { "--resume", resume } : Array.Empty<string>())],
                WorkingDirectory = request.WorkingDirectory ?? Root,
                Environment = _environment,
            };
        }

        public SessionController Controller(AutoRecoverPolicy? policy = null) =>
            new(Launch, new LaunchRequest(Root), clock: Clock) { AutoRecover = policy ?? Policy };

        /// <summary>Physical engine processes started so far (including ones that crashed while starting).</summary>
        public int EngineStarts => Lines("engines.jsonl").Length;
        /// <summary>Every prompt, steer and follow-up that reached any engine process.</summary>
        public string[] Inputs => Lines("inputs.log");
        /// <summary>While on, every engine start exits before it is ready.</summary>
        public bool CrashOnStart { set => Toggle("crash-on-start", value); }
        /// <summary>While on, every engine start never becomes ready (only the ready timeout or a cancel ends it).</summary>
        public bool HangOnStart { set => Toggle("hang-on-start", value); }

        private void Toggle(string name, bool on)
        {
            var path = Path.Combine(Root, name);
            if (on) File.WriteAllText(path, "");
            else File.Delete(path);
        }

        private string[] Lines(string name) =>
            File.Exists(Path.Combine(Root, name)) ? File.ReadAllLines(Path.Combine(Root, name)) : [];
    }

    /// <summary>The whole shared engine exits at once, like a runtime crash (every chat on it loses its session).</summary>
    internal static async Task CrashEngineAsync(SessionController chat)
    {
        try { await chat.Connection!.RequestAsync("test_crash"); }
        catch (RpcConnectionClosedException) { }
    }

    internal static Task<SessionSnapshot> CountdownAsync(SessionController chat, int attempt) =>
        TestProcesses.Eventually(chat.Snapshot, s => s.Phase == SessionPhase.Faulted && s.AutoRecover?.Attempt == attempt,
            Wait, $"automatic restart {attempt} scheduled");

    private static Task<SessionSnapshot> ReadyAsync(SessionController chat) =>
        TestProcesses.Eventually(chat.Snapshot, s => s.Phase == SessionPhase.Ready, Wait, "ready");

    private static async Task<string> CompleteTurnAsync(SessionController chat, string text)
    {
        await chat.PromptAsync(text);
        var done = await TestProcesses.Eventually(chat.Snapshot, s => s.Phase == SessionPhase.Ready && s.RunsEnded > 0
            && s.SessionFile is { } file && File.Exists(file) && s.Items.OfType<UserItem>().Any(u => u.Text == text && u.Confirmed),
            Wait, "turn persisted");
        return done.SessionFile!;
    }

    /// <summary>Nothing happens in real time either: the next step is due only on the injected clock.</summary>
    private static async Task AssertStaysAsync(SessionController chat, Fixture fixture, int engineStarts)
    {
        await Task.Delay(300);
        Assert.Equal(SessionPhase.Faulted, chat.Snapshot().Phase);
        Assert.Equal(engineStarts, fixture.EngineStarts);
    }

    [Fact]
    public async Task Crash_reopens_the_same_session_after_the_backoff_without_resending_the_run_or_the_queue()
    {
        var fixture = new Fixture("auto-recover-restart");
        await using var chat = fixture.Controller();
        await chat.StartAsync();
        var file = await CompleteTurnAsync(chat, "first turn");
        await chat.PromptAsync("FOREVER in flight");
        await TestProcesses.Eventually(chat.Snapshot, s => s.Phase == SessionPhase.Running
            && s.Items.OfType<UserItem>().Any(u => u.Text == "FOREVER in flight" && u.Confirmed), Wait, "run in flight");
        Assert.True(await chat.QueueAsync(QueueKind.FollowUp, "queued while running", []));
        var pid = chat.ProcessId;

        await CrashEngineAsync(chat);
        var countdown = (await CountdownAsync(chat, 1)).AutoRecover!;
        Assert.Equal(3, countdown.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(0.5), countdown.Delay);
        Assert.True(Assert.Single(chat.Snapshot().Queued).Recovered);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await AssertStaysAsync(chat, fixture, engineStarts: 1);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
        var back = await ReadyAsync(chat);

        Assert.Null(back.AutoRecover);
        Assert.Equal(file, back.SessionFile);
        Assert.NotEqual(pid, chat.ProcessId);
        Assert.Equal(2, fixture.EngineStarts);
        Assert.Contains(back.Items, item => item is UserItem { Text: "first turn" });
        Assert.DoesNotContain(back.Items, item => item is AssistantItem { Streaming: true });
        // The interrupted run and the queued follow-up reached omp once, before the crash; neither is sent again.
        Assert.Single(fixture.Inputs, line => line == "prompt FOREVER in flight");
        Assert.Single(fixture.Inputs, line => line == "follow_up queued while running");
        Assert.True(Assert.Single(back.Queued).Recovered);
        await Task.Delay(300);
        Assert.Equal(SessionPhase.Ready, chat.Snapshot().Phase);
        Assert.Equal(3, fixture.Inputs.Length);
    }

    [Fact]
    public async Task Repeated_crashes_back_off_and_stop_at_the_limit_leaving_manual_recovery()
    {
        var fixture = new Fixture("auto-recover-limit");
        await using var chat = fixture.Controller();
        await chat.StartAsync();
        var file = await CompleteTurnAsync(chat, "keep me");
        fixture.CrashOnStart = true;
        await CrashEngineAsync(chat);

        Assert.Equal(TimeSpan.FromSeconds(0.5), (await CountdownAsync(chat, 1)).AutoRecover!.Delay);
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(TimeSpan.FromSeconds(1), (await CountdownAsync(chat, 2)).AutoRecover!.Delay);
        Assert.Equal(2, fixture.EngineStarts);
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.9));
        await AssertStaysAsync(chat, fixture, engineStarts: 2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(TimeSpan.FromSeconds(2), (await CountdownAsync(chat, 3)).AutoRecover!.Delay);
        Assert.Equal(3, fixture.EngineStarts);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));

        var gaveUp = await TestProcesses.Eventually(chat.Snapshot, s => s.Phase == SessionPhase.Faulted && s.AutoRecover is null
            && s.Items.OfType<NoticeItem>().Any(n => n.Text.Contains("no longer restarted automatically", StringComparison.Ordinal)),
            Wait, "automatic restart gave up");
        Assert.Equal(4, fixture.EngineStarts);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await AssertStaysAsync(chat, fixture, engineStarts: 4);
        Assert.Null(gaveUp.AutoRecover);

        // Manual recovery is still offered and works once the engine can start again.
        fixture.CrashOnStart = false;
        await chat.RecoverAsync();
        var back = await ReadyAsync(chat);
        Assert.Equal(file, back.SessionFile);
        Assert.Contains(back.Items, item => item is UserItem { Text: "keep me" });
        Assert.Equal(5, fixture.EngineStarts);
    }

    [Fact]
    public async Task A_stable_period_resets_the_attempt_count()
    {
        var fixture = new Fixture("auto-recover-stable");
        await using var chat = fixture.Controller();
        await chat.StartAsync();
        await CrashEngineAsync(chat);
        await CountdownAsync(chat, 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        await ReadyAsync(chat);

        // Up only briefly: the next crash is the next attempt, with the longer wait.
        await CrashEngineAsync(chat);
        Assert.Equal(TimeSpan.FromSeconds(1), (await CountdownAsync(chat, 2)).AutoRecover!.Delay);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await ReadyAsync(chat);

        // Up for the stable period: the count starts again.
        fixture.Clock.Advance(Policy.StablePeriod);
        await CrashEngineAsync(chat);
        Assert.Equal(TimeSpan.FromSeconds(0.5), (await CountdownAsync(chat, 1)).AutoRecover!.Delay);
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        await ReadyAsync(chat);
        Assert.Equal(4, fixture.EngineStarts);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("cancel")]
    [InlineData("close")]
    public async Task An_explicit_stop_cancel_or_close_during_the_countdown_never_restarts(string action)
    {
        var fixture = new Fixture("auto-recover-explicit");
        var chat = fixture.Controller();
        try
        {
            await chat.StartAsync();
            await CrashEngineAsync(chat);
            await CountdownAsync(chat, 1);
            switch (action)
            {
                case "stop": await chat.StopAsync(); break;
                case "cancel": chat.CancelAutoRecover(); break;
                case "close": await chat.DisposeAsync(); break;
            }
            Assert.Null(chat.Snapshot().AutoRecover);
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            await AssertStaysAsync(chat, fixture, engineStarts: 1);
            Assert.Null(chat.Snapshot().AutoRecover);
            if (action == "close") return;

            // Manual recovery remains the way back.
            await chat.RecoverAsync();
            await ReadyAsync(chat);
            Assert.Equal(2, fixture.EngineStarts);
        }
        finally { await chat.DisposeAsync(); }
    }

    [Fact]
    public async Task A_requested_stop_is_never_recovered()
    {
        var fixture = new Fixture("auto-recover-stop");
        await using var chat = fixture.Controller();
        await chat.StartAsync();
        await chat.StopAsync();
        Assert.Equal(SessionPhase.Stopped, chat.Snapshot().Phase);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);
        Assert.Equal(SessionPhase.Stopped, chat.Snapshot().Phase);
        Assert.Null(chat.Snapshot().AutoRecover);
        Assert.Equal(1, fixture.EngineStarts);
    }

    [Fact]
    public async Task Two_chats_on_the_shared_engine_come_back_with_one_process_restart()
    {
        var fixture = new Fixture("auto-recover-shared");
        var a = fixture.Controller();
        await using var host = new SessionHost(a);
        var b = host.Open(new LaunchRequest(fixture.Root));
        await a.StartAsync();
        await b.StartAsync();
        var fileA = await CompleteTurnAsync(a, "chat a");
        var fileB = await CompleteTurnAsync(b, "chat b");
        Assert.Equal(a.ProcessId, b.ProcessId);

        await CrashEngineAsync(a);
        await CountdownAsync(a, 1);
        await CountdownAsync(b, 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        var backA = await ReadyAsync(a);
        var backB = await ReadyAsync(b);

        Assert.Equal(2, fixture.EngineStarts);
        Assert.NotNull(a.ProcessId);
        Assert.Equal(a.ProcessId, b.ProcessId);
        Assert.Equal(fileA, backA.SessionFile);
        Assert.Equal(fileB, backB.SessionFile);
        Assert.Contains(backA.Items, item => item is UserItem { Text: "chat a" });
        Assert.Contains(backB.Items, item => item is UserItem { Text: "chat b" });
    }

    [Fact]
    public async Task A_crash_looping_shared_engine_starts_once_per_attempt_for_all_chats()
    {
        var fixture = new Fixture("auto-recover-shared-loop");
        var a = fixture.Controller();
        await using var host = new SessionHost(a);
        var b = host.Open(new LaunchRequest(fixture.Root));
        await a.StartAsync();
        await b.StartAsync();
        fixture.CrashOnStart = true;
        await CrashEngineAsync(b);
        await CountdownAsync(a, 1);
        await CountdownAsync(b, 1);

        // One chat starts the engine; the other adopts its failure instead of starting another process.
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        await CountdownAsync(a, 2);
        await CountdownAsync(b, 2);
        Assert.Equal(2, fixture.EngineStarts);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await CountdownAsync(a, 3);
        await CountdownAsync(b, 3);
        Assert.Equal(3, fixture.EngineStarts);

        fixture.CrashOnStart = false;
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await ReadyAsync(a);
        await ReadyAsync(b);
        Assert.Equal(4, fixture.EngineStarts);
        Assert.Equal(a.ProcessId, b.ProcessId);
    }

    [Fact]
    public async Task A_chat_that_cannot_reopen_its_own_folder_does_not_hold_back_a_healthy_sibling()
    {
        var fixture = new Fixture("auto-recover-sibling-folder");
        var brokenFolder = Path.Combine(fixture.Root, "broken");
        Directory.CreateDirectory(brokenFolder);
        var healthy = fixture.Controller();
        await using var host = new SessionHost(healthy);
        var broken = host.Open(new LaunchRequest(brokenFolder));
        await healthy.StartAsync();
        await broken.StartAsync();
        await CrashEngineAsync(healthy);
        await CountdownAsync(healthy, 1);
        await CountdownAsync(broken, 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        await ReadyAsync(healthy);
        await ReadyAsync(broken);
        // The broken chat's count starts again (an explicit action); the healthy chat's does not, so it waits longer.
        broken.CancelAutoRecover();
        Directory.Delete(brokenFolder, recursive: true);

        await CrashEngineAsync(healthy);
        await CountdownAsync(broken, 1);
        Assert.Equal(TimeSpan.FromSeconds(1), (await CountdownAsync(healthy, 2)).AutoRecover!.Delay);
        // The broken chat tries first and fails on its own folder, without starting an engine.
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        await TestProcesses.Eventually(broken.Snapshot, s => s.AutoRecover?.Attempt == 2
            && s.Items.OfType<NoticeItem>().Any(n => n.Text.StartsWith("Folder not found", StringComparison.Ordinal)),
            Wait, "the broken chat's own failure");
        Assert.Equal(2, fixture.EngineStarts);

        // That failure is not the shared engine's: the healthy chat starts it on its own attempt.
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        var back = await ReadyAsync(healthy);
        Assert.Null(back.AutoRecover);
        Assert.Equal(3, fixture.EngineStarts);
    }

    /// <summary>Both chats crash; one automatic attempt then starts an engine that never becomes ready, holding startup
    /// admission (the other chat waits behind it).</summary>
    private static async Task HangingAttemptAsync(Fixture fixture, SessionHost host)
    {
        fixture.HangOnStart = true;
        await CrashEngineAsync(host.All[0]);
        foreach (var chat in host.All) await CountdownAsync(chat, 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(0.5));
        await TestProcesses.Eventually(() => fixture.EngineStarts == 2 && host.All.Any(c => c.Snapshot().Phase == SessionPhase.Starting),
            started => started, Wait, "an automatic attempt waiting for its engine");
    }

    [Fact]
    public async Task Closing_the_window_cancels_an_automatic_restart_that_holds_startup_admission()
    {
        var fixture = new Fixture("auto-recover-close-hung");
        var a = fixture.Controller();
        var host = new SessionHost(a);
        host.Open(new LaunchRequest(fixture.Root));
        foreach (var chat in host.All) await chat.StartAsync();
        await HangingAttemptAsync(fixture, host);

        // Well inside the 60 s ready timeout for which the hung start would otherwise keep admission.
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);
        Assert.Equal(2, fixture.EngineStarts);
        Assert.All(host.All, chat => Assert.Null(chat.Snapshot().AutoRecover));
    }

    [Fact]
    public async Task A_settings_restart_cancels_an_automatic_restart_that_holds_startup_admission()
    {
        var fixture = new Fixture("auto-recover-settings-hung");
        var a = fixture.Controller();
        await using var host = new SessionHost(a);
        host.Open(new LaunchRequest(fixture.Root));
        foreach (var chat in host.All) await chat.StartAsync();
        await HangingAttemptAsync(fixture, host);

        // Like MainViewModel's process-settings change: hold new starts, then restart the engine once all chats are idle.
        host.SetRestartPending(true);
        await host.RestartEngineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        host.SetRestartPending(false);
        fixture.HangOnStart = false;
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);
        Assert.Equal(2, fixture.EngineStarts);
        Assert.All(host.All, chat =>
        {
            var snapshot = chat.Snapshot();
            Assert.Null(snapshot.AutoRecover);
            Assert.True(snapshot.Phase is SessionPhase.Faulted or SessionPhase.Stopped, snapshot.Phase.ToString());
        });

        // Recovery is manual after the settings restart, and starts the engine on the new configuration.
        await a.RecoverAsync();
        await ReadyAsync(a);
        Assert.Equal(3, fixture.EngineStarts);
    }
}
