using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Ombra.App.Localization;
using Ombra.App.Nightly;
using Ombra.App.Platform.Mac;
using Ombra.ClientCore.Browser;
using SkiaSharp;

namespace Ombra.App.Controls;

/// <summary>
/// A page shown by the embedded Nightly runtime (Settings › General › Agent browser: Nightly, experimental; macOS).
/// Nightly renders the pane's own chrome-less window off screen and sends each frame as an IOSurface; the pane shows
/// each frame as a Core Animation layer under Avalonia's view (<see cref="NightlyLayer"/>), through a hole it clears in
/// what Avalonia draws, 1:1 (surface pixels / Nightly's backing
/// scale = DIP, origin on a device pixel; letterboxed only while a resize is in flight, never stretched or cropped;
/// scaled down, aspect kept, only for a <see cref="Viewport"/> larger than the room the pane gets) and sends mouse,
/// scroll (with phases, <see cref="NightlyScrollMonitor"/>), keys, text, focus and its size and screen back.
/// The page's popups (<c>&lt;select&gt;</c>, autocomplete, context menu) are Ombra's (<see cref="NightlyPopupPresenter"/>),
/// so is its pointer shape; the input method composes in Ombra (the pane is a <see cref="TextInputMethodClient"/>) and
/// Nightly gets the composition, the commit and the keys it did not take. A window the page opens goes to whoever
/// listens to <see cref="PopupWindowOpened"/> (a tab of its own). While the page cannot be seen (its tab in the
/// background, the pane out of a window, the window hidden, minimized or covered) Nightly renders none of it and the
/// pane holds none of its frames (<see cref="NightlyVisibility"/>). While the runtime starts, restarts or fails, the pane
/// says so over its last picture.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class NightlyPane : Panel, INightlyView, NightlyScrollMonitor.ITarget, NightlyShortcutMonitor.ITarget, NightlyAgent.ITarget
{
    private readonly Surface _host = new() { Focusable = true, ClipToBounds = true };
    private readonly Border _overlay;
    private readonly TextBlock _status;
    private readonly Button _retry;

    private NightlyRuntime? _runtime;
    private uint _token;
    private uint _windowId;
    private bool _closed;
    // Navigations asked for before the window exists, in order (a page pane ends on the last; a browser window opens a
    // tab for each).
    private readonly List<Uri> _pendingUrls = [];

    // The frames: a Core Animation layer under Avalonia's view, seen through the hole _host clears where the frame is.
    private NightlyLayer? _layer;
    // Whether Nightly renders the window, and the frames held from it (the shown one, one waiting for the layer).
    private readonly NightlyVisibility _visibility;
    // Whether the window is covered whole by others (Avalonia does not say).
    private MacWindowOcclusion? _occlusion;

    private int _frameWidth, _frameHeight;
    private double _geckoScale = 2;
    private Rect _shown;
    private bool _hasFrame;
    private (double W, double H, (double X, double Y)? Origin) _lastResize;
    private int _pressedButton = -1;
    private TopLevel? _top;
    private readonly NightlyPopupPresenter _popups;
    private readonly ImeClient _ime;
    // The input method's composition as Nightly has it, and one that ended with nothing (unless a commit follows at once).
    private bool _composing, _cancelComposition;
    // The native key event Nightly got already (or the input method took), and a Shift/Option key down waiting for the
    // input method's answer.
    private (nint Event, double Timestamp)? _keySent, _keyDeferred;
    private readonly NightlyFrames _frames = new();
    private long _lastUserInput;
    // The window's own drag (DragStarted): its image under the pointer (Nightly's window is never on screen, so AppKit
    // draws none), carried on as Ombra's AppKit drag once the pointer leaves the pane (NightlyDrag).
    private NightlyProtocol.DragStartedInfo? _drag;
    private bool _dragCarried;
    private readonly Image _dragImage = new() { Stretch = Stretch.Fill };
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false, IsVisible = false };
    // The page's pointer shape while a drag shows its own (AppKit's arrow, copy or link).
    private Cursor? _pageCursor;
    private Point _pointer;
    // What a drop where an AppKit drag is over the pane would do (Nightly's last DragStatus, NSDragOperation).
    private ulong _dropOperation;

    public NightlyPane()
    {
        _visibility = new NightlyVisibility(m => _runtime?.Send(m) == true, (window, seq) => _runtime?.ReleaseFrame(window, seq),
            HideFrame, (delay, retry) => DispatcherTimer.RunOnce(retry, delay));
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420 };
        _status.Classes.Add("muted");
        _retry = new Button { Content = Loc.T("Try again"), HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
        _retry.Click += (_, _) => Retry();
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x16, 0x18)),
            Child = new StackPanel
            {
                Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { _status, _retry },
            },
        };
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18));
        _popups = new NightlyPopupPresenter(AnswerPopup);
        _ime = new ImeClient(this);
        Children.Add(_host);
        _dragLayer.Children.Add(_dragImage);
        Children.Add(_dragLayer);
        Children.Add(_popups.Layer);
        Children.Add(_overlay);
        WireInput();
        LayoutUpdated += (_, _) =>
        {
            UpdateVisibility();
            if (_layer is null) EnsureLayer();
            PlaceLayer();
        };
        ShowStatus(Loc.T("Starting Nightly…"), retry: false);
        Agent = new NightlyAgent(this, _frames);
    }

    /// <summary>A window a page opened that this pane shows (<see cref="PopupWindowOpened"/>); 0: the pane opens its own.</summary>
    public uint AdoptWindow { get; init; }

    /// <summary>
    /// The pane shows a whole Nightly browser window (the rail's Browser page): <c>browser.xhtml</c> with Nightly's own
    /// sidebar, spaces, URL bar, library and menus, whose popups are Nightly's windows on screen over the pane (not
    /// <see cref="Popups"/>), and whose keyboard shortcuts are its own while it has the keyboard
    /// (<see cref="NightlyShortcutMonitor"/>). False: a chrome-less page (the agent's panel, the built-in preview's tabs).
    /// </summary>
    public bool IsBrowserWindow { get; init; }

    /// <summary>
    /// For a browser window: whether its shortcuts are its own also while Ombra's keyboard focus is elsewhere in the
    /// window, on nothing that takes keys (the rail after a click on it, or no element: the page showed before the
    /// pane could take the focus). The key then gives the pane the keyboard first, so what is typed next (the URL after
    /// ⌘T) reaches the window too. Unset: only while the pane has the keyboard.
    /// </summary>
    public Func<bool>? TakesShortcutsUnfocused { get; init; }

    /// <summary>
    /// The page opened a window (<c>window.open</c>, <c>target=_blank</c>, "Open link in new tab"): its id, for a new
    /// pane's <see cref="AdoptWindow"/>. Without a listener the window is closed.
    /// </summary>
    public event Action<uint>? PopupWindowOpened;

    /// <summary>The page closed the pane's window (<c>window.close()</c>).</summary>
    public event Action? PageClosed;

    /// <summary>The page's popup Ombra shows over the pane.</summary>
    internal NightlyPopupPresenter Popups => _popups;

    /// <summary>The agent's browser tools on this pane: scripts, native input and screenshots over the embed channel.</summary>
    public NightlyAgent Agent { get; }

    /// <summary>The tab's engine hooks: the page's questions (dialogs, sign-in, permissions, files, downloads) are answered
    /// through them, as the built-in preview's are (<see cref="NightlyDialogs"/>). Null: nobody answers (default answers).</summary>
    public Ombra.ClientCore.Browser.BrowserPageHooks? Page { get; set; }

    // Questions the page is waiting on, by dialog id: cancelled when Nightly withdraws them or the window goes.
    private readonly Dictionary<uint, CancellationTokenSource> _dialogs = [];
    private Uri? _pageUrl;

    NightlyRuntime? NightlyAgent.ITarget.Runtime => _runtime;

    long NightlyAgent.ITarget.LastUserInput => Interlocked.Read(ref _lastUserInput);

    private void NoteUserInput() => Interlocked.Exchange(ref _lastUserInput, Environment.TickCount64);

    /// <summary>The page's address, title and history as Nightly reports them (the top-level document).</summary>
    public event Action<NightlyProtocol.LocationInfo>? LocationChanged;

    /// <summary>The runtime's process (0 when none), for diagnostics and tests.</summary>
    public int RuntimePid => _runtime?.Pid ?? 0;

    /// <summary>The window Nightly made for this pane (0 until it exists).</summary>
    public uint WindowId => _windowId;

    /// <summary>Frames shown so far.</summary>
    public int FramesShown { get; private set; }

    /// <summary>The frames' layer is on screen in the window (development checks).</summary>
    internal bool LayerShown => _layer?.IsShown == true;

    /// <summary>Records when frames arrive and are shown (development checks); null records nothing.</summary>
    internal NightlyFrameTiming? Timing { get; set; }

    /// <summary>Where the last frame is drawn in the pane (DIP) and its size in surface pixels.</summary>
    public (Rect Shown, int Width, int Height) FrameLayout => (_shown, _frameWidth, _frameHeight);

    // ── navigation (the window's chrome script: zen-embed.js, or ZenEmbedBrowser.sys.mjs for a browser window) ──

    /// <summary>Loads <paramref name="uri"/> (a browser window: in a new tab).</summary>
    public void Navigate(Uri uri)
    {
        // The agent's open or goto (its tab's hooks say it acts now): the page is seen while it loads (AgentSeen).
        if (Page?.AgentActive() == true) NoteAgentAction();
        if (_pendingUrls.Count > 0 || !SendNavigate(uri))
            _pendingUrls.Add(uri); // sent when the window exists
    }

    private bool SendNavigate(Uri uri) => _windowId != 0 && _runtime is not null
        && NightlyProtocol.CommandMessage(_windowId, NightlyProtocol.CommandOp.Navigate, uri.AbsoluteUri) is { } m && _runtime.Send(m);

    /// <summary>
    /// A navigation of this pane about to start (Augur: <see cref="SpeculationHint"/>): Nightly opens the connection to
    /// its origin meanwhile. Before the pane has a window it goes for window 0 (a pane whose window is being opened);
    /// nothing is sent while the runtime is not connected.
    /// </summary>
    public void Speculate(SpeculationHint hint)
    {
        if (!_closed) _runtime?.Speculate(_windowId, hint);
    }

    /// <summary>Withdraws hint <paramref name="id"/> (<see cref="Speculate"/>): its navigation will not come.</summary>
    public void CancelSpeculation(string id)
    {
        if (!_closed) _runtime?.CancelSpeculation(_windowId, id);
    }

    /// <summary>The page takes the keyboard (the Browser page shown).</summary>
    public void FocusPage()
    {
        if (!_closed && _host.IsEffectivelyVisible) _host.Focus();
    }

    public void Reload() => Command(NightlyProtocol.CommandOp.Reload);
    public void GoBack() => Command(NightlyProtocol.CommandOp.Back);
    public void GoForward() => Command(NightlyProtocol.CommandOp.Forward);

    private void Command(NightlyProtocol.CommandOp op)
    {
        if (_windowId != 0 && NightlyProtocol.CommandMessage(_windowId, op) is { } m) _runtime?.Send(m);
    }

    /// <summary>The device the page emulates (null: none); kept and sent again whenever the pane gets a window.</summary>
    public NightlyProtocol.Device? Device
    {
        get => _device;
        set
        {
            if (_device == value) return;
            _device = value;
            SendDevice();
        }
    }

    private NightlyProtocol.Device? _device;

    private void SendDevice()
    {
        if (_windowId != 0 && NightlyProtocol.DeviceMessage(_windowId, _device) is { } m) _runtime?.Send(m);
    }

    /// <summary>
    /// The page's size in CSS px (the device chosen in the toolbar, or omp's); null: the pane's own. Nightly's window
    /// takes exactly this size, so the page lays out at it wherever the pane is; the pane is that size where it fits and
    /// otherwise as large as fits with its aspect, the frame scaled down into it (as a browser's device toolbar does).
    /// Input, popups and the input method's caret map through the scale.
    /// </summary>
    public Size? Viewport
    {
        get => _viewport;
        set
        {
            if (_viewport == value) return;
            _viewport = value;
            InvalidateMeasure();
            SendResize();
        }
    }

    private Size? _viewport;

    /// <summary>The pane is gone for good (its tab closed): its window closes; the last pane stops the runtime.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        NightlyScrollMonitor.Remove(this);
        NightlyShortcutMonitor.Remove(this);
        if (_runtime is { } runtime && _token != 0) runtime.Unregister(_token);
        _runtime = null;
        OnDetached();
        _agentSeen?.Stop();
        _occlusion?.Dispose();
        _occlusion = null;
        _layer?.Remove();
        _layer = null;
    }

    // ── lifetime ──

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_closed) return;
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null)
        {
            _top.ScalingChanged += OnScreenChanged;
            if (_top is Window w)
            {
                w.PositionChanged += OnScreenChanged;
                w.Activated += OnWindowActivation;
                w.Deactivated += OnWindowActivation;
                w.PropertyChanged += OnWindowPropertyChanged;
            }
        }
        NightlyScrollMonitor.Add(this);
        if (IsBrowserWindow) NightlyShortcutMonitor.Add(this);
        UpdateVisibility();
    }

    // The layer goes into the window's view once the pane is in a laid out window.
    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (!_closed) SetUp();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        NightlyScrollMonitor.Remove(this);
        NightlyShortcutMonitor.Remove(this);
        if (_top is not null)
        {
            _top.ScalingChanged -= OnScreenChanged;
            if (_top is Window w)
            {
                w.PositionChanged -= OnScreenChanged;
                w.Activated -= OnWindowActivation;
                w.Deactivated -= OnWindowActivation;
                w.PropertyChanged -= OnWindowPropertyChanged;
            }
        }
        _top = null;
        _occlusion?.Dispose();
        _occlusion = null;
        UpdateVisibility(); // out of the window: hidden, its frames back to Nightly
        _layer?.Remove(); // the pane may come back in another window
        _layer = null;
    }

    private void SetUp()
    {
        EnsureLayer();
        if (_runtime is null && !_closed) Connect();
    }

    /// <summary>Puts the layer under the window's Avalonia view (again, when the pane came back in another window); a
    /// window not on screen yet has no view, so this runs again after layout passes and frames until it has.</summary>
    private void EnsureLayer()
    {
        if (_closed || _top?.TryGetPlatformHandle() is not { Handle: not 0 } handle || _layer?.IsUnder(handle.Handle) == true) return;
        _layer?.Remove();
        _layer = NightlyLayer.Under(handle.Handle);
        if (_layer is null) return;
        if (_visibility.Shown is { } shown && _frames.Surface(shown.SurfaceId) is var surface and not 0) _layer.Show(surface);
        LayoutFrame();
        ShowHeldFrame();
    }

    // ── visibility (protocol 5 Visibility, NightlyVisibility) ──

    /// <summary>
    /// How long after the agent's last action on the pane its page still counts as seen, wherever its tab or window is:
    /// the agent works while the user is elsewhere (another tab, another app over Ombra), and in a hidden page rAF
    /// stops, the refresh driver and timers crawl (lazy loading stalls) and no frame comes for a screenshot.
    /// </summary>
    internal static readonly TimeSpan AgentSeen = TimeSpan.FromSeconds(10);

    // The agent's actions on the pane under way, and when the last began or ended (Environment.TickCount64; 0: never).
    private int _agentActions;
    private long _agentActed;
    private DispatcherTimer? _agentSeen;
    // A screenshot waiting for the first frame since the page came back on screen (ShownFrameAsync).
    private TaskCompletionSource? _frameWaiters;

    /// <summary>
    /// Whether the page can be seen: the pane in a window and shown (its tab in front, everything holding it shown), the
    /// window shown and not minimized, and not covered whole by other windows (AppKit's occlusion; not in a harness run,
    /// whose window lies behind the owner's on purpose: <see cref="Platform.TestFocus"/>). While the agent acts on the
    /// pane, and <see cref="AgentSeen"/> after, it counts as seen whenever the pane is in a window.
    /// </summary>
    private bool OnScreen() =>
        !_closed && _top is { } top
        && (AgentActing || (IsEffectivelyVisible && (top is not Window w || (w.IsVisible && w.WindowState != WindowState.Minimized))
            && (_occlusion is null || _occlusion.Visible || Platform.TestFocus.NoActivate)));

    /// <summary>The agent acts on the pane (<see cref="NightlyAgent.ITarget.AgentActsAsync"/>), or did within <see cref="AgentSeen"/>.</summary>
    internal bool AgentActing => Volatile.Read(ref _agentActions) > 0
        || (Interlocked.Read(ref _agentActed) is var at and not 0 && Environment.TickCount64 - at < (long)AgentSeen.TotalMilliseconds);

    /// <summary>
    /// After anything that can show or hide the page (a layout pass, the tab shown or hidden, the pane in or out of a
    /// window, the window minimized, hidden or covered, the agent acting or done): Nightly is told when that changed; off
    /// screen the layer lets its surface go first (NightlyVisibility's hide), then Nightly has every frame back. Shown
    /// again, the next frame Nightly paints comes.
    /// </summary>
    private void UpdateVisibility()
    {
        EnsureOcclusion();
        if (!_visibility.Update(OnScreen()) || _visibility.Visible) return;
        PlaceLayer();
        FrameShown(); // none comes while hidden
    }

    /// <summary>The layer lets the frame's surface go and Core Animation commits that now, not at the end of this run
    /// loop turn: Nightly reuses a surface as soon as it has it back (NightlyVisibility releases right after).</summary>
    private void HideFrame()
    {
        if (_layer is null) return;
        _layer.Show(0);
        MacObjC.SendVoid(MacObjC.Cls("CATransaction"), "flush");
    }

    async Task<IDisposable> NightlyAgent.ITarget.AgentActsAsync()
    {
        Interlocked.Increment(ref _agentActions);
        var scope = new AgentScope(this);
        Interlocked.Exchange(ref _agentActed, Environment.TickCount64);
        try
        {
            // Applied before the agent's script or input goes out: Nightly has the page visible by then.
            if (Dispatcher.UIThread.CheckAccess()) AgentActed();
            else await Dispatcher.UIThread.InvokeAsync(AgentActed);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
        return scope;
    }

    private sealed class AgentScope(NightlyPane pane) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Interlocked.Decrement(ref pane._agentActions);
            pane.NoteAgentAction();
        }
    }

    /// <summary>The agent acts on the pane (any thread): its page is seen now, and until <see cref="AgentSeen"/> after the
    /// last action, when it is looked at again.</summary>
    private void NoteAgentAction()
    {
        Interlocked.Exchange(ref _agentActed, Environment.TickCount64);
        if (Dispatcher.UIThread.CheckAccess()) AgentActed();
        else Dispatcher.UIThread.Post(AgentActed);
    }

    private void AgentActed()
    {
        UpdateVisibility();
        if (_closed) return;
        _agentSeen ??= new DispatcherTimer(AgentSeen, DispatcherPriority.Background, (_, _) =>
        {
            _agentSeen!.Stop();
            UpdateVisibility();
        });
        _agentSeen.Stop();
        _agentSeen.Start();
    }

    async Task NightlyAgent.ITarget.ShownFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        var shown = await Dispatcher.UIThread.InvokeAsync<Task>(() =>
            _closed || _windowId == 0 || !_visibility.Visible || _visibility.Shown is not null
                ? Task.CompletedTask
                : (_frameWaiters ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
        try { await shown.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { } // a page that does not paint (its process hangs): the frame before it is all there is
    }

    /// <summary>What waits for a frame (<see cref="NightlyAgent.ITarget.ShownFrameAsync"/>) goes on: one is shown, or none comes.</summary>
    private void FrameShown()
    {
        var waiting = _frameWaiters;
        _frameWaiters = null;
        waiting?.TrySetResult();
    }

    private void EnsureOcclusion()
    {
        if (_occlusion is not null || _closed || _top?.TryGetPlatformHandle() is not { Handle: not 0 } handle) return;
        var window = NightlyCocoa.WindowOf(handle.Handle);
        // Posted: AppKit says it while it orders the window, before Avalonia has its state.
        if (window != 0) _occlusion = new MacWindowOcclusion(window, () => Dispatcher.UIThread.Post(UpdateVisibility));
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) UpdateVisibility();
    }

    private void Connect()
    {
        var (runtime, problem) = NightlyHost.Get();
        if (runtime is null)
        {
            ShowStatus(problem ?? Loc.T("Nightly could not start."), retry: true);
            RuntimeUnusable?.Invoke(problem ?? Loc.T("Nightly could not start."));
            return;
        }
        _runtime = runtime;
        _token = AdoptWindow != 0 ? runtime.Adopt(this, AdoptWindow) : runtime.Register(this);
    }

    private void Retry()
    {
        if (_closed) return;
        if (_runtime is null) Connect();
        else _runtime.Retry();
    }

    /// <summary>Nightly cannot show this pane (it cannot start, or gave up restarting): the preview falls back.</summary>
    public event Action<string>? RuntimeUnusable;

    // ── INightlyView ──

    void INightlyView.OnRuntimeState(NightlyState state, string? detail)
    {
        switch (state)
        {
            case NightlyState.Starting:
                ShowStatus(Loc.T("Starting Nightly…"), retry: false);
                break;
            case NightlyState.Restarting:
                ShowStatus(Loc.F("Nightly stopped: {0} Restarting…", detail ?? ""), retry: false);
                break;
            case NightlyState.Failed:
                ShowStatus(Loc.F("Nightly could not start: {0}", detail ?? ""), retry: true);
                RuntimeUnusable?.Invoke(detail ?? Loc.T("Nightly could not start."));
                break;
            case NightlyState.Stopped:
                ShowStatus(Loc.T("Nightly is not running."), retry: true);
                break;
            case NightlyState.Running when _hasFrame:
                _overlay.IsVisible = false;
                break;
            case NightlyState.Running:
                ShowStatus(Loc.T("Opening the page…"), retry: false);
                break;
        }
    }

    void INightlyView.OnAttached(uint windowId)
    {
        _windowId = windowId;
        _visibility.Attach(windowId, OnScreen());
        _lastResize = default;
        SendResize();
        SendFocus();
        // Before the first page, so it loads as the device.
        if (_device is not null) SendDevice();
        foreach (var url in _pendingUrls.ToList())
        {
            if (!SendNavigate(url)) break;
            _pendingUrls.Remove(url);
        }
    }

    public void OnDetached()
    {
        _windowId = 0;
        _hasFrame = false;
        _visibility.Detach(); // its window is gone, and with it the frames held from it
        FrameShown(); // none comes from it
        _layer?.Show(0);
        _frames.Clear();
        _frameWidth = _frameHeight = 0;
        PlaceLayer();
        _popups.Close();
        _composing = _cancelComposition = false;
        EndDrag();
        foreach (var cts in _dialogs.Values) cts.Cancel();
        _dialogs.Clear();
    }

    void INightlyView.OnWindow(NightlyProtocol.WindowInfo window)
    {
        if (window.Scale > 0) _geckoScale = window.Scale;
    }

    void INightlyView.OnSurface(NightlyProtocol.SurfaceInfo info, nint surface)
    {
        if (!_closed) _frames.AddSurface(info, surface); // its own reference
        NightlyMach.CFRelease(surface);
    }

    void INightlyView.OnFrame(NightlyProtocol.FrameInfo frame)
    {
        Timing?.Received();
        UpdateVisibility();
        if (_layer is null && _visibility.Visible) EnsureLayer();
        // Off screen it goes back at once; without a layer it waits for one (a page that paints once and then stays
        // still would otherwise never show).
        var show = _visibility.Arrived(frame, canShow: _layer is not null);
        if (show || _visibility.Held == frame) _frames.SetLatest(frame);
        if (show) Show(frame);
    }

    private void ShowHeldFrame()
    {
        if (_layer is not null && _visibility.TakeHeld() is { } frame) Show(frame);
    }

    /// <summary>The layer shows <paramref name="frame"/> from now on; the one it showed goes back to Nightly.</summary>
    private void Show(NightlyProtocol.FrameInfo frame)
    {
        var received = Timing is null ? 0 : NightlyFrameTiming.Now();
        var surface = _frames.Surface(frame.SurfaceId);
        if (_layer is null || surface == 0 || frame.WindowId != _windowId)
        {
            _visibility.Release(frame);
            return;
        }
        _layer.Show(surface);
        _visibility.Showing(frame);
        FrameShown();
        _frameWidth = frame.Width;
        _frameHeight = frame.Height;
        FramesShown++;
        Timing?.Shown(frame, received);
        if (!_hasFrame)
        {
            _hasFrame = true;
            _overlay.IsVisible = false;
        }
        LayoutFrame();
    }

    void INightlyView.OnLocation(NightlyProtocol.LocationInfo location)
    {
        _pageUrl = Uri.TryCreate(location.Url, UriKind.Absolute, out var u) ? u : null;
        LocationChanged?.Invoke(location);
    }

    async void INightlyView.OnDialog(uint dialogId, string json)
    {
        var runtime = _runtime;
        var window = _windowId;
        if (runtime is null || window == 0) return;
        // Native file and print panels are sheets on Ombra, never on Nightly's hidden window.
        var panel = IsBrowserWindow ? NightlyFilePanel.Parse(json) : null;
        var storage = panel is null ? null : TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) panel = null;
        var print = OperatingSystem.IsMacOS() ? NightlyPrintPanel.Parse(json) : null;
        var nativeWindow = print is null ? 0 : NightlyCocoa.WindowOf(TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0);
        if (panel is null && print is null && Page is null || _closed)
        {
            await runtime.ReplyDialogAsync(window, dialogId, "{}");
            return;
        }
        using var cts = new CancellationTokenSource();
        _dialogs[dialogId] = cts;
        string answer;
        try
        {
            answer = print is not null && OperatingSystem.IsMacOS()
                ? await NightlyPrintPanel.ShowAsync(nativeWindow, print, cts.Token)
                : panel is not null
                    ? await NightlyFilePanel.ShowAsync(storage!, panel)
                    : await NightlyDialogs.AnswerAsync(Page!, dialogId, json, _pageUrl, d => runtime.TrackDownload(window, d), cts.Token);
        }
        catch (Exception e)
        {
            Services.ClientLog.Shared.Write("nightly", e);
            answer = "{}";
        }
        finally
        {
            if (_dialogs.TryGetValue(dialogId, out var current) && current == cts) _dialogs.Remove(dialogId);
        }
        // Withdrawn meanwhile (its window gone or Nightly closed it): Nightly no longer waits.
        if (!cts.IsCancellationRequested && _windowId == window) await runtime.ReplyDialogAsync(window, dialogId, answer);
        if ((panel is not null || print is not null) && IsEffectivelyVisible) FocusPage();
    }

    void INightlyView.OnDialogClosed(uint dialogId)
    {
        if (_dialogs.Remove(dialogId, out var cts)) cts.Cancel();
    }

    void INightlyView.OnPopup(uint popupId, NightlyPopup popup)
    {
        // A hidden tab's page cannot have a popup on screen.
        if (!IsEffectivelyVisible) AnswerPopup(popupId, -1);
        else _popups.Show(popupId, popup, FromGecko(popup.Anchor.X, popup.Anchor.Y, popup.Anchor.Width, popup.Anchor.Height));
    }

    void INightlyView.OnPopupClosed(uint popupId) => _popups.Close(popupId);

    private void AnswerPopup(uint popupId, int index)
    {
        if (_runtime is { } runtime && _windowId != 0) runtime.Send(NightlyProtocol.PopupChoiceMessage(_windowId, popupId, index));
    }

    void INightlyView.OnCursor(int cursor)
    {
        // During the window's own drag the pointer is the drag's (AppKit's); the page's comes back after it.
        if (_drag is not null) _pageCursor = CursorFor(cursor);
        else _host.Cursor = CursorFor(cursor);
    }

    // ── drags (desktop ZenEmbedHost.mm, "Drags") ──

    void INightlyView.OnDragStarted(NightlyProtocol.DragStartedInfo drag)
    {
        if (_drag is null) _pageCursor = _host.Cursor;
        _drag = drag;
        _dragCarried = false;
        var old = _dragImage.Source as IDisposable;
        _dragImage.Source = drag.Png.Length > 0 ? new Bitmap(new MemoryStream(drag.Png)) : null;
        old?.Dispose();
        _host.Cursor = DragCursors[0];
        PlaceDragImage();
    }

    void INightlyView.OnDragStatus(ulong operation, bool ended)
    {
        _dropOperation = operation;
        if (ended) EndDrag();
        else if (_drag is not null && !_dragCarried) _host.Cursor = DragCursors[DragCursorIndex(operation)];
    }

    /// <summary>The pointer AppKit shows over a drag: the arrow, with a plus where a drop copies, a link arrow where it links.</summary>
    private static readonly Cursor[] DragCursors =
        [new(StandardCursorType.Arrow), new(StandardCursorType.DragCopy), new(StandardCursorType.DragLink)];

    private static int DragCursorIndex(ulong operation) =>
        NightlyDrag.Effect(operation) switch { DragDropEffects.Copy => 1, DragDropEffects.Link => 2, _ => 0 };

    /// <summary>The window's drag is over: its image goes, the page's pointer comes back.</summary>
    private void EndDrag()
    {
        if (_drag is null) return;
        _drag = null;
        _dragCarried = false;
        _dragLayer.IsVisible = false;
        var old = _dragImage.Source as IDisposable;
        _dragImage.Source = null;
        old?.Dispose();
        _host.Cursor = _pageCursor;
        _pageCursor = null;
    }

    /// <summary>The drag image where AppKit would draw it: at the same place relative to the pointer as when it began.</summary>
    private void PlaceDragImage()
    {
        if (_drag is not { } d || _dragCarried || _dragImage.Source is null || d.Image.Width <= 0 || d.Image.Height <= 0)
        {
            _dragLayer.IsVisible = false;
            return;
        }
        var image = DragImageRect(d, _pointer);
        _dragImage.Width = image.Width;
        _dragImage.Height = image.Height;
        Canvas.SetLeft(_dragImage, image.X);
        Canvas.SetTop(_dragImage, image.Y);
        _dragLayer.IsVisible = true;
    }

    private Rect DragImageRect(NightlyProtocol.DragStartedInfo d, Point pointer)
    {
        var s = _shown.Width > 0 && _frameWidth > 0 ? _shown.Width / (_frameWidth / _geckoScale) : 1;
        return new Rect(pointer.X + (d.Image.X - d.PointerX) * s, pointer.Y + (d.Image.Y - d.PointerY) * s, d.Image.Width * s, d.Image.Height * s);
    }

    /// <summary>
    /// The pointer left the pane during the window's own drag: Nightly's window lets it go (dragleave, as at a window's
    /// edge), and Ombra's AppKit drag of the same data carries it on over other windows and apps (Finder, Notes, Ombra's
    /// own); where that one ends the window's drag ends (dragend).
    /// </summary>
    private void CarryDrag(Point at, KeyModifiers modifiers)
    {
        if (_drag is not { } drag || _runtime is not { } runtime || _windowId == 0) return;
        _dragCarried = true;
        _dragLayer.IsVisible = false;
        var window = _windowId;
        var flags = MacModifiers(modifiers);
        var g = ToGeckoAnywhere(at) ?? (-1, -1);
        SendMouse(NightlyProtocol.MouseKind.Move, 0, g, modifiers, 0);
        runtime.Send(NightlyProtocol.DragSourceMessage(window, NightlyProtocol.DragSourceState.Carried, g.X, g.Y, 0, flags));
        var began = BeginAppKitDrag(drag, at, (operation, cancelled) =>
        {
            Services.ClientLog.Shared.Write("nightly", $"carried drag of window {window} ended: operation {operation}{(cancelled ? ", cancelled" : "")}");
            runtime.Send(NightlyProtocol.DragSourceMessage(window,
                cancelled ? NightlyProtocol.DragSourceState.Cancelled : NightlyProtocol.DragSourceState.Ended, g.X, g.Y, operation, flags));
        });
        Services.ClientLog.Shared.Write("nightly", $"drag of window {window} left the pane: {(began ? "carried on by an AppKit drag" : "AppKit refused a drag")}");
        if (!began) runtime.Send(NightlyProtocol.DragSourceMessage(window, NightlyProtocol.DragSourceState.Ended, g.X, g.Y, 0, flags));
    }

    /// <summary>Ombra's AppKit drag from the view under the pointer, with the mouse event being handled (the one that left
    /// the pane); the drag image where the pane drew it. False when AppKit refused (no button held).</summary>
    private unsafe bool BeginAppKitDrag(NightlyProtocol.DragStartedInfo drag, Point at, Action<ulong, bool> ended)
    {
        if (_top is null || _top.TryGetPlatformHandle() is not { Handle: not 0 } handle) return false;
        var nsWindow = NightlyCocoa.WindowOf(handle.Handle);
        var ev = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication"), "currentEvent");
        var content = nsWindow == 0 ? 0 : MacObjC.Send(nsWindow, "contentView");
        if (ev == 0 || content == 0) return false;
        var location = ((delegate* unmanaged<nint, nint, MacRuntime.CGPoint>)MacObjC.MsgSend)(ev, MacObjC.Sel("locationInWindow"));
        var view = ((delegate* unmanaged<nint, nint, MacRuntime.CGPoint, nint>)MacObjC.MsgSend)(content, MacObjC.Sel("hitTest:"), location);
        if (view == 0) view = content;
        var image = DragImageRect(drag, at);
        var topLeft = _host.TranslatePoint(image.TopLeft, _top) ?? default;
        var frame = ViewRect(content, view, new Rect(topLeft, image.Size));
        return NightlyDrag.Begin(view, ev, drag.Pasteboard, drag.Png, frame,
            (operation, _, cancelled) => Dispatcher.UIThread.Post(() => ended(operation, cancelled)));
    }

    /// <summary>A rectangle of the window's client area (DIP, top-left origin) in <paramref name="view"/>'s coordinates.</summary>
    private static unsafe MacRuntime.CGRect ViewRect(nint content, nint view, Rect client)
    {
        var bounds = ((delegate* unmanaged<nint, nint, MacRuntime.CGRect>)MacObjC.MsgSend)(content, MacObjC.Sel("bounds"));
        var flipped = MacObjC.SendBool(content, "isFlipped");
        MacRuntime.CGPoint InView(double x, double y)
        {
            var inContent = new MacRuntime.CGPoint(x, flipped ? y : bounds.Height - y);
            var inWindow = ((delegate* unmanaged<nint, nint, MacRuntime.CGPoint, nint, MacRuntime.CGPoint>)MacObjC.MsgSend)(
                content, MacObjC.Sel("convertPoint:toView:"), inContent, 0);
            return ((delegate* unmanaged<nint, nint, MacRuntime.CGPoint, nint, MacRuntime.CGPoint>)MacObjC.MsgSend)(
                view, MacObjC.Sel("convertPoint:fromView:"), inWindow, 0);
        }
        var a = InView(client.X, client.Y);
        var b = InView(client.Right, client.Bottom);
        return new MacRuntime.CGRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    /// <summary>An AppKit drag over the pane (Finder's files, another app's text, Ombra's carried drag): Nightly's window
    /// takes it at the pointer; the drop does what Nightly last said (DragStatus). The window's file drop (the composer)
    /// does not get it.</summary>
    private void OnAppKitDrag(DragEventArgs e, NightlyProtocol.DragPhase phase)
    {
        e.Handled = true;
        var allowed = e.DragEffects;
        if (_runtime is not { } runtime || _windowId == 0 || ToGeckoAnywhere(e.GetPosition(_host)) is not { } g)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        if (phase == NightlyProtocol.DragPhase.Enter) _dropOperation = NightlyDrag.None;
        if (phase == NightlyProtocol.DragPhase.Drop) NoteUserInput();
        runtime.Send(NightlyProtocol.DragTargetMessage(_windowId, phase, g.X, g.Y, NightlyDrag.Operations(allowed), MacModifiers(e.KeyModifiers)));
        e.DragEffects = phase == NightlyProtocol.DragPhase.Exit ? DragDropEffects.None : NightlyDrag.Effect(_dropOperation) & allowed;
    }

    void INightlyView.OnTextRect(NightlyProtocol.TextRectInfo rect)
    {
        TextRects++;
        LastTextRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        _ime.SetCaret(FromGecko(rect.X, rect.Y, rect.Width, rect.Height));
    }

    /// <summary>How many times Nightly said where its composition is, and the last place (its window's points, top-left
    /// origin): development checks see where the keyboard's editor is.</summary>
    internal int TextRects { get; private set; }

    internal Rect LastTextRect { get; private set; }

    void INightlyView.OnPopupWindow(uint windowId)
    {
        if (PopupWindowOpened is { } opened) opened(windowId);
        else _runtime?.CloseWindow(windowId);
    }

    void INightlyView.OnWindowClosed() => PageClosed?.Invoke();

    private void ShowStatus(string text, bool retry)
    {
        _status.Text = text;
        _retry.IsVisible = retry;
        _overlay.IsVisible = true;
    }

    // ── layout: 1:1, centred, letterbox only while the frame does not match the pane ──

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateVisibility(); // its tab is shown or hidden
    }

    // The page takes the space it is given (as a web view does); with a Viewport the pane is that size, scaled down
    // (aspect kept) where it does not fit, and PreviewPanel.ApplyViewport places it.
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        if (_viewport is { Width: > 0, Height: > 0 } v)
        {
            var fit = Math.Min(1, Math.Min(availableSize.Width / v.Width, availableSize.Height / v.Height));
            return new Size(v.Width * fit, v.Height * fit);
        }
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        SendResize();
        LayoutFrame();
        ShowHeldFrame();
        return size;
    }

    private void LayoutFrame()
    {
        if (_frameWidth == 0 || _top is null)
        {
            PlaceLayer();
            return;
        }
        double s = _top.RenderScaling, bw = _host.Bounds.Width, bh = _host.Bounds.Height;
        double w = _frameWidth / s, h = _frameHeight / s;
        var fit = Math.Min(1, Math.Min(bw / w, bh / h));
        w *= fit;
        h *= fit;
        // The origin on a device pixel, so a 1:1 frame is not resampled (never left of or above the pane: a frame that
        // fills it exactly once scaled must not move by a rounding error).
        double x = Math.Max(0, Math.Floor((bw - w) / 2 * s) / s), y = Math.Max(0, Math.Floor((bh - h) / 2 * s) / s);
        _shown = new Rect(x, y, w, h);
        PlaceLayer();
    }

    /// <summary>
    /// The layer where the frame is drawn in the window, and the hole over it in what Avalonia draws; both gone while the
    /// pane shows no frame (none yet, or none since it was shown again) or is not shown (after every layout pass too: the
    /// pane moves or hides with what holds it).
    /// </summary>
    private void PlaceLayer()
    {
        var shown = _visibility.Shown is not null && _top is not null && IsEffectivelyVisible && _shown.Width > 0;
        var at = shown ? _host.TranslatePoint(_shown.TopLeft, _top!) : null;
        _layer?.Place(at is { } p ? new Rect(p, _shown.Size) : default, _top?.RenderScaling ?? 2, hidden: at is null || _layer is null);
        _host.Hole = at is not null && _layer is not null ? _shown : default;
    }

    /// <summary>A point of the pane in Nightly's window (points, top-left origin); null on the letterbox bars.</summary>
    private (double X, double Y)? ToGecko(Point p)
    {
        if (_shown.Width <= 0 || !_shown.Contains(p)) return null;
        double gw = _frameWidth / _geckoScale, gh = _frameHeight / _geckoScale;
        return ((p.X - _shown.X) * gw / _shown.Width, (p.Y - _shown.Y) * gh / _shown.Height);
    }

    /// <summary>Any point of the pane in Nightly's window (points, top-left origin), outside the frame too; null without one.</summary>
    private (double X, double Y)? ToGeckoAnywhere(Point p)
    {
        if (_shown.Width <= 0 || _shown.Height <= 0) return null;
        double gw = _frameWidth / _geckoScale, gh = _frameHeight / _geckoScale;
        return ((p.X - _shown.X) * gw / _shown.Width, (p.Y - _shown.Y) * gh / _shown.Height);
    }

    /// <summary>A rectangle of Nightly's window (points, top-left origin) in the pane.</summary>
    private Rect FromGecko(double x, double y, double width, double height)
    {
        var s = _shown.Width > 0 && _frameWidth > 0 ? _shown.Width / (_frameWidth / _geckoScale) : 1;
        return new Rect(_shown.X + x * s, _shown.Y + y * s, Math.Max(0, width * s), Math.Max(0, height * s));
    }

    private void OnScreenChanged(object? sender, EventArgs e) => SendResize();

    private void SendResize()
    {
        if (_runtime is null || _windowId == 0) return;
        if (_host.Bounds.Width < 1 || _host.Bounds.Height < 1) return;
        // The window is the page's size: the viewport's, else the pane's.
        var (w, h) = _viewport is { Width: >= 1, Height: >= 1 } v ? (v.Width, v.Height) : (_host.Bounds.Width, _host.Bounds.Height);
        var origin = ScreenOrigin();
        if (_lastResize == (w, h, origin)) return;
        if (_runtime.Send(NightlyProtocol.ResizeMessage(_windowId, w, h, origin))) _lastResize = (w, h, origin);
    }

    /// <summary>The pane's bottom-left corner on screen in Cocoa coordinates (Nightly's window goes there, off screen as it is).</summary>
    private (double X, double Y)? ScreenOrigin() =>
        _top?.TryGetPlatformHandle() is { Handle: not 0 } handle && _host.TranslatePoint(default, _top) is { } topLeft
            ? NightlyCocoa.ScreenBottomLeft(NightlyCocoa.WindowOf(handle.Handle), topLeft.X, topLeft.Y, _host.Bounds.Width, _host.Bounds.Height)
            : null;

    // ── input ──

    private void WireInput()
    {
        _host.PointerPressed += (_, e) =>
        {
            _host.Focus();
            NoteUserInput();
            TakeActive();
            var props = e.GetCurrentPoint(_host).Properties;
            _pressedButton = props.IsRightButtonPressed ? 1 : props.IsMiddleButtonPressed ? 2 : 0;
            if (ToGecko(e.GetPosition(_host)) is { } g)
                SendMouse(NightlyProtocol.MouseKind.Down, _pressedButton, g, e.KeyModifiers, e.ClickCount);
            e.Handled = true;
        };
        _host.PointerReleased += (_, e) =>
        {
            var button = e.InitialPressMouseButton switch { MouseButton.Right => 1, MouseButton.Middle => 2, _ => 0 };
            NoteUserInput();
            // A press the window got is released there wherever the pointer is now, as AppKit sends it to the window
            // that got the press (outside it too).
            if ((_pressedButton >= 0 ? ToGeckoAnywhere(e.GetPosition(_host)) : ToGecko(e.GetPosition(_host))) is { } g)
                SendMouse(NightlyProtocol.MouseKind.Up, button, g, e.KeyModifiers, 1);
            _pressedButton = -1;
            e.Handled = true;
        };
        _host.PointerMoved += (_, e) =>
        {
            var at = e.GetPosition(_host);
            _pointer = at;
            if (_drag is not null && !_dragCarried)
            {
                if (!new Rect(_host.Bounds.Size).Contains(at))
                {
                    CarryDrag(at, e.KeyModifiers);
                    return;
                }
                PlaceDragImage();
            }
            // With a button held the window follows the pointer outside its frame too (a selection, a drag).
            if ((_pressedButton >= 0 ? ToGeckoAnywhere(at) : ToGecko(at)) is { } g)
                SendMouse(NightlyProtocol.MouseKind.Move, _pressedButton, g, e.KeyModifiers, 0);
        };
        _host.PointerExited += (_, e) =>
        {
            if (_pressedButton < 0) SendMouse(NightlyProtocol.MouseKind.Exit, -1, (-1, -1), e.KeyModifiers, 0);
        };
        // AppKit drags over the pane (files from Finder, Ombra's own carried drag) are the window's: Nightly's view takes
        // them as if it were on screen (DragTarget), and says what a drop would do (OnDragStatus).
        _host.AddHandler(DragDrop.DragEnterEvent, (_, e) => OnAppKitDrag(e, NightlyProtocol.DragPhase.Enter));
        _host.AddHandler(DragDrop.DragOverEvent, (_, e) => OnAppKitDrag(e, NightlyProtocol.DragPhase.Over));
        _host.AddHandler(DragDrop.DragLeaveEvent, (_, e) => OnAppKitDrag(e, NightlyProtocol.DragPhase.Exit));
        _host.AddHandler(DragDrop.DropEvent, (_, e) => OnAppKitDrag(e, NightlyProtocol.DragPhase.Drop));
        // macOS sends scroll events through NightlyScrollMonitor (with their phases); this is only what reaches Avalonia anyway.
        _host.PointerWheelChanged += (_, e) => e.Handled = true;
        _host.KeyDown += (_, e) => OnKey(e, down: true);
        _host.KeyUp += (_, e) => OnKey(e, down: false);
        _host.TextInput += (_, e) => OnText(e);
        // The input method composes in Ombra, for the page (OnPreedit, OnText); its candidate window goes where Nightly
        // says the composition is (OnTextRect).
        _host.TextInputMethodClientRequested += (_, e) => e.Client = _ime;
        _host.GotFocus += (_, _) => SendFocus();
        _host.LostFocus += (_, _) => SendFocus();
    }

    private void OnWindowActivation(object? sender, EventArgs e) => SendFocus();

    private void SendFocus()
    {
        if (_runtime is null || _windowId == 0) return;
        var focused = _host.IsFocused && _top is Window { IsActive: true };
        _runtime.SetActive(_windowId, focused);
    }

    /// <summary>The user acts in the pane: its window is Gecko's active one again (the agent may have typed elsewhere).</summary>
    private void TakeActive()
    {
        if (_runtime is { } runtime && _windowId != 0 && runtime.ActiveWindow != _windowId) runtime.SetActive(_windowId, true);
    }

    private void SendMouse(NightlyProtocol.MouseKind kind, int button, (double X, double Y) at, KeyModifiers modifiers, int clicks)
    {
        if (_runtime is null || _windowId == 0) return;
        _runtime.Send(NightlyProtocol.MouseMessage(_windowId, kind, button, clicks, at.X, at.Y, MacModifiers(modifiers)));
    }

    // ── NightlyShortcutMonitor.ITarget (a browser window's ⌘ and Control shortcuts) ──

    bool NightlyShortcutMonitor.ITarget.TakesShortcut(nint nsWindow) =>
        !_closed && _runtime is not null && _windowId != 0 && IsEffectivelyVisible
        && (_host.IsFocused || (IsBrowserWindow && TakesShortcutsUnfocused?.Invoke() == true))
        && _top?.TryGetPlatformHandle() is { Handle: not 0 } handle && NightlyCocoa.WindowOf(handle.Handle) == nsWindow;

    void NightlyShortcutMonitor.ITarget.Shortcut(in NightlyCocoa.KeyEvent key)
    {
        if (_runtime is null || _windowId == 0) return;
        // A shortcut taken while Ombra's focus was elsewhere (TakesShortcutsUnfocused): the keys after it are the
        // window's too. Focusing activates the window in Gecko before the key arrives (SendFocus).
        if (!_host.IsFocused) _host.Focus();
        _keySent = key.Identity;
        NoteUserInput();
        TakeActive();
        _runtime.Send(NightlyProtocol.KeyMessage(_windowId, true, key.KeyCode, key.Characters, key.Unmodified, key.Modifiers, key.IsRepeat));
    }

    /// <summary>
    /// A key of the user's. Avalonia asks the input method first (inside <c>keyDown:</c>): a key that types text comes as
    /// <see cref="OnText"/>, one the input method composes with as <see cref="OnPreedit"/>, the rest (arrows, Enter,
    /// Backspace, shortcuts) here; with Shift or Option the key comes here first, and is left to the input method (dead
    /// keys, kana) unless it answers nothing. Each native key event goes to Nightly once, with its key code, characters
    /// and modifiers, so the page gets keydown, keypress and keyup as from a keyboard.
    /// </summary>
    private void OnKey(KeyEventArgs e, bool down)
    {
        if (_runtime is null || _windowId == 0) return;
        var native = NightlyCocoa.CurrentKeyEvent();
        if (ModifierKeys.TryGetValue(e.PhysicalKey, out var modifierCode))
        {
            // A modifier alone (flagsChanged:): the page sees Shift, Control, Option and Command go down and up. Not
            // handled: Ombra keeps track of its modifiers too.
            NoteUserInput();
            TakeActive();
            _runtime.Send(NightlyProtocol.FlagsMessage(_windowId, native is { Type: 12 } flags ? flags.KeyCode : modifierCode,
                native?.Modifiers ?? MacModifiers(e.KeyModifiers)));
            return;
        }
        int code;
        string chars, unmodified;
        ulong modifiers;
        var repeat = false;
        if (native is { Type: 10 or 11 } k)
        {
            (code, chars, unmodified, modifiers, repeat) = (k.KeyCode, k.Characters, k.Unmodified, k.Modifiers, k.IsRepeat);
        }
        else if (MacKeys.TryGetValue(e.PhysicalKey, out var key))
        {
            code = key.Code;
            chars = unmodified = key.Chars ?? e.KeySymbol ?? "";
            modifiers = MacModifiers(e.KeyModifiers);
        }
        else return;
        NoteUserInput();
        TakeActive();
        // ⌘ shortcuts also stay Ombra's (its menus and key bindings); everything else is the page's. A browser window
        // takes its shortcuts too (NightlyShortcutMonitor gives it the key downs before Ombra's menus see them).
        var handled = IsBrowserWindow || !e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (_popups.TakesKeys)
        {
            // A dropdown or a context menu is open: the keys are its own (arrows, Enter, Esc, a letter), as in Gecko.
            if (down) _popups.HandleKey(e.Key, e.KeySymbol);
            _keySent = native?.Identity;
            e.Handled = true;
            return;
        }
        if (!down)
        {
            _runtime.Send(NightlyProtocol.KeyMessage(_windowId, false, code, chars, unmodified, modifiers, isRepeat: false));
            e.Handled = handled;
            return;
        }
        if (native is { Type: 10 } down0)
        {
            var id = down0.Identity;
            if (_keySent == id)
            {
                // Nightly has it already (a ⌘ key the input method passed back as a command), or the input method took it.
                e.Handled = handled;
                return;
            }
            if (_keyDeferred != id && (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) != 0
                && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
            {
                // Shift or Option: the input method answers first (text, a composition, a command: each comes back here
                // or to OnText / OnPreedit). Nothing at all: the key goes as it is once keyDown: is over.
                _keyDeferred = id;
                var window = _windowId;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_keySent == id || _windowId != window || _runtime is not { } runtime) return;
                    _keySent = id;
                    runtime.Send(NightlyProtocol.KeyMessage(window, true, code, chars, unmodified, modifiers, repeat));
                });
                return;
            }
            _keySent = id;
        }
        _runtime.Send(NightlyProtocol.KeyMessage(_windowId, true, code, chars, unmodified, modifiers, repeat));
        e.Handled = handled;
    }

    /// <summary>
    /// Text from the input method: a key that typed it (it goes to Nightly as that key, with this text), a commit of the
    /// composition (it replaces the composition), or text with no key at all (the emoji picker, dictation).
    /// </summary>
    private void OnText(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _windowId == 0 || _runtime is null) return;
        e.Handled = true;
        var native = NightlyCocoa.CurrentKeyEvent() is { Type: 10 } k ? k : (NightlyCocoa.KeyEvent?)null;
        var committing = _composing || _cancelComposition;
        if (_popups.TakesKeys && !committing)
        {
            _popups.TypeAhead(e.Text); // a letter jumps in the open dropdown or menu
            return;
        }
        if (native is { } sent && _keySent == sent.Identity && !committing) return; // its key went to Nightly, which typed it
        NoteUserInput();
        TakeActive();
        _composing = _cancelComposition = false;
        if (native is { } key)
        {
            _keySent = key.Identity;
            if (!committing && e.Text.Length <= 16)
            {
                _runtime.Send(NightlyProtocol.KeyMessage(_windowId, true, key.KeyCode, e.Text, key.Unmodified, key.Modifiers, key.IsRepeat));
                return;
            }
        }
        for (var at = 0; at < e.Text.Length;)
        {
            var n = Math.Min(NightlyProtocol.MaxInputText, e.Text.Length - at);
            if (at + n < e.Text.Length && char.IsHighSurrogate(e.Text[at + n - 1])) n--;
            _runtime.Send(NightlyProtocol.TextMessage(_windowId, e.Text.Substring(at, n)));
            at += n;
        }
    }

    /// <summary>The input method's composition (<c>setMarkedText:</c>; null or empty: it ended).</summary>
    private void OnPreedit(string? text)
    {
        if (_runtime is null || _windowId == 0) return;
        // An empty preedit with no composition (Avalonia clears it before inserting a key's text, as for ⇧ or ⌥ keys) is
        // nothing the input method took: the key's text still comes (OnText) or the key goes as it is (OnKey).
        if (string.IsNullOrEmpty(text) && !_composing) return;
        if (NightlyCocoa.CurrentKeyEvent() is { Type: 10 } key) _keySent = key.Identity; // the input method took the key
        if (string.IsNullOrEmpty(text))
        {
            _composing = false;
            // A commit unmarks first (insertText:), and its text comes as input after that: only a composition that ends
            // with nothing is cancelled in the page. Background: below the input the commit's text is queued at.
            _cancelComposition = true;
            var window = _windowId;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_cancelComposition || _windowId != window) return;
                _cancelComposition = false;
                _runtime?.Send(NightlyProtocol.MarkedTextMessage(window, "", 0, 0));
            }, DispatcherPriority.Background);
            return;
        }
        NoteUserInput();
        TakeActive();
        _cancelComposition = false;
        _composing = true;
        // Avalonia passes the marked text alone: the caret at its end.
        _runtime.Send(NightlyProtocol.MarkedTextMessage(_windowId, text, text.Length, 0));
    }

    /// <summary>The pane as the input method's client: preedit goes to the page, the candidate window to its composition.</summary>
    private sealed class ImeClient(NightlyPane pane) : TextInputMethodClient
    {
        private Rect _caret;

        public override Visual TextViewVisual => pane._host;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => _caret;
        public override TextSelection Selection { get; set; }

        public override void SetPreeditText(string? preeditText) => pane.OnPreedit(preeditText);

        /// <summary>Where Nightly says the composition's caret is (pane coordinates).</summary>
        public void SetCaret(Rect caret)
        {
            if (caret == _caret) return;
            _caret = caret;
            RaiseCursorRectangleChanged();
        }
    }

    /// <summary>Gecko's nsCursor values (widget/nsIWidget.h) as the pointer shapes Avalonia has.</summary>
    private static Cursor CursorFor(int cursor)
    {
        var type = cursor switch
        {
            1 or 21 => StandardCursorType.Wait, // wait, spinning
            2 or 28 => StandardCursorType.Ibeam, // select, vertical-text
            3 => StandardCursorType.Hand, // hyperlink
            4 => StandardCursorType.TopSide,
            5 => StandardCursorType.BottomSide,
            6 => StandardCursorType.LeftSide,
            7 => StandardCursorType.RightSide,
            8 or 31 => StandardCursorType.TopLeftCorner, // nw, nwse
            9 => StandardCursorType.BottomRightCorner,
            10 or 30 => StandardCursorType.TopRightCorner, // ne, nesw
            11 => StandardCursorType.BottomLeftCorner,
            12 or 18 => StandardCursorType.Cross, // crosshair, cell
            13 or 29 => StandardCursorType.SizeAll, // move, all-scroll
            14 or 17 => StandardCursorType.Help, // help, context-menu
            15 => StandardCursorType.DragCopy,
            16 => StandardCursorType.DragLink,
            19 or 20 => StandardCursorType.DragMove, // grab, grabbing (the open hand)
            24 or 27 => StandardCursorType.No, // not-allowed, no-drop
            25 or 33 => StandardCursorType.SizeWestEast, // col-resize, ew-resize
            26 or 32 => StandardCursorType.SizeNorthSouth, // row-resize, ns-resize
            34 => StandardCursorType.None,
            _ => StandardCursorType.Arrow, // standard, zoom-in, zoom-out
        };
        if (!Cursors.TryGetValue(type, out var c)) Cursors[type] = c = new Cursor(type);
        return c;
    }

    private static readonly Dictionary<StandardCursorType, Cursor> Cursors = [];

    /// <summary>The shape the page last asked for (for tests and the acceptance check).</summary>
    internal StandardCursorType? PageCursor => _host.Cursor is { } c ? Cursors.FirstOrDefault(p => p.Value == c).Key : null;

    /// <summary>Modifier keys (flagsChanged:) → their macOS virtual key codes.</summary>
    private static readonly Dictionary<PhysicalKey, int> ModifierKeys = new()
    {
        [PhysicalKey.ShiftLeft] = 0x38, [PhysicalKey.ShiftRight] = 0x3C, [PhysicalKey.ControlLeft] = 0x3B, [PhysicalKey.ControlRight] = 0x3E,
        [PhysicalKey.AltLeft] = 0x3A, [PhysicalKey.AltRight] = 0x3D, [PhysicalKey.MetaLeft] = 0x37, [PhysicalKey.MetaRight] = 0x36,
        [PhysicalKey.CapsLock] = 0x39,
    };

    private static ulong MacModifiers(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Shift) ? 1UL << 17 : 0) | (m.HasFlag(KeyModifiers.Control) ? 1UL << 18 : 0) |
        (m.HasFlag(KeyModifiers.Alt) ? 1UL << 19 : 0) | (m.HasFlag(KeyModifiers.Meta) ? 1UL << 20 : 0);

    // ── NightlyScrollMonitor.ITarget ──

    bool NightlyScrollMonitor.ITarget.Hits(nint nsWindow, double x, double y) =>
        WindowPointToPane(nsWindow, x, y) is { } p && _top is { } top && _host.TranslatePoint(p, top) is { } inTop
        && top.InputHitTest(inTop) is Visual v && (v == _host || _host.IsVisualAncestorOf(v));

    void NightlyScrollMonitor.ITarget.Scroll(nint nsWindow, double x, double y, in WheelInput input)
    {
        if (_runtime is null || _windowId == 0) return;
        NoteUserInput();
        // Momentum and the end of a gesture can come with the pointer elsewhere: Nightly pins them to where it began.
        var p = WindowPointToPane(nsWindow, x, y) ?? default;
        var g = ToGecko(p) ?? (_shown.Width > 0 && _shown.Height > 0
            ? ToGecko(new Point(Math.Clamp(p.X, _shown.X, _shown.Right - 0.01), Math.Clamp(p.Y, _shown.Y, _shown.Bottom - 0.01)))
            : null) ?? (0, 0);
        _runtime.Send(NightlyProtocol.WheelMessage(_windowId, g.Item1, g.Item2, input));
    }

    /// <summary>A point of <paramref name="nsWindow"/> (bottom-left origin) in the pane, when it is this pane's window.</summary>
    private Point? WindowPointToPane(nint nsWindow, double x, double y)
    {
        if (_closed || _top?.TryGetPlatformHandle() is not { Handle: not 0 } handle || NightlyCocoa.WindowOf(handle.Handle) != nsWindow
            || !IsEffectivelyVisible || NightlyCocoa.ClientPoint(nsWindow, x, y) is not { } client)
            return null;
        return _top.TranslatePoint(new Point(client.X, client.Y), _host) is { } p && new Rect(_host.Bounds.Size).Contains(p) ? p : null;
    }

    /// <summary>
    /// The pane's own surface, separate from the status overlay above it. It paints a transparent rectangle so that it
    /// is hit-tested over its whole area (the frames are a layer under Avalonia's view, which hit-testing does not see:
    /// without it the pointer, the keyboard focus and the scroll monitor's hit test all landed on the pane's background),
    /// and clears <see cref="Hole"/> in what Avalonia draws (through the clips and corners of what holds the pane), so
    /// the layer shows there under whatever is drawn over the pane.
    /// </summary>
    private sealed class Surface : Control
    {
        private Rect _hole;

        /// <summary>Where the frame is (DIP of the surface); empty: none.</summary>
        public Rect Hole
        {
            get => _hole;
            set
            {
                if (value == _hole) return;
                _hole = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            if (_hole.Width > 0 && _hole.Height > 0) context.Custom(new ClearOp(_hole));
        }
    }

    /// <summary>Clears a rectangle to transparent (Skia's clear blend), whatever was drawn under it in the window.</summary>
    private sealed class ClearOp(Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => other is ClearOp o && o.Bounds == Bounds;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            using var paint = new SKPaint { BlendMode = SKBlendMode.Clear };
            lease.SkCanvas.DrawRect(new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom), paint);
        }
    }

    // ── keys: Avalonia's physical key → macOS virtual key code and the NSEvent characters of keys that type none ──

    private readonly record struct MacKey(int Code, string? Chars = null);

    private static readonly Dictionary<PhysicalKey, MacKey> MacKeys = new()
    {
        [PhysicalKey.A] = new(0x00), [PhysicalKey.S] = new(0x01), [PhysicalKey.D] = new(0x02), [PhysicalKey.F] = new(0x03),
        [PhysicalKey.H] = new(0x04), [PhysicalKey.G] = new(0x05), [PhysicalKey.Z] = new(0x06), [PhysicalKey.X] = new(0x07),
        [PhysicalKey.C] = new(0x08), [PhysicalKey.V] = new(0x09), [PhysicalKey.IntlBackslash] = new(0x0A), [PhysicalKey.B] = new(0x0B),
        [PhysicalKey.Q] = new(0x0C), [PhysicalKey.W] = new(0x0D), [PhysicalKey.E] = new(0x0E), [PhysicalKey.R] = new(0x0F),
        [PhysicalKey.Y] = new(0x10), [PhysicalKey.T] = new(0x11), [PhysicalKey.Digit1] = new(0x12), [PhysicalKey.Digit2] = new(0x13),
        [PhysicalKey.Digit3] = new(0x14), [PhysicalKey.Digit4] = new(0x15), [PhysicalKey.Digit6] = new(0x16), [PhysicalKey.Digit5] = new(0x17),
        [PhysicalKey.Equal] = new(0x18), [PhysicalKey.Digit9] = new(0x19), [PhysicalKey.Digit7] = new(0x1A), [PhysicalKey.Minus] = new(0x1B),
        [PhysicalKey.Digit8] = new(0x1C), [PhysicalKey.Digit0] = new(0x1D), [PhysicalKey.BracketRight] = new(0x1E), [PhysicalKey.O] = new(0x1F),
        [PhysicalKey.U] = new(0x20), [PhysicalKey.BracketLeft] = new(0x21), [PhysicalKey.I] = new(0x22), [PhysicalKey.P] = new(0x23),
        [PhysicalKey.Enter] = new(0x24, "\r"), [PhysicalKey.L] = new(0x25), [PhysicalKey.J] = new(0x26), [PhysicalKey.Quote] = new(0x27),
        [PhysicalKey.K] = new(0x28), [PhysicalKey.Semicolon] = new(0x29), [PhysicalKey.Backslash] = new(0x2A), [PhysicalKey.Comma] = new(0x2B),
        [PhysicalKey.Slash] = new(0x2C), [PhysicalKey.N] = new(0x2D), [PhysicalKey.M] = new(0x2E), [PhysicalKey.Period] = new(0x2F),
        [PhysicalKey.Tab] = new(0x30, "\t"), [PhysicalKey.Space] = new(0x31, " "), [PhysicalKey.Backquote] = new(0x32),
        [PhysicalKey.Backspace] = new(0x33, "\u007f"), [PhysicalKey.Escape] = new(0x35, "\u001b"),
        [PhysicalKey.NumPadDecimal] = new(0x41), [PhysicalKey.NumPadMultiply] = new(0x43), [PhysicalKey.NumPadAdd] = new(0x45),
        [PhysicalKey.NumPadClear] = new(0x47, "\uF739"), [PhysicalKey.NumPadDivide] = new(0x4B), [PhysicalKey.NumPadEnter] = new(0x4C, "\u0003"),
        [PhysicalKey.NumPadSubtract] = new(0x4E), [PhysicalKey.NumPadEqual] = new(0x51), [PhysicalKey.NumPad0] = new(0x52),
        [PhysicalKey.NumPad1] = new(0x53), [PhysicalKey.NumPad2] = new(0x54), [PhysicalKey.NumPad3] = new(0x55), [PhysicalKey.NumPad4] = new(0x56),
        [PhysicalKey.NumPad5] = new(0x57), [PhysicalKey.NumPad6] = new(0x58), [PhysicalKey.NumPad7] = new(0x59), [PhysicalKey.NumPad8] = new(0x5B),
        [PhysicalKey.NumPad9] = new(0x5C),
        [PhysicalKey.F1] = new(0x7A, "\uF704"), [PhysicalKey.F2] = new(0x78, "\uF705"), [PhysicalKey.F3] = new(0x63, "\uF706"),
        [PhysicalKey.F4] = new(0x76, "\uF707"), [PhysicalKey.F5] = new(0x60, "\uF708"), [PhysicalKey.F6] = new(0x61, "\uF709"),
        [PhysicalKey.F7] = new(0x62, "\uF70A"), [PhysicalKey.F8] = new(0x64, "\uF70B"), [PhysicalKey.F9] = new(0x65, "\uF70C"),
        [PhysicalKey.F10] = new(0x6D, "\uF70D"), [PhysicalKey.F11] = new(0x67, "\uF70E"), [PhysicalKey.F12] = new(0x6F, "\uF70F"),
        [PhysicalKey.Help] = new(0x72, "\uF746"), [PhysicalKey.Insert] = new(0x72, "\uF727"), [PhysicalKey.Home] = new(0x73, "\uF729"),
        [PhysicalKey.PageUp] = new(0x74, "\uF72C"), [PhysicalKey.Delete] = new(0x75, "\uF728"), [PhysicalKey.End] = new(0x77, "\uF72B"),
        [PhysicalKey.PageDown] = new(0x79, "\uF72D"), [PhysicalKey.ArrowLeft] = new(0x7B, "\uF702"), [PhysicalKey.ArrowRight] = new(0x7C, "\uF703"),
        [PhysicalKey.ArrowDown] = new(0x7D, "\uF701"), [PhysicalKey.ArrowUp] = new(0x7E, "\uF700"),
    };
}
