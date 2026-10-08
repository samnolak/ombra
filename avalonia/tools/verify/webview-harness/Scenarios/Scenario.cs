using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;
using OmpGui.WebViewHarness.Server;

namespace OmpGui.WebViewHarness.Scenarios;

internal interface IScenario
{
    string Name { get; }

    string Description { get; }

    Task RunAsync(HarnessContext ctx);
}

/// <summary>A scenario of the app's own hooks only (there is no reference delegate for it): not run with <c>--hooks harness</c>.</summary>
internal interface IAppHooksOnly : IScenario
{
}

/// <summary>What a scenario works with, and what it reports: named checks plus free-form data.</summary>
internal sealed class HarnessContext(MainLoop loop, TestServer server, string hooksMode) : IDisposable
{
    private readonly List<HarnessWebView> _views = [];

    public MainLoop Loop { get; } = loop;
    public TestServer Server { get; } = server;

    /// <summary><c>app</c> when the app's Platform/Mac hooks are linked and requested, else <c>harness</c>.</summary>
    public string HooksMode { get; } = hooksMode;

    public JsonArray Checks { get; } = [];
    public JsonObject Data { get; } = [];

    public string TempDir { get; } = Directory.CreateTempSubdirectory("ompgui-harness-").FullName;

    /// <summary>
    /// A web view never on screen. <see cref="HookOptions.Reference"/> means "the hooks under test": the app's own
    /// (<see cref="HookOptions.App"/>) in app mode, the harness's reference delegates otherwise.
    /// </summary>
    public HarnessWebView CreateWebView(HookOptions hooks, double width = 1024, double height = 768)
    {
        if (hooks == HookOptions.Reference && HooksMode == "app") hooks = HookOptions.App;
        var view = HarnessWebView.Create(hooks, width, height);
        _views.Add(view);
        return view;
    }

    /// <summary>A web view (hooks as in <see cref="CreateWebView"/>) whose configuration <paramref name="configure"/> prepares first.</summary>
    public HarnessWebView CreateConfiguredWebView(HookOptions hooks, Action<nint> configure)
    {
        if (hooks == HookOptions.Reference && HooksMode == "app") hooks = HookOptions.App;
        var view = HarnessWebView.CreateConfigured(hooks, configure);
        _views.Add(view);
        return view;
    }

    /// <summary>Records a named check; the scenario passes when every check passes.</summary>
    public bool Check(string name, bool ok, JsonNode? detail = null)
    {
        Checks.Add(new JsonObject { ["name"] = name, ["ok"] = ok, ["detail"] = detail?.DeepClone() });
        return ok;
    }

    public bool Ok => Checks.All(c => c?["ok"]?.GetValue<bool>() == true);

    public void Dispose()
    {
        foreach (var view in _views) view.Dispose();
        try
        {
            Directory.Delete(TempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
