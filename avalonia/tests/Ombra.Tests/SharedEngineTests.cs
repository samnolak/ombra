using System.Diagnostics;
using System.Text.Json;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>Real process transport with one fake engine VM and independently routed logical sessions.</summary>
public sealed class SharedEngineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static OmpLaunchSpec Spec(string scenario, string root, string? cwd = null) => TestProcesses.Fake(scenario) with
    {
        WorkingDirectory = cwd ?? root,
        Environment = new Dictionary<string, string?>
        {
            ["FAKE_SESSION_DIR"] = root,
            ["FAKE_OMP_ENGINE_LOG"] = Path.Combine(root, "engines.jsonl"),
            ["FAKE_OMP_SESSION_LOG"] = Path.Combine(root, "opens.jsonl"),
        },
    };

    private static async Task<RpcFrame> EventAsync(RpcConnection connection, string type)
    {
        using var timeout = new CancellationTokenSource(Wait);
        await foreach (var frame in connection.Events.ReadAllAsync(timeout.Token))
            if (frame.Type == type) return frame;
        throw new InvalidOperationException($"Connection ended before {type}");
    }

    private static async Task<string[]> UserMessagesAsync(RpcConnection connection)
    {
        var response = await connection.RequestAsync("get_messages");
        Assert.True(response.Success);
        return response.Data!.Value.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "user")
            .Select(message => string.Concat(message.GetProperty("content").EnumerateArray()
                .Where(part => part.GetProperty("type").GetString() == "text")
                .Select(part => part.GetProperty("text").GetString())))
            .ToArray();
    }

    [Fact]
    public async Task Concurrent_opens_share_one_pid_and_closing_the_last_session_keeps_the_engine_without_a_spare()
    {
        var root = TestProcesses.TempDir("shared-engine-lifetime");
        await using var engine = new OmpEngine();
        var sessions = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => engine.OpenSessionAsync(Spec("normal", root), Wait)));
        using var process = Process.GetProcessById(sessions[0].ProcessId);
        Assert.All(sessions, session => Assert.Equal(process.Id, session.ProcessId));
        Assert.Single(File.ReadAllLines(Path.Combine(root, "engines.jsonl")));

        // Every endpoint starts with the same request IDs: only the envelope key can distinguish them.
        await Task.WhenAll(sessions.Select((session, index) => session.Connection.RequestAsync("set_session_name",
            writer => writer.WriteString("name", "chat-" + index))));
        var states = await Task.WhenAll(sessions.Select(session => session.Connection.RequestAsync("get_state")));
        for (var index = 0; index < states.Length; index++)
            Assert.Equal("chat-" + index, states[index].Data!.Value.GetProperty("sessionName").GetString());

        await sessions[0].DisposeAsync();
        Assert.Equal(0, await sessions[0].Exited.WaitAsync(Wait));
        Assert.False(process.HasExited);
        Assert.True((await sessions[1].Connection.RequestAsync("get_state")).Success);
        foreach (var session in sessions.Skip(1)) await session.DisposeAsync();
        Assert.False(process.HasExited);

        await using var reopened = await engine.OpenSessionAsync(Spec("normal", root), Wait);
        Assert.Equal(process.Id, reopened.ProcessId);
        Assert.Single(File.ReadAllLines(Path.Combine(root, "engines.jsonl")));
        var opens = File.ReadAllLines(Path.Combine(root, "opens.jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal(7, opens.Length);
        Assert.Equal(7, opens.Select(open => open.GetProperty("sessionKey").GetString()).Distinct().Count());
        Assert.All(opens, open => Assert.True(Guid.TryParse(open.GetProperty("sessionKey").GetString(), out _)));
        await reopened.DisposeAsync();
        await engine.DisposeAsync();
        await process.WaitForExitAsync().WaitAsync(Wait);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Concurrent_queues_and_working_directories_are_private_to_each_logical_session()
    {
        var root = TestProcesses.TempDir("shared-engine-queues");
        var cwdA = TestProcesses.TempDir("shared-engine-project-a");
        var cwdB = TestProcesses.TempDir("shared-engine-project-b");
        await using var engine = new OmpEngine();
        await using var a = await engine.OpenSessionAsync(Spec("queue-stream-2", root, cwdA), Wait);
        await using var b = await engine.OpenSessionAsync(Spec("queue-stream-2", root, cwdB), Wait);
        Assert.Equal(a.ProcessId, b.ProcessId);
        await Task.WhenAll(a.Connection.PromptAsync("a-prompt"), b.Connection.PromptAsync("b-prompt"));
        await Task.WhenAll(a.Connection.FollowUpAsync("a-follow", []), b.Connection.FollowUpAsync("b-follow", []));
        await Task.WhenAll(a.Connection.SteerAsync("a-steer", []), b.Connection.SteerAsync("b-steer", []));
        await Task.WhenAll(EventAsync(a.Connection, "agent_end"), EventAsync(b.Connection, "agent_end"));
        Assert.Equal(["a-prompt", "a-follow", "a-steer"], await UserMessagesAsync(a.Connection));
        Assert.Equal(["b-prompt", "b-follow", "b-steer"], await UserMessagesAsync(b.Connection));
        foreach (var (session, cwd) in new[] { (a, cwdA), (b, cwdB) })
        {
            var state = await session.Connection.RequestAsync("get_state");
            var file = state.Data!.Value.GetProperty("sessionFile").GetString()!;
            var header = File.ReadLines(file).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Single(line => line.GetProperty("type").GetString() == "session");
            Assert.Equal(cwd, header.GetProperty("cwd").GetString());
        }
    }

    [Fact]
    public async Task Tool_flood_over_shared_process_pipe_keeps_session_results_deltas_and_order_isolated()
    {
        var root = TestProcesses.TempDir("shared-engine-tool-flood");
        await using var engine = new OmpEngine(new RpcConnectionOptions { EventQueueCapacity = 128 });
        await using var a = await engine.OpenSessionAsync(Spec("tool-flood", root), Wait);
        await using var b = await engine.OpenSessionAsync(Spec("tool-flood", root), Wait);
        var sessions = new[] { a, b };
        var names = new[] { "session-a é", "session-b 漢" };
        var backlog = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var rawUpdates = new int[2];
        for (var index = 0; index < sessions.Length; index++)
        {
            var slot = index;
            sessions[index].Connection.Intercept = (type, _) =>
            {
                if (type == "tool_execution_update" && ++rawUpdates[slot] == 32) backlog[slot].TrySetResult();
                return false;
            };
        }

        async Task<(ConversationState State, long LastSequence, int Updates)> Consume(int slot)
        {
            // Deliberately create unread backlog without sleeps, then exercise the public read path.
            await backlog[slot].Task.WaitAsync(Wait);
            using var timeout = new CancellationTokenSource(Wait);
            var state = new ConversationState();
            var boundaries = new List<string>();
            long sequence = 0;
            var delivered = 0;
            string? latest = null;
            await foreach (var frame in sessions[slot].Connection.Events.ReadAllAsync(timeout.Token))
            {
                Assert.True(frame.Sequence > sequence);
                sequence = frame.Sequence;
                state.Apply(frame, DateTimeOffset.UtcNow);
                if (frame.Type is "tool_execution_start" or "tool_execution_update" or "tool_execution_end" or "tool_stream_update")
                    Assert.Equal("shared-tool", frame.Json.GetProperty("toolCallId").GetString());
                switch (frame.Type)
                {
                    case "tool_execution_start":
                        boundaries.Add(frame.Type);
                        break;
                    case "tool_execution_update":
                        Assert.Contains("tool_execution_start", boundaries);
                        Assert.DoesNotContain("tool_execution_end", boundaries);
                        latest = frame.Json.GetProperty("partialResult").GetProperty("content")[0].GetProperty("text").GetString()!;
                        Assert.StartsWith(names[slot] + ": ", latest);
                        Assert.Equal(latest, Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);
                        delivered++;
                        break;
                    case "tool_stream_update":
                        Assert.Equal(names[slot] + ": " + new string('x', 1024), latest);
                        Assert.Equal(names[slot] + ": delta", frame.Json.GetProperty("update").GetProperty("delta").GetString());
                        boundaries.Add(frame.Type);
                        break;
                    case "notice":
                        Assert.Equal(names[slot] + ": midway", frame.Json.GetProperty("message").GetString());
                        boundaries.Add(frame.Type);
                        break;
                    case "tool_execution_end":
                        Assert.Equal(names[slot] + ": " + new string('x', 2048), latest);
                        boundaries.Add(frame.Type);
                        break;
                    case "agent_end":
                        Assert.Equal(["tool_execution_start", "tool_stream_update", "notice", "tool_execution_end"], boundaries);
                        return (state, sequence, delivered);
                }
            }
            throw new InvalidOperationException("Shared tool flood ended without agent_end");
        }

        var prompts = sessions.Select((session, index) => session.Connection.PromptAsync(names[index])).ToArray();
        var drains = new[] { Consume(0), Consume(1) };
        await Task.WhenAll(prompts).WaitAsync(Wait);
        var results = await Task.WhenAll(drains).WaitAsync(Wait);
        Assert.Equal(a.ProcessId, b.ProcessId);
        for (var index = 0; index < results.Length; index++)
        {
            Assert.Equal(2048, rawUpdates[index]);
            Assert.InRange(results[index].Updates, 1, 2048 - 30);
            var tool = Assert.Single(results[index].State.Snapshot().Items.OfType<ToolItem>());
            Assert.Equal(names[index] + ": complete", tool.Output);
            Assert.Equal(ToolStatus.Succeeded, tool.Status);
            var snapshot = await sessions[index].Connection.RequestAsync("get_messages");
            Assert.Equal(results[index].LastSequence, snapshot.Timings.EventsBefore);
        }
        await a.DisposeAsync();
        Assert.True((await b.Connection.RequestAsync("get_state")).Success);
    }

    [Fact]
    public async Task Closing_a_flooding_session_unblocks_the_shared_pipe_without_losing_its_peer_result()
    {
        var root = TestProcesses.TempDir("shared-engine-tool-flood-close");
        await using var engine = new OmpEngine(new RpcConnectionOptions { EventQueueCapacity = 8 });
        await using var a = await engine.OpenSessionAsync(Spec("tool-flood", root), Wait);
        await using var b = await engine.OpenSessionAsync(Spec("tool-flood", root), Wait);
        var backlog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = 0;
        a.Connection.Intercept = (type, _) =>
        {
            if (type == "tool_execution_update" && ++updates == 4) backlog.TrySetResult();
            return false;
        };
        await a.Connection.PromptAsync("closing");
        await backlog.Task.WaitAsync(Wait);
        var pending = a.Connection.RequestAsync("get_state");
        var peerPrompt = b.Connection.PromptAsync("surviving");
        var peerEnd = EventAsync(b.Connection, "tool_execution_end");
        await a.DisposeAsync().AsTask().WaitAsync(Wait);
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => pending);
        await peerPrompt.WaitAsync(Wait);
        var result = await peerEnd.WaitAsync(Wait);
        Assert.Equal("surviving: complete", result.Json.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        await EventAsync(b.Connection, "agent_end");
        Assert.False(b.Exited.IsCompleted);
        Assert.True((await b.Connection.RequestAsync("get_state")).Success);
    }

    [Fact]
    public async Task Identical_dialog_ids_and_sidechannel_responses_stay_with_their_own_session()
    {
        var root = TestProcesses.TempDir("shared-engine-dialogs");
        await using var engine = new OmpEngine();
        await using var a = await engine.OpenSessionAsync(Spec("approval", root), Wait);
        await using var b = await engine.OpenSessionAsync(Spec("approval", root), Wait);
        await Task.WhenAll(a.Connection.PromptAsync("a-approval"), b.Connection.PromptAsync("b-approval"));
        var dialogs = await Task.WhenAll(EventAsync(a.Connection, "extension_ui_request"), EventAsync(b.Connection, "extension_ui_request"));
        Assert.Equal("ap1", dialogs[0].Id);
        Assert.Equal(dialogs[0].Id, dialogs[1].Id);

        await a.Connection.SendExtensionUiValueAsync(dialogs[0].Id!, "Approve");
        var allowed = await EventAsync(a.Connection, "tool_execution_end");
        Assert.Equal("hi", allowed.Json.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        await EventAsync(a.Connection, "agent_end");
        Assert.True((await b.Connection.GetStateAsync()).IsStreaming);
        Assert.Empty(await UserMessagesAsync(b.Connection));

        // Closing A after answering its dialog must not cancel B's matching dialog ID.
        await a.DisposeAsync();
        Assert.False(b.Exited.IsCompleted);
        await b.Connection.SendExtensionUiValueAsync(dialogs[1].Id!, "Deny");
        var denied = await EventAsync(b.Connection, "tool_execution_end");
        Assert.True(denied.Json.GetProperty("isError").GetBoolean());
        await EventAsync(b.Connection, "agent_end");
        Assert.Equal(["b-approval"], await UserMessagesAsync(b.Connection));
    }

    [Fact]
    public async Task Root_crash_faults_all_chats_and_only_manual_recovery_reopens_without_resending_queues()
    {
        var root = TestProcesses.TempDir("shared-engine-recovery");
        OmpLaunchSpec Factory(LaunchRequest request)
        {
            var spec = Spec("multi", root, request.WorkingDirectory);
            return request.ResumeSessionFile is { } resume
                ? spec with { SessionArguments = [.. spec.SessionArguments, "--resume", resume] } : spec;
        }
        var a = new SessionController(Factory, new LaunchRequest(root));
        await using var host = new SessionHost(a);
        var b = host.Open(new LaunchRequest(root));
        var crashing = host.Open(new LaunchRequest(root));
        await Task.WhenAll(a.StartAsync(), b.StartAsync(), crashing.StartAsync());
        var originalPid = a.ProcessId;
        Assert.Equal(originalPid, b.ProcessId);
        Assert.Equal(originalPid, crashing.ProcessId);
        await Task.WhenAll(a.PromptAsync("FOREVER a"), b.PromptAsync("APPROVE b"));
        await TestProcesses.Eventually(b.Snapshot, snapshot => snapshot.Dialogs.Count == 1, Wait, "B approval");
        await Task.WhenAll(a.QueueAsync(QueueKind.FollowUp, "a-unsent", []), b.QueueAsync(QueueKind.Steer, "b-unsent", []));
        await crashing.PromptAsync("CRASH the shared host");
        foreach (var chat in host.All)
        {
            var snapshot = await TestProcesses.Eventually(chat.Snapshot, value => value.Phase == SessionPhase.Faulted, Wait, "shared crash");
            Assert.Empty(snapshot.Dialogs);
            Assert.All(snapshot.Items.OfType<AssistantItem>(), item => Assert.False(item.Streaming));
        }
        Assert.All(a.Snapshot().Queued.Concat(b.Snapshot().Queued), queued => Assert.True(queued.Recovered));
        Assert.Equal("a-unsent", Assert.Single(a.Snapshot().Queued).Text);
        Assert.Equal("b-unsent", Assert.Single(b.Snapshot().Queued).Text);
        Assert.Single(File.ReadAllLines(Path.Combine(root, "engines.jsonl")));

        await a.RecoverAsync();
        Assert.Equal(SessionPhase.Ready, a.Snapshot().Phase);
        Assert.NotEqual(originalPid, a.ProcessId);
        Assert.Equal(SessionPhase.Faulted, b.Snapshot().Phase);
        Assert.Equal(SessionPhase.Faulted, crashing.Snapshot().Phase);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "engines.jsonl")).Length);
        Assert.True(Assert.Single(a.Snapshot().Queued).Recovered);
        Assert.DoesNotContain(a.Snapshot().Items.OfType<UserItem>(), user => user.Text == "a-unsent");

        await a.PromptAsync("explicit input after recovery");
        var done = await TestProcesses.Eventually(a.Snapshot, snapshot => snapshot.Phase == SessionPhase.Ready
            && snapshot.Items.OfType<UserItem>().Any(user => user.Text == "explicit input after recovery"), Wait, "manual turn");
        Assert.DoesNotContain(done.Items.OfType<UserItem>(), user => user.Text == "a-unsent");
        Assert.True(Assert.Single(done.Queued).Recovered);
        await b.RecoverAsync();
        Assert.Equal(a.ProcessId, b.ProcessId);
        Assert.True(Assert.Single(b.Snapshot().Queued).Recovered);
        Assert.DoesNotContain(b.Snapshot().Items.OfType<UserItem>(), user => user.Text == "b-unsent");
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root, "engines.jsonl")).Length);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Readonly_history_uses_its_own_deadline_and_cancellation_leaves_live_chat_usable(
        bool cancel, bool blockWriter, bool closeDuringCleanup)
    {
        var root = TestProcesses.TempDir("shared-engine-read-deadline");
        var readLog = Path.Combine(root, "reads.jsonl");
        var original = Spec("normal", root);
        var spec = original with
        {
            Environment = new Dictionary<string, string?>(original.Environment)
            {
                ["FAKE_OMP_READ_LOG"] = readLog,
                ["FAKE_OMP_HOLD_READS"] = "1",
            },
        };
        var clock = new PowerLifecycleTests.ManualClock();
        await using var engine = new OmpEngine(new RpcConnectionOptions { Clock = clock });
        await using var live = await engine.OpenSessionAsync(spec, Wait);
        using var cancellation = new CancellationTokenSource();
        var history = engine.ReadSessionPageAsync(Path.Combine(root, "saved.jsonl"), spec, Wait, ct: cancellation.Token);
        await TestProcesses.Eventually(() => File.Exists(readLog) ? File.ReadAllText(readLog) : "",
            text => text.Contains("engine_read_session", StringComparison.Ordinal), Wait, "root history request");
        RpcConnection? rootConnection = null;
        SemaphoreSlim? heldWriter = null;
        if (blockWriter)
        {
            // Hold the real transport's writer admission without a production synchronization hook.
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var host = Assert.IsType<OmpEngine.Host>(typeof(OmpEngine).GetField("_host", fields)!.GetValue(engine));
            rootConnection = host.Root;
            heldWriter = Assert.IsType<SemaphoreSlim>(typeof(RpcConnection).GetField("_writeLock", fields)!.GetValue(rootConnection));
            Assert.True(await heldWriter.WaitAsync(Wait));
        }
        try
        {
            clock.Advance(TimeSpan.FromSeconds(119));
            Assert.False(history.IsCompleted);
            if (cancel) cancellation.Cancel();
            else clock.Advance(TimeSpan.FromSeconds(1));

            // Do not mistake the test's own watchdog TimeoutException for the RPC's outcome.
            Assert.Same(history, await Task.WhenAny(history, Task.Delay(TimeSpan.FromSeconds(5))));
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history);
            else await Assert.ThrowsAsync<TimeoutException>(() => history);
            if (rootConnection is not null) Assert.Equal(0, rootConnection.PendingRequestCount);

            // The detached cleanup is now queued behind the held writer. Closing the root
            // cancels that wait, but must not replace the already-delivered history outcome.
            if (closeDuringCleanup)
            {
                rootConnection!.Close(new RpcConnectionClosedException("Host closed during read cleanup"));
                if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history);
                else await Assert.ThrowsAsync<TimeoutException>(() => history);
            }
        }
        finally { heldWriter?.Release(); }
        if (closeDuringCleanup)
        {
            await Assert.ThrowsAsync<RpcConnectionClosedException>(() => rootConnection!.RequestAsync("get_state"));
            return;
        }
        if (!blockWriter)
        {
            await TestProcesses.Eventually(() => File.ReadAllText(readLog),
                text => text.Contains("engine_cancel_read", StringComparison.Ordinal), Wait, "scoped read cancellation");
            var commands = File.ReadAllLines(readLog).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal(commands[0].GetProperty("readId").GetString(), commands[1].GetProperty("requestId").GetString());
        }
        Assert.True((await live.Connection.RequestAsync("get_state").WaitAsync(Wait)).Success);
        Assert.Equal(0, live.Connection.PendingRequestCount);
    }
}
