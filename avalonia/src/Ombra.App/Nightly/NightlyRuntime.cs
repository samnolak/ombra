using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Ombra.App.Localization;

namespace Ombra.App.Nightly;

/// <summary>Where the runtime comes from and how it may trust Ombra (<see cref="Resolve"/>).</summary>
/// <param name="Binary">The Nightly executable (<c>Nightly.app/Contents/MacOS/nightly</c>).</param>
/// <param name="ProfileDirectory">The runtime's own profile, owned by Ombra (never the user's Nightly profile).</param>
/// <param name="PeerRequirement">Ombra's Developer ID designated requirement, which Nightly checks the embedder against
/// (<c>zen.embed.peer-requirement</c>); null while Ombra has no Developer ID.</param>
/// <param name="PeerCdhash">Without a Developer ID: the code directory hash of this ad-hoc signed Ombra, which Nightly
/// checks the embedder against instead (<c>zen.embed.peer-cdhash</c>, written into the runtime profile Ombra owns).</param>
/// <param name="NightlyCdhash">The cdhash the Nightly process must have (the pinned manifest's); null only in development
/// with <see cref="AllowUnsignedVariable"/>.</param>
/// <param name="AllowUnsignedPeer">Development only (<c>OMBRA_NIGHTLY_ALLOW_UNSIGNED=1</c>): Nightly accepts an unsigned
/// embedder of the same user (its <c>zen.embed.allow-unsigned-peer</c> test pref), and Ombra does not check Nightly's cdhash.</param>
internal sealed record NightlyRuntimeOptions(string Binary, string ProfileDirectory, string? PeerRequirement, string? PeerCdhash,
    string? NightlyCdhash, bool AllowUnsignedPeer)
{
    public const string AppVariable = "OMBRA_NIGHTLY_APP";
    public const string ProfileVariable = "OMBRA_NIGHTLY_PROFILE";
    public const string AllowUnsignedVariable = "OMBRA_NIGHTLY_ALLOW_UNSIGNED";

    /// <summary>Development switch <see cref="AllowUnsignedVariable"/>=1.</summary>
    public static bool DevelopmentUnsigned => Environment.GetEnvironmentVariable(AllowUnsignedVariable) == "1";

    /// <summary>
    /// The runtime: <see cref="AppVariable"/> (development), else the pinned package Ombra installed
    /// (<see cref="NightlyRuntimePackage"/>); its profile next to Ombra's settings (<see cref="ProfileVariable"/>
    /// overrides), how Nightly checks Ombra (Developer ID requirement, else Ombra's cdhash) and the cdhash Nightly must
    /// have (the pin's, also for <see cref="AppVariable"/>). Otherwise why it cannot run (<paramref name="problem"/>, with
    /// <see cref="NightlyRuntimePackage.Problem"/> saying whether installing would help).
    [SupportedOSPlatform("macos")]
    public static (NightlyRuntimeOptions? Options, string? Problem) Resolve(string settingsPath) => Resolve(settingsPath, out _);

    [SupportedOSPlatform("macos")]
    public static (NightlyRuntimeOptions? Options, string? Problem) Resolve(string settingsPath, out NightlyRuntimePackage.Problem missing)
    {
        missing = NightlyRuntimePackage.Problem.None;
        string app;
        if (Environment.GetEnvironmentVariable(AppVariable) is { Length: > 0 } a) app = a;
        else
        {
            var (installed, problem) = NightlyRuntimePackage.Find();
            missing = problem;
            if (installed is null) return (null, Describe(problem));
            app = installed;
        }
        var binary = app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Directory.Exists(app)
            ? Path.Combine(app, "Contents", "MacOS", "nightly")
            : app;
        if (!File.Exists(binary))
            return (null, Loc.F("Nightly is not installed at {0} ({1}).", app, AppVariable));
        var profile = Environment.GetEnvironmentVariable(ProfileVariable) is { Length: > 0 } p ? p
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? ".", "nightly-profile");
        var requirement = NightlyMach.OwnDesignatedRequirement();
        var cdhash = requirement is null ? NightlyMach.OwnCdhash() : null;
        var allowUnsigned = DevelopmentUnsigned;
        if (requirement is null && cdhash is null && !allowUnsigned)
            return (null, Loc.F("This Ombra build is not signed, so Nightly will not accept it. For development, start Ombra with {0}=1.", AllowUnsignedVariable));
        var nightly = allowUnsigned ? null : NightlyRuntimePackage.Pinned?.Cdhash;
        if (nightly is null && !allowUnsigned)
            return (null, Loc.T("This Ombra build pins no Nightly runtime."));
        return (new NightlyRuntimeOptions(NightlyMach.RealPath(binary), profile, requirement, cdhash, nightly, allowUnsigned), null);
    }

    /// <summary>What the user is told when the pinned runtime is not there.</summary>
    public static string Describe(NightlyRuntimePackage.Problem problem) => problem switch
    {
        NightlyRuntimePackage.Problem.UpdateRequired => Loc.F("Ombra needs Nightly {0}; an older version is installed.", NightlyRuntimePackage.Pinned?.Version ?? "?"),
        NightlyRuntimePackage.Problem.NotInstalled => Loc.F("Nightly {0} is not installed yet.", NightlyRuntimePackage.Pinned?.Version ?? "?"),
        NightlyRuntimePackage.Problem.PackageMissing => Loc.F("The Nightly {0} runtime package is not available on this Mac.", NightlyRuntimePackage.Pinned?.Version ?? "?"),
        _ => Loc.T("This Ombra build pins no Nightly runtime."),
    };
}

