using CommunityToolkit.Mvvm.ComponentModel;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.ViewModels;

/// <summary>
/// A tab of the browser preview (<see cref="PreviewViewModel.Tabs"/>), each with its own web view: the user's own tab
/// (always there, <see cref="Id"/> empty), one per page omp's browser tool opened (its cmux surface id), and one per
/// pop-up a page opened (<see cref="Popup"/>, next to its <see cref="Opener"/>). While a tab is the active one,
/// <see cref="PreviewViewModel"/>'s page properties are its state; the others keep theirs here.
/// </summary>
public sealed partial class PreviewTab(string id, bool isAgentTab) : ObservableObject
{
    /// <summary>How long after omp's last action on the page what the page asks still counts as caused by omp.</summary>
    internal static readonly TimeSpan AgentGrace = TimeSpan.FromSeconds(1);

    private volatile bool _closed;
    private int _agentActions;
    private long _agentGraceUntil;

    /// <summary>The cmux surface id of an agent tab; empty for the user's own tab and pop-ups.</summary>
    public string Id { get; } = id;

    /// <summary>omp opened it (the user's own tab stays for good; agent tabs can be closed).</summary>
    public bool IsAgentTab { get; } = isAgentTab;

    /// <summary>The engine hooks of this tab's web view (questions, loading events, downloads, pop-ups); configured by <see cref="PreviewViewModel"/>.</summary>
    public BrowserPageHooks Page { get; } = new();

    /// <summary>The questions this tab's page asks (each tab its own: a dialog in one tab never holds another's scripts).
    /// The pane's cards show the active tab's.</summary>
    public DialogBroker Dialogs { get; } = new();

    /// <summary>The window a page opened (<c>window.open</c>, <c>target=_blank</c>), hosted in this tab; null for ordinary tabs.</summary>
    public BrowserPopup? Popup { get; init; }

    /// <summary>The tab whose page opened this pop-up.</summary>
    public PreviewTab? Opener { get; init; }

    public bool IsPopup => Popup is not null;

    /// <summary>The user can close it: omp's tabs and pop-ups (their own tab stays).</summary>
    public bool CanClose => IsAgentTab || IsPopup;

    /// <summary>
    /// omp's browser tool acts on this page until the result is disposed (and <see cref="AgentGrace"/> after): what the
    /// page asks meanwhile goes to omp first (<see cref="BrowserDialogRequest.CausedByAgent"/>). Any thread.
    /// </summary>
    public IDisposable AgentAction()
    {
        Interlocked.Increment(ref _agentActions);
        return new AgentActionScope(this);
    }

    /// <summary>omp is acting on the page now, or did less than <see cref="AgentGrace"/> ago. Any thread.</summary>
    public bool IsAgentActing =>
        Volatile.Read(ref _agentActions) > 0 || DateTime.UtcNow.Ticks < Interlocked.Read(ref _agentGraceUntil);

    private sealed class AgentActionScope(PreviewTab tab) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Interlocked.Exchange(ref tab._agentGraceUntil, (DateTime.UtcNow + AgentGrace).Ticks);
            Interlocked.Decrement(ref tab._agentActions);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private Uri? _url;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private string _title = "";

    [ObservableProperty] private bool _isLoading;

    /// <summary>The tab the pane shows.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>What the tab strip shows: the page title, its host, or "New tab".</summary>
    public string Header => Title.Length > 0 ? Title
        : Url is { } u ? (u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port)
        : "New tab";

    /// <summary>What the tab's tooltip says.</summary>
    public string ToolTip => IsPopup ? $"Pop-up from {Opener?.Header ?? "a page"}" : IsAgentTab ? "Opened by omp" : "Preview";

    // The rest of the page state, kept while another tab is active (the toolbar shows the active one's)
    internal string Address { get; set; } = "";
    internal bool LoadFailed { get; set; }
    internal bool CanGoBack { get; set; }
    internal bool CanGoForward { get; set; }
    internal string Message { get; set; } = "";
    internal BrowserLoadFailure? LoadError { get; set; }
    internal bool Crashed { get; set; }

    /// <summary>Runs a script in this tab's page; set by the view once its web view exists.</summary>
    public Func<string, Task<string?>>? RunScript { get; set; }

    /// <summary>Captures this tab's page as a PNG (Platform/WebViewSnapshot); set by the view with <see cref="RunScript"/>.</summary>
    public Func<CancellationToken, Task<OmpGui.ClientCore.AgentScreenshot>>? CaptureScreenshot { get; set; }

    /// <summary>The tab's native web view (<c>WKWebView</c> / <c>ICoreWebView2</c> / <c>WebKitWebView</c>) while its adapter exists.</summary>
    public Avalonia.Platform.IPlatformHandle? NativeHandle { get; private set; }

    /// <summary>Input to the page the engine treats like the user's (Platform/NativeInputs); null while there is no web view.</summary>
    public INativeInput? Input { get; private set; }

    /// <summary>The app's script world in the page, apart from the page's own; null while there is no web view.</summary>
    public IIsolatedScripts? Isolated { get; private set; }

    /// <summary><see cref="NativeHandle"/>, <see cref="Input"/> and <see cref="Isolated"/> changed (UI thread).</summary>
    public event Action<PreviewTab>? NativeChanged;

    /// <summary>Sets the native web view and what drives it (null: gone); the previous ones are disposed. UI thread.</summary>
    internal void SetNative(Avalonia.Platform.IPlatformHandle? handle, INativeInput? input, IIsolatedScripts? isolated)
    {
        var (oldInput, oldIsolated) = (Input, Isolated);
        (NativeHandle, Input, Isolated) = (handle, input, isolated);
        (oldInput as IDisposable)?.Dispose();
        if (!ReferenceEquals(oldIsolated, oldInput)) (oldIsolated as IDisposable)?.Dispose();
        NativeChanged?.Invoke(this);
    }

    /// <summary>The page size omp asked for (Tern <c>viewport</c>, CSS pixels): the web view takes at most this size,
    /// top-left in the pane (a pane smaller than that limits it; the page reports what it got). Null: the pane's size.</summary>
    [ObservableProperty] private Avalonia.PixelSize? _viewport;

    /// <summary>Whether the page may go to an address (omp's allowed hosts); a refused one is not loaded. Null: any.</summary>
    public Func<Uri, bool>? NavigationFilter { get; set; }

    /// <summary>The tab left the pane (closed by the user or by omp). Any thread.</summary>
    public bool IsClosed => _closed;

    internal void MarkClosed()
    {
        _closed = true;
        RunScript = null;
        CaptureScreenshot = null;
        SetNative(null, null, null);
        Dialogs.Dispose(); // what the page still asks gets the nobody-answered answer
    }
}
