using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;

namespace Ombra.Server.Streaming;

/// <summary>
/// WSS /v1/stream connections (§6.1): notifications only, never an event store. Each connection has its own bounded
/// send queues and writer loop (one writer per WebSocket): content events (revision/pending/removed) and presence are
/// queued separately, so presence traffic can never evict content events. Revocation closes all sockets of the
/// affected devices; <see cref="StreamRevalidator"/> re-checks access without relying on NOTIFY.
/// </summary>
public sealed class StreamHub(ILogger<StreamHub> log)
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Connection, byte>> _byDevice = new();
    private int _pendingAuth;

    public sealed class Connection(Guid deviceId, Guid memberId, IReadOnlyList<string> teams, WebSocket socket)
    {
        public Guid DeviceId { get; } = deviceId;
        public Guid MemberId { get; } = memberId;
        /// <summary>Teams the device currently has access to; re-targeted by <see cref="StreamRevalidator"/>.</summary>
        public IReadOnlyList<string> Teams { get; set; } = teams;
        public WebSocket Socket { get; } = socket;
        public Channel<StreamEvent?> Outbox { get; } = Channel.CreateBounded<StreamEvent?>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        public Channel<StreamEvent> Presence { get; } = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        public CancellationTokenSource Closing { get; } = new();
        public string State { get; set; } = "online";
        internal double PresenceTokens = double.NaN;
        internal long PresenceRefilledAt;
    }

    /// <summary>Counts unauthenticated WebSocket handshakes in progress; false when <paramref name="max"/> are pending.</summary>
    public bool TryEnterPreAuth(int max)
    {
        if (Interlocked.Increment(ref _pendingAuth) <= max) return true;
        Interlocked.Decrement(ref _pendingAuth);
        return false;
    }

    public void ExitPreAuth() => Interlocked.Decrement(ref _pendingAuth);

    public bool IsOnline(Guid deviceId) => _byDevice.TryGetValue(deviceId, out var set) && !set.IsEmpty;

    public IReadOnlyList<Guid> OnlineDevices() => _byDevice.Where(kv => !kv.Value.IsEmpty).Select(kv => kv.Key).ToList();

    public IReadOnlyList<Connection> Connections() => _byDevice.Values.SelectMany(s => s.Keys).ToList();

    public void Add(Connection c)
    {
        _byDevice.GetOrAdd(c.DeviceId, _ => new()).TryAdd(c, 0);
        BroadcastPresence(c, "online");
    }

    public void Remove(Connection c)
    {
        if (_byDevice.TryGetValue(c.DeviceId, out var set))
        {
            set.TryRemove(c, out _);
            if (set.IsEmpty) BroadcastPresence(c, "offline");
        }
    }

    /// <summary>
    /// Activity frame. A state change spends one token of a per-connection bucket (<paramref name="burst"/> tokens, one
    /// back every <paramref name="refill"/>); false when the bucket is empty — the caller closes the connection.
    /// Repeating the current state is free and broadcasts nothing.
    /// </summary>
    public bool TrySetState(Connection c, string state, int burst, TimeSpan refill, long nowTicks)
    {
        if (state is not ("online" or "inactive")) return false;
        if (c.State == state) return true;
        if (double.IsNaN(c.PresenceTokens)) { c.PresenceTokens = burst; c.PresenceRefilledAt = nowTicks; }
        if (refill > TimeSpan.Zero)
        {
            c.PresenceTokens = Math.Min(burst, c.PresenceTokens + (double)(nowTicks - c.PresenceRefilledAt) / refill.Ticks);
            c.PresenceRefilledAt = nowTicks;
        }
        if (c.PresenceTokens < 1) return false;
        c.PresenceTokens -= 1;
        c.State = state;
        BroadcastPresence(c, state);
        return true;
    }

    private void BroadcastPresence(Connection source, string state)
    {
        foreach (var team in source.Teams)
        {
            var evt = new StreamEvent(StreamEventTypes.Presence, TeamId: team, DeviceId: source.DeviceId, State: state);
            foreach (var other in _byDevice.Values.SelectMany(s => s.Keys))
                if (other.DeviceId != source.DeviceId && other.Teams.Contains(team))
                    other.Presence.Writer.TryWrite(evt);
        }
    }

    /// <summary>New team list of every socket of the device (access shrank or grew since the snapshot).</summary>
    public void Retarget(Guid deviceId, IReadOnlyList<string> teams)
    {
        if (!_byDevice.TryGetValue(deviceId, out var set)) return;
        foreach (var c in set.Keys) c.Teams = teams;
    }

    public void Send(Guid deviceId, StreamEvent evt)
    {
        if (!_byDevice.TryGetValue(deviceId, out var set)) return;
        foreach (var c in set.Keys) c.Outbox.Writer.TryWrite(evt);
    }

    public void Send(IEnumerable<Guid> devices, StreamEvent evt)
    {
        foreach (var d in devices) Send(d, evt);
    }

    /// <summary>Sends <c>removed</c> and closes every socket of the devices (§5.2 п.1: no byte after the block).</summary>
    public void Close(IEnumerable<Guid> devices, string reason)
    {
        foreach (var d in devices)
        {
            if (!_byDevice.TryRemove(d, out var set)) continue;
            foreach (var c in set.Keys)
            {
                c.Outbox.Writer.TryWrite(new StreamEvent(StreamEventTypes.Removed, Reason: reason));
                c.Outbox.Writer.TryWrite(null); // close marker
                BroadcastPresence(c, "offline");
            }
        }
    }

    /// <summary>Writer loop; content events first, then presence. Returns when the connection is closed by either side.</summary>
    public async Task PumpAsync(Connection c, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                if (c.Outbox.Reader.TryRead(out var evt))
                {
                    if (evt is null)
                    {
                        await c.Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "removed", ct);
                        c.Closing.Cancel();
                        return;
                    }
                    await SendAsync(c, evt, ct);
                    continue;
                }
                if (c.Presence.Reader.TryRead(out var presence))
                {
                    await SendAsync(c, presence, ct);
                    continue;
                }
                var content = c.Outbox.Reader.WaitToReadAsync(ct).AsTask();
                var any = await Task.WhenAny(content, c.Presence.Reader.WaitToReadAsync(ct).AsTask());
                if (any == content && !await content) return; // outbox completed: connection ends
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            log.LogDebug("stream writer for {Device} ended: {Message}", c.DeviceId, e.Message);
        }
    }

    private static Task SendAsync(Connection c, StreamEvent evt, CancellationToken ct) =>
        c.Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(evt, JsonDefaults.Options), WebSocketMessageType.Text, true, ct);
}
