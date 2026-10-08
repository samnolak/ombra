using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;
using OmpGui.WebViewHarness.Server;
#if APP_HOOKS
using OmpGui.ClientCore.Browser;
#endif

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>Content-Disposition attachment, &lt;a download&gt; (http and blob:), unknown MIME → WKDownload to a temp folder.</summary>
internal sealed class DownloadScenario : IScenario
{
    public string Name => "download";

    public string Description => "downloads via decidePolicyForNavigationResponse / preferences-variant action policy / WKDownloadDelegate";

    public async Task RunAsync(HarnessContext ctx)
    {
        // Today's app: <a download> just navigates to the file
        var baseline = ctx.CreateWebView(HookOptions.None);
        await baseline.LoadAsync(ctx.Server.Url("/download/page"));
        var baseNav = baseline.ExpectNavigation(TimeSpan.FromSeconds(5));
        await baseline.EvalAsync("document.getElementById('adownload').click(), true");
        await baseNav;
        ctx.Check("baseline (no hooks): <a download> navigates instead of downloading",
            baseline.Url?.EndsWith("/download/plain.txt") == true, baseline.Url);

        var view = ctx.CreateWebView(HookOptions.Reference);
        view.DownloadDestination = rec => Path.Combine(ctx.TempDir, rec.SuggestedFilename ?? "download.bin");
        view.DownloadFolder = ctx.TempDir; // app hooks: the folder the user chose; "Save" on the download card saves there
        await view.LoadAsync(ctx.Server.Url("/download/page"));

        await Click(view, "attachment", 1);
        await Click(view, "adownload", 2);
        await Click(view, "blob", 3);
        _ = view.LoadAsync(ctx.Server.Url("/download/binary"), TimeSpan.FromSeconds(5));
        await HarnessWebView.WaitForAsync(() => view.Downloads.Count >= 4 && view.Downloads.All(d => d.Finished || d.Error is not null), TimeSpan.FromSeconds(10));

        var records = new JsonArray(view.Downloads.Select(d => (JsonNode)new JsonObject
        {
            ["from"] = d.Source, ["suggestedFilename"] = d.SuggestedFilename, ["destination"] = d.Destination, ["finished"] = d.Finished, ["error"] = d.Error,
        }).ToArray());
        ctx.Data["downloads"] = records;
        Saved(ctx, "Content-Disposition: attachment saved as report.txt", view, "report.txt", TestServer.DownloadBody);
        Saved(ctx, "<a download> saved under its download name", view, "saved.txt", TestServer.DownloadBody);
        Saved(ctx, "<a download> of a blob: URL saved", view, "blob.txt", "hello blob\n");
        ctx.Check("non-displayable MIME type becomes a download",
            view.Downloads.Any(d => d.Finished && d.Source.Contains("Response", StringComparison.Ordinal) && d.SuggestedFilename == "binary"), records);
        ctx.Check("page stayed on the download page", view.Url?.EndsWith("/download/page") == true, view.Url);
#if APP_HOOKS
        if (view.App is { } app)
        {
            // The app's path: one download card per file (answered "Save"), saved into the chosen folder only, listed in the pane
            var cards = app.Asked.OfType<DownloadRequest>().Select(r => r.FileName).ToList();
            ctx.Check("app: each download asked on the download card with its file name",
                cards.Contains("report.txt") && cards.Contains("saved.txt") && cards.Contains("blob.txt") && cards.Contains("binary"),
                new JsonArray([.. cards.Select(c => (JsonNode)c)]));
            ctx.Check("app: every file saved inside the chosen folder",
                view.Downloads.Count >= 4 && view.Downloads.All(d => d.Destination is { } p && Path.GetDirectoryName(p) == ctx.TempDir), records);
            ctx.Check("app: the pane's downloads list shows them finished",
                app.Downloads.Items.Count >= 4 && app.Downloads.Items.All(d => d.State == BrowserDownloadState.Finished && d.TotalBytes > 0),
                new JsonArray([.. app.Downloads.Items.Select(d => (JsonNode)$"{d.FileName}: {d.StatusText}")]));

            // Stopping a download before a place was chosen: nothing saved, no card left, WebKit's handler answered once
            var stopView = ctx.CreateWebView(HookOptions.Reference);
            stopView.DownloadFolder = ctx.TempDir;
            var stopApp = stopView.App!;
            await stopView.LoadAsync(ctx.Server.Url("/download/page"));
            var before = Directory.GetFiles(ctx.TempDir).Length;
            var stopped = new TaskCompletionSource<BrowserDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
            stopApp.Hooks.DownloadChanged += d =>
            {
                if (d.State == BrowserDownloadState.Asking) d.Cancel();
                if (d.State == BrowserDownloadState.Canceled) stopped.TrySetResult(d);
            };
            await stopView.EvalAsync("document.getElementById('attachment').click(), true");
            var done = await Task.WhenAny(stopped.Task, Task.Delay(5000)) == stopped.Task;
            await Task.Delay(300);
            ctx.Check("app: a download stopped before a place was chosen saves nothing and is not listed",
                done && Directory.GetFiles(ctx.TempDir).Length == before && stopApp.Downloads.Items.Count == 0 && stopApp.Broker.Pending.Count == 0,
                new JsonObject { ["stopped"] = done, ["filesBefore"] = before, ["filesAfter"] = Directory.GetFiles(ctx.TempDir).Length });
        }
#endif
    }

    private static async Task Click(HarnessWebView view, string id, int expected)
    {
        await view.EvalAsync($"document.getElementById('{id}').click(), true");
        await HarnessWebView.WaitForAsync(() => view.Downloads.Count >= expected && view.Downloads[expected - 1] is { } d && (d.Finished || d.Error is not null),
            TimeSpan.FromSeconds(5));
    }

    private static void Saved(HarnessContext ctx, string name, HarnessWebView view, string fileName, string body)
    {
        var rec = view.Downloads.FirstOrDefault(d => d.SuggestedFilename == fileName);
        var path = rec?.Destination;
        var content = path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
        ctx.Check(name, rec?.Finished == true && content == body, new JsonObject { ["path"] = path, ["content"] = content, ["error"] = rec?.Error });
    }
}
