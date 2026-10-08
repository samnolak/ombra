using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>&lt;input type=file multiple&gt; through runOpenPanelWithParameters: no NSOpenPanel, files preset by the harness.</summary>
internal sealed class FileInputScenario : IScenario
{
    public string Name => "file-input";

    public string Description => "file input via runOpenPanelWithParameters: parameters, chosen files reach the page, cancel";

    public async Task RunAsync(HarnessContext ctx)
    {
        // Today's app: no UI delegate → the chooser is cancelled
        var baseline = ctx.CreateWebView(HookOptions.None);
        await baseline.LoadAsync(ctx.Server.Url("/file-input"));
        await baseline.EvalAsync("document.getElementById('f').click(), true");
        await Task.Delay(500);
        var baseFiles = await baseline.EvalAsync("window.__files");
        ctx.Check("baseline (no UI delegate): no file reaches the page", baseFiles is null, baseFiles);

        var a = Path.Combine(ctx.TempDir, "a.txt");
        var b = Path.Combine(ctx.TempDir, "b.txt");
        await File.WriteAllTextAsync(a, "alpha");
        await File.WriteAllTextAsync(b, "beta");
        var view = ctx.CreateWebView(HookOptions.Reference);
        OpenPanelRequest? asked = null;
        view.OnOpenPanel = req =>
        {
            asked = req;
            return Task.FromResult<IReadOnlyList<string>?>([a, b]);
        };
        await view.LoadAsync(ctx.Server.Url("/file-input"));
        await view.EvalAsync("document.getElementById('f').click(), true"); // evaluateJavaScript counts as a user gesture
        var got = await view.WaitForJsAsync("window.__files", TimeSpan.FromSeconds(5));
        var files = await view.EvalAsync("window.__files");
        ctx.Check("open panel requested with allowsMultipleSelection", asked is { AllowsMultiple: true, AllowsDirectories: false },
            asked is null ? null : new JsonObject { ["allowsMultipleSelection"] = asked.AllowsMultiple, ["allowsDirectories"] = asked.AllowsDirectories });
        ctx.Check("chosen files reach the page (names and contents)",
            got && files?[0]?["name"]?.GetValue<string>() == "a.txt" && files?[0]?["text"]?.GetValue<string>() == "alpha"
            && files?[1]?["name"]?.GetValue<string>() == "b.txt" && files?[1]?["text"]?.GetValue<string>() == "beta", files);

        view.OnOpenPanel = _ => Task.FromResult<IReadOnlyList<string>?>(null);
        await view.LoadAsync(ctx.Server.Url("/file-input"));
        await view.EvalAsync("document.getElementById('f').click(), true");
        await view.WaitForJsAsync("window.__events.length > 0", TimeSpan.FromSeconds(2));
        var cancelEvents = await view.EvalAsync("window.__events");
        var cancelFiles = await view.EvalAsync("window.__files");
        ctx.Data["cancelEvents"] = cancelEvents?.DeepClone();
        ctx.Check("cancel → no files", cancelFiles is null, cancelFiles);
    }
}
