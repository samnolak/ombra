using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmpGui.ClientCore.Browser;

/// <summary>
/// One tab omp drives over Tern (a "block", <c>tern://block/&lt;n&gt;</c>): its <see cref="ITernPage"/>, the events omp
/// pulls (<c>events</c>, seq increasing), and the settings omp gives it — dialog policy, file-chooser preset, download
/// folder, sign-in credentials, allowed hosts. Ops (omp 18.8 tools/browser/tern/tern-tab.ts) arrive concurrently; each
/// answers a JSON result or throws <see cref="TernException"/>.
/// </summary>
/// <remarks>
/// Dialogs (<see cref="DialogBroker"/>, one per tab): what omp's action caused is omp's. An alert is accepted at once; a
/// confirm, prompt or "Leave page?" follows the <c>dialogs</c> policy (<c>accept</c>, <c>dismiss</c>, or
/// <c>default</c> = held for omp's <c>dialog</c> op and reported as a <c>dialog</c> event with <c>handled: null</c>).
/// A held one omp leaves unanswered goes to the user's card after <see cref="HandOffAfter"/>, so the page never stays
/// frozen. A file chooser omp caused gets the <c>files</c> preset (then a <c>chooser</c> event), else goes to the user;
/// permissions and popups always go to the user. Sign-in is the user's, except that credentials omp set with
/// <c>credentials</c> answer a first challenge. While a JavaScript dialog is open the page's scripts wait, so <c>eval</c>
/// answers <c>failed</c> naming the dialog instead of stalling.
/// </remarks>
public sealed class TernBlock : ITernEventSink
{
    /// <summary>Events kept for omp (omp keeps as many itself).</summary>
    internal const int EventLimit = 4000;

    private readonly Lock _gate = new();
    private readonly List<JsonObject> _events = [];
    private long _seq;
    private long _firstKept = 1;
    private readonly HashSet<long> _seen = [];
    private readonly List<TaskCompletionSource> _modalWaiters = [];
    private string _policy = "default";
    private BrowserDialog? _held;
    private IReadOnlyList<string>? _files;
    private CredentialAnswer? _credentials;
    private string[] _allowed = [];
    private ITernPage? _page;
    private DialogBroker? _broker;
    private volatile bool _closed;

    internal TernBlock(int id, int owner)
    {
        Id = id;
        Owner = owner;
    }

    /// <summary>The block id omp addresses the tab by.</summary>
    public int Id { get; }

    /// <summary>The <c>TERN_PANE</c> of the omp that opened it.</summary>
    public int Owner { get; }

    /// <summary>Where omp wants downloads its actions start (<c>downloads</c> op), or null: then the user is asked.</summary>
    public string? DownloadDirectory { get; private set; }

    /// <summary>How long a confirm/prompt held for omp waits before the user gets its card.</summary>
    public TimeSpan HandOffAfter { get; set; } = TimeSpan.FromSeconds(60);

    internal ITernPage Page => _page ?? throw new InvalidOperationException("The block has no page yet.");

    internal bool IsClosed => _closed || _page?.IsClosed == true;

    internal void Attach(ITernPage page)
    {
        _page = page;
        _broker = page.Dialogs;
        if (_broker is not null)
        {
            _broker.Changed += OnDialogsChanged;
            OnDialogsChanged(_broker, EventArgs.Empty);
        }
    }

    /// <summary>The block is gone (closed by omp, or omp's connection ended).</summary>
    internal void Detach()
    {
        _closed = true;
        if (_broker is not null) _broker.Changed -= OnDialogsChanged;
        BrowserDialog? held;
        List<TaskCompletionSource> waiters;
        lock (_gate)
        {
            held = _held;
            _held = null;
            waiters = [.. _modalWaiters];
            _modalWaiters.Clear();
        }
        // A question omp held goes to the user rather than freezing the page
        if (held is not null && !held.IsCompleted) _broker?.HandToUser(held);
        foreach (var w in waiters) w.TrySetResult();
    }

    // ── Events ──

