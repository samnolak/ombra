using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Threading;
using Ombra.App.Platform.Mac;

namespace Ombra.App.Nightly;

/// <summary>Native print/page setup sheets belong to Ombra, never to Nightly's hidden window.
/// NSPrintInfo travels only over the authenticated runtime channel; the preview's Gecko-specific settings stay in Nightly.</summary>
[SupportedOSPlatform("macos")]
internal static class NightlyPrintPanel
{
    public sealed record Request(string PrintInfo, bool PageSetup);
    private sealed record Pending(TaskCompletionSource<string> Completion, nint Info, nint Panel, nint View, nint Operation);
    private static readonly Dictionary<nint, Pending> PendingPanels = new();
    private static nint _delegateClass;

    public static Request? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var q = doc.RootElement;
        return q.TryGetProperty("kind", out var kind) && kind.GetString() == "print"
            && q.TryGetProperty("printInfo", out var info) && info.GetString() is { } archive
            ? new Request(archive, q.TryGetProperty("pageSetup", out var setup) && setup.GetBoolean()) : null;
    }
    public static unsafe Task<string> ShowAsync(nint window, Request request, CancellationToken cancellation)
    {
        if (window == 0) return Task.FromResult("{}");
        var data = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSData"), "alloc"), "initWithBase64EncodedString:options:", MacObjC.Str(request.PrintInfo), 0);
        var info = MacObjC.Retain(MacObjC.Send(MacObjC.Cls("NSKeyedUnarchiver"), "unarchivedObjectOfClass:fromData:error:", MacObjC.Cls("NSPrintInfo"), data, 0));
        MacObjC.Release(data);
        if (!MacObjC.IsKindOf(info, "NSPrintInfo"))
        {
            MacObjC.Release(info);
            return Task.FromResult("{}");
        }
        nint view = 0, operation = 0;
        if (!request.PageSetup)
        {
            view = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSView"), "alloc"), "init");
            operation = MacObjC.Retain(MacObjC.Send(MacObjC.Cls("NSPrintOperation"), "printOperationWithView:printInfo:", view, info));
            MacObjC.SendVoid(MacObjC.Cls("NSPrintOperation"), "setCurrentOperation:", operation);
        }
        var panel = MacObjC.Retain(MacObjC.Send(MacObjC.Cls(request.PageSetup ? "NSPageLayout" : "NSPrintPanel"), request.PageSetup ? "pageLayout" : "printPanel"));
        if (!request.PageSetup)
            MacObjC.SendVoidLong(panel, "setOptions:", 1 | 2 | 4 | 8 | 16 | 256);
        if (_delegateClass == 0)
            _delegateClass = MacObjC.DefineClass("OmbraPrintPanelDelegate", null,
                ("panelDidEnd:returnCode:contextInfo:", (nint)(delegate* unmanaged<nint, nint, nint, long, nint, void>)&DidEnd, "v@:@q^v"));
        var callback = MacObjC.Send(MacObjC.Send(_delegateClass, "alloc"), "init");
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingPanels.Add(callback, new(done, info, panel, view, operation));
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, void>)MacObjC.MsgSend)(panel,
            MacObjC.Sel("beginSheetWithPrintInfo:modalForWindow:delegate:didEndSelector:contextInfo:"),
            info, window, callback, MacObjC.Sel("panelDidEnd:returnCode:contextInfo:"), 0);
        return AwaitPanelAsync(done.Task, window, callback, cancellation);
    }

    private static async Task<string> AwaitPanelAsync(Task<string> task, nint window, nint callback, CancellationToken cancellation)
    {
        using var registration = cancellation.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (!PendingPanels.ContainsKey(callback)) return;
            var sheet = MacObjC.Send(window, "attachedSheet");
            if (sheet != 0) MacObjC.SendVoid(window, "endSheet:returnCode:", sheet, 0);
        }));
        return await task;
    }

    [UnmanagedCallersOnly]
    private static unsafe void DidEnd(nint self, nint cmd, nint panel, long code, nint context)
    {
        if (!PendingPanels.Remove(self, out var pending)) return;
        try
        {
            var answer = "{}";
            if (code == 1)
            {
                var archive = ((delegate* unmanaged<nint, nint, nint, byte, nint, nint>)MacObjC.MsgSend)(
                    MacObjC.Cls("NSKeyedArchiver"), MacObjC.Sel("archivedDataWithRootObject:requiringSecureCoding:error:"), pending.Info, 1, 0);
                var text = MacObjC.ToStr(MacObjC.Send(archive, "base64EncodedStringWithOptions:", 0));
                if (text is not null) answer = JsonSerializer.Serialize(new { printInfo = text });
            }
            pending.Completion.TrySetResult(answer);
        }
        catch (Exception error) { pending.Completion.TrySetException(error); }
        finally
        {
            if (pending.Operation != 0) MacObjC.SendVoid(MacObjC.Cls("NSPrintOperation"), "setCurrentOperation:", 0);
            MacObjC.Release(pending.Operation);
            MacObjC.Release(pending.View);
            MacObjC.Release(pending.Info);
            MacObjC.Release(pending.Panel);
            MacObjC.Release(self);
        }
    }
}
