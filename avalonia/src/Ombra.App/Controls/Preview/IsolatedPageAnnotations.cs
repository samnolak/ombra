using System.Text.Json;
using Ombra.App.ViewModels;
using Ombra.ClientCore.Browser;

namespace Ombra.App.Controls;

/// <summary>
/// Annotations on a tab Nightly shows (<see cref="PreviewPanel"/>). Nightly has no host bridge for the page script's
/// messages (the built-in preview's <c>invokeCSharpAction</c>), so annotate.js runs in Ombra's isolated world of the page
/// (<see cref="IIsolatedScripts"/>: the page neither sees nor reaches it) and keeps its messages until a reader takes
/// them: one call at a time waiting on <c>__ompAnnotate.next()</c>, which answers once there is a message. Each loaded
/// document (and each address a page moves to without a load) gets the script and a new reader; the script ends the
/// earlier reader's wait, and a reader whose document went gets an error: either way that one stops. UI thread.
/// </summary>
/// <param name="isShown">The tab is the one the toolbar shows: the page's messages are about it only then.</param>
internal sealed class IsolatedPageAnnotations(IIsolatedScripts scripts, PreviewViewModel vm, Func<bool> isShown)
{
    internal const string NextSource = "() => window.__ompAnnotate ? window.__ompAnnotate.next() : null";

    /// <summary>Mode, pins and the pin to reveal as arguments (nothing spliced into the source); true when it revealed it.</summary>
    internal const string SyncSource = "(on, pins, reveal) => { const a = window.__ompAnnotate; if (!a) return false; "
        + "a.setMode(on); a.setPins(pins); return reveal !== null && a.reveal(reveal); }";

    private int _reader;
    private string? _startedOn;
    private bool _loading = true;
    private bool _closed;

    /// <summary>The page started loading a document: the script starts on it once it has loaded.</summary>
    public void OnLoading() => _loading = true;

    /// <summary>
    /// The page's address after a load, or after a change in the page (its title, a route): a loaded document or a new
    /// address gets the script (it ignores a second start on its document) and a new reader; a title change nothing.
    /// </summary>
    public async Task OnLoadedAsync(string url)
    {
        if (_closed || (!_loading && url == _startedOn)) return;
        _loading = false;
        _startedOn = url;
        try
        {
            await scripts.CallAsync(PreviewPanel.AnnotateFunction,
                "[" + JsonSerializer.Serialize(vm.AnnotationToken, PreviewJsonContext.Default.String) + ",true]", CancellationToken.None);
        }
        catch (Exception)
        {
            return; // a page that refuses scripts (or went away): no comments on it; the rest of the preview works
        }
        _ = ReadAsync(++_reader);
        if (isShown()) await SyncAsync();
    }

    private async Task ReadAsync(int reader)
    {
        while (!_closed && reader == _reader)
        {
            string? batch;
            try
            {
                batch = await scripts.CallAsync(NextSource, "[]", CancellationToken.None);
            }
            catch (Exception)
            {
                return; // the document went (the next one gets its own reader), or Nightly did
            }
            if (_closed || Messages(batch) is not { } messages) return; // a newer reader took over, or no script
            foreach (var message in messages) Deliver(message);
        }
    }

    private static List<string>? Messages(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? [.. doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Deliver(string message)
    {
        if (!isShown()) return; // another tab's route change is not the toolbar's page
        // "ready": the script started (again); an annotation: its pin is numbered from the list (a change or a removal
        // syncs through the list's events)
        if (vm.OnPageMessage(message) is "ready" or "annotation") _ = SyncAsync();
    }

    /// <summary>The page's mode and pins follow the panel's (after a load, a toggle, a comment added or removed).</summary>
    public async Task SyncAsync()
    {
        if (_closed) return;
        var reveal = vm.PendingReveal;
        var args = "[" + (vm.IsAnnotating ? "true" : "false") + "," + vm.PinsJson(vm.CurrentUrl) + ","
            + (reveal is null ? "null" : JsonSerializer.Serialize(reveal, PreviewJsonContext.Default.String)) + "]";
        try
        {
            // A team member's mark asked for (MainViewModel.SharedMarks.cs): done once the page has scrolled to its pin
            if (await scripts.CallAsync(SyncSource, args, CancellationToken.None) == "true" && reveal is not null && vm.PendingReveal == reveal)
                vm.PendingReveal = null;
        }
        catch (Exception)
        {
            // No page yet, or it is loading: the load syncs
        }
    }

    /// <summary>The tab's pane went: nothing more is read or sent.</summary>
    public void Close() => _closed = true;
}
