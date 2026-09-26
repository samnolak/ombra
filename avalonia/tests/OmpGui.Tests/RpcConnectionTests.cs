using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using OmpGui.Rpc;

namespace OmpGui.Tests;

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

    [Fact]
    public async Task Rejects_commands_over_the_line_limit()
    {
        await Send(Ready);
        await Assert.ThrowsAsync<ArgumentException>(() => _conn.RequestAsync("prompt", w => w.WriteString("message", new string('x', 1_100_000))));
    }
}
