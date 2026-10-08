using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.X11.Interop;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Linux;

/// <summary>
/// WebKitGTK's <c>authenticate</c> signal (Basic, Digest, NTLM, Negotiate, proxies): the pane's sign-in card instead of
/// WebKitGTK's built-in dialog. The handler keeps a reference to the <c>WebKitAuthenticationRequest</c>, returns TRUE
/// (handled) and answers later on the GLib thread with a session credential
/// (<c>WEBKIT_CREDENTIAL_PERSISTENCE_FOR_SESSION</c>) or <c>webkit_authentication_request_cancel</c>. Certificate and
/// server-trust requests return FALSE: WebKitGTK's default handling. WebKitGTK lives on Avalonia's GLib thread, so
/// connecting, answering and disconnecting happen there (<see cref="GtkInteropHelper.RunOnGlibThread{T}"/>).
/// </summary>
/// <remarks>
/// Every request is answered once: the user's answer, Cancel on detach, or nothing more when WebKitGTK cancels it
/// itself (its <c>cancelled</c> signal: the load stopped), which also takes the card away. Only the GTK engine is
/// handled; Avalonia's WPE adapter (picked when WPE is installed) keeps its default (no dialog).
/// </remarks>
[SupportedOSPlatform("linux")]
internal static unsafe class WebKitGtkHooks
{
    private const int PersistenceForSession = 1; // WEBKIT_CREDENTIAL_PERSISTENCE_FOR_SESSION

    // WebKitAuthenticationScheme values that take a username and password, with the name shown on the card
    private static readonly Dictionary<int, string> PasswordSchemes = new()
    {
        [1] = "Basic", // DEFAULT
        [2] = "Basic", // HTTP_BASIC
        [3] = "Digest", // HTTP_DIGEST
        [4] = "Form", // HTML_FORM
        [5] = "NTLM", // NTLM
        [6] = "Negotiate", // NEGOTIATE
    };

    private static readonly string[] WebKitNames = ["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.1.so", "libwebkit2gtk-4.0.so.37", "libwebkit2gtk-4.0.so"];

    private static readonly Lock Gate = new();
    private static Library? _library;

    /// <summary>The functions used, resolved once from the loaded libraries.</summary>
    private sealed class Library
    {
        public readonly delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, nuint> SignalConnectData;
        public readonly delegate* unmanaged[Cdecl]<nint, nuint, void> SignalHandlerDisconnect;
        public readonly delegate* unmanaged[Cdecl]<nint, nint> ObjectRef;
        public readonly delegate* unmanaged[Cdecl]<nint, void> ObjectUnref;
        public readonly delegate* unmanaged[Cdecl]<nint, nint> GetHost;
        public readonly delegate* unmanaged[Cdecl]<nint, uint> GetPort;
        public readonly delegate* unmanaged[Cdecl]<nint, nint> GetRealm;
        public readonly delegate* unmanaged[Cdecl]<nint, int> GetScheme;
        public readonly delegate* unmanaged[Cdecl]<nint, int> IsRetry;
        public readonly delegate* unmanaged[Cdecl]<nint, int> IsForProxy;
        public readonly delegate* unmanaged[Cdecl]<nint, nint> GetProposedCredential;
        public readonly delegate* unmanaged[Cdecl]<nint, nint, void> Authenticate;
        public readonly delegate* unmanaged[Cdecl]<nint, void> Cancel;
        public readonly delegate* unmanaged[Cdecl]<byte*, byte*, int, nint> CredentialNew;
        public readonly delegate* unmanaged[Cdecl]<nint, nint> CredentialGetUsername;
        public readonly delegate* unmanaged[Cdecl]<nint, void> CredentialFree;
        public readonly delegate* unmanaged[Cdecl]<nint, nint> GetUri;
        /// <summary>WebKitGTK 2.30+; null before.</summary>
        public readonly delegate* unmanaged[Cdecl]<nint, nint> GetSecurityOrigin;
        public readonly delegate* unmanaged[Cdecl]<nint, nint> OriginGetProtocol;
        public readonly delegate* unmanaged[Cdecl]<nint, void> OriginUnref;

