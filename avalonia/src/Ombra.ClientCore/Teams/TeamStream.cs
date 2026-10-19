using System.Net.WebSockets;
using System.Text.Json;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// WSS /v1/stream (§6.1): notifications only (<c>revision</c>, <c>pending</c>, <c>removed</c>, <c>heartbeat</c>,
/// <c>ownership</c>, presence); the client fetches data with <see cref="TeamClient.SyncAsync"/>. The server closes the
/// socket on revocation/removal; <see cref="ReadAsync"/> then returns.
/// </summary>
public sealed class TeamStream : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();

    private TeamStream() { }

    public WebSocketCloseStatus? CloseStatus => _ws.CloseStatus;

    public static async Task<TeamStream> ConnectAsync(TeamsServer server, CancellationToken ct = default)
    {
        var s = new TeamStream();
        server.Endpoint.ConfigureWebSocket(s._ws.Options);
        await s._ws.ConnectAsync(server.Endpoint.WebSocketUri("v1/stream"), ct);
        var token = server.AccessToken ?? (await server.HelloAsync(ct)).Tokens.AccessToken;
        await s._ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new StreamAuth("auth", token), TeamsServer.Json), WebSocketMessageType.Text, true, ct);
        var ready = await s.ReceiveAsync(ct);
        if (ready?.Type != StreamEventTypes.Ready) throw new TeamsServerException(401, Reasons.Unauthorized, "stream not authenticated");
        return s;
    }

    /// <summary>Next event, or null when the server closed the socket.</summary>
    public async Task<StreamEvent?> ReceiveAsync(CancellationToken ct = default)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await _ws.ReceiveAsync(buf, ct); }
            catch (WebSocketException) { return null; }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return JsonSerializer.Deserialize<StreamEvent>(ms.ToArray(), TeamsServer.Json);
        }
    }

    /// <summary>Delivers events until the socket closes or <paramref name="ct"/> is cancelled.</summary>
    public async Task ReadAsync(Func<StreamEvent, Task> onEvent, CancellationToken ct)
    {
        while (await ReceiveAsync(ct) is { } e) await onEvent(e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (WebSocketException) { }
        }
        _ws.Dispose();
    }
}
