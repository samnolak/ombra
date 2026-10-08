using System.Text.Json.Nodes;

namespace OmpGui.WebViewHarness.Native;

/// <summary>The asynchronous halves of the delegate callbacks: ask the scenario's handler, then answer the block once.</summary>
internal static partial class Delegates
{
    private static async Task AnswerAuthAsync(HarnessWebView view, AuthChallenge info, PendingBlock pending)
    {
        (string User, string Password)? answer = null;
        try
        {
            answer = await view.OnAuth!(info);
        }
        catch (Exception e)
        {
            HarnessLog.Add(view.Label, "auth-handler-exception", new JsonObject { ["error"] = e.Message });
        }
        view.PendingHandlers.Remove(pending);
        if (answer is { } a) pending.Complete(b => UseCredential(b, a.User, a.Password));
        else pending.Default();
    }

    private static async Task AnswerPanelAsync(HarnessWebView view, JsDialog dialog, PendingBlock pending, Action<nint, object?> answer)
    {
        object? value = null;
        var ok = false;
        try
        {
            value = await view.OnJsDialog!(dialog);
            ok = true;
        }
        catch (Exception e)
        {
            HarnessLog.Add(view.Label, "dialog-handler-exception", new JsonObject { ["error"] = e.Message });
        }
        view.PendingHandlers.Remove(pending);
        if (ok) pending.Complete(b => answer(b, value));
        else pending.Default();
    }

    private static async Task AnswerOpenPanelAsync(HarnessWebView view, OpenPanelRequest request, PendingBlock pending)
    {
        IReadOnlyList<string>? files = null;
        try
        {
            files = await view.OnOpenPanel!(request);
        }
        catch (Exception e)
        {
            HarnessLog.Add(view.Label, "openpanel-handler-exception", new JsonObject { ["error"] = e.Message });
        }
        view.PendingHandlers.Remove(pending);
        if (files is null)
        {
            pending.Default();
            return;
        }
        pending.Complete(b =>
        {
            var array = ObjC.Send(ObjC.Cls("NSMutableArray"), "array");
            foreach (var f in files) ObjC.SendVoid(array, "addObject:", ObjC.FileUrl(f));
            InvokeObject(b, array);
        });
    }
}