/// <summary>What the runtime tells a pane (<see cref="Controls.NightlyPane"/>). Called on the runtime's thread (the UI thread).</summary>
internal interface INightlyView
{
    /// <summary>The pane shows a full browser window (<c>browser.xhtml</c>, Nightly's own UI) rather than a chrome-less
    /// page; fixed for the pane's life.</summary>
    bool IsBrowserWindow => false;
    void OnRuntimeState(NightlyState state, string? detail);
    /// <summary>The pane has a window in the runtime now (after a start or restart).</summary>
    void OnAttached(uint windowId);
    /// <summary>The window is gone (the runtime stopped or restarted): images and frames of it are void.</summary>
    void OnDetached();
    void OnWindow(NightlyProtocol.WindowInfo window);
    /// <summary>A surface of the pane's window (+1 reference: the view releases it with <see cref="NightlyMach.CFRelease"/>).</summary>
    void OnSurface(NightlyProtocol.SurfaceInfo surface, nint ioSurface);
    /// <summary>A frame to show; the view answers <see cref="NightlyRuntime.ReleaseFrame"/> once it no longer reads it.</summary>
    void OnFrame(NightlyProtocol.FrameInfo frame);
    void OnLocation(NightlyProtocol.LocationInfo location);
    /// <summary>A popup of the page to draw (<see cref="NightlyPopup"/>): new, or the one shown with new items.</summary>
    void OnPopup(uint popupId, NightlyPopup popup);
    /// <summary>The page closed popup <paramref name="popupId"/> itself.</summary>
    void OnPopupClosed(uint popupId);
    /// <summary>The pointer shape the page wants (nsCursor).</summary>
    void OnCursor(int cursor);
    /// <summary>Where the input method's composition is (window points), for the candidate window.</summary>
    void OnTextRect(NightlyProtocol.TextRectInfo rect);
    /// <summary>
    /// The page opened a window (<c>window.open</c>, <c>target=_blank</c>, a link opened in a new tab): a pane takes it
    /// with <see cref="NightlyRuntime.Adopt"/>, or it is closed with <see cref="NightlyRuntime.CloseWindow"/>.
    /// </summary>
    void OnPopupWindow(uint windowId);
    /// <summary>The page closed the pane's window (<c>window.close()</c>): nothing shows in the pane any more.</summary>
    void OnWindowClosed();
    /// <summary>The page asks question <paramref name="dialogId"/> (<see cref="NightlyDialogs"/>); the view answers with
    /// <see cref="NightlyRuntime.ReplyDialogAsync"/>.</summary>
    void OnDialog(uint dialogId, string json);
    /// <summary>Nightly withdrew question <paramref name="dialogId"/> (navigation, page closed): its card goes.</summary>
    void OnDialogClosed(uint dialogId);
}