    public void Post(string type, JsonObject? fields = null)
    {
        var e = fields ?? new JsonObject();
        lock (_gate)
        {
            e["seq"] = ++_seq;
            e["type"] = type;
            _events.Add(e);
            if (_events.Count > EventLimit)
            {
                var drop = _events.Count - EventLimit;
                _events.RemoveRange(0, drop);
                _firstKept += drop;
            }
        }
    }

    /// <summary>Whether omp may load <paramref name="url"/> (<c>allow</c>); a refused one is reported as a <c>blocked</c>
    /// event. Pages other than http/https (about:blank) are never refused.</summary>
    public bool AllowsNavigation(Uri url)
    {
        string[] allowed;
        lock (_gate) allowed = _allowed;
        if (allowed.Length == 0 || url.Scheme is not ("http" or "https")) return true;
        var host = url.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        foreach (var pattern in allowed)
        {
            if (!pattern.StartsWith("*.", StringComparison.Ordinal) ? host == pattern
                : host == pattern[2..] || host.EndsWith(pattern[1..], StringComparison.Ordinal))
                return true;
        }
        Post("blocked", new JsonObject { ["url"] = url.AbsoluteUri });
        return false;
    }

    private JsonObject Events(long after)
    {
        var list = new JsonArray();
        long dropped;
        lock (_gate)
        {
            dropped = Math.Max(0, _firstKept - 1 - after);
            foreach (var e in _events)
                if (e["seq"]!.GetValue<long>() > after) list.Add(e.DeepClone());
        }
        var result = new JsonObject { ["events"] = list };
        if (dropped > 0) result["dropped"] = dropped;
        return result;
    }

    // ── Ops ──

