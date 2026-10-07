using Avalonia.Threading;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The preview panel as omp's browser (<see cref="AgentBrowserBridge"/>): a page the agent opens opens the panel,
/// and the panel shows while the agent uses it. Everything runs on the UI thread.
/// </summary>
internal sealed class PreviewAgentPage(MainViewModel main) : IAgentBrowserPage
{
    private const string NoPage = "No page is open in the preview yet: open one with browser.open(url) or tab.goto(url).";
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(300);

    public async Task ShowAsync(CancellationToken ct) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            main.Preview.NoteAgentActivity();
            main.IsPreviewOpen = true;
        });

    public async Task<Uri> NavigateAsync(string address, CancellationToken ct)
    {
        var (url, code, error) = await Dispatcher.UIThread.InvokeAsync<(Uri?, string, string)>(() =>
        {
            var p = main.Preview;
            p.NoteAgentActivity();
            main.IsPreviewOpen = true;
            if (p.IsEngineUnavailable) return (null, "unavailable", Unavailable(p));
            if (p.NavigateForAgent(address) is { } refused) return (null, "invalid_params", refused);
            return (p.CurrentUrl, "", "");
        });
        return url ?? throw new AgentBrowserException(code, error);
    }

    public async Task<AgentPageState> GetStateAsync(CancellationToken ct) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var p = main.Preview;
            p.NoteAgentActivity();
            return new AgentPageState(p.CurrentUrl, p.IsLoading, p.LoadFailed, p.IsEngineUnavailable ? Unavailable(p) : null);
        });

    public async Task<string> RunScriptAsync(string script, CancellationToken ct)
    {
        var (hasPage, raw) = await Dispatcher.UIThread.InvokeAsync<(bool, string?)>(async () =>
        {
            var p = main.Preview;
            p.NoteAgentActivity();
            return p.RunScript is { } run ? (true, await run(script)) : (false, (string?)null);
        }).WaitAsync(ct);
        return hasPage ? raw ?? "" : throw new AgentBrowserException("no_page", NoPage);
    }

    public async Task<AgentScreenshot> CaptureAsync(CancellationToken ct)
    {
        var opened = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            main.Preview.NoteAgentActivity();
            var closed = !main.IsPreviewOpen;
            main.IsPreviewOpen = true;
            return closed;
        });
        // The web view only draws while the panel shows it: give a panel just opened a moment to lay the page out
        if (opened) await Task.Delay(ShowDelay, ct);
        var shot = await Dispatcher.UIThread.InvokeAsync<AgentScreenshot?>(async () =>
        {
            var p = main.Preview;
            p.NoteAgentActivity();
            if (p.IsEngineUnavailable) throw new AgentBrowserException("unavailable", Unavailable(p));
            return p.CaptureScreenshot is { } capture ? await capture(ct) : null;
        }).WaitAsync(ct);
        return shot ?? throw new AgentBrowserException("no_page", NoPage);
    }

    private static string Unavailable(PreviewViewModel p) =>
        p.EngineDetail.Length > 0 ? $"{p.FallbackText}. {p.EngineDetail}" : p.FallbackText + ".";
}