/// <summary>
/// The Nightly runtime Ombra embeds (K1 option A; desktop <c>src/zen/embed/</c>): one process for all panes, started
/// when the first pane needs it with <c>NIGHTLY_EMBED_SERVICE</c> and a chrome-less window per pane, restarted when it
/// crashes, and ended with its whole process tree when the last pane closes or Ombra quits (Nightly also quits by
/// itself when Ombra's process exits). Each side authenticates the other: Ombra accepts only the process it started
/// (pid and executable, same user); Nightly accepts only Ombra's code requirement (or, in development, an unsigned peer).
/// All members run on the thread of <c>post</c> (the UI thread); the receive thread only reads and posts.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class NightlyRuntime : IDisposable
{
    public const string EmbedDocument = "chrome://browser/content/zen-embed.xhtml";
    public const string BrowserDocument = "chrome://browser/content/browser.xhtml";
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);
    /// <summary>How long Nightly may take to quit by itself (session and profile written) before it is ended.</summary>
    public static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(8);

    private readonly NightlyRuntimeOptions _options;
    private readonly Action<Action> _post;
    private readonly NightlyChannel _channel;
    private readonly NightlySupervisor _supervisor;
    private readonly Dictionary<uint, INightlyView> _panes = [];
    private readonly Dictionary<uint, Window> _windows = [];
    // Windows of this process that were closed (Nightly never reuses an id): what it still sends for them while they go
    // (a last Window or Surface) must not bring them back as a page's new window to offer again.
    private readonly HashSet<uint> _closedWindows = [];
    private uint _nextToken = 1;
    // Written on the UI thread, read on the receive thread.
    private volatile int _launchedPid;
    private volatile int _peerPid;
    private uint _peerPort;
    private bool _disposed;
    // Scripts waiting for Nightly's answer, by request id (answered on the receive thread).
    private readonly ConcurrentDictionary<uint, (uint WindowId, TaskCompletionSource<NightlyProtocol.ScriptResultInfo> Done)> _scripts = new();
    private int _nextRequest;
    // Downloads whose path a dialog chose, by dialog id: Nightly reports their progress (UI thread).
    private readonly Dictionary<uint, NightlyDialogs.StartedDownload> _downloads = [];

    /// <summary>A window of the runtime: its pane, once the document named it (or a pane adopted it), and the surfaces it
    /// sent before that.</summary>
    private sealed class Window(uint id)
    {
        public uint Id { get; } = id;
        public uint Pane;
        public NightlyProtocol.WindowInfo? Info;
        /// <summary>A page's new window was offered to its opener's pane (<see cref="INightlyView.OnPopupWindow"/>).</summary>
        public bool Offered;
        /// <summary>Before a pane shows it: its last location and frame (kept from Nightly), given to the pane that adopts it.</summary>
        public NightlyProtocol.LocationInfo? Location;
        public NightlyProtocol.FrameInfo? HeldFrame;
        public readonly Dictionary<uint, (NightlyProtocol.SurfaceInfo Info, nint Surface)> Pending = [];
    }

    public NightlyRuntime(NightlyRuntimeOptions options, Action<Action> post)
    {
        _options = options;
        _post = post;
        _channel = new NightlyChannel(OnMessage);
        _supervisor = new NightlySupervisor(StartProcess, post, ReadyTimeout);
        _supervisor.StateChanged += OnStateChanged;
        _supervisor.ProcessStarted += p =>
        {
            ForgetWindows();
            _launchedPid = p.Id;
        };
    }

    public NightlyState State => _supervisor.State;
    public string? Detail => _supervisor.Detail;
    public int Pid => _supervisor.Pid;
    public int Starts => _supervisor.Starts;
    public NightlyRuntimeOptions Options => _options;

    private bool _darkTheme;

    /// <summary>Ombra's appearance, which Nightly follows (its windows' chrome and the pages' <c>prefers-color-scheme</c>):
    /// written into the profile before each start and sent to the running process when it changes.</summary>
    public bool DarkTheme
    {
        get => _darkTheme;
        set
        {
            if (_darkTheme == value) return;
            _darkTheme = value;
            Send(NightlyProtocol.ThemeMessage(value));
        }
    }

    private bool _windowControlsShown = true;

    /// <summary>Whether Ombra's traffic lights show over Nightly's windows (not in full screen), whose top row then keeps
    /// their place free: written into the profile before each start and sent to the running process when it changes.</summary>
    public bool WindowControlsShown
    {
        get => _windowControlsShown;
        set
        {
            if (_windowControlsShown == value) return;
            _windowControlsShown = value;
            Send(NightlyProtocol.WindowControlsMessage(value));
        }
    }

    /// <summary>Adds a pane: the runtime starts if it is not running, else it opens a window for the pane.</summary>
    public uint Register(INightlyView view)
    {
        var token = _nextToken++;
        _panes[token] = view;
        view.OnRuntimeState(State, Detail);
        if (State is NightlyState.Stopped) _supervisor.Start();
        else if (State is NightlyState.Running) Send(OpenMessage(token));
        return token;
    }

    /// <summary>
    /// Adds a pane that shows <paramref name="windowId"/>, a window a page opened (<see cref="INightlyView.OnPopupWindow"/>).
    /// When that window is gone already (the page closed it, the runtime restarted), the pane gets a window of its own.
    /// </summary>
    public uint Adopt(INightlyView view, uint windowId)
    {
        var token = _nextToken++;
        _panes[token] = view;
        view.OnRuntimeState(State, Detail);
        if (_windows.TryGetValue(windowId, out var w) && w.Pane == 0) Attach(w, token);
        else if (State is NightlyState.Stopped) _supervisor.Start();
        else if (State is NightlyState.Running) Send(OpenMessage(token));
        return token;
    }

    /// <summary>What asks Nightly for pane <paramref name="token"/>'s window: a chrome-less one, or a browser window.</summary>
    private byte[] OpenMessage(uint token) => _panes.TryGetValue(token, out var view) && view.IsBrowserWindow
        ? NightlyProtocol.OpenBrowserWindowMessage(token)
        : NightlyProtocol.OpenWindowMessage(token);

    /// <summary>The pane whose window comes with the command line: the first one (a chrome-less window, or a browser
    /// window named by <c>NIGHTLY_EMBED_BROWSER_PANE</c>); every other window is asked for after Hello.</summary>
    private uint CommandLinePane() => _panes.Keys.Order().FirstOrDefault();

    // The pane whose window the running process got from its command line.
    private uint _commandLinePane;

    /// <summary>Closes a window no pane shows (a page's new window nobody adopted).</summary>
    public void CloseWindow(uint windowId)
    {
        if (!_windows.TryGetValue(windowId, out var w) || w.Pane != 0) return;
        if (NightlyProtocol.CommandMessage(w.Id, NightlyProtocol.CommandOp.Close) is { } close) Send(close);
        Drop(w);
    }

    /// <summary>Removes a pane and closes its window; the last one stops the runtime.</summary>
    public void Unregister(uint token)
    {
        if (!_panes.Remove(token)) return;
        foreach (var w in _windows.Values.Where(w => w.Pane == token).ToList())
        {
            if (NightlyProtocol.CommandMessage(w.Id, NightlyProtocol.CommandOp.Close) is { } close) Send(close);
            Drop(w);
        }
        if (_panes.Count == 0) _ending = _supervisor.Stop(RequestQuit, QuitGrace);
    }

    /// <summary>After a failure: starts again (the panes see the state change).</summary>
    public void Retry() => _supervisor.Retry();

    /// <summary>The last stop's ending: completes once nothing of the previous process runs (tests, app exit).</summary>
    public Task Ending => _ending;
    private Task _ending = Task.CompletedTask;

    private bool RequestQuit() => Send(NightlyProtocol.QuitMessage());

    /// <summary>Answers question <paramref name="dialogId"/> of window <paramref name="windowId"/> with <paramref name="json"/>
    /// (off the caller's thread: the kernel copies the text while sending). False when Nightly is not connected.</summary>
    public Task<bool> ReplyDialogAsync(uint windowId, uint dialogId, string json) => Task.Run(() => SendReply(windowId, dialogId, json));

    private unsafe bool SendReply(uint windowId, uint dialogId, string json)
    {
        var port = _peerPort;
        var length = System.Text.Encoding.UTF8.GetByteCount(json);
        if (port == 0 || length > NightlyProtocol.MaxReply) return false;
        var data = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)Math.Max(1, length));
        try
        {
            System.Text.Encoding.UTF8.GetBytes(json, new Span<byte>(data, length));
            return NightlyMach.Send(port, NightlyProtocol.DialogReplyMessage(windowId, dialogId, (nint)data, length), 2000) == NightlyMach.KernSuccess;
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(data);
        }
    }

    /// <summary>A download a pane's dialog started: Nightly's progress reports update it, and cancelling it stops it in Nightly.</summary>
    public void TrackDownload(uint windowId, NightlyDialogs.StartedDownload download)
    {
        _downloads[download.DialogId] = download;
        download.Item.Canceller = () =>
        {
            _post(() => Send(NightlyProtocol.DownloadCancelMessage(windowId, download.DialogId)));
            return true;
        };
    }

    /// <summary>Sends to Nightly; false when it is not connected or the message could not be queued.</summary>
    public bool Send(byte[] message)
    {
        var port = _peerPort;
        return port != 0 && NightlyMach.Send(port, message, 50) == NightlyMach.KernSuccess;
    }

    public void ReleaseFrame(uint windowId, ulong seq) => Send(NightlyProtocol.ReleaseMessage(windowId, seq));

    private uint _activeWindow;

    /// <summary>The window Gecko takes as the active one (keys and text go to its focused element); 0 for none.</summary>
    public uint ActiveWindow => Volatile.Read(ref _activeWindow);

    /// <summary>
    /// Activates <paramref name="windowId"/> in Gecko (<paramref name="active"/>), or deactivates it when it is the active
    /// one. Gecko has one active window: whoever types (the user in a focused pane, the agent in its tab) activates the
    /// window first, so a pane the agent typed into last does not keep the user's keys. Any thread.
    /// </summary>
    public void SetActive(uint windowId, bool active)
    {
        if (windowId == 0) return;
        if (active)
        {
            Volatile.Write(ref _activeWindow, windowId);
            Send(NightlyProtocol.FocusMessage(windowId, true));
        }
        else if (Interlocked.CompareExchange(ref _activeWindow, 0, windowId) == windowId) Send(NightlyProtocol.FocusMessage(windowId, false));
    }

    /// <summary>
    /// Runs a script in the top-level document of window <paramref name="windowId"/> (the agent's browser tools): in the
    /// page's own world, or <paramref name="source"/> as a function called with the JSON array <paramref name="args"/>
    /// in Ombra's isolated world. Any thread. Throws <see cref="InvalidOperationException"/> when Nightly is not
    /// connected, stops, or the window goes before it answers.
    /// </summary>
    public async Task<NightlyProtocol.ScriptResultInfo> RunScriptAsync(uint windowId, NightlyProtocol.ScriptKind kind, string source, string args,
        CancellationToken ct)
    {
        var sourceLength = Encoding.UTF8.GetByteCount(source);
        var argsLength = Encoding.UTF8.GetByteCount(args);
        if ((long)sourceLength + argsLength > NightlyProtocol.MaxScript)
            throw new InvalidOperationException("The script is larger than the Nightly embed channel carries.");
        var id = (uint)Interlocked.Increment(ref _nextRequest);
        var done = new TaskCompletionSource<NightlyProtocol.ScriptResultInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scripts[id] = (windowId, done);
        try
        {
            // A full queue waits a little (frames go the other way, input this way): off the caller's thread.
            if (!await Task.Run(() => SendScript(windowId, id, kind, source, sourceLength, args, argsLength), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Nightly is not connected.");
            await using var cancel = ct.Register(() => done.TrySetCanceled(ct)).ConfigureAwait(false);
            return await done.Task.ConfigureAwait(false);
        }
        finally
        {
            _scripts.TryRemove(id, out _);
        }
    }

    private unsafe bool SendScript(uint windowId, uint id, NightlyProtocol.ScriptKind kind, string source, int sourceLength, string args, int argsLength)
    {
        var port = _peerPort;
        if (port == 0) return false;
        var total = sourceLength + argsLength;
        var data = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)Math.Max(1, total));
        try
        {
            Encoding.UTF8.GetBytes(source, new Span<byte>(data, sourceLength));
            Encoding.UTF8.GetBytes(args, new Span<byte>(data + sourceLength, argsLength));
            // The kernel copies the text while sending: the memory is ours again when Send returns.
            return NightlyMach.Send(port, NightlyProtocol.ScriptMessage(windowId, id, kind, (nint)data, sourceLength, argsLength), 2000) == NightlyMach.KernSuccess;
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(data);
        }
    }

    /// <summary>The scripts waiting on <paramref name="windowId"/> (null: all) get <paramref name="why"/>.</summary>
    private void FailScripts(uint? windowId, string why)
    {
        foreach (var (_, (window, done)) in _scripts)
            if (windowId is null || window == windowId) done.TrySetException(new InvalidOperationException(why));
    }

    /// <summary>The pane's window, when it has one.</summary>
    public uint? WindowOf(uint token) => _windows.Values.FirstOrDefault(w => w.Pane == token)?.Id;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Quitting by itself (session, profile closed) within the grace; whatever is left is ended then.
        var ending = _supervisor.Stop(RequestQuit, QuitGrace);
        try { ending.Wait(QuitGrace + TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _supervisor.Dispose();
        ForgetWindows();
        _channel.Dispose();
        FailScripts(null, "Nightly stopped.");
        _panes.Clear();
    }

    // ── process ──

    private Process StartProcess()
    {
        if (_panes.Count == 0) throw new InvalidOperationException("No pane wants Nightly.");
        var first = _commandLinePane = CommandLinePane();
        Directory.CreateDirectory(_options.ProfileDirectory);
        WriteUserPrefs();
        var start = new ProcessStartInfo(_options.Binary)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = _options.ProfileDirectory,
        };
        // Its own profile, no remoting to (or from) a standalone Nightly, and the first pane's window: chrome-less, or a
        // browser window (Nightly embeds the first browser.xhtml as the pane NIGHTLY_EMBED_BROWSER_PANE names).
        start.ArgumentList.Add("-no-remote");
        start.ArgumentList.Add("-profile");
        start.ArgumentList.Add(_options.ProfileDirectory);
        start.ArgumentList.Add("-chrome");
        if (_panes[first].IsBrowserWindow)
        {
            start.ArgumentList.Add(BrowserDocument);
            start.Environment["NIGHTLY_EMBED_BROWSER_PANE"] = first.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else start.ArgumentList.Add($"{EmbedDocument}?pane={first}");
        start.Environment["NIGHTLY_EMBED_SERVICE"] = _channel.ServiceName;
        start.Environment["MOZ_APP_NO_DOCK"] = "1"; // a background runtime: no Dock icon, no menu bar
        start.Environment["MOZ_LOG"] = Environment.GetEnvironmentVariable("OMBRA_NIGHTLY_MOZ_LOG") is { Length: > 0 } log ? log : "ZenEmbed:3";
        foreach (var name in new[] { "MOZ_HEADLESS", "MOZ_NO_REMOTE", "XRE_PROFILE_PATH", "NIGHTLY_EMBED_BROWSER_PANE" })
            if (!_panes[first].IsBrowserWindow || name != "NIGHTLY_EMBED_BROWSER_PANE") start.Environment.Remove(name);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Nightly did not start.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data?.Contains("ZenEmbed", StringComparison.Ordinal) == true) Services.ClientLog.Shared.Write("nightly", e.Data);
        };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return process;
    }

    /// <summary>The runtime profile's prefs, written before each start: how Nightly trusts Ombra, and a quiet start.</summary>
    internal void WriteUserPrefs()
    {
        static string Str(string s) => JsonSerializer.Serialize(s);
        var prefs = new StringBuilder();
        prefs.AppendLine("// Written by Ombra before each start of its embedded Nightly runtime; edits are replaced.");
        prefs.AppendLine($"user_pref(\"zen.embed.peer-requirement\", {Str(_options.PeerRequirement ?? "")});");
        prefs.AppendLine($"user_pref(\"zen.embed.peer-cdhash\", {Str(_options.PeerCdhash ?? "")});");
        prefs.AppendLine($"user_pref(\"zen.embed.allow-unsigned-peer\", {(_options.AllowUnsignedPeer ? "true" : "false")});");
        foreach (var line in new[]
        {
            "user_pref(\"browser.shell.checkDefaultBrowser\", false);",
            "user_pref(\"browser.startup.page\", 0);",
            "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");",
            "user_pref(\"browser.sessionstore.resume_from_crash\", false);",
            "user_pref(\"toolkit.startup.max_resumed_crashes\", -1);",
            "user_pref(\"zen.welcome-screen.seen\", true);",
            // That pref sends ZenStartup to its update branch, whose "Update Complete!" toast the embedded runtime skips.
            "user_pref(\"zen.updates.show-update-notification\", false);",
            "user_pref(\"datareporting.policy.dataSubmissionEnabled\", false);",
            "user_pref(\"toolkit.telemetry.reportingpolicy.firstRun\", false);",
            // Every download asks Ombra where to save (ZenEmbedDialogs), and Nightly's own panels never open.
            "user_pref(\"browser.download.useDownloadDir\", false);",
            "user_pref(\"browser.download.always_ask_before_handling_new_types\", false);",
            "user_pref(\"browser.download.alwaysOpenPanel\", false);",
            "user_pref(\"signon.rememberSignons\", false);",
            // The Browser page's window (OpenBrowserWindow): its menus are Gecko's own popup windows (a native NSMenu of
            // the background runtime could not take the keys Ombra forwards); behind-window vibrancy never reaches the
            // exported frames (the chrome would be see-through); closing its last tab keeps the window.
            "user_pref(\"widget.macos.native-context-menus\", false);",
            "user_pref(\"zen.widget.macos.window-vibrancy\", false);",
            "user_pref(\"browser.tabs.closeWindowWithLastTab\", false);",
            // Zen's automatic scheme, which follows ui.systemUsesDarkTheme (Ombra's appearance; DarkTheme).
            "user_pref(\"zen.view.window.scheme\", 2);",
        }) prefs.AppendLine(line);
        prefs.AppendLine($"user_pref(\"ui.systemUsesDarkTheme\", {(_darkTheme ? 1 : 0)});");
        prefs.AppendLine($"user_pref(\"zen.embed.window-controls-hidden\", {(_windowControlsShown ? "false" : "true")});");
        File.WriteAllText(Path.Combine(_options.ProfileDirectory, "user.js"), prefs.ToString());
    }

    private void OnStateChanged()
    {
        if (State is not NightlyState.Running)
        {
            _peerPid = 0;
            var old = _peerPort;
            _peerPort = 0;
            NightlyMach.Deallocate(old);
            FailScripts(null, "Nightly stopped.");
            if (State is NightlyState.Stopped or NightlyState.Restarting or NightlyState.Failed) ForgetWindows();
        }
        foreach (var view in _panes.Values.ToList()) view.OnRuntimeState(State, Detail);
    }

    // ── messages (receive thread → UI) ──

    private void OnMessage(NightlyChannel.Message m)
    {
        var bytes = m.Bytes;
        var id = NightlyProtocol.Id(bytes);
        var fromPeer = m.SenderPid != 0 && m.SenderPid == _peerPid;
        switch (id)
        {
            case NightlyProtocol.Hello:
                OnHello(bytes, m.SenderPid, m.SenderEuid, m.AuditToken);
                return;
            case NightlyProtocol.Surface:
            {
                var port = NightlyProtocol.PortName(bytes);
                if (!fromPeer)
                {
                    NightlyMach.Deallocate(port);
                    return;
                }
                var info = NightlyProtocol.ReadSurface(bytes);
                var surface = NightlyMach.SurfaceFromPort(port);
                if (surface == 0) return;
                var pid = m.SenderPid;
                _post(() =>
                {
                    if (pid == _peerPid) OnSurface(info, surface);
                    else NightlyMach.CFRelease(surface);
                });
                return;
            }
        }
        if (!fromPeer) return;
        var sender = m.SenderPid;
        void Post(Action a) => _post(() =>
        {
            if (sender == _peerPid) a();
        });
        switch (id)
        {
            case NightlyProtocol.Frame:
                var frame = NightlyProtocol.ReadFrame(bytes);
                Post(() => OnFrame(frame));
                break;
            case NightlyProtocol.Window:
                var window = NightlyProtocol.ReadWindow(bytes);
                Post(() => OnWindow(window));
                break;
            case NightlyProtocol.Location:
                if (NightlyProtocol.ReadLocation(bytes) is { } location) Post(() => OnLocation(location));
                break;
            case NightlyProtocol.ScriptResult:
                // Straight from the receive thread: the caller waits on its own thread, not the UI's.
                if (NightlyProtocol.ReadScriptResult(bytes, m.Payload) is { } result && _scripts.TryGetValue(result.RequestId, out var pending)
                    && pending.WindowId == result.WindowId)
                    pending.Done.TrySetResult(result);
                break;
            case NightlyProtocol.Popup:
                if (NightlyProtocol.ReadPopup(bytes, m.Payload) is { } popup) Post(() => OnPopup(popup));
                break;
            case NightlyProtocol.PopupClosed when bytes.Length >= 32:
                var (closedWindow, closedPopup) = NightlyProtocol.ReadPopupClosed(bytes);
                Post(() => ViewOf(closedWindow)?.OnPopupClosed(closedPopup));
                break;
            case NightlyProtocol.Cursor when bytes.Length >= 32:
                var (cursorWindow, cursor) = NightlyProtocol.ReadCursor(bytes);
                Post(() => ViewOf(cursorWindow)?.OnCursor(cursor));
                break;
            case NightlyProtocol.TextRect when bytes.Length >= 64:
                var rect = NightlyProtocol.ReadTextRect(bytes);
                Post(() => ViewOf(rect.WindowId)?.OnTextRect(rect));
                break;
            case NightlyProtocol.Dialog:
                if (NightlyProtocol.ReadDialog(bytes, m.Payload) is { } dialog) Post(() => OnDialog(dialog.WindowId, dialog.DialogId, dialog.Json));
                break;
            case NightlyProtocol.DialogClosed when bytes.Length >= 32:
                var (dialogWindow, dialogId) = NightlyProtocol.ReadDialogClosed(bytes);
                Post(() => ViewOf(dialogWindow)?.OnDialogClosed(dialogId));
                break;
            case NightlyProtocol.Download:
                if (NightlyProtocol.ReadDialog(bytes, m.Payload) is { } download) Post(() => OnDownload(download.DialogId, download.Json));
                break;
        }
    }

    /// <summary>
    /// Nightly's Hello (receive thread): accepted only from the process this runtime started, running the binary it
    /// started, as the same user, with the pinned cdhash (<see cref="NightlyRuntimeOptions.NightlyCdhash"/>), speaking
    /// this protocol version. Then Nightly gets HelloAck and checks Ombra in turn.
    /// </summary>
    private void OnHello(byte[] bytes, int senderPid, uint senderEuid, byte[]? auditToken)
    {
        var port = NightlyProtocol.PortName(bytes);
        var hello = NightlyProtocol.ReadHello(bytes);
        var launched = _launchedPid;
        string? problem = null;
        if (hello.Version != NightlyProtocol.Version)
            problem = Loc.F("This Nightly speaks embed protocol {0}; Ombra needs {1}. Update Nightly.", hello.Version, NightlyProtocol.Version);
        else if (senderPid == 0 || senderPid != hello.Pid || senderPid != launched || senderEuid != NightlyMach.EffectiveUserId
                 || NightlyMach.ExecutableOf(senderPid) != _options.Binary)
            problem = "";
        else if (_options.NightlyCdhash is { } cdhash && (auditToken is null || !NightlyMach.HasCdhash(auditToken, cdhash)))
            problem = Loc.T("This Nightly is not the runtime this Ombra pins (its code signature differs). Reinstall Nightly.");
        if (problem is not null)
        {
            Services.ClientLog.Shared.Write("nightly", $"refused embed hello from pid {senderPid} (started {launched}, v{hello.Version}){(problem.Length > 0 ? ": " + problem : "")}");
            NightlyMach.Deallocate(port);
            if (problem.Length > 0 && senderPid == launched) _post(() => { if (_launchedPid == launched) _supervisor.Fail(problem); });
            return;
        }
        if (NightlyMach.Send(port, NightlyProtocol.HelloAckMessage(), 1000) != NightlyMach.KernSuccess)
        {
            NightlyMach.Deallocate(port);
            return;
        }
        _post(() =>
        {
            if (_launchedPid != senderPid || _disposed)
            {
                NightlyMach.Deallocate(port);
                return;
            }
            NightlyMach.Deallocate(_peerPort);
            _peerPort = port;
            _peerPid = senderPid;
            _supervisor.MarkReady();
            // The appearance and the traffic lights may have changed since user.js was written.
            Send(NightlyProtocol.ThemeMessage(_darkTheme));
            Send(NightlyProtocol.WindowControlsMessage(_windowControlsShown));
            // The command line's chrome-less window is on its way; every other pane's window is asked for now.
            foreach (var token in _panes.Keys.Order().Where(t => t != _commandLinePane)) Send(OpenMessage(token));
        });
    }

    // ── windows (UI thread) ──

    private Window WindowFor(uint id)
    {
        if (!_windows.TryGetValue(id, out var w)) _windows[id] = w = new Window(id);
        return w;
    }

    private INightlyView? ViewOf(Window w) => w.Pane != 0 ? _panes.GetValueOrDefault(w.Pane) : null;

    private INightlyView? ViewOf(uint windowId) => _windows.TryGetValue(windowId, out var w) ? ViewOf(w) : null;

    private void OnWindow(NightlyProtocol.WindowInfo info)
    {
        if (_closedWindows.Contains(info.WindowId)) return;
        var w = WindowFor(info.WindowId);
        if (!info.Visible && w.Info is { Visible: true })
        {
            // Closed (or hidden by Gecko): it shows nothing any more.
            var view = ViewOf(w);
            Drop(w);
            view?.OnWindowClosed();
            return;
        }
        w.Info = info;
        if (w.Pane == 0 && info.Pane != 0)
        {
            if (!_panes.ContainsKey(info.Pane))
            {
                // A pane closed while its window opened: close the window.
                if (NightlyProtocol.CommandMessage(w.Id, NightlyProtocol.CommandOp.Close) is { } close) Send(close);
                Drop(w);
                return;
            }
            Attach(w, info.Pane);
            return;
        }
        if (w.Pane == 0 && info.Opener != 0 && !w.Offered)
        {
            // A page's new window: its opener's pane decides (a tab of its own), else nobody shows it.
            w.Offered = true;
            if (ViewOf(info.Opener) is { } opener) opener.OnPopupWindow(w.Id);
            else CloseWindow(w.Id);
            return;
        }
        ViewOf(w)?.OnWindow(info);
    }

    /// <summary>Window <paramref name="w"/> is pane <paramref name="token"/>'s from now on: what it sent so far goes there.</summary>
    private void Attach(Window w, uint token)
    {
        w.Pane = token;
        var view = _panes[token];
        view.OnAttached(w.Id);
        foreach (var (surface, ioSurface) in w.Pending.Values) view.OnSurface(surface, ioSurface);
        w.Pending.Clear();
        if (w.Info is { } info) view.OnWindow(info);
        if (w.Location is { } location) view.OnLocation(location);
        w.Location = null;
        if (w.HeldFrame is { } frame) view.OnFrame(frame);
        w.HeldFrame = null;
    }

    private void OnSurface(NightlyProtocol.SurfaceInfo info, nint surface)
    {
        if (_closedWindows.Contains(info.WindowId))
        {
            NightlyMach.CFRelease(surface);
            return;
        }
        var w = WindowFor(info.WindowId);
        if (ViewOf(w) is { } view)
        {
            view.OnSurface(info, surface);
            return;
        }
        // Not named yet: kept for its pane (Nightly sends each surface once). A new size replaces the older ones.
        foreach (var (key, old) in w.Pending.Where(p => p.Value.Info.Width != info.Width || p.Value.Info.Height != info.Height).ToList())
        {
            NightlyMach.CFRelease(old.Surface);
            w.Pending.Remove(key);
        }
        if (w.Pending.Remove(info.SurfaceId, out var replaced)) NightlyMach.CFRelease(replaced.Surface);
        w.Pending[info.SurfaceId] = (info, surface);
    }

    private void OnFrame(NightlyProtocol.FrameInfo frame)
    {
        if (!_windows.TryGetValue(frame.WindowId, out var w)) ReleaseFrame(frame.WindowId, frame.Seq); // nobody shows it
        else if (ViewOf(w) is { } view) view.OnFrame(frame);
        else
        {
            // A window no pane has yet: its latest frame waits for the pane that adopts it.
            if (w.HeldFrame is { } older) ReleaseFrame(older.WindowId, older.Seq);
            w.HeldFrame = frame;
        }
    }

    private void OnLocation(NightlyProtocol.LocationInfo location)
    {
        if (!_windows.TryGetValue(location.WindowId, out var w)) return;
        if (ViewOf(w) is { } view) view.OnLocation(location);
        else w.Location = location;
    }

    private void OnPopup(NightlyProtocol.PopupInfo info)
    {
        var view = ViewOf(info.WindowId);
        if (view is not null && NightlyPopup.Parse(info.Json) is { } popup) view.OnPopup(info.PopupId, popup);
        else Send(NightlyProtocol.PopupChoiceMessage(info.WindowId, info.PopupId, -1)); // nobody can show it: dismissed
    }

    private void OnDialog(uint windowId, uint dialogId, string json)
    {
        if (ViewOf(windowId) is { } view) view.OnDialog(dialogId, json);
        else _ = ReplyDialogAsync(windowId, dialogId, "{}"); // nobody shows it: the nobody-answered answer
    }

    private void OnDownload(uint dialogId, string json)
    {
        if (!_downloads.TryGetValue(dialogId, out var d)) return;
        if (NightlyDialogs.ApplyDownload(d.Item, json)) _downloads.Remove(dialogId);
        d.Hooks.RaiseDownloadChanged(d.Item);
    }

    private void Drop(Window w)
    {
        foreach (var (_, surface) in w.Pending.Values) NightlyMach.CFRelease(surface);
        w.Pending.Clear();
        if (w.HeldFrame is { } held) ReleaseFrame(held.WindowId, held.Seq);
        w.HeldFrame = null;
        _windows.Remove(w.Id);
        _closedWindows.Add(w.Id);
        FailScripts(w.Id, "The page's Nightly window closed.");
        ViewOf(w)?.OnDetached();
    }

    private void ForgetWindows()
    {
        foreach (var w in _windows.Values.ToList()) Drop(w);
        _closedWindows.Clear(); // the next process numbers its windows from 1 again
    }
}
