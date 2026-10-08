using OmpGui.ClientCore;
using OmpGui.ClientCore.Browser;

namespace OmpGui.Tests;

/// <summary>
/// The built-in browser as the default, against a real omp (BROWSER_PLAN A2). Runs only when OMPGUI_TEST_CONFIG
/// points at a runtime options file whose omp profile routes to tools/mock-model (BROWSERCALL makes one `eval` call
/// with <c>browser.open</c>): the open must reach the app's bridge, not omp's own Chromium, and the system prompt must
/// carry the extension's note. The user's omp configuration is whatever the runtime options name; nothing is written to it.
/// </summary>
public sealed class RealOmpBrowserTests
{
    /// <summary>The pane as the bridge sees it: every tab records where it was sent.</summary>
    private sealed class RecordingHost : IAgentBrowserHost
    {
        public List<string> Navigations { get; } = [];
        public Task<IAgentBrowserPage> OpenTabAsync(string surfaceId, CancellationToken ct) => Task.FromResult<IAgentBrowserPage>(new Tab(this));

        private sealed class Tab(RecordingHost host) : IAgentBrowserPage
        {
            private Uri? _url;
            public Task ShowAsync(CancellationToken ct) => Task.CompletedTask;

            public Task<Uri> NavigateAsync(string address, CancellationToken ct)
            {
                lock (host.Navigations) host.Navigations.Add(address);
                _url = new Uri(address);
                return Task.FromResult(_url);
            }

            public Task<AgentPageState> GetStateAsync(CancellationToken ct) => Task.FromResult(new AgentPageState(_url, false, false, null));
            // Enough of a page for omp's open: loaded at once; omp's own probes get nothing back
            public Task<string> RunScriptAsync(string script, CancellationToken ct) => Task.FromResult(script == "document.readyState" ? "\"complete\"" : "null");
            public Task<AgentScreenshot> CaptureAsync(CancellationToken ct) => throw new AgentBrowserException("not_supported", "No capture here.");
            public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
            public bool IsClosed => false;
            public OmpGui.ClientCore.Browser.DialogBroker? Dialogs => null;
        }
    }

    [Fact]
    public async Task Browser_open_in_eval_reaches_the_apps_browser_and_the_note_is_in_the_prompt()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var page = new RecordingHost();
        bridge.Host = page;
        var defaults = new AgentBrowserDefaults(bridge, Path.Combine(TestProcesses.TempDir("real-browser-default"), "agent-browser"));
        await using var s = new SessionController(r => defaults.ForSession(options, r), new LaunchRequest(options.WorkingDirectory, ApprovalMode: "yolo"),
            TimeSpan.FromSeconds(90));
        await s.StartAsync();

        const string url = "http://127.0.0.1:9/omp-gui-default-check";
        await s.PromptAsync("BROWSERCALL " + url);
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Items.OfType<AssistantItem>().Any(a => a.Text.Contains("BROWSERCALL done")),
            TimeSpan.FromSeconds(120), "run end");
        lock (page.Navigations) Assert.Contains(url, page.Navigations);
        var final = snap.Items.OfType<AssistantItem>().Last(a => a.Text.Contains("BROWSERCALL done")).Text;
        Assert.Contains("NOTE-SEEN=yes", final); // the extension's note reached the model's system prompt
        var tool = snap.Items.OfType<ToolItem>().Last(t => t.Name == "eval");
        Assert.True(tool.Status == ToolStatus.Succeeded, tool.Output);
        Assert.Contains("on cmux browser", tool.Output); // omp names the backend: "Opened tab "main" on cmux browser (split)"
    }
}
