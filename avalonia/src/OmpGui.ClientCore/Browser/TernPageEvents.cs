using System.Text.Json.Nodes;

namespace OmpGui.ClientCore.Browser;

/// <summary>
/// What the engine hooks report about a tab (<see cref="BrowserPageHooks"/>), as the Tern events omp pulls from its
/// <see cref="TernBlock"/>: <c>committed</c>, <c>loaded</c>, <c>failed</c>, <c>response</c>, <c>download</c>. Downloads
/// omp's action starts go to the folder omp chose (<c>downloads</c>) without asking; the rest as the hooks decide.
/// Engine-neutral: the app and the windowless harness connect each Tern tab with it. Disposing disconnects.
/// </summary>
public sealed class TernPageEvents : IDisposable
{
    private readonly BrowserPageHooks _hooks;
    private readonly TernBlock _block;
    private readonly Action? _committed;
    private readonly Func<DownloadRequest, CancellationToken, Task<string?>> _choose;
    private readonly Func<DownloadRequest, CancellationToken, Task<string?>> _ours;

    /// <param name="committed">Called on every new main-frame document, before omp hears of it (the engine forgets its frames).</param>
    public TernPageEvents(BrowserPageHooks hooks, TernBlock block, Action? committed = null)
    {
        _hooks = hooks;
        _block = block;
        _committed = committed;
        _choose = hooks.ChooseDownloadPath;
        hooks.Committed += OnCommitted;
        hooks.Finished += OnFinished;
        hooks.Failed += OnFailed;
        hooks.Response += OnResponse;
        hooks.Terminated += OnTerminated;
        hooks.DownloadChanged += OnDownloadChanged;
        _ours = ChooseDownloadPathAsync;
        hooks.ChooseDownloadPath = _ours;
    }

    private void OnCommitted(Uri? url)
    {
        _committed?.Invoke();
        _block.Post("committed", new JsonObject { ["url"] = url?.AbsoluteUri ?? "about:blank" });
    }

    private void OnFinished(Uri? url) => _block.Post("loaded");

    private void OnFailed(BrowserLoadFailure failure) =>
        _block.Post("failed", new JsonObject { ["url"] = failure.Url?.AbsoluteUri, ["message"] = failure.Message });

    private void OnResponse(BrowserResponse response)
    {
        var headers = new JsonObject();
        foreach (var (name, value) in response.Headers) headers[name.ToLowerInvariant()] = value;
        _block.Post("response", new JsonObject
        {
            ["url"] = response.Url?.AbsoluteUri ?? "",
            ["status"] = response.Status,
            ["headers"] = headers,
            ["mime"] = response.MimeType ?? "",
            ["main"] = response.IsMainFrame,
        });
    }

    private void OnTerminated() =>
        _block.Post("failed", new JsonObject { ["message"] = "The page's web content process ended; reload the page." });

    private void OnDownloadChanged(BrowserDownload d)
    {
        var state = d.State switch
        {
            BrowserDownloadState.Asking => null,
            BrowserDownloadState.Saving => "started",
            BrowserDownloadState.Finished => "finished",
            _ => "failed",
        };
        if (state is null) return;
        var e = new JsonObject { ["id"] = d.Id, ["url"] = d.Url?.AbsoluteUri ?? "", ["state"] = state };
        if (d.Path is { } path) e["path"] = path;
        if (state == "failed") e["error"] = d.State == BrowserDownloadState.Canceled ? "canceled" : d.Error ?? "failed";
        _block.Post("download", e);
    }

    /// <summary>A download omp's action started goes to omp's folder (a free name in it); others as before.</summary>
    private Task<string?> ChooseDownloadPathAsync(DownloadRequest request, CancellationToken ct)
    {
        if (!request.CausedByAgent || _block.DownloadDirectory is not { } dir) return _choose(request, ct);
        var name = Path.GetFileName(request.FileName.Replace('\\', '/'));
        if (name.Length == 0 || name is "." or "..") name = "download";
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var path = Path.Combine(dir, name);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem} ({n}){extension}");
        return Task.FromResult<string?>(path);
    }

    public void Dispose()
    {
        _hooks.Committed -= OnCommitted;
        _hooks.Finished -= OnFinished;
        _hooks.Failed -= OnFailed;
        _hooks.Response -= OnResponse;
        _hooks.Terminated -= OnTerminated;
        _hooks.DownloadChanged -= OnDownloadChanged;
        if (_hooks.ChooseDownloadPath == _ours) _hooks.ChooseDownloadPath = _choose;
    }
}