    internal async Task<JsonObject> DispatchAsync(string op, JsonObject p, CancellationToken ct)
    {
        if (op == "events") return Events((long)(Num(p, "after") ?? 0));
        if (_closed) throw new TernException(TernException.NotFound, $"Tern block {Id} is closed.");
        var page = Page;
        if (page.IsClosed) throw new TernException(TernException.WindowClosed, "The user closed this tab in the OMP GUI browser: open a new one if the page is still needed.");
        switch (op)
        {
            case "state":
            {
                var s = await page.GetStateAsync(ct).ConfigureAwait(false);
                return new JsonObject
                {
                    ["url"] = s.Url,
                    ["title"] = s.Title,
                    ["width"] = s.Width,
                    ["height"] = s.Height,
                    ["loading"] = s.Loading,
                    ["back"] = s.CanGoBack,
                    ["forward"] = s.CanGoForward,
                };
            }
            case "goto":
            {
                var address = Str(p, "url") ?? throw Invalid("goto needs a url");
                if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https" or "about"))
                    throw new TernException(TernException.Invalid, $"The OMP GUI browser opens http and https pages only, not {address}.");
                if (!AllowsNavigation(url)) return [];
                using (page.BeginAgentAction()) await page.NavigateAsync(url, ct).ConfigureAwait(false);
                return [];
            }
            case "nav":
            {
                var go = Str(p, "go") switch
                {
                    "back" => TernHistory.Back,
                    "forward" => TernHistory.Forward,
                    "reload" => TernHistory.Reload,
                    var other => throw Invalid($"nav go must be back, forward or reload, not {other ?? "nothing"}"),
                };
                using (page.BeginAgentAction()) await page.HistoryAsync(go, ct).ConfigureAwait(false);
                return [];
            }
            case "eval":
                return await EvaluateAsync(page, p, ct).ConfigureAwait(false);
            case "input":
            {
                var steps = Steps(p["events"] as JsonArray ?? throw Invalid("input needs events"));
                using (page.BeginAgentAction())
                {
                    await page.InputAsync(steps, ct).ConfigureAwait(false);
                    await SettleAsync(page, ct).ConfigureAwait(false);
                }
                return [];
            }
            case "capture":
            {
                var format = Str(p, "format") ?? "png";
                if (format is not ("png" or "jpeg")) throw Invalid($"capture format must be png or jpeg, not {format}");
                TernRect? rect = null;
                if (p["rect"] is JsonObject r)
                    rect = new TernRect(Num(r, "x") ?? 0, Num(r, "y") ?? 0, Num(r, "width") ?? 0, Num(r, "height") ?? 0);
                if (rect is { } box && (box.Width <= 0 || box.Height <= 0)) throw Invalid("capture rect needs a width and a height");
                var scale = Num(p, "scale") ?? 1;
                if (scale is <= 0 or > 8) throw Invalid("capture scale must be above 0 and at most 8");
                var request = new TernCaptureRequest(scale, format == "jpeg", (int)Math.Clamp(Num(p, "quality") ?? 80, 1, 100), rect, Bool(p, "full") == true);
                var bytes = await page.CaptureAsync(request, ct).ConfigureAwait(false);
                return new JsonObject { ["data"] = Convert.ToBase64String(bytes) };
            }
            case "pdf":
                return new JsonObject { ["data"] = Convert.ToBase64String(await page.PdfAsync(ct).ConfigureAwait(false)) };
            case "viewport":
            {
                var width = (int)Math.Round(Num(p, "width") ?? 0);
                var height = (int)Math.Round(Num(p, "height") ?? 0);
                if (width is < 1 or > 10000 || height is < 1 or > 10000) throw Invalid("viewport needs a width and a height from 1 to 10000");
                await page.SetViewportAsync(width, height, ct).ConfigureAwait(false);
                return [];
            }
            case "scripts":
                await page.SetScriptsAsync(Scripts(p["scripts"] as JsonArray ?? throw Invalid("scripts needs a list")), ct).ConfigureAwait(false);
                return [];
            case "agent":
                await page.SetUserAgentAsync(Str(p, "value") is { Length: > 0 } agent ? agent : null, ct).ConfigureAwait(false);
                return [];
            case "appearance":
            {
                var scheme = Str(p, "value");
                if (scheme is not (null or "light" or "dark")) throw Invalid($"appearance must be light, dark or null, not {scheme}");
                await page.SetAppearanceAsync(scheme, ct).ConfigureAwait(false);
                return [];
            }
            case "cookies":
            {
                var list = new JsonArray();
                foreach (var c in await page.GetCookiesAsync(ct).ConfigureAwait(false))
                    list.Add(new JsonObject
                    {
                        ["name"] = c.Name,
                        ["value"] = c.Value,
                        ["domain"] = c.Domain,
                        ["path"] = c.Path,
                        ["expires"] = c.Expires,
                        ["httpOnly"] = c.HttpOnly,
                        ["secure"] = c.Secure,
                        ["sameSite"] = c.SameSite,
                    });
                return new JsonObject { ["cookies"] = list };
            }
            case "setCookie":
            {
                var c = p["cookie"] as JsonObject ?? throw Invalid("setCookie needs a cookie");
                var cookie = new TernCookie(Str(c, "name") ?? throw Invalid("a cookie needs a name"), Str(c, "value") ?? "",
                    Str(c, "domain") ?? throw Invalid("a cookie needs a domain"), Str(c, "path") ?? "/", Num(c, "expires") ?? -1,
                    Bool(c, "httpOnly") == true, Bool(c, "secure") == true, Str(c, "sameSite"));
                await page.SetCookieAsync(cookie, ct).ConfigureAwait(false);
                return [];
            }
            case "deleteCookie":
                await page.DeleteCookieAsync(Str(p, "name") ?? throw Invalid("deleteCookie needs a name"), Str(p, "domain") ?? "",
                    Str(p, "path") ?? "/", ct).ConfigureAwait(false);
                return [];
            case "edit":
                switch (Str(p, "action"))
                {
                    case "copy":
                        using (page.BeginAgentAction()) await page.CopyAsync(ct).ConfigureAwait(false);
                        return [];
                    case "paste":
                        // Reading the user's clipboard into a page is the user's call (macOS asks before an app reads it)
                        throw new TernException(TernException.Unsupported, "Pasting the user's clipboard is not available in the OMP GUI browser: type the text instead.");
                    default:
                        throw Invalid("edit action must be copy or paste");
                }
            case "dialogs":
            {
                var policy = Str(p, "policy") ?? "default";
                if (policy is not ("accept" or "dismiss" or "default")) throw Invalid($"dialogs policy must be accept, dismiss or default, not {policy}");
                lock (_gate) _policy = policy;
                return [];
            }
            case "dialog":
                AnswerHeld(Bool(p, "accept") == true, Str(p, "text"));
                return [];
            case "files":
            {
                IReadOnlyList<string>? files = null;
                if (p["paths"] is JsonArray paths)
                {
                    var list = new List<string>();
                    foreach (var item in paths)
                    {
                        var path = item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : throw Invalid("files paths must be strings");
                        if (!Path.IsPathFullyQualified(path)) throw Invalid($"files needs absolute paths, not {path}");
                        list.Add(path);
                    }
                    files = list;
                }
                lock (_gate) _files = files;
                return [];
            }
            case "downloads":
            {
                var dir = Str(p, "dir") ?? throw Invalid("downloads needs a dir");
                if (!Path.IsPathFullyQualified(dir)) throw Invalid($"downloads needs an absolute folder, not {dir}");
                Directory.CreateDirectory(dir);
                DownloadDirectory = dir;
                return [];
            }
            case "credentials":
            {
                var user = Str(p, "username");
                lock (_gate) _credentials = user is null ? null : new CredentialAnswer(user, Str(p, "password") ?? "");
                return [];
            }
            case "allow":
            {
                var hosts = new List<string>();
                foreach (var item in p["hosts"] as JsonArray ?? throw Invalid("allow needs hosts"))
                    if (item?.GetValueKind() == JsonValueKind.String && item.GetValue<string>().Trim().ToLowerInvariant().TrimEnd('.') is { Length: > 0 } h)
                        hosts.Add(h);
                lock (_gate) _allowed = [.. hosts];
                return [];
            }
            case "insecure":
                if (Bool(p, "value") != true) return [];
                throw new TernException(TernException.Unsupported, "The OMP GUI browser never accepts invalid TLS certificates.");
            default:
                throw new TernException(TernException.Unsupported, $"The OMP GUI browser does not know the Tern op {op}.");
        }
    }

