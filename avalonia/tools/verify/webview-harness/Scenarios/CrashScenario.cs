using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>kill -9 of the page's WebContent process → webViewWebContentProcessDidTerminate, pending dialog answered, reload recovers.</summary>
internal sealed partial class CrashScenario : IScenario
{
    private const int SIGKILL = 9;

    public string Name => "crash";

    public string Description => "killed web process: termination callback, pending handlers answered with defaults, scripts fail, reload recovers";

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.Reference);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.OnJsDialog = _ =>
        {
            opened.TrySetResult();
            return new TaskCompletionSource<object?>().Task;
        };
        await view.LoadAsync(ctx.Server.Url("/dialogs"));
        var pid = view.WebProcessId;
        ctx.Check("WebContent pid known (_webProcessIdentifier)", pid > 0, pid);
        if (pid <= 0) return;
        await view.EvalAsync("setTimeout(() => confirm('still there?'), 0), true");
        await Task.WhenAny(opened.Task, Task.Delay(5000));

        var killed = kill(pid, SIGKILL) == 0;
        ctx.Check("kill -9 of our own WebContent process", killed, pid);
        var noticed = await HarnessWebView.WaitForAsync(() => view.ContentProcessTerminations > 0, TimeSpan.FromSeconds(10));
        ctx.Check("webViewWebContentProcessDidTerminate called", noticed, view.ContentProcessTerminations);
        var answered = HarnessLog.Snapshot().FirstOrDefault(e => (string?)e["event"] == "webContentProcessDidTerminate")?["pendingAnsweredWithDefault"];
        ctx.Check("pending dialog handler answered with its default on crash", answered?.GetValue<int>() == 1, answered);

        string? scriptError = null;
        try
        {
            var evalTask = view.EvalRawAsync("1");
            if (await Task.WhenAny(evalTask, Task.Delay(3000)) != evalTask) scriptError = "timeout";
            else await evalTask;
        }
        catch (JsException e)
        {
            scriptError = e.Message;
        }
        ctx.Data["scriptAfterCrash"] = scriptError ?? "(returned)";
        ctx.Check("scripts after the crash fail instead of hanging", scriptError is not null && scriptError != "timeout", scriptError);

        var reload = await view.ReloadAsync();
        var alive = await view.EvalAsync("document.title");
        ctx.Check("reload recovers with a new web process",
            reload["ok"]?.GetValue<bool>() == true && alive?.GetValue<string>() == "dialogs" && view.WebProcessId is var p && p > 0 && p != pid,
            new JsonObject { ["reload"] = reload.DeepClone(), ["newPid"] = view.WebProcessId });
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);
}
