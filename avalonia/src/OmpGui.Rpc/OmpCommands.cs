using System.Text.Json;

namespace OmpGui.Rpc;

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
            d.TryGetProperty("contextUsage", out var cu) && cu.ValueKind == JsonValueKind.Object && cu.TryGetProperty("percent", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : null);
    }
}

/// <summary>A model omp can use now (<c>get_available_models</c>: only providers with credentials are listed).</summary>
public sealed record OmpModel(string Provider, string Id, string Name, bool Reasoning, bool AcceptsImages, long ContextWindow)
{
    public string Key => $"{Provider}/{Id}";
}

/// <summary>A provider omp can sign in to (<c>get_login_providers</c>).</summary>
public sealed record OmpLoginProvider(string Id, string Name, bool Available, bool Authenticated);

/// <summary>The omp RPC commands the client uses (docs/rpc.md "Command Schema").</summary>
public static class OmpCommands
{
    public static async Task<OmpSessionState> GetStateAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_state", ct: ct).ConfigureAwait(false));
        return OmpSessionState.Parse(r.Data ?? default);
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

    /// <summary>Delivered to the agent at the next tool boundary of the current run.</summary>
    public static async Task SteerAsync(this RpcConnection c, string message, IReadOnlyList<(string MimeType, byte[] Data)> images, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("steer", w => { w.WriteString("message", message); WriteImages(w, images); }, ct: ct).ConfigureAwait(false));

    /// <summary>Delivered when the agent would otherwise stop.</summary>
    public static async Task FollowUpAsync(this RpcConnection c, string message, IReadOnlyList<(string MimeType, byte[] Data)> images, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("follow_up", w => { w.WriteString("message", message); WriteImages(w, images); }, ct: ct).ConfigureAwait(false));

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

    /// <summary>The whole current branch; use get_messages_page for large histories.</summary>
    public static async Task<JsonElement> GetMessagesAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_messages", ct: ct).ConfigureAwait(false));
        return r.Data is { } d && d.TryGetProperty("messages", out var m) ? m : default;
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
                m.TryGetProperty("contextWindow", out var cw) && cw.ValueKind == JsonValueKind.Number && cw.TryGetInt64(out var n) ? n : 0));
        }
        return list;
    }

    public static async Task SetModelAsync(this RpcConnection c, string provider, string modelId, CancellationToken ct = default) =>
        Ok(await c.RequestAsync("set_model", w =>
        {
            w.WriteString("provider", provider);
            w.WriteString("modelId", modelId);
        }, ct: ct).ConfigureAwait(false));

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

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Cancelled(RpcResponse r) =>
        r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("cancelled", out var x) && x.ValueKind == JsonValueKind.True;

    /// <summary>Answers a <c>select</c>, <c>input</c> or <c>editor</c> request.</summary>
    public static Task SendExtensionUiValueAsync(this RpcConnection c, string requestId, string value, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w => w.WriteString("value", value), ct);

    /// <summary>Answers a <c>confirm</c> request.</summary>
    public static Task SendExtensionUiConfirmAsync(this RpcConnection c, string requestId, bool confirmed, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w => w.WriteBoolean("confirmed", confirmed), ct);

    /// <summary>Answers a dialog-style <c>extension_ui_request</c> with "cancelled".</summary>
    public static Task CancelExtensionUiAsync(this RpcConnection c, string requestId, CancellationToken ct = default) =>
        c.SendAsync("extension_ui_response", requestId, w => w.WriteBoolean("cancelled", true), ct);

    private static RpcResponse Ok(RpcResponse r) => r.Success ? r : throw new RpcCommandException(r.Command, r.Error ?? "unknown error");
}
