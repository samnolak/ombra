using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>R2 reproduced: no UI delegate (Avalonia sets none) → alert OK, confirm and prompt Cancel, nobody asked.</summary>
internal sealed class DialogsBaselineScenario : IScenario
{
    public string Name => "dialogs-baseline";

    public string Description => "alert/confirm/prompt with no WKUIDelegate (today's app): silently answered by WebKit";

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.None);
        await view.LoadAsync(ctx.Server.Url("/dialogs"));
        await view.EvalAsync("runAll()");
        var done = await view.WaitForJsAsync("window.__done", TimeSpan.FromSeconds(5));
        var log = await view.EvalAsync("window.__log");
        ctx.Check("page ran all dialogs", done, log);
        ctx.Check("confirm answered false, prompt null without asking anyone",
            log?[1]?["value"]?.GetValue<bool>() == false && log?[3]?["value"] is null && HarnessLog.Count("confirm") == 0, log);
    }
}

/// <summary>Our UI delegate gets each dialog with its text and origin; the page receives the answers.</summary>
internal sealed class DialogsScenario : IScenario
{
    public string Name => "dialogs";

    public string Description => "alert/confirm/prompt through a WKUIDelegate: requests, answers, JS suspended, exactly-once on dispose";

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.Reference);
        var seen = new List<JsDialog>();
        var confirms = 0;
        view.OnJsDialog = async d =>
        {
            seen.Add(d);
            switch (d.Kind)
            {
                case "alert":
                    await Task.Delay(300); // the page must stay suspended until we answer
                    return null;
                case "confirm":
                    return ++confirms == 1;
                default:
                    return d.Message == "Your name?" ? d.DefaultText + "-typed" : null;
            }
        };
        await view.LoadAsync(ctx.Server.Url("/dialogs"));
        await view.EvalAsync("runAll()");
        var done = await view.WaitForJsAsync("window.__done", TimeSpan.FromSeconds(10));
        var log = await view.EvalAsync("window.__log");
        ctx.Check("all five dialogs reached the delegate in order",
            done && string.Join(",", seen.Select(s => s.Kind)) == "alert,confirm,confirm,prompt,prompt",
            new JsonArray(seen.Select(s => (JsonNode)new JsonObject
            {
                ["kind"] = s.Kind, ["message"] = s.Message, ["defaultText"] = s.DefaultText, ["frameHost"] = s.FrameHost, ["isMainFrame"] = s.IsMainFrame,
            }).ToArray()));
        ctx.Check("messages, default text and origin are passed",
            seen.Count == 5 && seen[0].Message == "Saved." && seen[1].Message == "Delete project?" && seen[3].DefaultText == "Ada"
            && seen.All(s => s.FrameHost == "127.0.0.1" && s.IsMainFrame));
        ctx.Check("page got the answers (confirm true/false, prompt text/null)",
            log?[1]?["value"]?.GetValue<bool>() == true && log?[2]?["value"]?.GetValue<bool>() == false
            && log?[3]?["value"]?.GetValue<string>() == "Ada-typed" && log?[4]?["value"] is null, log);
        ctx.Check("page JS suspended while the alert was open (≥ 250 ms)", log?[0]?["elapsedMs"]?.GetValue<int>() >= 250, log?[0]);

        // A dialog left open: what an agent script sees, then exactly-once default on dispose.
        var pending = ctx.CreateWebView(HookOptions.Reference);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.OnJsDialog = _ =>
        {
            opened.TrySetResult();
            return new TaskCompletionSource<object?>().Task; // never answered by us
        };
        await pending.LoadAsync(ctx.Server.Url("/dialogs"));
        await pending.EvalAsync("setTimeout(() => { window.__r = confirm('Leave open?'); }, 0), true");
        var shown = await Task.WhenAny(opened.Task, Task.Delay(5000)) == opened.Task;
        ctx.Check("pending confirm reached the delegate", shown);
        var evalDuring = pending.EvalAsync("1 + 1");
        var evalReturned = await Task.WhenAny(evalDuring, Task.Delay(1500)) == evalDuring;
        ctx.Data["evaluateJavaScriptWhileDialogOpen"] = evalReturned ? "returned" : "blocked until the dialog is answered";
        var answered = pending.CompletePendingWithDefaults();
        ctx.Check("pending handler answered once with its default", answered == 1 && pending.PendingHandlers.Count == 0, answered);
        var after = await Task.WhenAny(evalDuring, Task.Delay(3000)) == evalDuring;
        var r = after ? await pending.EvalAsync("window.__r") : null;
        ctx.Check("page resumes after the default answer (confirm → false)", after && r?.GetValue<bool>() == false, r);
        pending.Dispose();
        var survivor = await view.EvalAsync("2 + 2");
        ctx.Check("no WebKit exception: process alive after disposing a view with a dialog", survivor?.GetValue<int>() == 4);
    }
}