    private async Task<JsonObject> EvaluateAsync(ITernPage page, JsonObject p, CancellationToken ct)
    {
        var function = Str(p, "function") ?? throw Invalid("eval needs a function");
        var args = p["args"] switch
        {
            null => "[]",
            JsonArray a => a.ToJsonString(),
            _ => throw Invalid("eval args must be a list"),
        };
        var world = Str(p, "world") switch
        {
            null or "page" => TernWorld.Page,
            "isolated" => TernWorld.Isolated,
            var other => throw Invalid($"eval world must be page or isolated, not {other}"),
        };
        var frame = Str(p, "frame") is { Length: > 0 } f ? f : null;
        ThrowIfModal();
        var modal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _modalWaiters.Add(modal);
        try
        {
            using var action = page.BeginAgentAction();
            var run = page.EvaluateAsync(function, args, world, frame, ct);
            if (await Task.WhenAny(run, modal.Task).ConfigureAwait(false) != run)
            {
                // The script waits behind a dialog it (or the page) opened: omp hears about the dialog now
                _ = run.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                ThrowIfModal();
                throw new TernException(TernException.Failed, "The tab closed while the script ran.");
            }
            var value = await run.ConfigureAwait(false);
            if (value is null) return [];
            try
            {
                return new JsonObject { ["value"] = JsonNode.Parse(value) };
            }
            catch (JsonException)
            {
                return new JsonObject { ["value"] = value };
            }
        }
        finally
        {
            lock (_gate) _modalWaiters.Remove(modal);
        }
    }

