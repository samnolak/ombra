using System.Text.Json;
using Ombra.ClientCore.Browser;

namespace Ombra.App.Nightly;

/// <summary>
/// The questions a Nightly page asks (desktop <c>ZenEmbedDialogs.sys.mjs</c>: alert/confirm/prompt/beforeunload,
/// sign-in, permissions, file pickers, downloads), answered through the tab's <see cref="BrowserPageHooks"/> exactly as
/// the built-in preview's are: the same cards (<see cref="DialogBroker"/>), the same remembered site permissions, the
/// same download destination flow. <see cref="AnswerAsync"/> turns Nightly's JSON question into a request and the
/// answer back into the JSON Nightly reads. Engine-neutral apart from the JSON, so it is unit tested without Nightly.
/// </summary>
internal static class NightlyDialogs
{
    /// <summary>A download whose path a dialog chose: <see cref="BrowserDownload"/> to update from Nightly's reports.</summary>
    public sealed record StartedDownload(uint DialogId, BrowserDownload Item, BrowserPageHooks Hooks);

    /// <summary>
    /// Answers question <paramref name="json"/> of a page at <paramref name="pageUrl"/>. Cancelling
    /// <paramref name="ct"/> (Nightly withdrew it) closes the card with the nobody-answered answer.
    /// <paramref name="downloadStarted"/> gets a download once a path was chosen for it (its progress follows).
    /// Never throws for a bad question: Nightly gets the nobody-answered answer.
    /// </summary>
    public static async Task<string> AnswerAsync(BrowserPageHooks hooks, uint dialogId, string json, Uri? pageUrl,
        Action<StartedDownload>? downloadStarted, CancellationToken ct)
    {
        JsonElement q;
        try
        {
            using var doc = JsonDocument.Parse(json);
            q = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return "{}";
        }
        bool agent;
        try { agent = hooks.AgentActive(); }
        catch (Exception) { agent = false; }
        var origin = Str(q, "origin");
        var page = Uri.TryCreate(origin, UriKind.Absolute, out var o) && pageUrl is null ? o : pageUrl;
        var broker = hooks.Broker();
        switch (Str(q, "kind"))
        {
            case "alert":
                if (broker is not null) await broker.AlertAsync(new AlertRequest(Str(q, "message") ?? "") { PageUrl = page, CausedByAgent = agent }, ct).ConfigureAwait(false);
                return "{}";
            case "confirm":
                return Ok(broker is not null && await broker.ConfirmAsync(new ConfirmRequest(Str(q, "message") ?? "") { PageUrl = page, CausedByAgent = agent }, ct).ConfigureAwait(false));
            case "beforeunload":
                // Nobody to ask: leave, as the preview does.
                return Ok(broker is null || await broker.BeforeUnloadAsync(new BeforeUnloadRequest(Str(q, "message")) { PageUrl = page, CausedByAgent = agent }, ct).ConfigureAwait(false));
            case "prompt":
            {
                var value = broker is null ? null
                    : await broker.PromptAsync(new PromptRequest(Str(q, "message") ?? "", Str(q, "default")) { PageUrl = page, CausedByAgent = agent }, ct).ConfigureAwait(false);
                return value is null ? Ok(false) : Write(w => { w.WriteBoolean("ok", true); w.WriteString("value", value); });
            }
            case "auth":
            {
                var request = new CredentialRequest(Str(q, "host") ?? "", (int)Num(q, "port"), NullIfEmpty(Str(q, "realm")), Bool(q, "proxy"), Bool(q, "failed"),
                    NullIfEmpty(Str(q, "user"))) { PageUrl = page, CausedByAgent = agent };
                var answer = await ((IBrowserAuthHandler)hooks).RequestCredentialsAsync(request, ct).ConfigureAwait(false);
                return answer is null ? Ok(false)
                    : Write(w => { w.WriteBoolean("ok", true); w.WriteString("user", answer.User); w.WriteString("password", answer.Password); });
            }
            case "permission":
            {
                var kind = Str(q, "permission") switch
                {
                    "geolocation" => PermissionKind.Geolocation,
                    "notifications" => PermissionKind.Notifications,
                    "camera" => PermissionKind.Camera,
                    "microphone" => PermissionKind.Microphone,
                    "camera-microphone" => PermissionKind.CameraAndMicrophone,
                    "storage-access" => PermissionKind.StorageAccess,
                    "clipboard" => PermissionKind.Clipboard,
                    _ => PermissionKind.Other,
                };
                var site = origin is { Length: > 0 } ? origin : page?.GetLeftPart(UriPartial.Authority) ?? "";
                PermissionAnswer result;
                if (site.Length > 0 && hooks.SiteSettings?.IsAlwaysAllowed(site, kind) == true) result = PermissionAnswer.AlwaysForSite;
                else if (broker is null || site.Length == 0) result = PermissionAnswer.Deny;
                else
                {
                    result = await broker.PermissionAsync(new PermissionRequest(kind, site) { PageUrl = page, CausedByAgent = agent }, ct).ConfigureAwait(false);
                    if (result == PermissionAnswer.AlwaysForSite) hooks.SiteSettings?.AlwaysAllow(site, kind);
                }
                var text = result switch { PermissionAnswer.AllowOnce => "once", PermissionAnswer.AlwaysForSite => "always", _ => "deny" };
                return Write(w => w.WriteString("answer", text));
            }
            case "file":
            {
                var accept = q.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : [];
                var files = broker is null ? null
                    : await broker.ChooseFilesAsync(new FileChooserRequest(Bool(q, "multiple"), accept, Bool(q, "directory")) { PageUrl = page, CausedByAgent = agent }, ct).ConfigureAwait(false);
                return Write(w =>
                {
                    if (files is not { Count: > 0 })
                    {
                        w.WriteNull("paths");
                        return;
                    }
                    w.WriteStartArray("paths");
                    foreach (var f in files) w.WriteStringValue(f);
                    w.WriteEndArray();
                });
            }
            case "download":
            {
                var url = Uri.TryCreate(Str(q, "url"), UriKind.Absolute, out var u) ? u : null;
                var size = (long)Num(q, "size");
                var item = new BrowserDownload(url, page)
                {
                    CausedByAgent = agent,
                    State = BrowserDownloadState.Asking,
                    FileName = BrowserDownloads.SafeFileName(Str(q, "name")),
                    TotalBytes = size > 0 ? size : -1,
                };
                hooks.RaiseDownloadChanged(item);
                string? path;
                try
                {
                    path = await hooks.ChooseDownloadPath(new DownloadRequest(item.FileName, size > 0 ? size : null, url) { PageUrl = page, CausedByAgent = agent }, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    path = null;
                }
                if (path is null || ct.IsCancellationRequested)
                {
                    item.State = BrowserDownloadState.Canceled;
                    hooks.RaiseDownloadChanged(item);
                    return Write(w => w.WriteNull("path"));
                }
                item.Path = path;
                item.State = BrowserDownloadState.Saving;
                downloadStarted?.Invoke(new StartedDownload(dialogId, item, hooks));
                hooks.RaiseDownloadChanged(item);
                return Write(w => w.WriteString("path", path));
            }
            default:
                return "{}";
        }
    }

    /// <summary>Nightly's report on a download (<c>{state, received, total, path, error}</c>) applied to <paramref name="item"/>;
    /// true when it ended (finished, failed or canceled).</summary>
    public static bool ApplyDownload(BrowserDownload item, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var received = (long)Num(r, "received");
            var total = (long)Num(r, "total");
            if (total > 0) item.TotalBytes = total;
            if (received >= 0) item.BytesReceived = received;
            if (Str(r, "path") is { Length: > 0 } path) item.Path = path;
            switch (Str(r, "state"))
            {
                case "finished":
                    if (item.TotalBytes <= 0 && item.Path is { } p && File.Exists(p)) item.TotalBytes = new FileInfo(p).Length;
                    if (item.TotalBytes > 0) item.BytesReceived = item.TotalBytes;
                    item.State = BrowserDownloadState.Finished;
                    break;
                case "failed":
                    item.Error = Str(r, "error") is { Length: > 0 } e ? e : Localization.Loc.T("The download failed.");
                    item.State = BrowserDownloadState.Failed;
                    break;
                case "canceled":
                    item.State = BrowserDownloadState.Canceled;
                    break;
                default:
                    item.State = BrowserDownloadState.Saving;
                    return false;
            }
            item.Canceller = null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Ok(bool ok) => ok ? "{\"ok\":true}" : "{\"ok\":false}";

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : -1;
}
