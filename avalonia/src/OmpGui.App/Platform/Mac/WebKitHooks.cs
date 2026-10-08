using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// WKWebView hooks Avalonia 12.1 leaves out, added without forking it. HTTP authentication (Basic, Digest, NTLM,
/// Negotiate, proxies): Avalonia's navigation delegate (class <c>ManagedWKNavigationDelegate</c>) implements only
/// <c>webView:didFinishNavigation:</c> and <c>webView:decidePolicyForNavigationAction:decisionHandler:</c>, so WebKit
/// rejects every challenge and shows the server's 401 page. <see cref="Attach"/> adds
/// <c>webView:didReceiveAuthenticationChallenge:completionHandler:</c> to the runtime class of the web view's current
/// navigation delegate (<c>class_addMethod</c>) and assigns the same delegate again, because WebKit caches which
/// selectors a delegate answers when it is set. The challenge goes to the page's <see cref="BrowserPageHooks"/> (the
/// pane's sign-in card); its answer becomes an <c>NSURLCredential</c> with session persistence (never the keychain).
/// Everything else (UI delegate, loading events, crash, downloads, pop-ups, permissions) is <see cref="WebKitPage"/>,
/// installed by the same <see cref="Attach"/>.
/// </summary>
/// <remarks>
/// <para>Server trust and client certificates keep WebKit's default handling (TLS validation stays intact; a
/// certificate picker would need keychain access).</para>
/// <para>Blocks: WebKit's completion handler is copied (<c>_Block_copy</c>) when the user has to answer, invoked exactly
/// once on the main thread, then released. A handler never called would raise in WebKit when it is deallocated, so
/// every pending challenge is answered Cancel on <see cref="CancelPending"/> (navigation away) and on detach (web view
/// destroyed). Challenges for the same protection space that arrive while one is asked are answered together.</para>
/// <para>No Avalonia types here: the windowless harness (<c>tools/verify/webview-harness</c>) links this file. Delegate
/// callbacks arrive on the main thread; answers are moved back there with <c>dispatch_async_f</c> on the main queue.</para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe class WebKitHooks
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    // NSURLSessionAuthChallengeDisposition
    private const nint UseCredential = 0;
    private const nint PerformDefaultHandling = 1;
    private const nint CancelAuthenticationChallenge = 2;
    private const nuint PersistenceForSession = 1; // NSURLCredentialPersistenceForSession

    /// <summary>The added method: <c>-(void)webView:(WKWebView*) didReceiveAuthenticationChallenge:(NSURLAuthenticationChallenge*) completionHandler:(void(^)(NSURLSessionAuthChallengeDisposition, NSURLCredential*))</c>.</summary>
    private const string AuthChallengeSelector = "webView:didReceiveAuthenticationChallenge:completionHandler:";

    private static readonly Lock Gate = new();
    private static Runtime? _runtime;

    /// <summary>Hooks per web view (<c>WKWebView*</c>): the added IMP runs on Avalonia's delegate object, so it finds its hooks by the web view.</summary>
    private static readonly Dictionary<nint, Attachment> Attached = [];

    /// <summary>Selectors, classes and Foundation constants, looked up once.</summary>
    private sealed class Runtime
    {
        public readonly nint NSURLCredential = objc_getClass("NSURLCredential");
        public readonly nint NSString = objc_getClass("NSString");
        public readonly nint AuthChallenge = sel_registerName(AuthChallengeSelector);
        public readonly nint NavigationDelegate = sel_registerName("navigationDelegate");
        public readonly nint SetNavigationDelegate = sel_registerName("setNavigationDelegate:");
        public readonly nint ProtectionSpace = sel_registerName("protectionSpace");
        public readonly nint AuthenticationMethod = sel_registerName("authenticationMethod");
        public readonly nint Host = sel_registerName("host");
        public readonly nint Port = sel_registerName("port");
        public readonly nint Realm = sel_registerName("realm");
        public readonly nint Protocol = sel_registerName("protocol");
        public readonly nint IsProxy = sel_registerName("isProxy");
        public readonly nint PreviousFailureCount = sel_registerName("previousFailureCount");
        public readonly nint ProposedCredential = sel_registerName("proposedCredential");
        public readonly nint User = sel_registerName("user");
        public readonly nint IsEqualToString = sel_registerName("isEqualToString:");
        public readonly nint Utf8String = sel_registerName("UTF8String");
        public readonly nint StringWithUtf8String = sel_registerName("stringWithUTF8String:");
        public readonly nint CredentialWithUser = sel_registerName("credentialWithUser:password:persistence:");
        public readonly nint MainQueue;

        /// <summary>The methods that take a username and password, with the scheme name shown on the card.</summary>
        public readonly (nint Method, string Scheme)[] PasswordMethods;

        public Runtime()
        {
            if (NSURLCredential == 0 || NSString == 0) throw new InvalidOperationException("Foundation is not loaded.");
            var foundation = NativeLibrary.Load(Foundation);
            PasswordMethods =
            [
                (Constant(foundation, "NSURLAuthenticationMethodHTTPBasic"), "Basic"),
                (Constant(foundation, "NSURLAuthenticationMethodHTTPDigest"), "Digest"),
                (Constant(foundation, "NSURLAuthenticationMethodNTLM"), "NTLM"),
                (Constant(foundation, "NSURLAuthenticationMethodNegotiate"), "Negotiate"),
                (Constant(foundation, "NSURLAuthenticationMethodHTMLForm"), "Form"),
                (Constant(foundation, "NSURLAuthenticationMethodDefault"), "Basic"),
            ];
            // dispatch_get_main_queue() is a macro for &_dispatch_main_q
            MainQueue = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_dispatch_main_q");
        }

        /// <summary>An exported <c>NSString * const</c>: the export is the variable's address.</summary>
        private static nint Constant(nint library, string name) => *(nint*)NativeLibrary.GetExport(library, name);
    }

    private static Runtime Objc
    {
        get
        {
            lock (Gate) return _runtime ??= new Runtime();
        }
    }

    /// <summary>
    /// Installs the hooks on <paramref name="wkWebView"/> (a <c>WKWebView*</c> that has its navigation delegate):
    /// sign-in requests and the page's other questions go to <paramref name="page"/>, which also gets its events.
    /// Disposing the result detaches (what is still pending gets its default answer). Null, with
    /// <paramref name="problem"/>, when the delegate cannot take the methods (none set, or it already has its own: a
    /// newer Avalonia that handles authentication itself). Call on the main thread.
    /// </summary>
    public static IDisposable? Attach(nint wkWebView, BrowserPageHooks page, out string? problem)
    {
        problem = null;
        if (wkWebView == 0)
        {
            problem = "No web view.";
            return null;
        }
        var rt = Objc;
        var navigationDelegate = intptr_objc_msgSend(wkWebView, rt.NavigationDelegate);
        if (navigationDelegate == 0)
        {
            problem = "The web view has no navigation delegate.";
            return null;
        }
        var cls = object_getClass(navigationDelegate);
        var ours = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&OnAuthenticationChallenge;
        var existing = class_getInstanceMethod(cls, rt.AuthChallenge);
        if (existing != 0 && method_getImplementation(existing) != ours)
        {
            problem = $"The navigation delegate ({Marshal.PtrToStringUTF8(class_getName(cls))}) already handles authentication.";
            return null;
        }
        // void, self, _cmd, WKWebView*, NSURLAuthenticationChallenge*, block
        if (existing == 0 && class_addMethod(cls, rt.AuthChallenge, ours, "v@:@@@?") == 0)
        {
            problem = "The authentication method could not be added to the navigation delegate.";
            return null;
        }
        // Same object again: WebKit re-reads which methods the delegate implements (WebKitPage.Install does it once more)
        void_objc_msgSend(wkWebView, rt.SetNavigationDelegate, navigationDelegate);

        var attachment = new Attachment(wkWebView, page);
        Attachment? previous;
        lock (Gate)
        {
            Attached.Remove(wkWebView, out previous);
            Attached[wkWebView] = attachment;
        }
        previous?.CancelAll();
        attachment.Page = WebKitPage.Install(wkWebView, page);
        problem = attachment.Page.Problem;
        return attachment;
    }

    /// <summary>Answers Cancel to every challenge still waiting for the user on <paramref name="wkWebView"/> (the page navigated away).</summary>
    public static void CancelPending(nint wkWebView)
    {
        Attachment? attachment;
        lock (Gate) Attached.TryGetValue(wkWebView, out attachment);
        attachment?.CancelAll();
    }

    /// <summary>The hooks of one web view and its challenges waiting for an answer.</summary>
    private sealed class Attachment(nint webView, BrowserPageHooks hooks) : IDisposable
    {
        private readonly List<Challenge> _pending = [];

        /// <summary>The UI delegate and navigation extras of the same web view.</summary>
        public WebKitPage? Page { get; set; }

        /// <summary>Asks for this challenge, or joins one already asked for the same protection space.</summary>
        public void Ask(ChallengeKey key, CredentialRequest request, nint block)
        {
            Challenge challenge;
            lock (_pending)
            {
                var same = _pending.Find(c => c.Key == key);
                if (same is not null && same.TryJoin(block)) return;
                challenge = new Challenge(key, block);
                _pending.Add(challenge);
            }
            Task<CredentialAnswer?> answer;
            try
            {
                // The card says when omp opened the page; the credentials still come from the user only
                answer = ((IBrowserAuthHandler)hooks).RequestCredentialsAsync(request with { CausedByAgent = hooks.AgentActive() },
                    challenge.Cancellation.Token);
            }
            catch (Exception)
            {
                answer = Task.FromResult<CredentialAnswer?>(null);
            }
            answer.ContinueWith(t =>
            {
                var credentials = t.IsCompletedSuccessfully ? t.Result : null;
                OnMainThread(() => Finish(challenge, credentials));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Finish(Challenge challenge, CredentialAnswer? credentials)
        {
            lock (_pending) _pending.Remove(challenge);
            if (credentials is null) challenge.Cancel();
            else challenge.Answer(credentials);
        }

        public void CancelAll()
        {
            Challenge[] pending;
            lock (_pending)
            {
                pending = [.. _pending];
                _pending.Clear();
            }
            foreach (var challenge in pending) challenge.Cancel();
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (Attached.TryGetValue(webView, out var current) && current == this) Attached.Remove(webView);
            }
            CancelAll();
            Page?.Dispose();
        }
    }

    /// <summary>What makes two challenges the same question to the user.</summary>
    private readonly record struct ChallengeKey(string Host, int Port, string? Realm, string Scheme, bool IsProxy, bool FailedBefore);

    /// <summary>
    /// One question to the user and the copied completion blocks waiting for its answer, each invoked exactly once:
    /// the first of <see cref="Answer"/> / <see cref="Cancel"/> wins, later calls do nothing.
    /// </summary>
    private sealed class Challenge(ChallengeKey key, nint block)
    {
        private readonly List<nint> _blocks = [block];
        private bool _done;

        public ChallengeKey Key { get; } = key;

        public CancellationTokenSource Cancellation { get; } = new();

        public bool TryJoin(nint other)
        {
            lock (_blocks)
            {
                if (_done) return false;
                _blocks.Add(other);
                return true;
            }
        }

        public void Answer(CredentialAnswer credentials) => Complete(credentials);

        public void Cancel() => Complete(null);

        private void Complete(CredentialAnswer? credentials)
        {
            nint[] blocks;
            lock (_blocks)
            {
                if (_done) return;
                _done = true;
                blocks = [.. _blocks];
                _blocks.Clear();
            }
            Cancellation.Cancel(); // the card goes away if it is still shown
            Cancellation.Dispose();
            OnMainThread(() =>
            {
                foreach (var b in blocks) CompleteBlock(b, credentials);
            });
        }
    }

    /// <summary>Invokes and releases one copied completion block (main thread).</summary>
    private static void CompleteBlock(nint block, CredentialAnswer? credentials)
    {
        var rt = Objc;
        var pool = objc_autoreleasePoolPush();
        try
        {
            if (credentials is null)
            {
                Invoke(block, CancelAuthenticationChallenge, 0);
                return;
            }
            var credential = intptr_objc_msgSend(rt.NSURLCredential, rt.CredentialWithUser,
                NSString(rt, credentials.User), NSString(rt, credentials.Password), PersistenceForSession); // autoreleased
            Invoke(block, credential == 0 ? CancelAuthenticationChallenge : UseCredential, credential);
        }
        catch
        {
            // Nothing may unwind into WebKit
        }
        finally
        {
            _Block_release(block);
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The block's invoke pointer sits after <c>isa</c>, flags and reserved (offset 16).</summary>
    private static void Invoke(nint block, nint disposition, nint credential) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)*(nint*)(block + 16))(block, disposition, credential);

    /// <summary>The added method; <paramref name="self"/> is Avalonia's delegate, so the hooks are found by the web view.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAuthenticationChallenge(nint self, nint cmd, nint webView, nint challenge, nint completionHandler)
    {
        try
        {
            if (!TryAsk(webView, challenge, completionHandler)) Invoke(completionHandler, PerformDefaultHandling, 0);
        }
        catch
        {
            // Nothing may unwind into WebKit. The handler is answered once: TryAsk only throws before it copies it.
            try
            {
                Invoke(completionHandler, PerformDefaultHandling, 0);
            }
            catch
            {
                // nothing more to do
            }
        }
    }

    /// <summary>True when the challenge was taken (its block copied and answered later); false: WebKit's default handling.</summary>
    private static bool TryAsk(nint webView, nint challenge, nint completionHandler)
    {
        Attachment? attachment;
        lock (Gate) Attached.TryGetValue(webView, out attachment);
        if (attachment is null) return false;

        var rt = Objc;
        var space = intptr_objc_msgSend(challenge, rt.ProtectionSpace);
        if (space == 0) return false;
        var method = intptr_objc_msgSend(space, rt.AuthenticationMethod);
        string? scheme = null;
        foreach (var (known, name) in rt.PasswordMethods)
        {
            if (known != 0 && method != 0 && bool_objc_msgSend(method, rt.IsEqualToString, known) != 0)
            {
                scheme = name;
                break;
            }
        }
        // Server trust, client certificate and anything unknown: WebKit's default handling
        if (scheme is null) return false;

        var host = Text(rt, intptr_objc_msgSend(space, rt.Host)) ?? "";
        var port = (int)intptr_objc_msgSend(space, rt.Port);
        var realm = Text(rt, intptr_objc_msgSend(space, rt.Realm));
        var protocol = Text(rt, intptr_objc_msgSend(space, rt.Protocol)) ?? "https";
        var isProxy = bool_objc_msgSend(space, rt.IsProxy) != 0;
        var failedBefore = intptr_objc_msgSend(challenge, rt.PreviousFailureCount) > 0;
        var proposed = intptr_objc_msgSend(challenge, rt.ProposedCredential);
        var user = proposed == 0 ? null : Text(rt, intptr_objc_msgSend(proposed, rt.User));

        var request = new CredentialRequest(host, port, string.IsNullOrEmpty(realm) ? null : realm, isProxy, failedBefore,
            string.IsNullOrEmpty(user) ? null : user, scheme)
        {
            PageUrl = OriginOf(protocol, host, port),
        };
        var block = _Block_copy(completionHandler);
        if (block == 0) return false;
        attachment.Ask(new ChallengeKey(host, port, request.Realm, scheme, isProxy, failedBefore), request, block);
        return true;
    }

    /// <summary>The challenged origin (its scheme tells the card whether the password travels unencrypted).</summary>
    internal static Uri? OriginOf(string protocol, string host, int port)
    {
        if (host.Length == 0) return null;
        var scheme = protocol.Equals("http", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        try
        {
            return new UriBuilder(scheme, host, port > 0 ? port : -1).Uri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string? Text(Runtime rt, nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(intptr_objc_msgSend(nsString, rt.Utf8String));

    /// <summary>An autoreleased <c>NSString</c> (call inside an autorelease pool).</summary>
    private static nint NSString(Runtime rt, string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return intptr_objc_msgSend(rt.NSString, rt.StringWithUtf8String, utf8);
        }
        finally
        {
            // The password's bytes do not outlive the call
            new Span<byte>((void*)utf8, System.Text.Encoding.UTF8.GetByteCount(value)).Clear();
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    // ── Main thread: WebKit wants its completion handlers there ──

    private static void OnMainThread(Action action)
    {
        if (pthread_main_np() != 0)
        {
            action();
            return;
        }
        dispatch_async_f(Objc.MainQueue, GCHandle.ToIntPtr(GCHandle.Alloc(action)), &RunOnMain);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunOnMain(nint context)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(context);
            var action = handle.Target as Action;
            handle.Free();
            action?.Invoke();
        }
        catch
        {
            // Nothing may unwind into libdispatch
        }
    }

    // ── Objective-C runtime (objc_msgSend is called with the exact argument types of each method: arm64 needs that) ──

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint object_getClass(nint obj);

    [DllImport(ObjC)]
    private static extern nint class_getName(nint cls);

    [DllImport(ObjC)]
    private static extern nint class_getInstanceMethod(nint cls, nint selector);

    [DllImport(ObjC)]
    private static extern nint method_getImplementation(nint method);

    [DllImport(ObjC)]
    private static extern byte class_addMethod(nint cls, nint selector, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(ObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint intptr_objc_msgSend(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint intptr_objc_msgSend(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint intptr_objc_msgSend(nint receiver, nint selector, nint a, nint b, nuint c);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte bool_objc_msgSend(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte bool_objc_msgSend(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend(nint receiver, nint selector, nint a);

    [DllImport(LibSystem)]
    private static extern nint _Block_copy(nint block);

    [DllImport(LibSystem)]
    private static extern void _Block_release(nint block);

    [DllImport(LibSystem)]
    private static extern int pthread_main_np();

    [DllImport(LibSystem)]
    private static extern void dispatch_async_f(nint queue, nint context, delegate* unmanaged[Cdecl]<nint, void> work);
}
