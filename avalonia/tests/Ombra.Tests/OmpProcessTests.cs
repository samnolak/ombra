using System.Diagnostics;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>Shared host lifecycle and isolated logical connections against the scripted fake engine.</summary>
public sealed class OmpProcessTests
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Exit_before_root_ready_reports_code_and_stderr()
    {
        await using var engine = new OmpEngine();
        var spec = TestProcesses.Fake("normal");
        var e = await Assert.ThrowsAsync<OmpStartException>(() => engine.OpenSessionAsync(
            spec with { Arguments = [.. spec.Arguments, "root-crash-before-ready"] }, ReadyTimeout));
        Assert.Equal(3, e.ExitCode);
        Assert.Contains("no model configured", e.StderrTail);
    }

    [Fact]
    public async Task Missing_executable_is_a_start_error()
    {
        await using var engine = new OmpEngine();
        var spec = new OmpLaunchSpec { FileName = "/nonexistent/omp-does-not-exist" };
        var error = await Assert.ThrowsAsync<OmpStartException>(() => engine.OpenSessionAsync(spec, ReadyTimeout));
        Assert.True(error.LaunchFailed);
    }

    [Fact]
    public async Task A_host_without_the_supported_engine_protocol_is_rejected()
    {
        await using var engine = new OmpEngine();
        var spec = TestProcesses.Fake("normal");
        var error = await Assert.ThrowsAsync<OmpStartException>(() => engine.OpenSessionAsync(
            spec with { Arguments = [.. spec.Arguments, "unsupported-engine-protocol"] }, ReadyTimeout));
        Assert.Contains("engineProtocolVersion", error.Message);
    }

    [Fact]
    public async Task Logical_stop_and_connection_disposal_leave_the_root_and_peers_alive()
    {
        await using var engine = new OmpEngine();
        await using var first = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        await using var peer = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        Assert.Equal(first.ProcessId, peer.ProcessId);
        Assert.True(first.Ready.SupportsV2);
        Assert.Equal(2, await first.Connection.NegotiateAsync());
        Assert.Equal(0, await first.StopAsync(TimeSpan.FromSeconds(10)));
        await first.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(peer.Exited.IsCompleted);
        Assert.True((await peer.Connection.RequestAsync("get_state")).Success);

        await using var replacement = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        Assert.Equal(peer.ProcessId, replacement.ProcessId);
        Assert.NotEqual(first.SessionKey, replacement.SessionKey);
        await replacement.Connection.DisposeAsync();
        Assert.Equal(0, await replacement.Exited.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True((await peer.Connection.RequestAsync("get_state")).Success);
        await peer.DisposeAsync();
        Assert.Equal(first.ProcessId, engine.ProcessId);
        await using var afterEmpty = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        Assert.Equal(first.ProcessId, afterEmpty.ProcessId);
        Assert.True((await afterEmpty.Connection.RequestAsync("get_state")).Success);
    }

    [Fact]
    public async Task A_logical_stop_timeout_never_kills_the_shared_process()
    {
        await using var engine = new OmpEngine();
        await using var stuck = await engine.OpenSessionAsync(TestProcesses.Fake("ignore-eof"), ReadyTimeout);
        await using var peer = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        var sw = Stopwatch.StartNew();
        Assert.NotEqual(0, await stuck.StopAsync(TimeSpan.FromMilliseconds(500)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"stop took {sw.Elapsed}");
        Assert.False(peer.Exited.IsCompleted);
        Assert.True((await peer.Connection.RequestAsync("get_state")).Success);
    }

    [Fact]
    public async Task Failed_logical_start_preserves_its_exit_code_and_leaves_existing_sessions_alive()
    {
        await using var engine = new OmpEngine();
        await using var peer = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        var error = await Assert.ThrowsAsync<OmpStartException>(() => engine.OpenSessionAsync(TestProcesses.Fake("crash-before-ready"), ReadyTimeout));
        Assert.Equal(3, error.ExitCode);
        Assert.Equal(peer.ProcessId, engine.ProcessId);
        Assert.False(peer.Exited.IsCompleted);
        Assert.True((await peer.Connection.RequestAsync("get_state")).Success);
    }

    [Fact]
    public async Task Crash_fails_pending_requests_on_every_session_without_replaying_them()
    {
        await using var engine = new OmpEngine();
        await using var silent = await engine.OpenSessionAsync(TestProcesses.Fake("silent"), ReadyTimeout);
        await using var crashing = await engine.OpenSessionAsync(TestProcesses.Fake("exit-on-request"), ReadyTimeout);
        var waiting = silent.Connection.RequestAsync("get_state");
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => crashing.Connection.RequestAsync("get_state"));
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => waiting);
        Assert.Equal(5, await crashing.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(5, await silent.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => silent.Connection.RequestAsync("get_state"));
        Assert.Equal(0, silent.Connection.PendingRequestCount);
        await using var recovered = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        Assert.NotEqual(crashing.ProcessId, recovered.ProcessId);
        Assert.True((await recovered.Connection.RequestAsync("get_state")).Success);
        Assert.True(silent.Connection.Completion.IsCompleted);
    }

    [Fact]
    public async Task Fatal_root_framing_fails_every_logical_connection()
    {
        await using var engine = new OmpEngine();
        await using var silent = await engine.OpenSessionAsync(TestProcesses.Fake("silent"), ReadyTimeout);
        await using var bad = await engine.OpenSessionAsync(TestProcesses.Fake("bad-chunk"), ReadyTimeout);
        var waiting = silent.Connection.RequestAsync("get_state");
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => bad.Connection.RequestAsync("prompt", w => w.WriteString("message", "break framing")));
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => waiting);
        await silent.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(silent.Connection.CloseReason);
        Assert.Equal(0, silent.Connection.PendingRequestCount);
    }

    [Fact]
    public async Task Running_host_rejects_changed_process_configuration_but_accepts_session_arguments()
    {
        await using var engine = new OmpEngine();
        var environment = new Dictionary<string, string?> { ["OMP_ENGINE_TEST_CONFIG"] = "first" };
        var spec = TestProcesses.Fake("normal") with { Environment = environment };
        await using var first = await engine.OpenSessionAsync(spec, ReadyTimeout);
        await using var differentSession = await engine.OpenSessionAsync(spec with { SessionArguments = ["duplicate"] }, ReadyTimeout);
        Assert.Equal(first.ProcessId, differentSession.ProcessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenSessionAsync(spec with { FileName = "different-executable" }, ReadyTimeout));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenSessionAsync(spec with { Arguments = [.. spec.Arguments, "different-prefix"] }, ReadyTimeout));
        environment["OMP_ENGINE_TEST_CONFIG"] = "second"; // the active binding is a snapshot, not this mutable dictionary
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenSessionAsync(spec, ReadyTimeout));
        Assert.Contains("process configuration", mismatch.Message);
        Assert.True((await first.Connection.RequestAsync("get_state")).Success);
    }

    [Fact]
    public async Task Owner_stop_releases_configuration_and_disposal_is_final()
    {
        await using var engine = new OmpEngine();
        await using var first = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        using var process = Process.GetProcessById(first.ProcessId);
        await engine.StopAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await first.Exited);
        await using var replacement = await engine.OpenSessionAsync(TestProcesses.Fake("normal") with
        {
            Environment = new Dictionary<string, string?> { ["OMP_ENGINE_TEST_CONFIG"] = "replacement" },
        }, ReadyTimeout);
        Assert.NotEqual(first.ProcessId, replacement.ProcessId);
        await engine.DisposeAsync();
        Assert.Equal(0, await replacement.Exited);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout));
    }

    [Fact]
    public async Task Stderr_flood_is_drained_and_does_not_block_stdout()
    {
        await using var engine = new OmpEngine();
        await using var session = await engine.OpenSessionAsync(TestProcesses.Fake("stderr-flood"), ReadyTimeout);
        for (var i = 0; i < 20; i++)
            Assert.True((await session.Connection.RequestAsync("get_state")).Success);
        await TestProcesses.Eventually(() => session.StderrTail, t => t.Contains("noise line 19999"), TimeSpan.FromSeconds(20), "stderr drained");
    }

    [Fact]
    public async Task Out_of_order_and_duplicate_responses_through_the_shared_pipe()
    {
        await using var engine = new OmpEngine();
        await using (var session = await engine.OpenSessionAsync(TestProcesses.Fake("out-of-order"), ReadyTimeout))
        {
            var a = session.Connection.RequestAsync("get_state");
            var b = session.Connection.RequestAsync("get_available_models");
            Assert.Equal(0, (await a).Data!.Value.GetProperty("order").GetInt32());
            Assert.Equal(1, (await b).Data!.Value.GetProperty("order").GetInt32());
        }
        await using (var session = await engine.OpenSessionAsync(TestProcesses.Fake("duplicate"), ReadyTimeout))
        {
            Assert.True((await session.Connection.RequestAsync("get_state")).Success);
            RpcFrame duplicate;
            do duplicate = await session.Connection.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            while (duplicate.Type == "available_commands_update");
            Assert.Equal(RpcFrame.UnmatchedResponse, duplicate.Type);
        }
    }

    [Fact]
    public async Task Oversized_wrapped_v2_response_is_decoded_once_and_reaches_only_its_session()
    {
        await using var engine = new OmpEngine();
        await using var large = await engine.OpenSessionAsync(TestProcesses.Fake("big-messages"), ReadyTimeout);
        await using var peer = await engine.OpenSessionAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        var page = await large.Connection.GetMessagesPageAsync();
        Assert.Single(page.Messages.EnumerateArray());
        var text = page.Messages[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Equal(3 * 1024 * 1024 + 17 + 2, text.Length);
        Assert.EndsWith("é漢", text);
        Assert.True((await peer.Connection.RequestAsync("get_state")).Success);
    }
}
