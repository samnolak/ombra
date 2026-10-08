using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Windows;

/// <summary>
/// WebView2's <c>ICoreWebView2_10::add_BasicAuthenticationRequested</c> (Basic, Digest, NTLM and proxy challenges):
/// the pane's sign-in card instead of WebView2's own dialog. The app has <c>BuiltInComInteropSupport=false</c>, so the
/// calls go through the vtables (the raw style of <see cref="WebView2Snapshot"/>) and the event handler is a minimal COM
/// object made here. Slots and IIDs are from the WebView2 SDK's <c>WebView2.h</c> (1.0.2903.40).
/// </summary>
/// <remarks>
/// Each challenge takes a deferral and answers later on the UI thread (WebView2's thread, captured when the event is
/// raised): user name and password, or <c>Cancel=TRUE</c> (the server's 401 page). Leaving both unset would show
/// WebView2's default dialog, so every challenge is answered one way, exactly once, also when the web view goes away.
/// WebView2 has no failure count: a second challenge for the same origin and realm right after credentials were given
/// is shown as "that didn't work".
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe class WebView2Hooks
{
    private const int SOk = 0;
    private const int ENoInterface = unchecked((int)0x80004002);

    // IUnknown
    private const int AddRefSlot = 1;
    private const int ReleaseSlot = 2;
    // ICoreWebView2_10 (ICoreWebView2 … _9 have 94 methods after IUnknown's 3)
    private const int AddBasicAuthenticationRequestedSlot = 97;
    private const int RemoveBasicAuthenticationRequestedSlot = 98;
    // ICoreWebView2BasicAuthenticationRequestedEventArgs
    private const int ArgsGetUriSlot = 3;
    private const int ArgsGetChallengeSlot = 4;
    private const int ArgsGetResponseSlot = 5;
    private const int ArgsPutCancelSlot = 7;
    private const int ArgsGetDeferralSlot = 8;
    // ICoreWebView2BasicAuthenticationResponse
    private const int ResponsePutUserNameSlot = 4;
    private const int ResponsePutPasswordSlot = 6;
    // ICoreWebView2Deferral
    private const int DeferralCompleteSlot = 3;

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidCoreWebView2_10 = new("B1690564-6F5A-4983-8E48-31D1143FECDB");
    private static readonly Guid IidHandler = new("58B4D6C2-18D4-497E-B39B-9A96533FA278");

    /// <summary>The event handler object: a vtable pointer first, as COM expects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Handler
    {
        public nint* Vtable;
        public int References;
        public nint State; // GCHandle to the Attachment
    }

    private static readonly nint* HandlerVtable = CreateVtable();

    private static nint* CreateVtable()
    {
        // Lives as long as the app
        var vtable = (nint*)NativeMemory.AllocZeroed((nuint)(4 * sizeof(nint)));
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<Handler*, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<Handler*, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<Handler*, uint>)&ReleaseExport;
        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<Handler*, nint, nint, int>)&Invoke;
        return vtable;
    }

    /// <summary>
    /// Subscribes to authentication challenges of <paramref name="coreWebView2"/> (an AddRef'd <c>ICoreWebView2*</c>,
    /// released here). Disposing the result unsubscribes and cancels what is pending. Null, with
    /// <paramref name="problem"/>, when the WebView2 runtime is older than the event (1.0.902). Call on the UI thread.
    /// </summary>
    public static IDisposable? Attach(nint coreWebView2, IBrowserAuthHandler auth, out string? problem)
    {
        problem = null;
        if (coreWebView2 == 0)
        {
            problem = "No web view.";
            return null;
        }
        try
        {
            var iid = IidCoreWebView2_10;
            if (Marshal.QueryInterface(coreWebView2, in iid, out var webView10) < 0 || webView10 == 0)
            {
                problem = "This WebView2 runtime cannot ask for passwords (update Microsoft Edge WebView2).";
                return null;
            }
            var attachment = new Attachment(webView10, auth);
            var handler = (Handler*)NativeMemory.AllocZeroed((nuint)sizeof(Handler));
            handler->Vtable = HandlerVtable;
            handler->References = 1;
            handler->State = GCHandle.ToIntPtr(GCHandle.Alloc(attachment));
            long token;
            var add = (delegate* unmanaged[Stdcall]<nint, Handler*, long*, int>)(*(nint**)webView10)[AddBasicAuthenticationRequestedSlot];
            var hr = add(webView10, handler, &token);
            ReleaseHandler(handler); // WebView2 holds its own reference while subscribed
            if (hr < 0)
            {
                ComRelease(webView10);
                problem = $"WebView2 refused the authentication handler (0x{hr:X8}).";
                return null;
            }
            attachment.Subscribed(token);
            return attachment;
        }
        finally
        {
            Marshal.Release(coreWebView2);
        }
    }

    /// <summary>One web view's subscription and the challenges waiting for the user.</summary>
    private sealed class Attachment(nint webView10, IBrowserAuthHandler auth) : IDisposable
    {
        private readonly List<Challenge> _pending = [];
        private readonly HashSet<string> _answered = [];
        private long _token;
        private bool _disposed;

        public void Subscribed(long token) => _token = token;

        public void Ask(nint args)
        {
            if (_disposed) return;
            var uri = TakeString(args, ArgsGetUriSlot);
            var (scheme, realm) = WwwAuthenticate.Parse(TakeString(args, ArgsGetChallengeSlot));
            Uri.TryCreate(uri, UriKind.Absolute, out var page);
            var host = page?.Host ?? "";
            var key = $"{page?.GetLeftPart(UriPartial.Authority)}|{realm}";
            bool failedBefore;
            lock (_pending) failedBefore = _answered.Contains(key);

            nint response = 0, deferral = 0;
            var getResponse = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)args)[ArgsGetResponseSlot])(args, &response);
            var getDeferral = getResponse < 0 ? getResponse : ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)args)[ArgsGetDeferralSlot])(args, &deferral);
            if (getDeferral < 0 || response == 0 || deferral == 0)
            {
                // Not answerable later: WebView2 shows its own dialog
                ComRelease(response);
                ComRelease(deferral);
                return;
            }
            ComAddRef(args);
            var challenge = new Challenge(args, response, deferral, key, SynchronizationContext.Current);
            lock (_pending) _pending.Add(challenge);

            var request = new CredentialRequest(host, page?.Port ?? 0, realm, IsProxy: false, failedBefore, AuthScheme: scheme)
            {
                PageUrl = page,
            };
            Task<CredentialAnswer?> answer;
            try
            {
                answer = auth.RequestCredentialsAsync(request, challenge.Cancellation.Token);
            }
            catch (Exception)
            {
                answer = Task.FromResult<CredentialAnswer?>(null);
            }
            answer.ContinueWith(t => Finish(challenge, t.IsCompletedSuccessfully ? t.Result : null),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Finish(Challenge challenge, CredentialAnswer? credentials)
        {
            lock (_pending)
            {
                _pending.Remove(challenge);
                if (credentials is null) _answered.Remove(challenge.Key);
                else _answered.Add(challenge.Key);
            }
            challenge.Complete(credentials);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Challenge[] pending;
            lock (_pending)
            {
                pending = [.. _pending];
                _pending.Clear();
            }
            foreach (var challenge in pending) challenge.Complete(null);
            try
            {
                ((delegate* unmanaged[Stdcall]<nint, long, int>)(*(nint**)webView10)[RemoveBasicAuthenticationRequestedSlot])(webView10, _token);
            }
            catch
            {
                // The web view is already gone
            }
            ComRelease(webView10);
        }
    }

    /// <summary>One challenge: its args, response and deferral (each AddRef'd), answered exactly once on the UI thread.</summary>
    private sealed class Challenge(nint args, nint response, nint deferral, string key, SynchronizationContext? ui)
    {
        private int _done;

        public string Key { get; } = key;

        public CancellationTokenSource Cancellation { get; } = new();

        public void Complete(CredentialAnswer? credentials)
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Cancellation.Cancel();
            Cancellation.Dispose();
            if (ui is null || SynchronizationContext.Current == ui) Apply(credentials);
            else ui.Post(_ => Apply(credentials), null);
        }

        private void Apply(CredentialAnswer? credentials)
        {
            try
            {
                if (credentials is not null && response != 0)
                {
                    fixed (char* user = credentials.User)
                    fixed (char* password = credentials.Password)
                    {
                        ((delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)response)[ResponsePutUserNameSlot])(response, user);
                        ((delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)response)[ResponsePutPasswordSlot])(response, password);
                    }
                }
                else
                {
                    ((delegate* unmanaged[Stdcall]<nint, int, int>)(*(nint**)args)[ArgsPutCancelSlot])(args, 1);
                }
                if (deferral != 0) ((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)deferral)[DeferralCompleteSlot])(deferral);
            }
            catch
            {
                // The web view went away meanwhile
            }
            finally
            {
                ComRelease(deferral);
                ComRelease(response);
                ComRelease(args);
            }
        }
    }

    private static string? TakeString(nint obj, int slot)
    {
        nint text = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)obj)[slot])(obj, &text) < 0 || text == 0) return null;
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(Handler* self, Guid* iid, nint* result)
    {
        if (*iid == IidUnknown || *iid == IidHandler)
        {
            Interlocked.Increment(ref self->References);
            *result = (nint)self;
            return SOk;
        }
        *result = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Handler* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseExport(Handler* self) => ReleaseHandler(self);

    private static uint ReleaseHandler(Handler* self)
    {
        var left = Interlocked.Decrement(ref self->References);
        if (left == 0)
        {
            GCHandle.FromIntPtr(self->State).Free();
            NativeMemory.Free(self);
        }
        return (uint)left;
    }

    /// <summary><c>ICoreWebView2BasicAuthenticationRequestedEventHandler::Invoke(ICoreWebView2 *sender, args)</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(Handler* self, nint sender, nint args)
    {
        try
        {
            if (GCHandle.FromIntPtr(self->State).Target is Attachment attachment) attachment.Ask(args);
        }
        catch
        {
            // Nothing may unwind into WebView2. Without a deferral taken, WebView2 shows its own dialog.
        }
        return SOk;
    }

    private static void ComAddRef(nint unknown)
    {
        if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[AddRefSlot])(unknown);
    }

    private static void ComRelease(nint unknown)
    {
        if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[ReleaseSlot])(unknown);
    }
}
