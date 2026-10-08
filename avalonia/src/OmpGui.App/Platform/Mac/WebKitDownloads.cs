using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// Downloads (<c>WKDownload</c>, macOS 11.3+): a navigation the response policy or an <c>&lt;a download&gt;</c> turned
/// into a download gets our <c>WKDownloadDelegate</c> (class <c>OmpWKDownloadDelegate</c>). Where it is saved is
/// asked through <see cref="BrowserPageHooks.ChooseDownloadPath"/> (the app: the download card, then the folder the
/// user chose or the save panel); nothing is written anywhere until that answers with a path. Progress is read from
/// the download's <c>NSProgress</c> every 250 ms while it runs. Main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class WebKitDownloads
{
    private static readonly Dictionary<nint, Entry> Running = []; // WKDownload* (retained) → state; main thread only

    private static unsafe nint DownloadDelegateClass { get; } = MacObjC.DefineClass("OmpWKDownloadDelegate", "WKDownloadDelegate",
        ("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&DecideDestination, "v@:@@@@?"),
        ("downloadDidFinish:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DidFinish, "v@:@"),
        ("download:didFailWithError:resumeData:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidFail, "v@:@@@"));

    private static nint _delegate;

    private sealed class Entry(nint download, BrowserPageHooks hooks, BrowserDownload item)
    {
        public readonly nint Download = download;
        public readonly BrowserPageHooks Hooks = hooks;
        public readonly BrowserDownload Item = item;
        public readonly CancellationTokenSource Asking = new();
        public MacPendingBlock? Destination;
        public bool StoppedByUser;
    }

    /// <summary>How many downloads are still running (harness and tests).</summary>
    internal static int RunningCount => Running.Count;

    /// <summary><c>webView:navigationAction:didBecomeDownload:</c> and <c>webView:navigationResponse:didBecomeDownload:</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void DidBecomeDownload(nint self, nint cmd, nint webView, nint actionOrResponse, nint download) =>
        MacObjC.Guard(() =>
        {
            if (WebKitPage.Find(webView) is not { } page || download == 0) return; // no delegate: WebKit cancels it at the destination step
            var request = MacObjC.Send(download, "originalRequest");
            var url = request == 0 ? null : MacObjC.ToUri(MacObjC.Send(request, "URL"));
            bool agent;
            try
            {
                agent = page.Hooks.AgentActive();
            }
            catch (Exception)
            {
                agent = false;
            }
            var item = new BrowserDownload(url, page.Url) { CausedByAgent = agent, State = BrowserDownloadState.Asking };
            var entry = new Entry(MacObjC.Retain(download), page.Hooks, item); // the delegate property is weak: we keep the download
            Running[entry.Download] = entry;
            item.Canceller = () =>
            {
                MacObjC.OnMainThread(() => Stop(entry));
                return true;
            };
            if (_delegate == 0) _delegate = MacObjC.Send(MacObjC.Send(DownloadDelegateClass, "alloc"), "init");
            MacObjC.SendVoid(download, "setDelegate:", _delegate);
            page.Hooks.RaiseDownloadChanged(item);
        });

    /// <summary>Required: where to save. Nil cancels the download.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DecideDestination(nint self, nint cmd, nint download, nint response, nint suggestedFilename, nint handler)
    {
        if (!Running.TryGetValue(download, out var entry))
        {
            MacBlocks.InvokeObject(handler, 0);
            return;
        }
        MacPendingBlock? pending = null;
        try
        {
            var item = entry.Item;
            item.FileName = BrowserDownloads.SafeFileName(MacObjC.ToStr(suggestedFilename));
            var length = response == 0 ? -1 : MacObjC.SendLong(response, "expectedContentLength");
            item.TotalBytes = length > 0 ? length : -1;
            pending = entry.Destination = new MacPendingBlock(handler, static b => MacBlocks.InvokeObject(b, 0));
            var url = response == 0 ? item.Url : MacObjC.ToUri(MacObjC.Send(response, "URL")) ?? item.Url;
            var request = new DownloadRequest(item.FileName, length > 0 ? length : null, url) { PageUrl = item.PageUrl, CausedByAgent = item.CausedByAgent };
            entry.Hooks.ChooseDownloadPath(request, entry.Asking.Token).ContinueWith(t =>
            {
                var path = t.IsCompletedSuccessfully ? t.Result : null;
                MacObjC.OnMainThread(() => Decided(entry, path));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception)
        {
            if (pending is null) MacBlocks.InvokeObject(handler, 0);
            else MacObjC.OnMainThread(() => Decided(entry, null));
        }
    }

    private static void Decided(Entry entry, string? path)
    {
        if (!Running.ContainsKey(entry.Download)) return; // it failed while asking (answered there)
        var item = entry.Item;
        if (path is null || entry.StoppedByUser)
        {
            entry.Destination?.Default(); // WebKit cancels; download:didFailWithError: follows
            item.State = BrowserDownloadState.Canceled;
            entry.Hooks.RaiseDownloadChanged(item);
            return;
        }
        item.Path = path;
        item.State = BrowserDownloadState.Saving;
        entry.Destination?.Complete(b => MacBlocks.InvokeObject(b, MacObjC.FileUrl(path)));
        entry.Hooks.RaiseDownloadChanged(item);
        _ = FollowProgressAsync(entry);
    }

    private static async Task FollowProgressAsync(Entry entry)
    {
        while (entry.Item.State == BrowserDownloadState.Saving)
        {
            await Task.Delay(250).ConfigureAwait(false);
            MacObjC.OnMainThread(() => ReadProgress(entry));
        }
    }

    private static void ReadProgress(Entry entry)
    {
        if (!Running.ContainsKey(entry.Download)) return;
        var progress = MacObjC.Send(entry.Download, "progress");
        if (progress == 0) return;
        var done = MacObjC.SendLong(progress, "completedUnitCount");
        var total = MacObjC.SendLong(progress, "totalUnitCount");
        if (total > 0) entry.Item.TotalBytes = total;
        if (done >= 0) entry.Item.BytesReceived = done;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFinish(nint self, nint cmd, nint download) =>
        MacObjC.Guard(() =>
        {
            if (!Running.Remove(download, out var entry)) return;
            ReadProgressOf(entry);
            if (entry.Item.TotalBytes <= 0 && entry.Item.Path is { } p && File.Exists(p)) entry.Item.TotalBytes = new FileInfo(p).Length;
            if (entry.Item.TotalBytes > 0) entry.Item.BytesReceived = entry.Item.TotalBytes;
            entry.Item.State = BrowserDownloadState.Finished;
            entry.Item.Canceller = null;
            entry.Hooks.RaiseDownloadChanged(entry.Item);
            MacObjC.Release(entry.Download);
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFail(nint self, nint cmd, nint download, nint error, nint resumeData) =>
        MacObjC.Guard(() =>
        {
            if (!Running.Remove(download, out var entry)) return;
            entry.Asking.Cancel();
            entry.Destination?.Default(); // failed before a place was chosen: the handler is still owed its answer
            var item = entry.Item;
            item.Canceller = null;
            if (entry.StoppedByUser || item.State == BrowserDownloadState.Canceled) item.State = BrowserDownloadState.Canceled;
            else
            {
                var (domain, code, message) = MacObjC.Error(error);
                item.Error = message.Length > 0 ? message : $"{domain} {code}";
                item.State = BrowserDownloadState.Failed;
            }
            entry.Hooks.RaiseDownloadChanged(item);
            MacObjC.Release(entry.Download);
        });

    private static void ReadProgressOf(Entry entry)
    {
        try
        {
            ReadProgress(entry);
        }
        catch (Exception)
        {
            // the sizes stay as they were
        }
    }

    /// <summary>The user stopped it: asking ends (the card goes), a running download is cancelled (<c>cancel:</c>).</summary>
    private static void Stop(Entry entry)
    {
        if (!Running.ContainsKey(entry.Download)) return;
        entry.StoppedByUser = true;
        entry.Asking.Cancel();
        if (entry.Item.State == BrowserDownloadState.Saving) MacObjC.SendVoid(entry.Download, "cancel:", 0);
    }
}
