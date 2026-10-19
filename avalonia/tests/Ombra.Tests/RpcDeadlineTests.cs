using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>Exercise elapsed request deadlines over the protocol without sleeping for production timeouts.</summary>
public sealed class RpcDeadlineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("engine_read_session")]
    [InlineData("get_messages")]
    [InlineData("get_messages_page")]
    [InlineData("get_tree")]
    [InlineData("get_entries")]
    [InlineData("get_branch_messages")]
    [InlineData("get_subagent_messages")]
    public async Task History_reads_accept_delayed_responses_but_expire_at_the_history_deadline(string command)
    {
        await using var peer = new Peer();
        var request = peer.Connection.RequestAsync(command);
        var sent = await peer.ReadCommand();
        peer.Clock.Advance(TimeSpan.FromSeconds(119));
        Assert.False(request.IsCompleted);
        await peer.Respond(sent);
        Assert.True((await request.WaitAsync(Wait)).Success);

        request = peer.Connection.RequestAsync(command);
        sent = await peer.ReadCommand();
        peer.Clock.Advance(TimeSpan.FromSeconds(120));
        await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(Wait));
        Assert.Equal(0, peer.Connection.PendingRequestCount);
        await peer.Respond(sent);
        var late = await peer.Connection.Events.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(RpcFrame.UnmatchedResponse, late.Type);
        Assert.Equal(sent.GetProperty("id").GetString(), late.Id);
    }

    [Theory]
    [InlineData("get_state")]
    [InlineData("engine_open")]
    [InlineData("prompt")]
    [InlineData("set_thinking_level")]
    [InlineData("engine_delete_session")]
    public async Task Ordinary_requests_survive_the_old_deadline_without_receiving_the_history_allowance(string command)
    {
        await using var peer = new Peer();
        var request = peer.Connection.RequestAsync(command);
        var sent = await peer.ReadCommand();
        peer.Clock.Advance(TimeSpan.FromSeconds(39));
        Assert.False(request.IsCompleted);
        await peer.Respond(sent);
        Assert.True((await request.WaitAsync(Wait)).Success);

        request = peer.Connection.RequestAsync(command);
        await peer.ReadCommand();
        peer.Clock.Advance(TimeSpan.FromSeconds(40));
        await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(Wait));
        Assert.Equal(0, peer.Connection.PendingRequestCount);
    }

    [Theory]
    [InlineData("get_state", 30, null)]
    [InlineData("get_messages", 30, null)]
    [InlineData("get_messages_page", 180, null)]
    [InlineData("engine_read_session", 30, 7)]
    [InlineData("get_tree", 30, 180)]
    public async Task Configured_and_per_request_deadlines_override_categories_in_both_directions(
        string command, int configuredSeconds, int? requestSeconds)
    {
        await using var peer = new Peer(configuredSeconds);
        TimeSpan? timeout = requestSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        var request = peer.Connection.RequestAsync(command, timeout: timeout);
        await peer.ReadCommand();
        var effectiveSeconds = requestSeconds ?? configuredSeconds;
        peer.Clock.Advance(TimeSpan.FromSeconds(effectiveSeconds - 1));
        Assert.False(request.IsCompleted);
        peer.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(Wait));
        Assert.Equal(0, peer.Connection.PendingRequestCount);
    }

    [Fact]
    public async Task Live_page_helper_uses_history_deadline_for_tail_and_older_cursor()
    {
        await using var peer = new Peer();
        foreach (var cursor in new string?[] { null, "opaque-older-cursor" })
        {
            var request = peer.Connection.GetMessagesPageAsync(cursor, limit: 1);
            var sent = await peer.ReadCommand();
            peer.Clock.Advance(TimeSpan.FromSeconds(90));
            Assert.False(request.IsCompleted);
            await peer.Respond(sent, new
            {
                messages = new[] { new { role = "user", content = "delayed history" } },
                direction = "backward", startOffset = 0, endOffset = 1, totalMessages = 1,
                sessionId = "session", snapshotId = "snapshot", nextCursor = (string?)null,
            });
            Assert.Equal("delayed history", (await request.WaitAsync(Wait)).Messages[0].GetProperty("content").GetString());
        }
    }

    [Fact]
    public async Task Abort_keeps_its_explicit_grace_instead_of_the_ordinary_or_history_deadline()
    {
        await using var peer = new Peer(configuredSeconds: 30);
        var request = peer.Connection.AbortAsync();
        await peer.ReadCommand();
        peer.Clock.Advance(TimeSpan.FromSeconds(299));
        Assert.False(request.IsCompleted);
        peer.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => request.WaitAsync(Wait));
        Assert.Equal(0, peer.Connection.PendingRequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Long_history_deadline_does_not_delay_caller_cancellation_or_eof(bool close)
    {
        await using var peer = new Peer();
        using var cancellation = new CancellationTokenSource();
        var request = peer.Connection.RequestAsync("get_messages_page", ct: cancellation.Token);
        await peer.ReadCommand();
        if (close)
        {
            await peer.EndInput();
            await Assert.ThrowsAsync<RpcConnectionClosedException>(() => request.WaitAsync(Wait));
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Wait));
            Assert.True((await peer.CompleteNextRequest()).Success);
        }
        Assert.Equal(0, peer.Connection.PendingRequestCount);
    }

    [Fact]
    public async Task Logical_session_history_keeps_its_deadline_and_cancels_without_closing_the_session()
    {
        var clock = new PowerLifecycleTests.ManualClock();
        string? requestId = null;
        await using var connection = new RpcConnection(new RpcConnectionOptions { Clock = clock }, 2,
            (_, id, _, _) => { requestId = id; return Task.CompletedTask; }, () => ValueTask.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        var request = connection.RequestAsync("get_entries", ct: cancellation.Token);
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.False(request.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Wait));
        Assert.Equal(0, connection.PendingRequestCount);
        Assert.Null(connection.CloseReason);

        request = connection.RequestAsync("get_tree");
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.False(request.IsCompleted);
        await connection.ReceiveAsync(JsonSerializer.SerializeToElement(new
        {
            id = requestId, type = "response", command = "get_tree", success = true,
        }), Stopwatch.GetTimestamp(), 0, CancellationToken.None);
        Assert.True((await request.WaitAsync(Wait)).Success);
        Assert.Equal(0, connection.PendingRequestCount);
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly Pipe _toClient = new();
        private readonly Pipe _toServer = new();
        private readonly StreamReader _commands;
        public PowerLifecycleTests.ManualClock Clock { get; } = new();
        public RpcConnection Connection { get; }

        public Peer(int? configuredSeconds = null)
        {
            var options = configuredSeconds is { } seconds
                ? new RpcConnectionOptions { Clock = Clock, DefaultRequestTimeout = TimeSpan.FromSeconds(seconds) }
                : new RpcConnectionOptions { Clock = Clock };
            Connection = new RpcConnection(_toClient.Reader.AsStream(), _toServer.Writer.AsStream(), options);
            _commands = new StreamReader(_toServer.Reader.AsStream(), Encoding.UTF8);
        }

        public async Task<JsonElement> ReadCommand()
        {
            var line = await _commands.ReadLineAsync().WaitAsync(Wait);
            using var document = JsonDocument.Parse(line!);
            return document.RootElement.Clone();
        }

        public async Task Respond(JsonElement command, object? data = null)
        {
            var frame = JsonSerializer.Serialize(new
            {
                id = command.GetProperty("id").GetString(), type = "response",
                command = command.GetProperty("type").GetString(), success = true, data,
            });
            await _toClient.Writer.WriteAsync(Encoding.UTF8.GetBytes(frame + "\n"));
        }

        public async Task<RpcResponse> CompleteNextRequest()
        {
            var request = Connection.RequestAsync("get_state");
            await Respond(await ReadCommand());
            return await request.WaitAsync(Wait);
        }

        public ValueTask EndInput() => _toClient.Writer.CompleteAsync();

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            _commands.Dispose();
        }
    }
}
