#if APP_HOOKS
using System.Text.Json.Nodes;
using OmpGui.App.Platform.Mac;
using OmpGui.ClientCore.Browser;

namespace OmpGui.WebViewHarness.Native;

/// <summary>
/// The app's hooks (<c>Platform/Mac/WebKitHooks</c>: UI delegate, navigation extras, downloads, pop-ups) on a harness
/// web view, answered through the app's own <see cref="DialogBroker"/> and <see cref="BrowserDownloads"/> by the
/// scenario's handlers on <see cref="HarnessWebView"/> (<c>OnJsDialog</c>, <c>OnOpenPanel</c>, <c>DownloadFolder</c>,
/// <c>AllowPopups</c>). Events are logged under the reference delegates' names, so every reference scenario runs
/// unchanged against the app's code (<c>--hooks app</c>).
/// </summary>
internal sealed class AppHooksBridge : IDisposable
{
    private readonly HarnessWebView _view;
    private readonly HashSet<BrowserDialog> _seen = [];
    private readonly HashSet<BrowserDialog> _answeredByScenario = [];
    private readonly Dictionary<long, DownloadRecord> _downloads = [];
    private readonly HashSet<string> _responseDownloads = [];
    private IDisposable? _attachment;

    private AppHooksBridge(HarnessWebView view)
    {
        _view = view;
        Downloads = new BrowserDownloads(new BrowserSiteSettings());
        Hooks.Broker = () => Broker;
        Hooks.SiteSettings = new BrowserSiteSettings();
        Hooks.ChooseDownloadPath = (request, ct) =>
        {
            Downloads.Settings.DownloadFolder = _view.DownloadFolder;
            return Downloads.ChooseDestinationAsync(request, Broker, ct);
        };
        Hooks.OpenPopup = OpenPopup;
        Broker.Changed += (_, _) => Dispatch();
        Hooks.Started += url => HarnessLog.Add(_view.Label, "didStartProvisionalNavigation", new JsonObject { ["url"] = url?.AbsoluteUri });
        Hooks.Committed += url => HarnessLog.Add(_view.Label, "didCommitNavigation", new JsonObject { ["url"] = url?.AbsoluteUri });
        Hooks.Finished += url =>
        {
            // Pop-ups only (the Avalonia-shaped main delegate reports its own finish)
            var result = new JsonObject { ["ok"] = true, ["url"] = url?.AbsoluteUri };
            HarnessLog.Add(_view.Label, "didFinishNavigation", (JsonObject)result.DeepClone());
            _view.NavigationFinished(result);
        };
        Hooks.Failed += f =>
        {
            var data = new JsonObject { ["ok"] = false, ["domain"] = f.Domain, ["code"] = f.Code, ["error"] = f.Message, ["provisional"] = f.Provisional, ["url"] = f.Url?.AbsoluteUri };
            HarnessLog.Add(_view.Label, f.Provisional ? "didFailProvisionalNavigation" : "didFailNavigation", (JsonObject)data.DeepClone());
            _view.NavigationFinished(data);
        };
        Hooks.Terminated += () =>
        {
            _view.ContentProcessTerminations++;
            // What the page asked and the scenario never answered: closed by the termination with its default
            var defaulted = _seen.Count(d => d.IsModal && d.IsCompleted && !_answeredByScenario.Contains(d));
            HarnessLog.Add(_view.Label, "webContentProcessDidTerminate", new JsonObject { ["pendingAnsweredWithDefault"] = defaulted, ["brokerPending"] = Broker.Pending.Count });
            _view.NavigationFinished(new JsonObject { ["ok"] = false, ["crashed"] = true });
        };
        Hooks.Closed += () =>
        {
            HarnessLog.Add(_view.Label, "webViewDidClose");
            _view.Dispose();
        };
        Hooks.Response += r =>
        {
            if (r.Download && r.Url is { } u) _responseDownloads.Add(u.AbsoluteUri);
            HarnessLog.Add(_view.Label, "decidePolicyForNavigationResponse", new JsonObject
            {
                ["url"] = r.Url?.AbsoluteUri, ["status"] = r.Status, ["mime"] = r.MimeType, ["mainFrame"] = r.IsMainFrame, ["policy"] = r.Download ? 2 : 1,
                ["contentDisposition"] = r.Headers.GetValueOrDefault("Content-Disposition"),
            });
        };
        Hooks.DownloadChanged += OnDownload;
        _view.CancelPendingOverride = () =>
        {
            var pending = Broker.Pending.Count;
            Broker.CancelAll();
            return pending;
        };
    }

    public DialogBroker Broker { get; } = new();
    public BrowserPageHooks Hooks { get; } = new();
    public BrowserDownloads Downloads { get; }

    /// <summary>Every question the app asked through the broker, in order.</summary>
    public List<BrowserDialogRequest> Asked { get; } = [];

    /// <summary>A scenario's own answer: true when it takes the question (it answers it itself, or leaves it open).</summary>
    public Func<BrowserDialog, bool>? Intercept { get; set; }

