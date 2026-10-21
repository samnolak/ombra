using System.Text.Json;

namespace Ombra.Rpc;

/// <summary>Subset of <c>RpcSessionState</c> (rpc-types.ts) the client uses.</summary>
public sealed record OmpSessionState(
    string? ModelProvider,
    string? ModelId,
    string? ModelName,
    bool IsStreaming,
    string? SessionId,
    string? SessionFile,
    int MessageCount,
    string? ThinkingLevel,
    bool IsCompacting = false,
    int QueuedMessageCount = 0,
    bool? IsSettled = null,
    string? SessionName = null,
    JsonElement TodoPhases = default,
    double? ContextPercent = null)
{
    /// <summary>The read-side watermark of get_state, used to preserve newer event-driven state.</summary>
    public RpcTimings? Timings { get; init; }
    public OmpUsageLimit? UsageLimit { get; init; }
    /// <summary>What omp left out of the folder for <c>--untrusted-project</c> (Ombra's engine); null when the session
    /// was opened without it.</summary>
    public IReadOnlyList<OmpProjectItem>? UntrustedProject { get; init; }
    /// <summary>Plan mode (Ombra's engine, <see cref="OmpPlanMode"/>); null from an omp without plan mode over RPC.</summary>
    public OmpPlanMode? PlanMode { get; init; }
    /// <summary>This omp reports goal mode in <c>get_state</c> (Ombra's engine); <see cref="Goal"/> is then the session's goal.</summary>
    public bool GoalSupported { get; init; }
    /// <summary>The session's goal (<c>get_state.goal</c>); null when it has none (or omp has no goal mode).</summary>
    public OmpGoal? Goal { get; init; }
    /// <summary>omp's queue snapshot; the leading <c>LiveSteered</c> steering chips have reached the provider and cannot be withdrawn.</summary>
    public (IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp, int LiveSteered)? QueuedMessages { get; init; }
    /// <summary>
    /// Nothing is running, compacting or queued. Uses <c>isSettled</c> when omp provides it (upstream after
    /// 18.2.0); 18.2.0 has no such field, so the three 18.2.0 fields are combined.
    /// </summary>
    public bool LooksSettled => IsSettled ?? (!IsStreaming && !IsCompacting && QueuedMessageCount == 0);

    public static OmpSessionState Parse(JsonElement d)
    {
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var hasModel = d.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object;
        return new OmpSessionState(
            hasModel ? S(m, "provider") : null,
            hasModel ? S(m, "id") : null,
            hasModel ? S(m, "name") : null,
            d.TryGetProperty("isStreaming", out var st) && st.ValueKind == JsonValueKind.True,
            S(d, "sessionId"),
            S(d, "sessionFile"),
            d.TryGetProperty("messageCount", out var mc) && mc.ValueKind == JsonValueKind.Number && mc.TryGetInt32(out var n) ? n : 0,
            S(d, "thinkingLevel"),
            d.TryGetProperty("isCompacting", out var ic) && ic.ValueKind == JsonValueKind.True,
            d.TryGetProperty("queuedMessageCount", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var qn) ? qn : 0,
            d.TryGetProperty("isSettled", out var se) && se.ValueKind is JsonValueKind.True or JsonValueKind.False ? se.GetBoolean() : null,
            S(d, "sessionName"),
            d.TryGetProperty("todoPhases", out var tp) ? tp.Clone() : default,
            d.TryGetProperty("contextUsage", out var cu) && cu.ValueKind == JsonValueKind.Object && cu.TryGetProperty("percent", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : null)
        {
            UsageLimit = hasModel && S(m, "provider") is not null && S(m, "id") is not null
                && d.TryGetProperty("usageLimit", out var limit) ? OmpUsageLimit.Parse(limit) : null,
            UntrustedProject = d.TryGetProperty("untrustedProject", out var up) && up.ValueKind == JsonValueKind.Object
                ? OmpProjectItem.ParseList(up) : null,
            PlanMode = d.TryGetProperty("planMode", out var pm) && pm.ValueKind == JsonValueKind.Object ? OmpPlanMode.Parse(pm) : null,
            GoalSupported = d.TryGetProperty("goal", out var goal) && goal.ValueKind is JsonValueKind.Object or JsonValueKind.Null,
            Goal = d.TryGetProperty("goal", out var goalState) ? OmpGoal.ParseState(goalState) : null,
            QueuedMessages = d.TryGetProperty("queuedMessages", out var qm) && qm.ValueKind == JsonValueKind.Object
                && qm.TryGetProperty("steering", out var qs) && qs.ValueKind == JsonValueKind.Array
                && qm.TryGetProperty("followUp", out var qf) && qf.ValueKind == JsonValueKind.Array
                ? (Texts(qs), Texts(qf), qm.TryGetProperty("liveSteered", out var live) && live.ValueKind == JsonValueKind.Number && live.TryGetInt32(out var count) ? Math.Clamp(count, 0, qs.GetArrayLength()) : 0) : null,
        };
    }

    private static string[] Texts(JsonElement a) =>
        [.. a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
}

/// <summary>
/// One thing a folder would have contributed to omp and <c>--untrusted-project</c> left out. <see cref="Kind"/>: mcp,
/// extension, hook, tool, command, plugin, lsp or settings; <see cref="Source"/>: the file that declares or is it.
/// </summary>
public sealed record OmpProjectItem(string Kind, string Name, string Source)
{
    internal static IReadOnlyList<OmpProjectItem> ParseList(JsonElement report)
    {
        if (!report.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var list = new List<OmpProjectItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string S(string n) => item.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            if (S("kind") is { Length: > 0 } kind && S("name") is { Length: > 0 } name) list.Add(new OmpProjectItem(kind, name, S("source")));
        }
        return list;
    }
}

/// <summary>A model omp can use now (<c>get_available_models</c>: only providers with credentials are listed).</summary>
public sealed record OmpModel(string Provider, string Id, string Name, bool Reasoning, bool AcceptsImages, long ContextWindow)
{
    public string Key => $"{Provider}/{Id}";
    /// <summary>The provider's API address (omp's <c>baseUrl</c>); null when omp did not list one.</summary>
    public string? BaseUrl { get; init; }
}

/// <summary>A provider omp can sign in to (<c>get_login_providers</c>).</summary>
public sealed record OmpLoginProvider(string Id, string Name, bool Available, bool Authenticated);

/// <summary>
/// An MCP server of the session (owned runtime <c>get_mcp_servers</c>). Never carries credentials.
/// </summary>
/// <param name="Status">connected | connecting | disconnected | failed.</param>
/// <param name="Error">The last connection failure (URL queries removed) until it connects again.</param>
/// <param name="Auth">none | signed_in | signed_out | expired.</param>
public sealed record OmpMcpServer(string Name, string Transport, string? Scope, string Status, int ToolCount, string? Error, string Auth)
{
    public bool NeedsSignIn => Auth is "signed_out" or "expired";
}

/// <summary>
/// A credential omp has stored for a provider and can remove (<c>get_logout_accounts</c>, omp 18.8.0).
/// </summary>
/// <param name="Label">The account as omp names it (email, account or project id, "API key #3").</param>
/// <param name="Detail">More of the same (email, ids, "oauth #3").</param>
/// <param name="Type"><c>oauth</c> or <c>api_key</c>.</param>
/// <param name="Active">This session may be using it.</param>
public sealed record OmpLogoutAccount(long CredentialId, string Label, string Detail, string Type, bool Active);

/// <summary>Owned runtime queue admission: local input hooks can consume input without starting an agent.</summary>
public enum OmpInputDisposition { Unknown, Admitted, Local, Cancelled }

/// <summary>The original input's disposition, and whether it admitted original or extension-started agent work.</summary>
public readonly record struct OmpInputAcknowledgement(OmpInputDisposition Disposition, bool AgentInvoked);

/// <summary>The omp RPC commands the client uses (docs/rpc.md "Command Schema").</summary>
public static class OmpCommands
{
    /// <summary>Keep every event type, but omit cumulative snapshots from streaming updates.</summary>
    public static async Task SetDeltaMessageUpdatesAsync(this RpcConnection c, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("set_event_filter", w =>
        {
            w.WriteNull("events");
            w.WriteString("messageUpdates", "delta");
        }, ct: ct).ConfigureAwait(false));

    public static async Task<OmpSessionState> GetStateAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_state", ct: ct).ConfigureAwait(false));
        return OmpSessionState.Parse(r.Data ?? default) with { Timings = r.Timings };
    }

    /// <summary>Acknowledged immediately; the run completes on a terminal <c>agent_end</c>.</summary>
    public static async Task<RpcResponse> PromptAsync(this RpcConnection c, string message, string? streamingBehavior = null, CancellationToken ct = default) =>
        await c.PromptAsync(message, [], streamingBehavior, ct).ConfigureAwait(false);

    /// <param name="images">(MIME type, bytes) pairs sent as <c>ImageContent</c>.</param>
    public static async Task<RpcResponse> PromptAsync(this RpcConnection c, string message, IReadOnlyList<(string MimeType, byte[] Data)> images, string? streamingBehavior = null, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("prompt", w =>
        {
            w.WriteString("message", message);
            WriteImages(w, images);
            if (streamingBehavior is not null) w.WriteString("streamingBehavior", streamingBehavior);
        }, ct: ct).ConfigureAwait(false));

    /// <summary>Delivered at the next tool boundary, unless an input hook consumes or cancels the submission.</summary>
    public static async Task<OmpInputAcknowledgement> SteerAsync(this RpcConnection c, string message, IReadOnlyList<(string MimeType, byte[] Data)> images, CancellationToken ct = default) =>
        InputAcknowledgement(Ok(await c.RequestAsync("steer", w => { w.WriteString("message", message); WriteImages(w, images); }, ct: ct).ConfigureAwait(false)));

    /// <summary>Delivered when the agent would stop, unless an input hook consumes or cancels the submission.</summary>
    public static async Task<OmpInputAcknowledgement> FollowUpAsync(this RpcConnection c, string message, IReadOnlyList<(string MimeType, byte[] Data)> images, CancellationToken ct = default) =>
        InputAcknowledgement(Ok(await c.RequestAsync("follow_up", w => { w.WriteString("message", message); WriteImages(w, images); }, ct: ct).ConfigureAwait(false)));

    /// <summary>
    /// A message for a run under way sent as a <c>prompt</c> with <c>streamingBehavior</c> (<c>steer</c> or
    /// <c>followUp</c>), queued like <see cref="SteerAsync"/> / <see cref="FollowUpAsync"/>: omp expands a
    /// <c>/skill:</c> invocation only on <c>prompt</c>, and passes a steer or follow-up's text on as it is.
    /// </summary>
    public static async Task<OmpInputAcknowledgement> QueuePromptAsync(this RpcConnection c, string message, IReadOnlyList<(string MimeType, byte[] Data)> images, bool steer, CancellationToken ct = default) =>
        InputAcknowledgement(await c.PromptAsync(message, images, steer ? "steer" : "followUp", ct).ConfigureAwait(false));

    public static OmpInputAcknowledgement InputAcknowledgement(RpcResponse response)
    {
        if (response.Data is { ValueKind: JsonValueKind.Object } data
            && data.TryGetProperty("inputDisposition", out var value) && value.ValueKind == JsonValueKind.String
            && data.TryGetProperty("agentInvoked", out var invoked) && invoked.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (value.ValueEquals("admitted") && invoked.GetBoolean()) return new(OmpInputDisposition.Admitted, true);
            if (value.ValueEquals("local")) return new(OmpInputDisposition.Local, invoked.GetBoolean());
            if (value.ValueEquals("cancelled")) return new(OmpInputDisposition.Cancelled, invoked.GetBoolean());
        }
        // Missing/malformed metadata cannot prove that an actual idle drain may be overtaken.
        return new(OmpInputDisposition.Unknown, false);
    }

    /// <summary>Checks the shared engine's JSON wire representation before the UI clears any input.
    /// Reserves its fixed-width session key and longest request id without allocating another full message.</summary>
    public static void ValidateUserInput(string message, IReadOnlyList<(string MimeType, byte[] Data)> images)
    {
        using var count = new ByteCounter();
        using (var writer = new Utf8JsonWriter(count, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "engine_session_command");
            writer.WriteString("sessionKey", "00000000000000000000000000000000");
            writer.WriteStartObject("frame");
            writer.WriteString("id", "g-9223372036854775808");
            writer.WriteString("type", "follow_up");
            writer.WriteString("message", message);
            WriteImages(writer, images);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        if (count.Length + 1 > RpcFrameDecoder.MaxFrameBytes)
            throw new ArgumentException($"The message is {count.Length + 1} bytes; omp accepts at most {RpcFrameDecoder.MaxFrameBytes} per line. Shorten it or remove attachments.");
    }

    private sealed class ByteCounter : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public static async Task<(IReadOnlyList<(bool Steering, string Text, IReadOnlyList<(string MimeType, byte[] Data)> Images)> Messages, bool Truncated, bool ImagesDropped)>
        AbortAndRestoreQueueAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var response = Ok(await c.RequestAsync("abort_and_restore_queue", timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false));
        if (response.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("steering", out var steering) || steering.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("followUp", out var followUp) || followUp.ValueKind != JsonValueKind.Array)
            throw new RpcCommandException("abort_and_restore_queue", "omp returned no recovery payload; local input has been kept.");
        var messages = new List<(bool, string, IReadOnlyList<(string, byte[])>)>();
        foreach (var (queue, isSteering) in new[] { (steering, true), (followUp, false) })
            foreach (var entry in queue.EnumerateArray())
            {
                var images = new List<(string, byte[])>();
                if (entry.TryGetProperty("images", out var imageArray) && imageArray.ValueKind == JsonValueKind.Array)
                    foreach (var image in imageArray.EnumerateArray())
                        images.Add((image.GetProperty("mimeType").GetString() ?? "image/png", image.GetProperty("data").GetBytesFromBase64()));
                messages.Add((isSteering, entry.GetProperty("text").GetString() ?? "", images));
            }
        return (messages, data.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
            data.TryGetProperty("imagesDropped", out var dropped) && dropped.ValueKind == JsonValueKind.True);
    }

    /// <summary>
    /// Withdraws one pending steer (<paramref name="steering"/>) or follow-up by the text it was queued with (omp
    /// 18.4.4+). <c>Removed</c> is false when omp had already delivered it; its images come back (omp 18.6.3+) so the
    /// message can return to the editor whole.
    /// </summary>
    public static async Task<(bool Removed, IReadOnlyList<(string MimeType, byte[] Data)> Images)> RemoveQueuedMessageAsync(
        this RpcConnection c, string message, bool steering, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("remove_queued_message", w =>
        {
            w.WriteString("message", message);
            w.WriteString("queue", steering ? "steering" : "followUp");
        }, ct: ct).ConfigureAwait(false));
        if (r.Data is not { ValueKind: JsonValueKind.Object } d) return (false, []);
        var removed = d.TryGetProperty("removed", out var rm) && rm.ValueKind == JsonValueKind.True;
        var images = new List<(string, byte[])>();
        if (d.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in imgs.EnumerateArray())
            {
                if (i.ValueKind != JsonValueKind.Object || !i.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) continue;
                var mime = i.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "image/png" : "image/png";
                try { images.Add((mime, data.GetBytesFromBase64())); }
                catch (FormatException) { }
            }
        }
        return (removed, images);
    }

    /// <summary>
    /// Moves the first pending follow-up queued with <paramref name="message"/> to the end of the steering queue, so omp
    /// delivers it at its next step (omp 18.4.6+). False when omp had already delivered it or never held it.
    /// </summary>
    public static async Task<bool> PromoteQueuedMessageAsync(this RpcConnection c, string message, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("promote_queued_message", w => w.WriteString("message", message), ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("promoted", out var p) && p.ValueKind == JsonValueKind.True;
    }

    /// <summary>Stops the current run, then sends a prompt; answered after admission, like <c>prompt</c>.
    /// omp waits for the aborted run to go idle first, so this may take as long as <see cref="AbortAsync"/>.</summary>
    public static async Task<RpcResponse> AbortAndPromptAsync(this RpcConnection c, string message, IReadOnlyList<(string MimeType, byte[] Data)> images, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("abort_and_prompt", w =>
        {
            w.WriteString("message", message);
            WriteImages(w, images);
        }, timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false));

    private static void WriteImages(System.Text.Json.Utf8JsonWriter w, IReadOnlyList<(string MimeType, byte[] Data)> images)
    {
        if (images.Count == 0) return;
        w.WriteStartArray("images");
        foreach (var (mime, data) in images)
        {
            w.WriteStartObject();
            w.WriteString("type", "image");
            w.WriteBase64String("data", data);
            w.WriteString("mimeType", mime);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>omp answers once the agent is idle (after the aborted run's agent_end), and queues behind a long compact/login.</summary>
    public static async Task<RpcResponse> AbortAsync(this RpcConnection c, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("abort", timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false));

    /// <summary>One chronological page from the tail, using the owned Ombra history contract. Cursors stay opaque.</summary>
    public static async Task<OmpMessagesPage> GetMessagesPageAsync(this RpcConnection c, string? cursor = null, int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        var r = await c.RequestAsync("get_messages_page", w =>
        {
            w.WriteString("direction", "backward");
            w.WriteNumber("limit", limit);
            if (cursor is not null) w.WriteString("cursor", cursor);
        }, ct: ct).ConfigureAwait(false);
        if (!r.Success && r.Error?.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase) == true)
            throw new NotSupportedException(OmpMessagesPage.IncompatibleRuntime);
        Ok(r);
        var page = OmpMessagesPage.Parse(r);
        if (page.Messages.GetArrayLength() > limit) throw new InvalidDataException("omp returned more messages than the requested page limit.");
        return page;
    }

    public static async Task<JsonElement> GetAvailableCommandsAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_available_commands", ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("commands", out var cmds) ? cmds : default;
    }

    /// <summary>Starts a fresh session in the same project. Returns false when an extension cancelled it.</summary>
    public static async Task<bool> NewSessionAsync(this RpcConnection c, CancellationToken ct = default) =>
        !Cancelled(Ok(await c.RequestAsync("new_session", timeout: SessionChangeTimeout, ct: ct).ConfigureAwait(false)));

    /// <summary>
    /// Loads another saved session into this omp. omp 18.2.0 rejects a session recorded in a different working
    /// directory (the host must restart omp there instead). Returns false when an extension cancelled it.
    /// </summary>
    public static async Task<bool> SwitchSessionAsync(this RpcConnection c, string sessionPath, CancellationToken ct = default) =>
        !Cancelled(Ok(await c.RequestAsync("switch_session", w => w.WriteString("sessionPath", sessionPath), timeout: SessionChangeTimeout, ct: ct).ConfigureAwait(false)));

    /// <summary>
    /// omp's <c>fork</c>: a new session file with this conversation, the old one kept as it is. With
    /// <paramref name="entryId"/> (a <c>message</c> entry of <c>get_entries</c>) only the path up to that message, inclusive
    /// (omp keeps a tool-call batch whole). Returns false when an extension cancelled it.
    /// </summary>
    /// <exception cref="RpcCommandException">Code <c>session_busy</c>: omp is not idle (a reply, a command, a background
    /// task could still write into the transcript).</exception>
    public static async Task<bool> ForkAsync(this RpcConnection c, string? entryId, CancellationToken ct = default) =>
        !Cancelled(Ok(await c.RequestAsync("fork", entryId is null ? null : w => w.WriteString("entryId", entryId), timeout: SessionChangeTimeout, ct: ct).ConfigureAwait(false)));

    /// <summary>omp's <c>get_entries</c>: every entry of the session file in append order (all branches) and the current
    /// leaf; the data object (<c>entries</c>, <c>leafId</c>).</summary>
    public static async Task<JsonElement> GetEntriesAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_entries", timeout: TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d ? d : throw new InvalidDataException("omp answered get_entries without data.");
    }

    /// <summary>omp always answers session changes, but extensions may ask the user first (session_before_switch).</summary>
    private static readonly TimeSpan SessionChangeTimeout = TimeSpan.FromMinutes(10);

    public static async Task SetSessionNameAsync(this RpcConnection c, string name, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("set_session_name", w => w.WriteString("name", name), ct: ct).ConfigureAwait(false));

    public static async Task<IReadOnlyList<OmpModel>> GetAvailableModelsAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_available_models", timeout: TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false));
        if (r.Data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("models", out var ms) || ms.ValueKind != JsonValueKind.Array) return [];
        var list = new List<OmpModel>();
        foreach (var m in ms.EnumerateArray())
        {
            if (Str(m, "provider") is not { } provider || Str(m, "id") is not { } id) continue;
            var images = m.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array
                && input.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "image");
            list.Add(new OmpModel(provider, id, Str(m, "name") ?? id,
                m.TryGetProperty("reasoning", out var re) && re.ValueKind == JsonValueKind.True, images,
                m.TryGetProperty("contextWindow", out var cw) && cw.ValueKind == JsonValueKind.Number && cw.TryGetInt64(out var n) ? n : 0)
            { BaseUrl = Str(m, "baseUrl") });
        }
        return list;
    }

    public static async Task SetModelAsync(this RpcConnection c, string provider, string modelId, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("set_model", w =>
        {
            w.WriteString("provider", provider);
            w.WriteString("modelId", modelId);
        }, ct: ct).ConfigureAwait(false));

    /// <summary>
    /// The levels the live model accepts, <c>off</c> first (omp 18.8.0 <c>get_available_thinking_levels</c>; older omp
    /// has no such command and answers with an error).
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetAvailableThinkingLevelsAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_available_thinking_levels", ct: ct).ConfigureAwait(false));
        if (r.Data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("levels", out var ls) || ls.ValueKind != JsonValueKind.Array) return [];
        return [.. ls.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!)];
    }

    /// <summary>off | minimal | low | medium | high | xhigh | max (omp validates).</summary>
    public static async Task SetThinkingLevelAsync(this RpcConnection c, string level, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("set_thinking_level", w => w.WriteString("level", level), ct: ct).ConfigureAwait(false));

    public static async Task<IReadOnlyList<OmpLoginProvider>> GetLoginProvidersAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_login_providers", ct: ct).ConfigureAwait(false));
        if (r.Data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("providers", out var ps) || ps.ValueKind != JsonValueKind.Array) return [];
        return [.. ps.EnumerateArray()
            .Where(p => Str(p, "id") is not null)
            .Select(p => new OmpLoginProvider(Str(p, "id")!, Str(p, "name") ?? Str(p, "id")!,
                p.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True,
                p.TryGetProperty("authenticated", out var au) && au.ValueKind == JsonValueKind.True))];
    }

    /// <summary>
    /// Runs omp's sign-in flow for a provider. It asks the host to open a URL (<c>open_url</c>) and may ask for a
    /// code (extension dialogs); the response comes when the flow ends, so the deadline is generous.
    /// </summary>
    public static async Task LoginAsync(this RpcConnection c, string providerId, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("login", w => w.WriteString("providerId", providerId), timeout: TimeSpan.FromMinutes(15), ct: ct).ConfigureAwait(false));

    /// <summary>The credentials <see cref="LogoutAsync"/> can remove for a provider, active first (omp 18.8.0).</summary>
    public static async Task<IReadOnlyList<OmpLogoutAccount>> GetLogoutAccountsAsync(this RpcConnection c, string providerId, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_logout_accounts", w => w.WriteString("providerId", providerId), ct: ct).ConfigureAwait(false));
        if (r.Data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("accounts", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var accounts = new List<OmpLogoutAccount>();
        foreach (var a in list.EnumerateArray())
        {
            if (!a.TryGetProperty("credentialId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var credentialId)) continue;
            accounts.Add(new OmpLogoutAccount(credentialId, Str(a, "label") ?? $"#{credentialId}", Str(a, "detail") ?? "", Str(a, "type") ?? "",
                a.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True));
        }
        return accounts;
    }

    /// <summary>
    /// Removes one stored credential (omp 18.8.0 <c>logout</c>); fails when it is no longer stored. Returns the source
    /// that still signs omp in to the provider (an environment variable, another stored credential…), if any.
    /// </summary>
    public static async Task<string?> LogoutAsync(this RpcConnection c, string providerId, long credentialId, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("logout", w => { w.WriteString("providerId", providerId); w.WriteNumber("credentialId", credentialId); }, ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d ? Str(d, "remainingSource") : null;
    }

    /// <summary>The session's MCP servers with connection, tool count and sign-in state (owned runtime <c>get_mcp_servers</c>).</summary>
    public static async Task<IReadOnlyList<OmpMcpServer>> GetMcpServersAsync(this RpcConnection c, CancellationToken ct = default) =>
        McpServers(Ok(await c.RequestAsync("get_mcp_servers", ct: ct).ConfigureAwait(false)));

    /// <summary>
    /// Signs in to an MCP server with OAuth. omp sends the authorization URL as <c>open_url</c> with <c>mcpServer</c>
    /// and answers when the browser step ends (or <see cref="McpLoginCancelAsync"/>: code <c>mcp_login_cancelled</c>).
    /// </summary>
    public static async Task<OmpMcpServer?> McpLoginAsync(this RpcConnection c, string serverName, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("mcp_login", w => w.WriteString("serverName", serverName), timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d ? McpServer(d) : null;
    }

    /// <summary>Stops a running <see cref="McpLoginAsync"/>; false when none was running.</summary>
    public static async Task<bool> McpLoginCancelAsync(this RpcConnection c, string serverName, CancellationToken ct = default) =>
        Cancelled(Ok(await c.RequestAsync("mcp_login_cancel", w => w.WriteString("serverName", serverName), ct: ct).ConfigureAwait(false)));

    /// <summary>
    /// Reconnects one server from its config as it is now, or with null reads every MCP config file again (servers
    /// added, edited or removed in mcp.json) and reconnects all; returns every server's state after.
    /// </summary>
    public static async Task<IReadOnlyList<OmpMcpServer>> McpReconnectAsync(this RpcConnection c, string? serverName, CancellationToken ct = default) =>
        McpServers(Ok(await c.RequestAsync("mcp_reconnect", w => { if (serverName is not null) w.WriteString("serverName", serverName); },
            timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false)));

    /// <summary>Signs out of an MCP server: omp forgets its stored sign-in and disconnects it (owned runtime <c>mcp_logout</c>).</summary>
    public static async Task<OmpMcpServer?> McpLogoutAsync(this RpcConnection c, string serverName, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("mcp_logout", w => w.WriteString("serverName", serverName), timeout: TimeSpan.FromMinutes(1), ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d ? McpServer(d) : null;
    }

    private static IReadOnlyList<OmpMcpServer> McpServers(RpcResponse r) =>
        r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("servers", out var s) && s.ValueKind == JsonValueKind.Array
            ? [.. s.EnumerateArray().Select(McpServer).OfType<OmpMcpServer>()]
            : [];

    private static OmpMcpServer? McpServer(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && Str(e, "name") is { } name
            ? new(name, Str(e, "transport") ?? "stdio", Str(e, "scope"), Str(e, "status") ?? "disconnected",
                e.TryGetProperty("toolCount", out var t) && t.TryGetInt32(out var n) ? n : 0, Str(e, "error"), Str(e, "auth") ?? "none")
            : null;

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Cancelled(RpcResponse r) =>
        r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("cancelled", out var x) && x.ValueKind == JsonValueKind.True;

    /// <summary>Answers a <c>select</c>, <c>input</c> or <c>editor</c> request.</summary>
    public static Task SendExtensionUiValueAsync(this RpcConnection c, string requestId, string value, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w => w.WriteString("value", value), ct);

    /// <summary>Denies a structured tool approval (one with <c>approval</c>) with the user's reason for the agent.</summary>
    public static Task SendExtensionUiDenyAsync(this RpcConnection c, string requestId, string reason, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w =>
        {
            w.WriteString("value", "Deny");
            w.WriteString("reason", reason);
        }, ct);

    /// <summary>Answers a <c>confirm</c> request.</summary>
    public static Task SendExtensionUiConfirmAsync(this RpcConnection c, string requestId, bool confirmed, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w => w.WriteBoolean("confirmed", confirmed), ct);

    /// <summary>Answers a dialog-style <c>extension_ui_request</c> with "cancelled".</summary>
    public static Task CancelExtensionUiAsync(this RpcConnection c, string requestId, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w => w.WriteBoolean("cancelled", true), ct);

    /// <summary>Answers an <c>elicit</c> form: <c>accept</c> with the field values, or <c>decline</c> (no content).</summary>
    public static Task SendExtensionUiElicitAsync(this RpcConnection c, string requestId, bool accept, System.Text.Json.Nodes.JsonObject? content, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w =>
        {
            w.WriteString("action", accept ? "accept" : "decline");
            if (accept && content is not null)
            {
                w.WritePropertyName("content");
                content.WriteTo(w);
            }
        }, ct);

    /// <summary>
    /// Answers an <c>ask</c> form: one answer per question, in the questions' order, with the chosen option labels and
    /// the free text (left out when empty).
    /// </summary>
    public static Task SendExtensionUiAskAsync(this RpcConnection c, string requestId,
        IReadOnlyList<(string Id, IReadOnlyList<string> SelectedOptions, string? CustomInput)> answers, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w =>
        {
            w.WriteStartArray("answers");
            foreach (var (id, selected, custom) in answers)
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteStartArray("selectedOptions");
                foreach (var label in selected) w.WriteStringValue(label);
                w.WriteEndArray();
                if (!string.IsNullOrEmpty(custom)) w.WriteString("customInput", custom);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, ct);

    /// <summary>
    /// Opts in to (or out of) the <c>ask</c> form: omp then sends every question of one ask tool call as one
    /// <c>extension_ui_request</c> with method <c>ask</c> instead of a select per question. Returns the applied setting.
    /// An omp without the command throws <see cref="RpcCommandException"/>.
    /// </summary>
    public static async Task<bool> SetAskDialogAsync(this RpcConnection c, bool enabled, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("set_ask_dialog", w => w.WriteBoolean("enabled", enabled), ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Replaces the session's todo phases (<c>[{name, tasks:[{content, status, …}]}]</c>); returns omp's phases after the
    /// change (<c>todoPhases</c>). An omp without the command throws <see cref="RpcCommandException"/>.
    /// </summary>
    public static async Task<JsonElement> SetTodosAsync(this RpcConnection c, System.Text.Json.Nodes.JsonArray phases, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("set_todos", w =>
        {
            w.WritePropertyName("phases");
            phases.WriteTo(w);
        }, ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("todoPhases", out var tp) ? tp.Clone() : default;
    }

    private static RpcResponse Ok(RpcResponse r) => r.Success ? r : throw new RpcCommandException(r.Command, r.Error ?? "unknown error", r.Code, r.Data);
}
