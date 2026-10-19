using System.Diagnostics;
using System.Text.Json;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>One window owns one engine; logical chat selection and closure never evict peers.</summary>
public sealed class SessionHostTests
{
    private static SessionController Chat(string? resume = null) =>
        new(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("host-sessions")), new LaunchRequest(TestProcesses.TempDir("host-project"), resume));

    [Fact]
    public async Task A_chat_being_resumed_is_found_by_its_file_before_the_engine_reports_it()
    {
        await using var host = new SessionHost(Chat());
        var file = Path.Combine(TestProcesses.TempDir("host-files"), "a.jsonl");
        var opened = host.Open(new LaunchRequest(null, file));
        Assert.Same(opened, host.Find(file));
        Assert.Null(host.Find(file + ".other"));
        Assert.Same(opened, host.Active);
    }

    [Fact]
    public async Task Opening_and_selecting_chats_never_evicts_an_idle_peer()
    {
        var first = Chat();
        await using var host = new SessionHost(first);
        var closed = new List<SessionController>();
        host.Closed += closed.Add;
        var chats = Enumerable.Range(0, 8).Select(_ => host.Open(new LaunchRequest())).ToArray();
        host.Activate(first);
        Assert.Equal(9, host.All.Count);
        Assert.Empty(closed);
        Assert.All(chats, chat => Assert.Null(chat.ProcessId)); // opening a tab does not prewarm a spare session
        await host.CloseAsync(chats[3]);
        Assert.Equal([chats[3]], closed);
        Assert.Equal(8, host.All.Count);
        Assert.Throws<InvalidOperationException>(() => { _ = host.CloseAsync(first); });
    }

    [Fact]
    public async Task Closing_and_restarting_one_chat_keeps_the_engine_and_peer_alive()
    {
        var first = Chat();
        await using var host = new SessionHost(first);
        await first.StartAsync();
        var second = host.Open(new LaunchRequest(TestProcesses.TempDir("host-peer"), ApprovalMode: "always-ask"));
        await second.StartAsync();
        var pid = first.ProcessId!.Value;
        Assert.Equal(pid, second.ProcessId);
        var peerFile = second.SessionFile;
        var peerConnection = second.Connection;
        await first.SetApprovalModeAsync("yolo");
        Assert.Equal(pid, first.ProcessId);
        Assert.Same(peerConnection, second.Connection);
        Assert.Equal(peerFile, second.SessionFile);
        await host.CloseAsync(first);
        Assert.Equal(SessionPhase.Stopped, first.Snapshot().Phase);
        Assert.True((await second.ProbeAsync()).Success);
        using var process = Process.GetProcessById(pid);
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task Engine_remains_after_last_logical_session_stops_until_the_window_quits()
    {
        var first = Chat();
        var host = new SessionHost(first);
        await first.StartAsync();
        var pid = first.ProcessId!.Value;
        await first.StopAsync();
        using (var engine = Process.GetProcessById(pid)) Assert.False(engine.HasExited);
        var next = host.Open(new LaunchRequest(TestProcesses.TempDir("host-next")));
        await next.StartAsync();
        Assert.Equal(pid, next.ProcessId);
        await host.DisposeAsync();
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task Coordinated_restart_refuses_busy_peers_and_reopens_only_previously_live_chats()
    {
        var first = Chat();
        await using var host = new SessionHost(first);
        await first.StartAsync();
        var second = host.Open(new LaunchRequest(TestProcesses.TempDir("host-second")));
        await second.StartAsync();
        var unopened = host.Open(new LaunchRequest(TestProcesses.TempDir("host-unopened")));
        var pid = first.ProcessId;
        second.Mutate(state => state.SetPhase(SessionPhase.Running, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RestartEngineAsync());
        Assert.Equal(pid, first.ProcessId);
        second.Mutate(state => state.SetPhase(SessionPhase.Ready, DateTimeOffset.UtcNow));
        await host.StopEngineAsync();
        Assert.Equal(SessionPhase.Stopped, first.Snapshot().Phase);
        await host.RestartEngineAsync();
        Assert.NotEqual(pid, first.ProcessId);
        Assert.Equal(first.ProcessId, second.ProcessId);
        Assert.Equal(SessionPhase.Ready, second.Snapshot().Phase);
        Assert.Equal(SessionPhase.NotStarted, unopened.Snapshot().Phase);
    }

    [Fact]
    public async Task Settings_saved_between_peer_launches_do_not_split_an_engine_generation()
    {
        var wait = TimeSpan.FromSeconds(20);
        var root = TestProcesses.TempDir("host-generation");
        var log = Path.Combine(root, "launches.jsonl");
        const string variable = "PI_OMBRA_TEST_SETTINGS_GENERATION";
        var factory = TestProcesses.FakeFactory("normal", root);
        var generation = "0";
        var launches = 0;
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task<OmpLaunchSpec> Launch(LaunchRequest request, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref launches);
            if (index == 4) await changed.Task.WaitAsync(ct);
            var value = Volatile.Read(ref generation);
            requested.Enqueue(value);
            var spec = factory(request) with
            {
                Environment = new Dictionary<string, string?>
                {
                    ["FAKE_SESSION_DIR"] = root,
                    ["FAKE_OMP_LAUNCH_LOG"] = log,
                    [variable] = value,
                },
            };
            if (index == 3)
            {
                captured.TrySetResult();
                await changed.Task.WaitAsync(ct);
            }
            return spec;
        }
        string[] StartedGenerations(int? pid) => File.ReadAllLines(log).Select(line =>
        {
            using var start = JsonDocument.Parse(line);
            return (Pid: start.RootElement.GetProperty("pid").GetInt32(),
                Generation: start.RootElement.GetProperty("env").GetProperty(variable).GetString()!);
        }).Where(start => start.Pid == pid).Select(start => start.Generation).ToArray();

        var first = new SessionController(Launch, new LaunchRequest(root), readyTimeout: wait);
        await using var host = new SessionHost(first);
        Task? restarting = null;
        try
        {
            await first.StartAsync().WaitAsync(wait);
            var second = host.Open(new LaunchRequest(TestProcesses.TempDir("host-generation-peer")));
            await second.StartAsync().WaitAsync(wait);
            var originalPid = first.ProcessId;
            Volatile.Write(ref generation, "1");
            restarting = host.RestartEngineAsync();
            await captured.Task.WaitAsync(wait);
            Volatile.Write(ref generation, "2");
            changed.TrySetResult();
            await restarting.WaitAsync(wait);

            Assert.Equal(SessionPhase.Ready, first.Snapshot().Phase);
            Assert.Equal(SessionPhase.Ready, second.Snapshot().Phase);
            Assert.Equal(first.ProcessId, second.ProcessId);
            Assert.NotEqual(originalPid, first.ProcessId);
            Assert.Contains("1", requested);
            Assert.Contains("2", requested);
            var intermediate = StartedGenerations(first.ProcessId);
            Assert.Equal(2, intermediate.Length);
            Assert.Single(intermediate.Distinct()); // Both factories must join one immutable root configuration.

            var intermediatePid = first.ProcessId;
            await host.RestartEngineAsync().WaitAsync(wait);
            Assert.NotEqual(intermediatePid, first.ProcessId);
            Assert.Equal(first.ProcessId, second.ProcessId);
            Assert.Equal(new[] { "2", "2" }, StartedGenerations(first.ProcessId));
        }
        finally
        {
            changed.TrySetResult();
            if (restarting is not null) await restarting.WaitAsync(wait);
        }
    }

    [Fact]
    public async Task Explicit_force_stop_can_make_a_chat_idle_while_new_starts_are_deferred()
    {
        var wait = TimeSpan.FromSeconds(20);
        using var cancellation = new CancellationTokenSource(wait);
        var first = Chat();
        await using var host = new SessionHost(first);
        await first.StartAsync(cancellation.Token);
        var second = host.Open(new LaunchRequest(TestProcesses.TempDir("host-deferred-peer")));
        await second.StartAsync(cancellation.Token);
        var originalPid = second.ProcessId;
        first.Mutate(state => state.SetPhase(SessionPhase.Running, DateTimeOffset.UtcNow));
        second.Mutate(state => state.SetPhase(SessionPhase.Running, DateTimeOffset.UtcNow));
        host.SetRestartPending(true);
        try
        {
            var stopping = first.ForceStopAsync(cancellation.Token);
            await TestProcesses.Eventually(first.Snapshot, snapshot => snapshot.Phase == SessionPhase.Stopped,
                wait, "an explicit force-stop finishes before waiting for the new engine");
            Assert.False(stopping.IsCompleted);
            Assert.Equal(SessionPhase.Running, second.Snapshot().Phase);
            Assert.Equal(originalPid, second.ProcessId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RestartEngineAsync(cancellation.Token));

            second.Mutate(state => state.SetPhase(SessionPhase.Ready, DateTimeOffset.UtcNow));
            await host.RestartEngineAsync(cancellation.Token);
            host.SetRestartPending(false);
            await stopping.WaitAsync(wait);
            Assert.Equal(SessionPhase.Ready, first.Snapshot().Phase);
            Assert.NotEqual(originalPid, first.ProcessId);
            Assert.Equal(first.ProcessId, second.ProcessId);
        }
        finally { host.SetRestartPending(false); }
    }
}