    /// <summary>How long input waits for the page to have handled it.</summary>
    internal static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After input: the page has handled it (an empty script in omp's world comes back, so the events before it ran),
    /// or it opened a dialog (reported by then), or a second passed. omp reads the page right after its input, so a
    /// click's confirm or navigation must already be known when the input answers.
    /// </summary>
    private async Task SettleAsync(ITernPage page, CancellationToken ct)
    {
        if (_broker?.CurrentModal is not null) return;
        var modal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _modalWaiters.Add(modal);
        try
        {
            var barrier = page.EvaluateAsync("function () { return 1; }", "[]", TernWorld.Isolated, null, ct);
            _ = barrier.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            await Task.WhenAny(barrier, modal.Task, Task.Delay(SettleLimit, ct)).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _modalWaiters.Remove(modal);
        }
    }

    private void ThrowIfModal()
    {
        if (_broker?.CurrentModal is not { } d) return;
        var whose = d.RoutedToAgent ? "answer it with tab.handleDialog() (or set tab.setDialogs())" : "the user is answering it";
        throw new TernException(TernException.Failed,
            $"The page shows a {d.KindName} dialog (\"{d.Message}\"), so its scripts wait: {whose}.");
    }

    // ── Dialogs ──

    private void OnDialogsChanged(object? sender, EventArgs e)
    {
        if (_broker is not { } broker || _closed) return;
        foreach (var d in broker.Pending)
        {
            lock (_gate)
            {
                if (!_seen.Add(d.Id)) continue;
            }
            Handle(broker, d);
        }
        // Scripts running now wait behind a dialog still open: their eval answers at once
        if (broker.CurrentModal is null) return;
        List<TaskCompletionSource> waiters;
        lock (_gate) waiters = [.. _modalWaiters];
        foreach (var w in waiters) w.TrySetResult();
    }

    private void Handle(DialogBroker broker, BrowserDialog d)
    {
        if (d.Kind == BrowserDialogKind.Credentials)
        {
            CredentialAnswer? credentials;
            lock (_gate) credentials = _credentials;
            if (credentials is not null && d.Request is CredentialRequest { FailedBefore: false }) d.Complete(credentials);
            return;
        }
        if (!d.RoutedToAgent) return;
        switch (d.Kind)
        {
            case BrowserDialogKind.Alert:
                Report(d, "accepted");
                d.Complete(null);
                return;
            case BrowserDialogKind.Confirm or BrowserDialogKind.Prompt or BrowserDialogKind.BeforeUnload:
            {
                string policy;
                lock (_gate) policy = _policy;
                if (policy == "default")
                {
                    lock (_gate) _held = d;
                    Report(d, null);
                    _ = HandOffLaterAsync(broker, d);
                    return;
                }
                Report(d, policy == "accept" ? "accepted" : "dismissed");
                Answer(d, policy == "accept", null);
                return;
            }
            case BrowserDialogKind.FileChooser:
            {
                IReadOnlyList<string>? files;
                lock (_gate)
                {
                    files = _files;
                    _files = null;
                }
                if (files is null)
                {
                    broker.HandToUser(d);
                    return;
                }
                d.Complete(files);
                Post("chooser");
                return;
            }
            default:
                // Permissions, popups, downloads omp has no folder for: the user decides
                broker.HandToUser(d);
                return;
        }
    }

    private void Report(BrowserDialog d, string? handled)
    {
        var e = new JsonObject { ["kind"] = d.KindName, ["message"] = d.Message ?? "", ["handled"] = handled };
        if (d.Request is PromptRequest prompt) e["default"] = prompt.Default ?? "";
        Post("dialog", e);
    }

    private static void Answer(BrowserDialog d, bool accept, string? text) =>
        d.Complete(d.Request switch
        {
            PromptRequest prompt => accept ? text ?? prompt.Default ?? "" : null,
            _ => (object)accept,
        });

    private void AnswerHeld(bool accept, string? text)
    {
        BrowserDialog? held;
        lock (_gate)
        {
            held = _held;
            _held = null;
        }
        if (held is null || held.IsCompleted || !held.RoutedToAgent)
            throw new TernException(TernException.Failed, "No confirm or prompt is waiting for an answer.");
        Answer(held, accept, text);
    }

    private async Task HandOffLaterAsync(DialogBroker broker, BrowserDialog d)
    {
        await Task.Delay(HandOffAfter).ConfigureAwait(false);
        lock (_gate)
        {
            if (_held == d) _held = null;
        }
        if (!d.IsCompleted && broker.HandToUser(d)) Report(d, "user");
    }

    // ── Parsing ──

    private static IReadOnlyList<NativeInputStep> Steps(JsonArray events)
    {
        var steps = new List<NativeInputStep>(events.Count);
        foreach (var node in events)
        {
            if (node is not JsonObject e) throw Invalid("input events must be objects");
            var mods = Modifiers(e["mods"] as JsonArray);
            steps.Add(Str(e, "type") switch
            {
                "mouse" => Mouse(e, mods),
                "wheel" => new NativeWheelStep(Num(e, "x") ?? 0, Num(e, "y") ?? 0, Num(e, "dx") ?? 0, Num(e, "dy") ?? 0, mods),
                "key" => new NativeKeyStep(Str(e, "action") switch
                {
                    "down" => true,
                    "up" => false,
                    var a => throw Invalid($"key action must be down or up, not {a ?? "nothing"}"),
                }, Str(e, "key") is { Length: > 0 } key ? key : throw Invalid("a key event needs a key"), Str(e, "code"), mods),
                "text" => new NativeTextStep(Str(e, "text") ?? throw Invalid("a text event needs text")),
                var t => throw Invalid($"unknown input event type {t ?? "nothing"}"),
            });
        }
        return steps;
    }

    private static NativeMouseStep Mouse(JsonObject e, NativeModifiers mods)
    {
        var action = Str(e, "action") switch
        {
            "move" => NativeMouseAction.Move,
            "down" => NativeMouseAction.Down,
            "up" => NativeMouseAction.Up,
            var a => throw Invalid($"mouse action must be move, down or up, not {a ?? "nothing"}"),
        };
        var button = Str(e, "button") switch
        {
            null or "left" => NativeMouseButton.Left,
            "right" => NativeMouseButton.Right,
            "middle" => NativeMouseButton.Middle,
            var b => throw new TernException(TernException.Unsupported, $"The mouse button {b} is not available."),
        };
        var clicks = (int)(Num(e, "clicks") ?? (action == NativeMouseAction.Move ? 0 : 1));
        return new NativeMouseStep(action, Num(e, "x") ?? throw Invalid("a mouse event needs x"), Num(e, "y") ?? throw Invalid("a mouse event needs y"),
            button, Math.Max(0, clicks), mods);
    }

    private static NativeModifiers Modifiers(JsonArray? mods)
    {
        var result = NativeModifiers.None;
        foreach (var m in mods ?? [])
            result |= (m?.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : "") switch
            {
                "shift" => NativeModifiers.Shift,
                "ctrl" => NativeModifiers.Control,
                "alt" => NativeModifiers.Alt,
                "meta" => NativeModifiers.Meta,
                var other => throw Invalid($"unknown modifier {other}"),
            };
        return result;
    }

    private static List<TernScript> Scripts(JsonArray scripts)
    {
        var list = new List<TernScript>(scripts.Count);
        foreach (var node in scripts)
        {
            if (node is not JsonObject s) throw Invalid("scripts must be objects");
            list.Add(new TernScript(
                Str(s, "source") ?? throw Invalid("a script needs a source"),
                Str(s, "world") == "isolated" ? TernWorld.Isolated : TernWorld.Page,
                Str(s, "frames") != "main",
                Str(s, "at") != "end"));
        }
        return list;
    }

    private static TernException Invalid(string message) => new(TernException.Invalid, message);

    internal static string? Str(JsonObject p, string name) =>
        p[name] is { } v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A number field, whether the node came from omp's frame or was made in the app (an int, a long, a double).</summary>
    internal static double? Num(JsonObject p, string name) =>
        p[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    internal static bool? Bool(JsonObject p, string name) => p[name]?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
