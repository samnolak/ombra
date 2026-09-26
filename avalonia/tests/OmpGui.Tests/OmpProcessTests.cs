using System.Diagnostics;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>Process lifecycle against the scripted fake omp.</summary>
public sealed class OmpProcessTests
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Exit_before_ready_reports_code_and_stderr()
    {
        var e = await Assert.ThrowsAsync<OmpStartException>(() => OmpProcess.StartAsync(TestProcesses.Fake("crash-before-ready"), ReadyTimeout));
        Assert.Equal(3, e.ExitCode);
        Assert.Contains("no model configured", e.StderrTail);
    }

    [Fact]
    public async Task Missing_executable_is_a_start_error()
    {
        var spec = new OmpLaunchSpec { FileName = "/nonexistent/omp-does-not-exist" };
        await Assert.ThrowsAsync<OmpStartException>(() => OmpProcess.StartAsync(spec, ReadyTimeout));
    }

    [Fact]
    public async Task Graceful_stop_closes_stdin_and_omp_exits_zero()
    {
        await using var omp = await OmpProcess.StartAsync(TestProcesses.Fake("normal"), ReadyTimeout);
        Assert.True(omp.Ready.SupportsV2);
        Assert.Equal(2, await omp.Connection.NegotiateAsync());
        Assert.Equal(0, await omp.StopAsync(TimeSpan.FromSeconds(10)));
        await omp.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stop_kills_the_tree_when_omp_ignores_eof()
    {
        await using var omp = await OmpProcess.StartAsync(TestProcesses.Fake("ignore-eof"), ReadyTimeout);
        var sw = Stopwatch.StartNew();
        var code = await omp.StopAsync(TimeSpan.FromMilliseconds(500));
        Assert.NotEqual(0, code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"stop took {sw.Elapsed}");
    }

    [Fact]
    public async Task Crash_while_a_request_is_pending_fails_it()
    {
        await using var omp = await OmpProcess.StartAsync(TestProcesses.Fake("exit-on-request"), ReadyTimeout);
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => omp.Connection.RequestAsync("get_state"));
        Assert.Equal(5, await omp.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Stderr_flood_is_drained_and_does_not_block_stdout()
    {
        await using var omp = await OmpProcess.StartAsync(TestProcesses.Fake("stderr-flood"), ReadyTimeout);
        await omp.Connection.NegotiateAsync();
        for (var i = 0; i < 20; i++)
            Assert.True((await omp.Connection.RequestAsync("get_state")).Success);
        await TestProcesses.Eventually(() => omp.StderrTail, t => t.Contains("noise line 19999"), TimeSpan.FromSeconds(20), "stderr drained");
    }

    [Fact]
    public async Task Out_of_order_and_duplicate_responses_through_a_real_pipe()
    {
        await using (var omp = await OmpProcess.StartAsync(TestProcesses.Fake("out-of-order"), ReadyTimeout))
        {
            var a = omp.Connection.RequestAsync("get_state");
            var b = omp.Connection.RequestAsync("get_available_models");
            Assert.Equal(0, (await a).Data!.Value.GetProperty("order").GetInt32());
            Assert.Equal(1, (await b).Data!.Value.GetProperty("order").GetInt32());
        }
        await using (var omp = await OmpProcess.StartAsync(TestProcesses.Fake("duplicate"), ReadyTimeout))
        {
            Assert.True((await omp.Connection.RequestAsync("get_state")).Success);
            RpcFrame dup;
            do dup = await omp.Connection.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            while (dup.Type == "available_commands_update"); // omp announces its commands at start
            Assert.Equal(RpcFrame.UnmatchedResponse, dup.Type);
        }
    }

    [Fact]
    public async Task Oversized_v2_response_is_chunked_and_reassembled()
    {
        await using var omp = await OmpProcess.StartAsync(TestProcesses.Fake("big-messages"), ReadyTimeout);
        await omp.Connection.NegotiateAsync();
        var messages = await omp.Connection.GetMessagesAsync();
        var text = messages[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Equal(3 * 1024 * 1024 + 17 + 2, text.Length);
        Assert.EndsWith("é漢", text);
    }
}