    /// <summary>Attaches the app's hooks to <paramref name="view"/> (the Avalonia-shaped delegate it already has).</summary>
    public static AppHooksBridge Attach(HarnessWebView view)
    {
        var bridge = new AppHooksBridge(view);
        bridge._attachment = WebKitHooks.Attach(view.Handle, bridge.Hooks, out var problem)
            ?? throw new InvalidOperationException("WebKitHooks.Attach failed: " + problem);
        HarnessLog.Add(view.Label, "appHooksAttached", new JsonObject { ["problem"] = problem });
        return bridge;
    }

    private BrowserPageHooks? OpenPopup(BrowserPopup popup)
    {
        if (!_view.AllowPopups)
        {
            HarnessLog.Add(_view.Label, "createWebView", new JsonObject { ["url"] = popup.RequestedUrl?.AbsoluteUri, ["created"] = false });
            return null;
        }
        var view = HarnessWebView.Adopt(popup.NativeHandle, _view, popup.Close);
        var bridge = new AppHooksBridge(view); // the app attaches its hooks to the pop-up with these
        view.UseAppBridge(bridge);
        view.OnJsDialog = _view.OnJsDialog;
        view.OnOpenPanel = _view.OnOpenPanel;
        view.DownloadFolder = _view.DownloadFolder;
        _view.Popups.Add(view);
        HarnessLog.Add(_view.Label, "createWebView", new JsonObject { ["url"] = popup.RequestedUrl?.AbsoluteUri, ["created"] = true, ["popup"] = view.Label });
        return bridge.Hooks;
    }

    private void Dispatch()
    {
        foreach (var dialog in Broker.Pending)
        {
            if (!_seen.Add(dialog)) continue;
            Asked.Add(dialog.Request);
            _ = AnswerAsync(dialog);
        }
    }

    private async Task AnswerAsync(BrowserDialog dialog)
    {
        await Task.Yield(); // not inside the broker's Changed event
        if (Intercept?.Invoke(dialog) == true) return;
        var page = dialog.Request.PageUrl;
        switch (dialog.Request)
        {
            case AlertRequest or ConfirmRequest or PromptRequest:
                {
                    var kind = dialog.KindName;
                    var js = new JsDialog(kind, dialog.Message, (dialog.Request as PromptRequest)?.Default, page?.Host, true);
                    HarnessLog.Add(_view.Label, kind, new JsonObject { ["message"] = js.Message, ["defaultText"] = js.DefaultText, ["frameHost"] = js.FrameHost });
                    if (_view.OnJsDialog is not { } handler)
                    {
                        dialog.Cancel();
                        return;
                    }
                    object? value;
                    try
                    {
                        value = await handler(js);
                    }
                    catch (Exception e)
                    {
                        HarnessLog.Add(_view.Label, "dialog-handler-exception", new JsonObject { ["error"] = e.Message });
                        dialog.Cancel();
                        return;
                    }
                    _answeredByScenario.Add(dialog);
                    dialog.Complete(kind switch
                    {
                        "alert" => null,
                        "confirm" => value is true,
                        _ => value as string,
                    });
                    return;
                }
            case FileChooserRequest f:
                {
                    HarnessLog.Add(_view.Label, "runOpenPanel", new JsonObject
                    {
                        ["allowsMultipleSelection"] = f.Multiple, ["allowsDirectories"] = f.AllowDirectories, ["accept"] = new JsonArray([.. f.Accept.Select(a => (JsonNode)a)]),
                    });
                    var files = _view.OnOpenPanel is { } pick ? await pick(new OpenPanelRequest(f.Multiple, f.AllowDirectories)) : null;
                    _answeredByScenario.Add(dialog);
                    dialog.Complete(files);
                    return;
                }
            case DownloadRequest d:
                // The card's "Save": the folder the user chose (none: the save panel would ask; the harness has none)
                HarnessLog.Add(_view.Label, "downloadCard", new JsonObject { ["fileName"] = d.FileName, ["size"] = d.Size, ["url"] = d.Url?.AbsoluteUri });
                _answeredByScenario.Add(dialog);
                dialog.Complete(DownloadAnswer.Save);
                return;
            default:
                dialog.Cancel();
                return;
        }
    }

    private void OnDownload(BrowserDownload d)
    {
        if (!_downloads.TryGetValue(d.Id, out var record))
        {
            var source = d.Url is { } u && _responseDownloads.Contains(u.AbsoluteUri) ? "WKNavigationResponse (app)" : "WKNavigationAction (app)";
            record = new DownloadRecord(source);
            _downloads[d.Id] = record;
            _view.Downloads.Add(record);
            HarnessLog.Add(_view.Label, "didBecomeDownload", new JsonObject { ["from"] = source, ["url"] = d.Url?.AbsoluteUri });
        }
        Downloads.Track(d);
        if (d.FileName.Length > 0) record.SuggestedFilename = d.FileName;
        record.Destination = d.Path;
        record.Finished = d.State == BrowserDownloadState.Finished;
        record.Error = d.State switch
        {
            BrowserDownloadState.Failed => d.Error ?? "failed",
            BrowserDownloadState.Canceled => "canceled",
            _ => null,
        };
        HarnessLog.Add(_view.Label, "download" + d.State, new JsonObject { ["fileName"] = d.FileName, ["path"] = d.Path, ["bytes"] = d.BytesReceived, ["total"] = d.TotalBytes });
    }

    public void Dispose()
    {
        _attachment?.Dispose();
        _attachment = null;
        Broker.Dispose();
    }
}
#endif
