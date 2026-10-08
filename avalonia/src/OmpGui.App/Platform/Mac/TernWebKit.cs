using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmpGui.ClientCore.Browser;
using static OmpGui.App.Platform.Mac.MacObjC;
using static OmpGui.App.Platform.Mac.MacRuntime;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// The WKWebView side of a Tern tab (<see cref="ITernPage"/>, omp 18.8 tools/browser/tern): scripts in the page world
/// and in omp's isolated world (<see cref="WorldName"/>) of any frame, document-start scripts, the
/// <c>stencil</c> (page world: omp's console/network capture) and <c>stencilFrame</c> (isolated world: a child frame
/// announcing its index path) message handlers, URL and title changes (KVO, so same-document navigations count),
/// captures (viewport, rect, whole page; PNG or JPEG at any scale), PDF, user agent, appearance, cookies, copy.
/// </summary>
/// <remarks>
/// Main thread only for WebKit; the async methods move there. No navigation or UI delegate selectors: those are
/// <see cref="WebKitHooks"/>' (their events reach the tab through <see cref="BrowserPageHooks"/>). No Avalonia types:
/// the windowless harness compiles this file.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class TernWebKit : IDisposable
{
    /// <summary>omp's isolated world: every isolated eval and script shares it per document (the kit lives there).</summary>
    public const string WorldName = "omp-tern";

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const nuint KeyValueObservingNew = 1;
    private const nint PngFileType = 4; // NSBitmapImageFileTypePNG
    private const nint JpegFileType = 3; // NSBitmapImageFileTypeJPEG
    /// <summary>Largest capture edge in pixels (a whole-page capture of a very long page is cut there).</summary>
    private const double MaxCaptureEdge = 16384;

    private static nint _observerClass;

    private readonly nint _web;
    private readonly ITernEventSink _sink;
    private readonly Dictionary<string, nint> _frames = new(StringComparer.Ordinal);
    private readonly List<nint> _scripts = [];
    private readonly List<IDisposable> _handlers = [];
    private GCHandle _self;
    private nint _observer;
    private string? _lastUrl;
    private string? _lastTitle;
    private bool _disposed;

    private TernWebKit(nint web, ITernEventSink sink)
    {
        _web = Retain(web);
        _sink = sink;
    }

    /// <summary>The engine of the <c>WKWebView*</c> <paramref name="wkWebView"/>; its events go to <paramref name="sink"/>. Main thread.</summary>
    public static TernWebKit Attach(nint wkWebView, ITernEventSink sink)
    {
        if (wkWebView == 0) throw new ArgumentException("No web view.", nameof(wkWebView));
        if (!IsMainThread) throw new InvalidOperationException("WebKit is driven on the main thread.");
        var engine = new TernWebKit(wkWebView, sink);
        engine._self = GCHandle.Alloc(engine);
        engine._handlers.Add(IsolatedWorld.AddMessageHandler(wkWebView, null, "stencil",
            (body, _) => sink.Post("message", new JsonObject { ["world"] = "page", ["body"] = body })));
        engine._handlers.Add(IsolatedWorld.AddMessageHandler(wkWebView, WorldName, "stencilFrame", engine.OnFrameAnnounced));
        engine.Observe();
        return engine;
    }

    // ── Navigation (the app navigates through its pane; the harness through these) ──

    public Task NavigateAsync(Uri url) => OnMainAsync(() =>
    {
        using var address = new NSStr(url.AbsoluteUri);
        var nsUrl = Send(Cls("NSURL"), "URLWithString:", address.Handle);
        if (nsUrl == 0) throw new TernException(TernException.Invalid, $"WebKit refused the address {url}.");
        Send(_web, "loadRequest:", Send(Cls("NSURLRequest"), "requestWithURL:", nsUrl));
    });

    public Task HistoryAsync(TernHistory go) => OnMainAsync(() =>
    {
        Send(_web, go switch
        {
            TernHistory.Back => "goBack",
            TernHistory.Forward => "goForward",
            _ => "reload",
        });
    });

    /// <summary>The page's state as WebKit has it; the viewport is the web view's size (CSS pixels at zoom 1).</summary>
    public Task<TernPageState> GetStateAsync() => OnMainAsync(() =>
    {
        var size = Frame(_web);
        return new TernPageState(CurrentUrl() ?? "about:blank", ToStr(Send(_web, "title")) ?? "", (int)Math.Round(size.Width), (int)Math.Round(size.Height),
            SendBool(_web, "isLoading"), SendBool(_web, "canGoBack"), SendBool(_web, "canGoForward"));
    });

    /// <summary>Sets the web view's own size (the harness; in the app the pane lays it out).</summary>
    public unsafe Task SetFrameSizeAsync(int width, int height) => OnMainAsync(() =>
        ((delegate* unmanaged<nint, nint, CGSize, void>)MsgSend)(_web, Sel("setFrameSize:"), new CGSize(width, height)));

    // ── Scripts ──

    public async Task<string?> EvaluateAsync(string function, string argsJson, TernWorld world, string? frame, CancellationToken ct)
    {
        var frameInfo = frame is null ? 0 : await OnMainAsync(() => _frames.TryGetValue(frame, out var f) ? Retain(f) : 0).ConfigureAwait(false);
        if (frame is not null && frameInfo == 0)
            throw new TernException(TernException.Failed, $"Frame {frame} is not in the page (any more).");
        try
        {
            return await IsolatedWorld.CallAsync(_web, world == TernWorld.Isolated ? WorldName : null, function, argsJson, frameInfo, ct).ConfigureAwait(false);
        }
        catch (IsolatedScriptException e)
        {
            // A script exception (no domain, or WKErrorJavaScriptExceptionOccurred) is the page's; the rest is WebKit's
            var js = e.Domain is null || (e.Domain == "WKErrorDomain" && e.Code == 4);
            throw new TernException(js ? TernException.Js : TernException.Failed, e.Message);
        }
        finally
        {
            if (frameInfo != 0) OnMainThread(() => Release(frameInfo));
        }
    }

    /// <summary>
    /// First in omp's isolated world of every document: its <c>requestAnimationFrame</c> also fires after
    /// <see cref="FrameFallbackMs"/> ms. omp waits for a frame before each capture; a web view not on screen (a pane tab
    /// in the background, the harness) never draws one, and the wait would last until omp's timeout. Only omp's world
    /// sees it: the page's own rAF is untouched.
    /// </summary>
    private const string FrameFallback = """
        (() => {
          const raf = globalThis.requestAnimationFrame, caf = globalThis.cancelAnimationFrame;
          if (typeof raf !== "function" || raf.__ompFallback) return;
          const timers = new Map();
          const wrapped = function (callback) {
            let done = false, id = 0;
            const run = time => { if (done) return; done = true; clearTimeout(timers.get(id)); timers.delete(id); caf.call(globalThis, id); callback(time); };
            id = raf.call(globalThis, run);
            timers.set(id, setTimeout(() => run(performance.now()), FALLBACK_MS));
            return id;
          };
          wrapped.__ompFallback = true;
          globalThis.requestAnimationFrame = wrapped;
          globalThis.cancelAnimationFrame = function (id) { clearTimeout(timers.get(id)); timers.delete(id); caf.call(globalThis, id); };
        })();
        """;

    private const int FrameFallbackMs = 100;

    /// <summary>Replaces omp's document-start scripts (the app's own user scripts stay).</summary>
    public unsafe Task SetScriptsAsync(IReadOnlyList<TernScript> scripts) => OnMainAsync(() =>
    {
        var controller = Send(Send(_web, "configuration"), "userContentController");
        RemoveOurScripts(controller);
        var init = (delegate* unmanaged<nint, nint, nint, nint, byte, nint, nint>)MsgSend;
        var all = scripts.Prepend(new TernScript(FrameFallback.Replace("FALLBACK_MS", FrameFallbackMs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            TernWorld.Isolated, AllFrames: true, AtStart: true));
        foreach (var s in all)
        {
            using var source = new NSStr(s.Source);
            var world = s.World == TernWorld.Isolated ? IsolatedWorld.World(WorldName) : Send(Cls("WKContentWorld"), "pageWorld");
            var script = init(Send(Cls("WKUserScript"), "alloc"), Sel("initWithSource:injectionTime:forMainFrameOnly:inContentWorld:"),
                source.Handle, s.AtStart ? 0 : 1, s.AllFrames ? (byte)0 : (byte)1, world);
            if (script == 0) throw new TernException(TernException.Failed, "WebKit refused a document-start script.");
            SendVoid(controller, "addUserScript:", script);
            _scripts.Add(script); // +1 from alloc, released when replaced
        }
    });

    /// <summary>Removes omp's user scripts and keeps every other one (the app's, Avalonia's), in their order.</summary>
    private void RemoveOurScripts(nint controller)
    {
        if (_scripts.Count == 0) return;
        var all = Retain(Send(controller, "userScripts"));
        try
        {
            var keep = new List<nint>();
            var count = SendNUInt(all, "count");
            for (nuint i = 0; i < count; i++)
            {
                var script = SendIndex(all, "objectAtIndex:", i);
                if (!_scripts.Contains(script)) keep.Add(script);
            }
            SendVoid(controller, "removeAllUserScripts");
            foreach (var script in keep) SendVoid(controller, "addUserScript:", script);
        }
        finally
        {
            Release(all);
        }
        foreach (var script in _scripts) Release(script);
        _scripts.Clear();
    }

    // ── Pictures ──

    /// <summary>The viewport, a rectangle of it, or the whole page, at <see cref="TernCaptureRequest.Scale"/> pixels per CSS pixel.</summary>
    public async Task<byte[]> CaptureAsync(TernCaptureRequest request, CancellationToken ct)
    {
        CGRect rect;
        if (request.FullPage)
        {
            var geometry = await IsolatedWorld.CallAsync(_web, WorldName,
                "function () { const e = document.documentElement; return [Math.max(e.scrollWidth, innerWidth), Math.max(e.scrollHeight, innerHeight), scrollX, scrollY]; }",
                "[]", 0, ct).ConfigureAwait(false);
            var g = JsonNode.Parse(geometry ?? "[]") as JsonArray;
            if (g is not { Count: 4 }) throw new TernException(TernException.Failed, "The page did not report its size.");
            rect = new CGRect(-g[2]!.GetValue<double>(), -g[3]!.GetValue<double>(), g[0]!.GetValue<double>(), g[1]!.GetValue<double>());
        }
        else if (request.Rect is { } r) rect = new CGRect(r.X, r.Y, r.Width, r.Height);
        else
        {
            var size = await OnMainAsync(() => Frame(_web)).ConfigureAwait(false);
            rect = new CGRect(0, 0, size.Width, size.Height);
        }
        if (rect.Width < 1 || rect.Height < 1) throw new TernException(TernException.Failed, "There is nothing to capture: the page has no visible area.");
        var scale = Math.Min(request.Scale, MaxCaptureEdge / Math.Max(rect.Width, rect.Height));
        var pixels = new CGSize(Math.Max(1, Math.Round(rect.Width * scale)), Math.Max(1, Math.Round(rect.Height * scale)));

        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => done.TrySetCanceled(ct));
        await OnMainAsync(() =>
        {
            var config = Send(Send(Cls("WKSnapshotConfiguration"), "alloc"), "init");
            try
            {
                SetRect(config, rect);
                var width = Number(rect.Width);
                SendVoid(config, "setSnapshotWidth:", width);
                var block = ResultBlock((image, error) =>
                {
                    try
                    {
                        done.TrySetResult(Encode(image, error, pixels, request));
                    }
                    catch (Exception e)
                    {
                        done.TrySetException(e);
                    }
                });
                try
                {
                    SendVoid(_web, "takeSnapshotWithConfiguration:completionHandler:", config, block);
                }
                finally
                {
                    MacBlocks.Release(block);
                }
            }
            finally
            {
                Release(config);
            }
        }).ConfigureAwait(false);
        return await done.Task.ConfigureAwait(false);
    }

    /// <summary>The page as one PDF page (WebKit's <c>createPDFWithConfiguration:</c>, the whole content).</summary>
    public async Task<byte[]> PdfAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => done.TrySetCanceled(ct));
        await OnMainAsync(() =>
        {
            if (!RespondsTo(_web, "createPDFWithConfiguration:completionHandler:"))
                throw new TernException(TernException.Unsupported, "PDFs of a page need macOS 11 or later.");
            var block = ResultBlock((data, error) =>
            {
                if (data == 0) done.TrySetException(new TernException(TernException.Failed, "WebKit could not make the PDF: " + Reason(error)));
                else done.TrySetResult(Bytes(data));
            });
            try
            {
                SendVoid(_web, "createPDFWithConfiguration:completionHandler:", 0, block);
            }
            finally
            {
                MacBlocks.Release(block);
            }
        }).ConfigureAwait(false);
        return await done.Task.ConfigureAwait(false);
    }

    private static unsafe byte[] Encode(nint image, nint error, CGSize pixels, TernCaptureRequest request)
    {
        if (image == 0) throw new TernException(TernException.Failed, "WebKit could not take the picture: " + Reason(error));
        var pool = PoolPush();
        try
        {
            var source = ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(image, Sel("CGImageForProposedRect:context:hints:"), 0, 0, 0);
            if (source == 0) throw new TernException(TernException.Failed, "The page's picture is empty.");
            // Exactly the pixels omp asked for, whatever the screen's scale: drawn into a bitmap of that size
            var space = CGColorSpaceCreateDeviceRGB();
            var context = CGBitmapContextCreate(0, (nuint)pixels.Width, (nuint)pixels.Height, 8, 0, space,
                request.Jpeg ? 5u /* kCGImageAlphaNoneSkipLast */ : 1u /* kCGImageAlphaPremultipliedLast */);
            CGColorSpaceRelease(space);
            if (context == 0) throw new TernException(TernException.Failed, "The picture is too large.");
            nint scaled;
            try
            {
                var whole = new CGRect(0, 0, pixels.Width, pixels.Height);
                if (request.Jpeg)
                {
                    CGContextSetRGBFillColor(context, 1, 1, 1, 1);
                    CGContextFillRect(context, whole);
                }
                CGContextSetInterpolationQuality(context, 3 /* kCGInterpolationHigh */);
                CGContextDrawImage(context, whole, source);
                scaled = CGBitmapContextCreateImage(context);
            }
            finally
            {
                CGContextRelease(context);
            }
            if (scaled == 0) throw new TernException(TernException.Failed, "The picture could not be made.");
            var rep = Send(Send(Cls("NSBitmapImageRep"), "alloc"), "initWithCGImage:", scaled);
            CGImageRelease(scaled);
            try
            {
                var properties = Send(Cls("NSMutableDictionary"), "dictionary");
                if (request.Jpeg)
                {
                    var factor = Exported(AppKit, "NSImageCompressionFactor");
                    var quality = Number(request.Quality / 100.0);
                    SendVoid(properties, "setObject:forKey:", quality, factor);
                }
                var data = Send(rep, "representationUsingType:properties:", request.Jpeg ? JpegFileType : PngFileType, properties);
                if (data == 0) throw new TernException(TernException.Failed, "The picture could not be encoded.");
                return Bytes(data);
            }
            finally
            {
                Release(rep);
            }
        }
        finally
        {
            PoolPop(pool);
        }
    }

    // ── Identity and appearance ──

    public Task SetUserAgentAsync(string? userAgent) => OnMainAsync(() =>
    {
        if (userAgent is null)
        {
            SendVoid(_web, "setCustomUserAgent:", 0);
            return;
        }
        using var value = new NSStr(userAgent);
        SendVoid(_web, "setCustomUserAgent:", value.Handle);
    });

    /// <summary><c>prefers-color-scheme</c> follows the web view's appearance: Aqua, Dark Aqua, or the window's (null).</summary>
    public Task SetAppearanceAsync(string? scheme) => OnMainAsync(() =>
    {
        if (scheme is null)
        {
            SendVoid(_web, "setAppearance:", 0);
            return;
        }
        using var name = new NSStr(scheme == "dark" ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
        SendVoid(_web, "setAppearance:", Send(Cls("NSAppearance"), "appearanceNamed:", name.Handle));
    });

    /// <summary>The page's own Copy command (its selection to the system clipboard).</summary>
    public Task CopyAsync() => OnMainAsync(() => SendVoid(_web, "copy:", 0));

    // ── Cookies (the web view's data store) ──

    public async Task<IReadOnlyList<TernCookie>> GetCookiesAsync(CancellationToken ct)
    {
        var list = new List<TernCookie>();
        await ForEachCookieAsync(cookie => list.Add(Describe(cookie)), ct).ConfigureAwait(false);
        return list;
    }

    public async Task SetCookieAsync(TernCookie cookie, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => done.TrySetCanceled(ct));
        await OnMainAsync(() =>
        {
            nint Key(string name) => Exported(Foundation, name);
            var properties = Send(Cls("NSMutableDictionary"), "dictionary");
            var strings = new List<NSStr>();
            void Put(string key, string value)
            {
                var s = new NSStr(value);
                strings.Add(s);
                SendVoid(properties, "setObject:forKey:", s.Handle, Key(key));
            }
            try
            {
                Put("NSHTTPCookieName", cookie.Name);
                Put("NSHTTPCookieValue", cookie.Value);
                Put("NSHTTPCookieDomain", cookie.Domain);
                Put("NSHTTPCookiePath", cookie.Path);
                if (cookie.Secure) Put("NSHTTPCookieSecure", "TRUE");
                if (cookie.SameSite is { } sameSite) Put("NSHTTPCookieSameSitePolicy", sameSite.ToLowerInvariant());
                if (cookie.HttpOnly)
                {
                    // No public key constant: Foundation reads "HttpOnly"
                    var key = new NSStr("HttpOnly");
                    strings.Add(key);
                    var value = new NSStr("TRUE");
                    strings.Add(value);
                    SendVoid(properties, "setObject:forKey:", value.Handle, key.Handle);
                }
                if (cookie.Expires >= 0)
                {
                    var date = DateSince1970(cookie.Expires);
                    SendVoid(properties, "setObject:forKey:", date, Key("NSHTTPCookieExpires"));
                }
                var made = Send(Cls("NSHTTPCookie"), "cookieWithProperties:", properties);
                if (made == 0) throw new TernException(TernException.Invalid, $"The cookie {cookie.Name} is not valid for {cookie.Domain}.");
                var block = VoidBlock(() => done.TrySetResult());
                try
                {
                    SendVoid(CookieStore(), "setCookie:completionHandler:", made, block);
                }
                finally
                {
                    MacBlocks.Release(block);
                }
            }
            finally
            {
                foreach (var s in strings) s.Dispose();
            }
        }).ConfigureAwait(false);
        await done.Task.ConfigureAwait(false);
    }

    public async Task DeleteCookieAsync(string name, string domain, string path, CancellationToken ct)
    {
        var matches = new List<nint>();
        await ForEachCookieAsync(cookie =>
        {
            var c = Describe(cookie);
            if (c.Name == name && (domain.Length == 0 || c.Domain == domain) && c.Path == path) matches.Add(Retain(cookie));
        }, ct).ConfigureAwait(false);
        foreach (var cookie in matches)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => done.TrySetCanceled(ct));
            await OnMainAsync(() =>
            {
                var block = VoidBlock(() => done.TrySetResult());
                try
                {
                    SendVoid(CookieStore(), "deleteCookie:completionHandler:", cookie, block);
                }
                finally
                {
                    MacBlocks.Release(block);
                    Release(cookie);
                }
            }).ConfigureAwait(false);
            await done.Task.ConfigureAwait(false);
        }
    }

    private nint CookieStore() => Send(Send(Send(_web, "configuration"), "websiteDataStore"), "httpCookieStore");

    /// <summary>Calls <paramref name="each"/> on the main thread for every cookie of the store (NSHTTPCookie*).</summary>
    private async Task ForEachCookieAsync(Action<nint> each, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => done.TrySetCanceled(ct));
        await OnMainAsync(() =>
        {
            var block = ValueBlock(cookies =>
            {
                try
                {
                    var count = cookies == 0 ? 0 : SendNUInt(cookies, "count");
                    for (nuint i = 0; i < count; i++) each(SendIndex(cookies, "objectAtIndex:", i));
                    done.TrySetResult();
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                }
            });
            try
            {
                SendVoid(CookieStore(), "getAllCookies:", block);
            }
            finally
            {
                MacBlocks.Release(block);
            }
        }).ConfigureAwait(false);
        await done.Task.ConfigureAwait(false);
    }

    private static TernCookie Describe(nint cookie)
    {
        var expires = Send(cookie, "expiresDate");
        var sameSite = ToStr(Send(cookie, "sameSitePolicy"))?.ToLowerInvariant();
        return new TernCookie(ToStr(Send(cookie, "name")) ?? "", ToStr(Send(cookie, "value")) ?? "", ToStr(Send(cookie, "domain")) ?? "",
            ToStr(Send(cookie, "path")) ?? "/", expires == 0 ? -1 : SendDouble(expires, "timeIntervalSince1970"),
            SendBool(cookie, "isHTTPOnly"), SendBool(cookie, "isSecure"),
            sameSite switch { "strict" => "Strict", "lax" => "Lax", "none" => "None", _ => null });
    }

    // ── Frames: a child frame's kit announces its index path from omp's isolated world ──

    private void OnFrameAnnounced(string body, nint frameInfo)
    {
        if (_disposed || frameInfo == 0 || body.Length == 0 || body.Length > 64) return;
        if (_frames.Remove(body, out var old)) Release(old);
        _frames[body] = Retain(frameInfo);
        var url = ToStr(Send(Send(Send(frameInfo, "request"), "URL"), "absoluteString")) ?? "";
        _sink.Post("frame", new JsonObject { ["name"] = body, ["url"] = url });
    }

    /// <summary>A new document in the main frame: the frames of the old one are gone. Main thread.</summary>
    public void ForgetFrames()
    {
        foreach (var f in _frames.Values) Release(f);
        _frames.Clear();
    }

    // ── URL and title (KVO: same-document navigations and title changes too) ──

    private unsafe void Observe()
    {
        if (_observerClass == 0)
            _observerClass = DefineClass("OmpGuiTernObserver", null,
                ("observeValueForKeyPath:ofObject:change:context:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&OnObserved, "v@:@@@^v"));
        _observer = Send(Send(_observerClass, "alloc"), "init");
        var add = (delegate* unmanaged<nint, nint, nint, nint, nuint, nint, void>)MsgSend;
        foreach (var key in new[] { "URL", "title" })
        {
            using var path = new NSStr(key);
            add(_web, Sel("addObserver:forKeyPath:options:context:"), _observer, path.Handle, KeyValueObservingNew, GCHandle.ToIntPtr(_self));
        }
        _lastUrl = CurrentUrl();
        _lastTitle = ToStr(Send(_web, "title"));
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnObserved(nint self, nint cmd, nint keyPath, nint obj, nint change, nint context)
    {
        try
        {
            if (context == 0 || GCHandle.FromIntPtr(context).Target is not TernWebKit engine || engine._disposed) return;
            var key = ToStr(keyPath);
            if (key == "URL")
            {
                var url = engine.CurrentUrl();
                if (url is null || url == engine._lastUrl) return;
                engine._lastUrl = url;
                engine._sink.Post("url", new JsonObject { ["url"] = url });
            }
            else if (key == "title")
            {
                var title = ToStr(Send(engine._web, "title")) ?? "";
                if (title == engine._lastTitle) return;
                engine._lastTitle = title;
                engine._sink.Post("title", new JsonObject { ["title"] = title });
            }
        }
        catch
        {
            // Nothing may unwind into Foundation
        }
    }

    private string? CurrentUrl()
    {
        var url = Send(_web, "URL");
        return url == 0 ? null : ToStr(Send(url, "absoluteString"));
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OnMainThread(() =>
        {
            var remove = (delegate* unmanaged<nint, nint, nint, nint, nint, void>)MsgSend;
            foreach (var key in new[] { "URL", "title" })
            {
                using var path = new NSStr(key);
                remove(_web, Sel("removeObserver:forKeyPath:context:"), _observer, path.Handle, GCHandle.ToIntPtr(_self));
            }
            Release(_observer);
            _self.Free();
            foreach (var handler in _handlers) handler.Dispose();
            _handlers.Clear();
            // omp's scripts leave with omp: a tab kept for the user runs only the app's own
            RemoveOurScripts(Send(Send(_web, "configuration"), "userContentController"));
            ForgetFrames();
            Release(_web);
        });
    }

    // ── Helpers ──

    private static byte[] Bytes(nint data)
    {
        var length = checked((int)SendLong(data, "length"));
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(Send(data, "bytes"), bytes, 0, length);
        return bytes;
    }

    /// <summary>A view's frame size (structs come back in registers on arm64, through memory on x86-64).</summary>
    private static unsafe CGSize Frame(nint view)
    {
        CGRect frame;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            var stret = NativeLibrary.GetExport(NativeLibrary.Load(LibObjC), "objc_msgSend_stret");
            ((delegate* unmanaged<CGRect*, nint, nint, void>)stret)(&frame, view, Sel("frame"));
        }
        else frame = ((delegate* unmanaged<nint, nint, CGRect>)MsgSend)(view, Sel("frame"));
        return new CGSize(frame.Width, frame.Height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGSize(double Width, double Height);

    private static string Reason(nint error) => Error(error).Message is { Length: > 0 } m ? m + "." : "no reason given.";

    /// <summary>An exported <c>NSString * const</c> of <paramref name="library"/> (the export is the variable's address).</summary>
    private static unsafe nint Exported(string library, string name) => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(library), name);

    /// <summary>An autoreleased <c>NSNumber</c>.</summary>
    private static unsafe nint Number(double value) =>
        ((delegate* unmanaged<nint, nint, double, nint>)MsgSend)(Cls("NSNumber"), Sel("numberWithDouble:"), value);

    /// <summary>An autoreleased <c>NSDate</c>.</summary>
    private static unsafe nint DateSince1970(double seconds) =>
        ((delegate* unmanaged<nint, nint, double, nint>)MsgSend)(Cls("NSDate"), Sel("dateWithTimeIntervalSince1970:"), seconds);

    private static unsafe void SetRect(nint configuration, CGRect rect) =>
        ((delegate* unmanaged<nint, nint, CGRect, void>)MsgSend)(configuration, Sel("setRect:"), rect);

    /// <summary>A block for <c>void (^)(id)</c>, called at most once (+1: release it after handing it over).</summary>
    private static unsafe nint ValueBlock(Action<nint> handler) =>
        MacBlocks.Create((nint)(delegate* unmanaged<nint, nint, void>)&InvokeValue, handler, "v16@?0@8");

    /// <summary>A block for <c>void (^)(void)</c>, called at most once (+1: release it after handing it over).</summary>
    private static unsafe nint VoidBlock(Action handler) =>
        MacBlocks.Create((nint)(delegate* unmanaged<nint, void>)&InvokeVoid, handler, "v8@?0");

    [UnmanagedCallersOnly]
    private static void InvokeValue(nint block, nint value)
    {
        try
        {
            MacBlocks.TakeState<Action<nint>>(block)?.Invoke(value);
        }
        catch
        {
            // Nothing may unwind into WebKit
        }
    }

    [UnmanagedCallersOnly]
    private static void InvokeVoid(nint block)
    {
        try
        {
            MacBlocks.TakeState<Action>(block)?.Invoke();
        }
        catch
        {
            // Nothing may unwind into WebKit
        }
    }

    [DllImport(CoreGraphics)]
    private static extern nint CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphics)]
    private static extern void CGColorSpaceRelease(nint space);

    [DllImport(CoreGraphics)]
    private static extern nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);

    [DllImport(CoreGraphics)]
    private static extern void CGContextSetInterpolationQuality(nint context, int quality);

    [DllImport(CoreGraphics)]
    private static extern void CGContextSetRGBFillColor(nint context, double r, double g, double b, double a);

    [DllImport(CoreGraphics)]
    private static extern void CGContextFillRect(nint context, CGRect rect);

    [DllImport(CoreGraphics)]
    private static extern void CGContextDrawImage(nint context, CGRect rect, nint image);

    [DllImport(CoreGraphics)]
    private static extern nint CGBitmapContextCreateImage(nint context);

    [DllImport(CoreGraphics)]
    private static extern void CGContextRelease(nint context);

    [DllImport(CoreGraphics)]
    private static extern void CGImageRelease(nint image);
}