        public Library()
        {
            var webkit = WebKitNames.Select(n => NativeLibrary.TryLoad(n, out var h) ? h : 0).FirstOrDefault(h => h != 0);
            if (webkit == 0) throw new InvalidOperationException("WebKitGTK (libwebkit2gtk-4.1) is not loaded.");
            var gobject = NativeLibrary.Load("libgobject-2.0.so.0");
            SignalConnectData = (delegate* unmanaged[Cdecl]<nint, byte*, nint, nint, nint, int, nuint>)NativeLibrary.GetExport(gobject, "g_signal_connect_data");
            SignalHandlerDisconnect = (delegate* unmanaged[Cdecl]<nint, nuint, void>)NativeLibrary.GetExport(gobject, "g_signal_handler_disconnect");
            ObjectRef = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(gobject, "g_object_ref");
            ObjectUnref = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(gobject, "g_object_unref");
            GetHost = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_get_host");
            GetPort = (delegate* unmanaged[Cdecl]<nint, uint>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_get_port");
            GetRealm = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_get_realm");
            GetScheme = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_get_scheme");
            IsRetry = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_is_retry");
            IsForProxy = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_is_for_proxy");
            GetProposedCredential = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_get_proposed_credential");
            Authenticate = (delegate* unmanaged[Cdecl]<nint, nint, void>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_authenticate");
            Cancel = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(webkit, "webkit_authentication_request_cancel");
            CredentialNew = (delegate* unmanaged[Cdecl]<byte*, byte*, int, nint>)NativeLibrary.GetExport(webkit, "webkit_credential_new");
            CredentialGetUsername = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(webkit, "webkit_credential_get_username");
            CredentialFree = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(webkit, "webkit_credential_free");
            GetUri = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(webkit, "webkit_web_view_get_uri");
            if (NativeLibrary.TryGetExport(webkit, "webkit_authentication_request_get_security_origin", out var origin))
            {
                GetSecurityOrigin = (delegate* unmanaged[Cdecl]<nint, nint>)origin;
                OriginGetProtocol = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(webkit, "webkit_security_origin_get_protocol");
                OriginUnref = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(webkit, "webkit_security_origin_unref");
            }
        }
    }

    private static Library Lib
    {
        get
        {
            lock (Gate) return _library ??= new Library();
        }
    }

    /// <summary>
    /// Connects to <paramref name="webView"/>'s (a <c>WebKitWebView*</c>) <c>authenticate</c> signal; challenges go to
    /// <paramref name="auth"/>. Disposing the result disconnects and cancels what is pending.
    /// </summary>
    public static Task<IDisposable> AttachAsync(nint webView, IBrowserAuthHandler auth)
    {
        if (webView == 0) throw new ArgumentException("No web view.", nameof(webView));
        var attachment = new Attachment(Lib, webView, auth);
        return GtkInteropHelper.RunOnGlibThread(attachment.Connect).ContinueWith<IDisposable>(connect =>
        {
            connect.GetAwaiter().GetResult(); // a failure to connect surfaces to the caller
            return attachment;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>One web view's signal connection and the requests waiting for the user (GLib thread state).</summary>
    private sealed class Attachment(Library lib, nint webView, IBrowserAuthHandler auth) : IDisposable
    {
        private readonly List<Pending> _pending = [];
        private GCHandle _self;
        private nuint _handlerId;
        private bool _disposed;

        public bool Connect()
        {
            _self = GCHandle.Alloc(this);
            lib.ObjectRef(webView); // disconnecting needs it alive
            fixed (byte* name = "authenticate"u8)
                _handlerId = lib.SignalConnectData(webView, name,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnAuthenticate, GCHandle.ToIntPtr(_self), 0, 0);
            return true;
        }

        /// <summary>GLib thread: takes the request (TRUE) or leaves it to WebKitGTK (FALSE).</summary>
        public bool Ask(nint request)
        {
            if (_disposed || !PasswordSchemes.TryGetValue(lib.GetScheme(request), out var scheme)) return false;
            var host = Marshal.PtrToStringUTF8(lib.GetHost(request)) ?? "";
            var port = (int)lib.GetPort(request);
            var realm = Marshal.PtrToStringUTF8(lib.GetRealm(request));
            string? user = null;
            var proposed = lib.GetProposedCredential(request);
            if (proposed != 0)
            {
                user = Marshal.PtrToStringUTF8(lib.CredentialGetUsername(proposed));
                lib.CredentialFree(proposed);
            }
            var credentialRequest = new CredentialRequest(host, port, string.IsNullOrEmpty(realm) ? null : realm,
                lib.IsForProxy(request) != 0, lib.IsRetry(request) != 0, string.IsNullOrEmpty(user) ? null : user, scheme)
            {
                PageUrl = Origin(request, host, port),
            };

            lib.ObjectRef(request);
            var pending = new Pending(lib, request);
            _pending.Add(pending);
            fixed (byte* name = "cancelled"u8)
                pending.CancelledId = lib.SignalConnectData(request, name,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnCancelled, pending.Handle, 0, 0);

            Task<CredentialAnswer?> answer;
            try
            {
                answer = auth.RequestCredentialsAsync(credentialRequest, pending.Cancellation.Token);
            }
            catch (Exception)
            {
                answer = Task.FromResult<CredentialAnswer?>(null);
            }
            answer.ContinueWith(t =>
            {
                var credentials = t.IsCompletedSuccessfully ? t.Result : null;
                _ = GtkInteropHelper.RunOnGlibThread(() =>
                {
                    _pending.Remove(pending);
                    pending.Complete(credentials);
                    return true;
                });
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }

        /// <summary>The challenged origin: its scheme tells the card whether the password travels unencrypted.</summary>
        private Uri? Origin(nint request, string host, int port)
        {
            string? protocol = null;
            if (lib.GetSecurityOrigin != null)
            {
                var origin = lib.GetSecurityOrigin(request);
                if (origin != 0)
                {
                    protocol = Marshal.PtrToStringUTF8(lib.OriginGetProtocol(origin));
                    lib.OriginUnref(origin);
                }
            }
            if (protocol is null && Uri.TryCreate(Marshal.PtrToStringUTF8(lib.GetUri(webView)), UriKind.Absolute, out var page))
                protocol = page.Scheme;
            if (host.Length == 0) return null;
            try
            {
                return new UriBuilder(protocol == "http" ? "http" : "https", host, port > 0 ? port : -1).Uri;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _ = GtkInteropHelper.RunOnGlibThread(() =>
            {
                if (_disposed) return true;
                _disposed = true;
                foreach (var pending in _pending.ToArray()) pending.Complete(null);
                _pending.Clear();
                lib.SignalHandlerDisconnect(webView, _handlerId);
                lib.ObjectUnref(webView);
                _self.Free();
                return true;
            });
        }
    }

    /// <summary>One request with our reference, answered exactly once (GLib thread).</summary>
    private sealed class Pending
    {
        private readonly Library _lib;
        private readonly nint _request;
        private GCHandle _self;
        private bool _done;

        public Pending(Library lib, nint request)
        {
            _lib = lib;
            _request = request;
            _self = GCHandle.Alloc(this);
        }

        public nint Handle => GCHandle.ToIntPtr(_self);

        public nuint CancelledId { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>The user's answer (null: cancel), or nothing when WebKitGTK already cancelled the request.</summary>
        public void Complete(CredentialAnswer? credentials, bool cancelledByWebKit = false)
        {
            if (_done) return;
            _done = true;
            Cancellation.Cancel();
            Cancellation.Dispose();
            _lib.SignalHandlerDisconnect(_request, CancelledId);
            _self.Free();
            try
            {
                if (cancelledByWebKit) return;
                if (credentials is null)
                {
                    _lib.Cancel(_request);
                    return;
                }
                var user = Marshal.StringToCoTaskMemUTF8(credentials.User);
                var password = Marshal.StringToCoTaskMemUTF8(credentials.Password);
                try
                {
                    var credential = _lib.CredentialNew((byte*)user, (byte*)password, PersistenceForSession);
                    _lib.Authenticate(_request, credential);
                    _lib.CredentialFree(credential);
                }
                finally
                {
                    new Span<byte>((void*)password, System.Text.Encoding.UTF8.GetByteCount(credentials.Password)).Clear();
                    Marshal.FreeCoTaskMem(password);
                    Marshal.FreeCoTaskMem(user);
                }
            }
            finally
            {
                _lib.ObjectUnref(_request);
            }
        }
    }

    /// <summary><c>gboolean authenticate(WebKitWebView*, WebKitAuthenticationRequest*, gpointer)</c>, on the GLib thread.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAuthenticate(nint webView, nint request, nint userData)
    {
        try
        {
            return GCHandle.FromIntPtr(userData).Target is Attachment attachment && attachment.Ask(request) ? 1 : 0;
        }
        catch
        {
            return 0; // Nothing may unwind into GLib; WebKitGTK's own dialog
        }
    }

    /// <summary><c>void cancelled(WebKitAuthenticationRequest*, gpointer)</c>: WebKitGTK gave up on the request (load stopped).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCancelled(nint request, nint userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is Pending pending) pending.Complete(null, cancelledByWebKit: true);
        }
        catch
        {
            // Nothing may unwind into GLib
        }
    }
}
