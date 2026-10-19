using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>Transport behaviour over in-memory pipes: the "server" side is driven line by line by the test.</summary>
public sealed class RpcConnectionTests : IAsyncLifetime
{
    private readonly Pipe _toClient = new();
    private readonly Pipe _toServer = new();
    private RpcConnection _conn = null!;
    private StreamReader _serverIn = null!;
    private const string Ready = """{"type":"ready","protocolVersion":1,"supportedProtocolVersions":[1,2],"maxFrameBytes":1048576,"maxReassembledFrameBytes":67108864}""";

    public ValueTask InitializeAsync()
    {
        _conn = new RpcConnection(_toClient.Reader.AsStream(), _toServer.Writer.AsStream(),
            new RpcConnectionOptions { DefaultRequestTimeout = TimeSpan.FromSeconds(5), EventQueueCapacity = 8 });
        _serverIn = new StreamReader(_toServer.Reader.AsStream(), Encoding.UTF8);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();

    private async Task Send(string line) => await _toClient.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));

    private async Task<JsonElement> ReadCommand()
    {
        var line = await _serverIn.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return JsonDocument.Parse(line!).RootElement.Clone();
    }

    private async Task<RpcFrame> NextEvent() => await _conn.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Reads_ready_and_negotiates_v2()
    {
        await Send(Ready);
        var negotiate = _conn.NegotiateAsync();
        var cmd = await ReadCommand();
        Assert.Equal("negotiate_protocol", cmd.GetProperty("type").GetString());
        Assert.Equal(2, cmd.GetProperty("protocolVersion").GetInt32());
        await Send($$$"""{"id":"{{{cmd.GetProperty("id").GetString()}}}","type":"response","command":"negotiate_protocol","success":true,"data":{"protocolVersion":2}}""");
        Assert.Equal(2, await negotiate);
    }

    [Fact]
    public async Task Stays_on_v1_when_limits_differ()
    {
        await Send("""{"type":"ready","protocolVersion":1,"supportedProtocolVersions":[1,2],"maxFrameBytes":2048,"maxReassembledFrameBytes":67108864}""");
        Assert.Equal(1, await _conn.NegotiateAsync());
    }

    [Theory]
    [InlineData("""{"inputDisposition":"admitted","agentInvoked":true}""", OmpInputDisposition.Admitted, true)]
    [InlineData("""{"inputDisposition":"admitted","agentInvoked":false}""", OmpInputDisposition.Unknown, false)]
    [InlineData("""{"inputDisposition":"local","agentInvoked":false}""", OmpInputDisposition.Local, false)]
    [InlineData("""{"inputDisposition":"local","agentInvoked":true}""", OmpInputDisposition.Local, true)]
    [InlineData("""{"inputDisposition":"cancelled","agentInvoked":false}""", OmpInputDisposition.Cancelled, false)]
    [InlineData("""{"inputDisposition":"cancelled","agentInvoked":true}""", OmpInputDisposition.Cancelled, true)]
    [InlineData("""{"inputDisposition":"local"}""", OmpInputDisposition.Unknown, false)]
    [InlineData("""{"inputDisposition":"local","agentInvoked":"false"}""", OmpInputDisposition.Unknown, false)]
    [InlineData("""{"inputDisposition":"future","agentInvoked":false}""", OmpInputDisposition.Unknown, false)]
    [InlineData("{}", OmpInputDisposition.Unknown, false)]
    public async Task Queue_acknowledgements_preserve_local_consumption_and_nested_agent_admission(
        string data, OmpInputDisposition disposition, bool invoked)
    {
        await Send(Ready);
        foreach (var steering in new[] { false, true })
        {
            var pending = steering ? _conn.SteerAsync("input", []) : _conn.FollowUpAsync("input", []);
            var command = await ReadCommand();
            var type = steering ? "steer" : "follow_up";
            Assert.Equal(type, command.GetProperty("type").GetString());
            await Send($$$"""{"id":"{{{command.GetProperty("id").GetString()}}}","type":"response","command":"{{{type}}}","success":true,"data":{{{data}}}}""");
            var acknowledgement = await pending;
            Assert.Equal(disposition, acknowledgement.Disposition);
            Assert.Equal(invoked, acknowledgement.AgentInvoked);
        }
    }

    [Fact]
    public async Task Correlates_out_of_order_responses_by_id()
    {
        await Send(Ready);
        var a = _conn.RequestAsync("get_state");
        var b = _conn.RequestAsync("get_messages");
        var ca = await ReadCommand();
        var cb = await ReadCommand();
        await Send($$$"""{"id":"{{{cb.GetProperty("id").GetString()}}}","type":"response","command":"get_messages","success":true,"data":{"which":"b"}}""");
        await Send($$$"""{"id":"{{{ca.GetProperty("id").GetString()}}}","type":"response","command":"get_state","success":true,"data":{"which":"a"}}""");
        Assert.Equal("a", (await a).Data!.Value.GetProperty("which").GetString());
        Assert.Equal("b", (await b).Data!.Value.GetProperty("which").GetString());
        var t = (await a).Timings;
        Assert.True(t.Sent > 0 && t.Flushed >= t.Sent && t.ResponseRead >= t.Flushed && t.Delivered >= t.ResponseRead);
    }

    [Fact]
    public async Task Surfaces_duplicate_late_and_idless_responses_as_events()
    {
        await Send(Ready);
        var a = _conn.RequestAsync("get_state");
        var id = (await ReadCommand()).GetProperty("id").GetString();
        var response = $$"""{"id":"{{id}}","type":"response","command":"get_state","success":true}""";
        await Send(response);
        await a;
        await Send(response); // duplicate
        await Send("""{"type":"response","command":"frobnicate","success":false,"error":"Unknown command: frobnicate"}""");
        var dup = await NextEvent();
        Assert.Equal(RpcFrame.UnmatchedResponse, dup.Type);
        Assert.Equal(id, dup.Id);
        var unknown = await NextEvent();
        Assert.Equal(RpcFrame.UnmatchedResponse, unknown.Type);
        Assert.Equal("frobnicate", unknown.Json.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Tolerates_malformed_lines_unknown_events_and_blank_lines()
    {
        await Send(Ready);
        await Send("not json at all");
        await Send("");
        await Send("   \r");
        await Send("[1,2]");
        await Send("""{"type":"future_event","x":1}""");
        await Send("""{"type":"agent_start"}""");
        Assert.Equal(RpcFrame.Malformed, (await NextEvent()).Type);
        Assert.Equal(RpcFrame.Malformed, (await NextEvent()).Type);
        Assert.Equal("future_event", (await NextEvent()).Type);
        Assert.Equal("agent_start", (await NextEvent()).Type);
    }

    [Fact]
    public async Task Handles_a_frame_split_across_many_writes()
    {
        var bytes = Encoding.UTF8.GetBytes(Ready + "\n" + """{"type":"agent_start","note":"ünïcødé 漢字"}""" + "\n");
        foreach (var b in bytes)
        {
            await _toClient.Writer.WriteAsync(new[] { b });
            await _toClient.Writer.FlushAsync();
        }
        await _conn.WaitReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var e = await NextEvent();
        Assert.Equal("ünïcødé 漢字", e.Json.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Reassembles_v2_chunks_into_a_response()
    {
        await Send(Ready);
        await NegotiateV2();
        var req = _conn.RequestAsync("get_messages");
        var id = (await ReadCommand()).GetProperty("id").GetString();
        var payload = JsonSerializer.Serialize(new { id, type = "response", command = "get_messages", success = true, data = new { text = new string('q', 2_500_000) } });
        var bytes = Encoding.UTF8.GetBytes(payload);
        var count = (bytes.Length + 262143) / 262144;
        for (var i = 0; i < count; i++)
        {
            var slice = bytes.AsSpan(i * 262144, Math.Min(262144, bytes.Length - i * 262144)).ToArray();
            await Send(JsonSerializer.Serialize(new { type = "rpc_chunk", chunkId = "rpc-9", index = i, count, byteLength = bytes.Length, data = Convert.ToBase64String(slice) }));
        }
        var r = await req.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2_500_000, r.Data!.Value.GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public async Task A_broken_chunk_sequence_is_fatal_like_upstream()
    {
        await Send(Ready);
        await NegotiateV2();
        var pending = _conn.RequestAsync("get_state");
        await ReadCommand();
        await Send("""{"type":"rpc_chunk","chunkId":"a","index":0,"count":2,"byteLength":2000000,"data":"AAAA"}""");
        await Send("""{"type":"agent_start"}""");
        Assert.Equal(RpcFrame.ProtocolError, (await NextEvent()).Type);
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => pending);
        await _conn.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("framing error", _conn.CloseReason!.Message);
    }

    [Fact]
    public async Task A_chunk_before_negotiation_is_fatal()
    {
        await Send(Ready);
        await Send("""{"type":"rpc_chunk","chunkId":"a","index":0,"count":2,"byteLength":2000000,"data":"AAAA"}""");
        Assert.Equal(RpcFrame.ProtocolError, (await NextEvent()).Type);
        await _conn.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Writes_non_ascii_as_utf8_not_escapes()
    {
        await Send(Ready);
        var req = _conn.RequestAsync("prompt", w => w.WriteString("message", "Привет, 漢字 😀"));
        var raw = await _serverIn.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // BMP text stays raw UTF-8; only astral characters (emoji) are escaped as surrogate pairs.
        Assert.Contains("Привет, 漢字 ", raw);
        Assert.Equal("Привет, 漢字 😀", JsonDocument.Parse(raw!).RootElement.GetProperty("message").GetString());
        var id = JsonDocument.Parse(raw!).RootElement.GetProperty("id").GetString();
        await Send($$$"""{"id":"{{{id}}}","type":"response","command":"prompt","success":true}""");
        Assert.True((await req).Success);
    }

    [Fact]
    public async Task Accepts_deeply_nested_frames()
    {
        await Send(Ready);
        var deep = new string('[', 200) + new string(']', 200);
        await Send($$"""{"type":"tool_execution_end","result":{{deep}}}""");
        Assert.Equal("tool_execution_end", (await NextEvent()).Type);
    }

    private async Task NegotiateV2()
    {
        var negotiate = _conn.NegotiateAsync();
        var cmd = await ReadCommand();
        await Send($$$"""{"id":"{{{cmd.GetProperty("id").GetString()}}}","type":"response","command":"negotiate_protocol","success":true,"data":{"protocolVersion":2}}""");
        Assert.Equal(2, await negotiate);
    }

    [Fact]
    public async Task Times_out_by_deadline_and_reports_the_late_response()
    {
        await Send(Ready);
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => _conn.RequestAsync("get_state", timeout: TimeSpan.FromMilliseconds(200)));
        Assert.Contains("get_state", ex.Message);
        var id = (await ReadCommand()).GetProperty("id").GetString();
        Assert.Equal(0, _conn.PendingRequestCount);
        await Send($$"""{"id":"{{id}}","type":"response","command":"get_state","success":true}""");
        var late = await NextEvent();
        Assert.Equal(RpcFrame.UnmatchedResponse, late.Type);
    }

    [Fact]
    public async Task Cancellation_while_pending_removes_the_request()
    {
        await Send(Ready);
        using var cts = new CancellationTokenSource();
        var req = _conn.RequestAsync("get_state", ct: cts.Token);
        await ReadCommand();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => req);
        Assert.Equal(0, _conn.PendingRequestCount);
    }

    [Fact]
    public async Task Eof_fails_pending_requests_and_completes_events()
    {
        await Send(Ready);
        var req = _conn.RequestAsync("get_state");
        await ReadCommand();
        await _toClient.Writer.CompleteAsync();
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => req);
        await _conn.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await _conn.Events.WaitToReadAsync());
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => _conn.RequestAsync("get_state"));
    }

    [Fact]
    public async Task Eof_before_ready_fails_the_ready_wait()
    {
        await _toClient.Writer.CompleteAsync();
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => _conn.WaitReadyAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Full_event_queue_applies_backpressure_without_dropping()
    {
        await Send(Ready);
        const int n = 200; // queue capacity is 8
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < n; i++) await Send($$"""{"type":"message_update","i":{{i}}}""");
        });
        await Task.Delay(200);
        for (var i = 0; i < n; i++)
            Assert.Equal(i, (await NextEvent()).Json.GetProperty("i").GetInt32());
        await writer;
    }

    private static string ToolUpdate(string text, string toolCallId = "tool", string toolName = "bash", string args = "{}") =>
        JsonSerializer.Serialize(new
        {
            type = "tool_execution_update", toolCallId, toolName, args = JsonSerializer.Deserialize<JsonElement>(args),
            partialResult = new { content = new[] { new { type = "text", text } }, details = new { } },
        });

    [Fact]
    public async Task Tool_flood_pipe_coalesces_backlog_and_keeps_final_result_and_eof()
    {
        const int updates = 512;
        var backpressured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        _conn.Intercept = (type, _) =>
        {
            // The start and seven snapshots fill the eight-frame queue; this eighth snapshot cannot enter yet.
            if (type == "tool_execution_update" && ++observed == 8) backpressured.TrySetResult();
            return false;
        };
        var writer = Task.Run(async () =>
        {
            await Send("""{"type":"tool_execution_start","toolCallId":"tool","toolName":"bash","args":{}}""");
            for (var index = 1; index <= updates; index++) await Send(ToolUpdate("é漢 " + new string('x', index)));
            await Send("""{"type":"tool_execution_end","toolCallId":"tool","toolName":"bash","result":{"content":[{"type":"text","text":"complete é漢"}]},"isError":false}""");
            await Send("""{"type":"agent_end","messages":[]}""");
            await _toClient.Writer.CompleteAsync();
        });
        await backpressured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var state = new ConversationState();
        var deliveredUpdates = 0;
        long previous = 0;
        var kinds = new List<string>();
        while (true)
        {
            var frame = await NextEvent();
            Assert.True(frame.Sequence > previous);
            previous = frame.Sequence;
            state.Apply(frame, DateTimeOffset.UtcNow);
            if (frame.Type == "tool_execution_update")
            {
                deliveredUpdates++;
                Assert.Equal("é漢 " + new string('x', (int)frame.Sequence - 1),
                    Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);
            }
            else kinds.Add(frame.Type);
            if (frame.Type == "agent_end") break;
        }
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        await _conn.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(deliveredUpdates, 1, updates - 6);
        Assert.Equal(updates, observed); // Intercept still sees every original frame.
        Assert.Equal(updates + 3, previous);
        Assert.Equal(["tool_execution_start", "tool_execution_end", "agent_end"], kinds);
        var tool = Assert.Single(state.Snapshot().Items.OfType<ToolItem>());
        Assert.Equal("complete é漢", tool.Output);
        Assert.Equal(ToolStatus.Succeeded, tool.Status);
        Assert.False(await _conn.Events.WaitToReadAsync());
    }

    [Fact]
    public async Task Tool_snapshots_do_not_cross_response_history_barriers()
    {
        var firstRequest = _conn.RequestAsync("get_messages_page");
        var firstId = (await ReadCommand()).GetProperty("id").GetString();
        var secondRequest = _conn.RequestAsync("get_messages_page");
        var secondId = (await ReadCommand()).GetProperty("id").GetString();
        await Send("""{"type":"tool_execution_start","toolCallId":"tool","toolName":"bash","args":{}}""");
        await Send(ToolUpdate("before 1"));
        await Send(ToolUpdate("before 2"));
        await Send($$"""{"type":"response","id":"{{firstId}}","success":true}""");
        await Send(ToolUpdate("after 1"));
        await Send(ToolUpdate("after 2"));
        await Send($$"""{"type":"response","id":"{{secondId}}","success":true}""");
        await Send("""{"type":"tool_execution_end","toolCallId":"tool","toolName":"bash"}""");
        var first = await firstRequest;
        var second = await secondRequest;
        Assert.Equal(3, first.Timings.EventsBefore);
        Assert.Equal(5, second.Timings.EventsBefore);

        var state = new ConversationState();
        state.Apply(await NextEvent(), DateTimeOffset.UtcNow);
        var before = await NextEvent();
        Assert.Equal(first.Timings.EventsBefore, before.Sequence);
        state.Apply(before, DateTimeOffset.UtcNow);
        Assert.Equal("before 2", Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);
        var after = await NextEvent();
        Assert.Equal(second.Timings.EventsBefore, after.Sequence);
        state.Apply(after, DateTimeOffset.UtcNow);
        Assert.Equal("after 2", Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);
        var end = await NextEvent();
        Assert.Equal("tool_execution_end", end.Type);
        Assert.Equal(6, end.Sequence);
        state.Apply(end, DateTimeOffset.UtcNow);
        Assert.Equal("after 2", Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);
    }

    [Fact]
    public async Task Intercepted_events_separate_unread_tool_snapshots()
    {
        _conn.Intercept = (type, _) => type == "notice";
        var request = _conn.RequestAsync("get_state");
        var id = (await ReadCommand()).GetProperty("id").GetString();
        await Send(ToolUpdate("before 1"));
        await Send(ToolUpdate("before 2"));
        await Send("""{"type":"notice","message":"intercepted"}""");
        await Send(ToolUpdate("after 1"));
        await Send(ToolUpdate("after 2"));
        await Send($$"""{"type":"response","id":"{{id}}","success":true}""");
        Assert.Equal(4, (await request).Timings.EventsBefore);
        var before = await NextEvent();
        var after = await NextEvent();
        Assert.Equal(2, before.Sequence);
        Assert.Equal("before 2", before.Json.GetProperty("partialResult").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(4, after.Sequence);
        Assert.Equal("after 2", after.Json.GetProperty("partialResult").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("""{"type":"tool_execution_start","toolCallId":"other","toolName":"bash","args":{}}""", "tool_execution_start")]
    [InlineData("""{"type":"tool_execution_end","toolCallId":"tool","toolName":"bash","result":{"content":[]}}""", "tool_execution_end")]
    [InlineData("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"keep"}}""", "message_update")]
    [InlineData("""{"type":"tool_stream_update","toolCallId":"tool","toolName":"bash","update":{"delta":"keep"}}""", "tool_stream_update")]
    [InlineData("""{"type":"response","id":"unknown","success":false}""", RpcFrame.UnmatchedResponse)]
    [InlineData("not json", RpcFrame.Malformed)]
    public async Task Non_tool_and_lifecycle_frames_keep_their_position_between_snapshots(string separator, string kind)
    {
        var request = _conn.RequestAsync("get_state");
        var id = (await ReadCommand()).GetProperty("id").GetString();
        await Send(ToolUpdate("before 1"));
        await Send(ToolUpdate("before 2"));
        await Send(separator);
        await Send(ToolUpdate("after 1"));
        await Send(ToolUpdate("after 2"));
        await Send($$"""{"type":"response","id":"{{id}}","success":true}""");
        Assert.Equal(5, (await request).Timings.EventsBefore);
        Assert.Equal(2, (await NextEvent()).Sequence);
        var boundary = await NextEvent();
        Assert.Equal(kind, boundary.Type);
        Assert.Equal(3, boundary.Sequence);
        Assert.Equal(5, (await NextEvent()).Sequence);
    }

    [Theory]
    [InlineData("""{"type":"tool_execution_update","toolCallId":"tool","toolName":"bash","delta":"keep"}""")]
    [InlineData("""{"type":"tool_execution_update","toolCallId":"tool","toolName":"bash","partialResult":{"content":[{"type":"text","text":"keep"}],"delta":"keep"}}""")]
    [InlineData("""{"type":"tool_execution_update","toolCallId":"tool","toolName":"bash","partialResult":{"content":[{"type":"text","text":"keep"}],"details":{"terminalId":"keep"}}}""")]
    [InlineData("""{"type":"tool_execution_update","toolCallId":"tool","toolName":"bash","partialResult":{"content":[{"type":"text","text":"keep"}]},"progress":{"step":"keep"}}""")]
    [InlineData("""{"type":"tool_execution_update","toolCallId":"tool","toolName":"bash","partialResult":{"content":[{"type":"image","data":"keep","mimeType":"image/png"}]}}""")]
    [InlineData("""{"type":"tool_execution_update","toolCallId":"tool","toolName":"bash","partialResult":{"content":[{"type":"text","text":"keep","annotation":"keep"}]}}""")]
    [InlineData("""{"type":"tool_execution_update","toolCallId":"tool","toolName":"bash","partialResult":"keep"}""")]
    public async Task Deltas_and_nonreplaceable_metadata_are_never_coalesced(string update)
    {
        var request = _conn.RequestAsync("get_state");
        var id = (await ReadCommand()).GetProperty("id").GetString();
        await Send(update);
        await Send(update);
        await Send(ToolUpdate("latest snapshot"));
        await Send($$"""{"type":"response","id":"{{id}}","success":true}""");
        Assert.Equal(3, (await request).Timings.EventsBefore);
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            var frame = await NextEvent();
            Assert.Equal(sequence, frame.Sequence);
            Assert.Equal(update, frame.Json.GetRawText());
        }
        Assert.Equal(3, (await NextEvent()).Sequence);
    }

    [Theory]
    [InlineData("other", "bash", "{}")]
    [InlineData("tool", "read", "{}")]
    [InlineData("tool", "bash", """{"command":"different"}""")]
    public async Task Tool_identity_and_arguments_separate_contiguous_snapshots(string callId, string name, string args)
    {
        var request = _conn.RequestAsync("get_state");
        var id = (await ReadCommand()).GetProperty("id").GetString();
        await Send(ToolUpdate("a1"));
        await Send(ToolUpdate("a2"));
        await Send(ToolUpdate("b1", callId, name, args));
        await Send(ToolUpdate("b2", callId, name, args));
        await Send(ToolUpdate("a3"));
        await Send(ToolUpdate("a4"));
        await Send($$"""{"type":"response","id":"{{id}}","success":true}""");
        Assert.Equal(6, (await request).Timings.EventsBefore);
        foreach (var (sequence, text) in new[] { (2, "a2"), (4, "b2"), (6, "a4") })
        {
            var frame = await NextEvent();
            Assert.Equal(sequence, frame.Sequence);
            Assert.Equal(text, frame.Json.GetProperty("partialResult").GetProperty("content")[0].GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task A_lone_snapshot_is_delivered_without_waiting_for_another_frame_and_read_cancellation_is_local()
    {
        await Send(ToolUpdate("live"));
        Assert.Equal(1, (await NextEvent()).Sequence);
        using var cancellation = new CancellationTokenSource();
        var waiting = _conn.Events.ReadAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Send(ToolUpdate("still live"));
        Assert.Equal(2, (await NextEvent()).Sequence);
        await _toClient.Writer.CompleteAsync();
        await _conn.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await _conn.Events.WaitToReadAsync());
    }

    [Fact]
    public async Task Rejects_commands_over_the_line_limit()
    {
        await Send(Ready);
        await Assert.ThrowsAsync<ArgumentException>(() => _conn.RequestAsync("prompt", w => w.WriteString("message", new string('x', 1_100_000))));
    }

    [Fact]
    public void User_input_preflight_reserves_the_shared_engine_envelope()
    {
        const string emptyFrame = """{"type":"engine_session_command","sessionKey":"00000000000000000000000000000000","frame":{"id":"g-9223372036854775808","type":"follow_up","message":""}}""";
        var available = RpcFrameDecoder.MaxFrameBytes - Encoding.UTF8.GetByteCount(emptyFrame) - 1;
        OmpCommands.ValidateUserInput(new string('x', available), []);
        Assert.Throws<ArgumentException>(() => OmpCommands.ValidateUserInput(new string('x', available + 1), []));
    }

    [Fact]
    public async Task Ready_preserves_the_engine_protocol_capability()
    {
        await Send("""{"type":"ready","engineProtocolVersion":1,"supportedProtocolVersions":[1,2]}""");
        Assert.Equal(1, (await _conn.WaitReadyAsync()).EngineProtocolVersion);
    }

    [Fact]
    public async Task Parsed_logical_frames_keep_session_local_ids_timings_and_history_fences()
    {
        string? firstId = null;
        string? secondId = null;
        var closes = 0;
        await using var first = new RpcConnection(null, 2,
            (_, id, _, _) => { firstId = id; return Task.CompletedTask; },
            () => { closes++; return ValueTask.CompletedTask; });
        await using var second = new RpcConnection(null, 2,
            (_, id, _, _) => { secondId = id; return Task.CompletedTask; }, () => ValueTask.CompletedTask);
        using var ready = JsonDocument.Parse(Ready);
        await first.ReceiveAsync(ready.RootElement, 1, 100, default);
        await second.ReceiveAsync(ready.RootElement, 1, 100, default);
        Assert.Equal(2, await first.NegotiateAsync());
        Assert.Null(firstId); // negotiation belongs to the root, not a logical command

        var a = first.RequestAsync("get_messages_page");
        var b = second.RequestAsync("get_state");
        Assert.Equal(firstId, secondId); // request ids are deliberately scoped to each connection
        using var update = JsonDocument.Parse("""{"type":"message_update","message":{"text":"one"}}""");
        await first.ReceiveAsync(update.RootElement, 123, 321, default);
        using var aResponse = JsonDocument.Parse($$$"""{"type":"response","id":"{{{firstId}}}","success":true,"data":{"which":"first"}}""");
        using var bResponse = JsonDocument.Parse($$$"""{"type":"response","id":"{{{secondId}}}","success":true,"data":{"which":"second"}}""");
        await second.ReceiveAsync(bResponse.RootElement, 124, 75, default);
        await first.ReceiveAsync(aResponse.RootElement, 125, 76, default);

        var eventFrame = await first.Events.ReadAsync();
        Assert.Equal(update.RootElement, eventFrame.Json); // same parsed element, not a serialize/parse copy
        Assert.Equal(321, eventFrame.PhysicalBytes);
        Assert.Equal(1, eventFrame.Sequence);
        Assert.Equal(123, eventFrame.ReadTimestamp);
        Assert.Equal(1, (await a).Timings.EventsBefore);
        Assert.Equal(125, (await a).Timings.ResponseRead);
        Assert.Equal(0, (await b).Timings.EventsBefore);
        Assert.Equal("first", (await a).Data!.Value.GetProperty("which").GetString());
        Assert.Equal("second", (await b).Data!.Value.GetProperty("which").GetString());
        Assert.Contains("lines=3 lineBytes=497", first.ReadDiagnostics);
        Assert.Contains("lines=2 lineBytes=175", second.ReadDiagnostics);
        await first.DisposeAsync();
        Assert.Equal(1, closes);
        Assert.False(second.Completion.IsCompleted);
    }

    [Fact]
    public async Task Eof_retains_the_latest_unended_tool_snapshot_and_its_original_sequence()
    {
        await Send(ToolUpdate("first"));
        await Send(ToolUpdate("last"));
        await _toClient.Writer.CompleteAsync();
        await _conn.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var frame = await NextEvent();
        Assert.Equal(2, frame.Sequence);
        Assert.Equal("last", frame.Json.GetProperty("partialResult").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(await _conn.Events.WaitToReadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_a_backpressured_logical_session_unblocks_routing_and_fails_its_requests(bool toolUpdate)
    {
        await using var logical = new RpcConnection(new RpcConnectionOptions { EventQueueCapacity = 1 }, 2,
            (_, _, _, _) => Task.CompletedTask, () => ValueTask.CompletedTask);
        var request = logical.RequestAsync("get_state");
        using var update = JsonDocument.Parse(toolUpdate ? ToolUpdate("pending snapshot") : """{"type":"message_update"}""");
        await logical.ReceiveAsync(update.RootElement, 1, 20, default);
        var blocked = logical.ReceiveAsync(update.RootElement, 2, 20, default).AsTask();
        Assert.False(blocked.IsCompleted);
        await logical.DisposeAsync();
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => request);
        Assert.True(logical.Completion.IsCompleted);
    }
}
