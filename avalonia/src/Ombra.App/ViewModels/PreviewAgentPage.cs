using Avalonia.Threading;
using Ombra.App.Nightly;
using Ombra.ClientCore;
using Ombra.ClientCore.Browser;

namespace Ombra.App.ViewModels;

/// <summary>
/// The preview panel as omp's browser (<see cref="AgentBrowserBridge"/>): every page omp opens gets a tab of its own
/// in the panel (<see cref="PreviewViewModel.OpenAgentTab"/>), and the panel opens while the agent uses it.
/// </summary>
internal sealed class PreviewAgentHost(MainViewModel main) : IAgentBrowserHost, IAgentSpeculation
{
    public async Task<IAgentBrowserPage> OpenTabAsync(string surfaceId, CancellationToken ct) =>
        await Dispatcher.UIThread.InvokeAsync<IAgentBrowserPage>(() =>
        {
            var p = main.Preview;
            p.NoteAgentActivity();
            main.IsPreviewOpen = true;
            return new PreviewAgentPage(main, p.OpenAgentTab(surfaceId));
        });

    public IAgentSpeculation? Speculation => OperatingSystem.IsMacOS() ? this : null;

    // The tab the agent opens next has no Nightly window yet: window 0 is "a window about to open" (its default
    // OriginAttributes). Posted ahead of the tab's creation, so the hint goes out before the pane asks for its window.
    public void Speculate(SpeculationHint hint) => Dispatcher.UIThread.Post(() =>
    {
        if (OperatingSystem.IsMacOS() && main.Preview.UseNightly && NightlyHost.Get().Runtime is { } runtime) runtime.Speculate(0, hint);
    });

    public void CancelSpeculation(string id) => Dispatcher.UIThread.Post(() =>
    {
        if (OperatingSystem.IsMacOS() && main.Preview.UseNightly && NightlyHost.Get().Runtime is { } runtime) runtime.CancelSpeculation(0, id);
    });
}

/// <summary>One of omp's tabs in the preview panel (<see cref="PreviewTab"/>). Everything runs on the UI thread.</summary>
internal sealed class PreviewAgentPage(MainViewModel main, PreviewTab tab) : IAgentBrowserPage
{
    private const string NoPage = "No page is open in this tab yet: open one with browser.open(url) or tab.goto(url).";
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(300);

    public PreviewTab Tab => tab;

    public bool IsClosed => tab.IsClosed;

    // The tab's own questions: a dialog in another tab never holds this tab's scripts
    public DialogBroker? Dialogs => tab.Dialogs;

    public INativeInput? Input => tab.Input;

    public IIsolatedScripts? Isolated => tab.Isolated;

    // The Nightly runtime's agent takes intent hints for its window; the built-in preview's engines take none.
    public IAgentSpeculation? Speculation => tab.Input as IAgentSpeculation;

    public IDisposable? BeginAction() => tab.AgentAction();

    public IReadOnlyList<AgentDevicePreset> DevicePresets => PreviewViewModel.AgentDevicePresets;

    // The pane's device mode (every tab's, as the device bar sets it); the user sees it in the bar and can change it.
    public async Task<AgentDeviceState> SetDeviceAsync(AgentDeviceRequest? device, CancellationToken ct) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            main.Preview.NoteAgentActivity();
            return main.Preview.ApplyAgentDevice(device);
        });

    public async Task ShowAsync(CancellationToken ct) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            main.Preview.NoteAgentActivity();
            main.IsPreviewOpen = true;
            main.Preview.ShowForAgent(tab);
        });

    public async Task<Uri> NavigateAsync(string address, CancellationToken ct)
    {
        // What the page asks while omp acts on it (and a second after) is omp's to answer (BrowserPageHooks.AgentActive)
        using var acting = tab.AgentAction();
        var (url, code, error) = await Dispatcher.UIThread.InvokeAsync<(Uri?, string, string)>(() =>
        {
            var p = main.Preview;
            p.NoteAgentActivity();
            main.IsPreviewOpen = true;
            if (p.IsEngineUnavailable) return (null, "unavailable", Unavailable(p));
            if (tab.IsClosed) return (null, "not_found", "The user closed this tab in the Ombra preview: open a new one.");
            if (p.NavigateTabForAgent(tab, address) is { } refused) return (null, "invalid_params", refused);
            return (tab.Url, "", "");
        });
        return url ?? throw new AgentBrowserException(code, error);
    }

    public async Task<AgentPageState> GetStateAsync(CancellationToken ct) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var p = main.Preview;
            p.NoteAgentActivity();
            var active = p.ActiveTab == tab;
            return new AgentPageState(tab.Url, tab.IsLoading, active ? p.LoadFailed : tab.LoadFailed, p.IsEngineUnavailable ? Unavailable(p) : null,
                tab.Title.Length > 0 ? tab.Title : null);
        });

    public async Task<string> RunScriptAsync(string script, CancellationToken ct)
    {
        using var acting = tab.AgentAction();
        var (hasPage, raw) = await Dispatcher.UIThread.InvokeAsync<(bool, string?)>(async () =>
        {
            main.Preview.NoteAgentActivity();
            if (tab.RunScript is not { } run) return (false, null);
            try
            {
                return (true, await run(script, ct));
            }
            catch (Exception e) when (e is not (OperationCanceledException or AgentBrowserException or IsolatedScriptException))
            {
                // What the engine's evaluate throws is its word that the script threw or did not run (a syntax error, a
                // document between two loads); the runtime's own failures come typed (AgentBrowserException)
                throw new IsolatedScriptException(e.Message);
            }
        }).WaitAsync(ct);
        return hasPage ? raw ?? "" : throw new AgentBrowserException("no_page", NoPage);
    }

    public async Task<AgentScreenshot> CaptureAsync(CancellationToken ct)
    {
        // The web view only draws while the panel shows it: a tab in the background is shown for the capture (and the
        // user's pick comes back after it), a panel just opened gets a moment to lay the page out
        var (wait, restore) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var p = main.Preview;
            p.NoteAgentActivity();
            var closed = !main.IsPreviewOpen;
            main.IsPreviewOpen = true;
            var previous = p.ActiveTab;
            if (previous == tab || tab.IsClosed) return (closed, (PreviewTab?)null);
            var follows = p.FollowsAgent;
            p.ShowForAgent(tab);
            if (!follows) p.ActiveTab = tab;
            return (true, follows ? null : previous);
        });
        try
        {
            if (wait) await Task.Delay(ShowDelay, ct);
            var shot = await Dispatcher.UIThread.InvokeAsync<AgentScreenshot?>(async () =>
            {
                var p = main.Preview;
                p.NoteAgentActivity();
                if (p.IsEngineUnavailable) throw new AgentBrowserException("unavailable", Unavailable(p));
                return tab.CaptureScreenshot is { } capture ? await capture(ct) : null;
            }).WaitAsync(ct);
            return shot ?? throw new AgentBrowserException("no_page", NoPage);
        }
        finally
        {
            if (restore is not null)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var p = main.Preview;
                    if (p.ActiveTab == tab && !restore.IsClosed) p.ActiveTab = restore;
                });
        }
    }

    public async Task CloseAsync(CancellationToken ct) =>
        await Dispatcher.UIThread.InvokeAsync(() => main.Preview.CloseTabForAgent(tab));

    private static string Unavailable(PreviewViewModel p) =>
        p.EngineDetail.Length > 0 ? $"{p.FallbackText}. {p.EngineDetail}" : p.FallbackText + ".";
}
