using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The questions a page of the browser pane asks (<see cref="DialogBroker"/>), as one card at a time over the page
/// (Controls/PreviewPanel). The native web view draws over everything Avalonia draws there, so while a card is open the
/// view hides the web view and shows <see cref="Snapshot"/> (the page frozen as it was) under a scrim instead
/// (<see cref="CoversPage"/>). The broker is thread-safe; this model follows it on the UI thread.
/// </summary>
public sealed partial class BrowserDialogsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the page's picture may take before the card shows over the plain surface instead.</summary>
    internal static readonly TimeSpan SnapshotTimeout = TimeSpan.FromMilliseconds(700);

    private int _generation;
    private readonly DialogBroker _own = new();

    public BrowserDialogsViewModel()
    {
        Broker = _own;
        Broker.Changed += OnBrokerChanged;
    }

    /// <summary>The questions of the page shown: its tab's broker once <see cref="Follow"/>ed (each pane tab has its
    /// own), else this card layer's own. The engine hooks ask there (sign-in, alerts, files, downloads, permissions, popups).</summary>
    public DialogBroker Broker { get; private set; }

    /// <summary>The cards show <paramref name="broker"/>'s questions from now on (the pane switched tabs); the other
    /// broker's questions stay asked, for when its tab shows again. UI thread.</summary>
    public void Follow(DialogBroker broker)
    {
        if (ReferenceEquals(broker, Broker)) return;
        Broker.Changed -= OnBrokerChanged;
        Broker = broker;
        Broker.Changed += OnBrokerChanged;
        // The card (and the picture behind it) of the tab left behind goes; the new tab's, if any, is shown fresh
        if (Card is not null)
        {
            Card = null;
            Uncover();
        }
        Sync();
    }

    /// <summary>The page as a picture for the card's background; set by the view while it has a web view (null: no picture).</summary>
    public Func<CancellationToken, Task<Bitmap?>>? CaptureSnapshot { get; set; }

    /// <summary>Opens the system's file picker for a file card; set by the view. Null result: nothing chosen.</summary>
    public Func<FileChooserRequest, Task<IReadOnlyList<string>?>>? PickFiles { get; set; }

    /// <summary>The card shown; null when the page asks nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private BrowserDialogCardViewModel? _card;

    /// <summary>The page as it was when the card opened; null when no picture could be taken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshot))]
    private Bitmap? _snapshot;

    /// <summary>The card covers the page: the web view is hidden (its picture, if any, shows instead).</summary>
    [ObservableProperty] private bool _coversPage;

    public bool IsOpen => Card is not null;
    public bool HasSnapshot => Snapshot is not null;

    private void OnBrokerChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Sync();
        else Dispatcher.UIThread.Post(Sync);
    }

    /// <summary>Shows the broker's current card (a new one, the same with a new queue count, or none).</summary>
    private void Sync()
    {
        var current = Broker.Current;
        var queued = Broker.QueuedBehindCurrent;
        if (current is null)
        {
            if (Card is null) return;
            Card = null;
            Uncover();
            return;
        }
        if (Card?.Dialog == current)
        {
            Card.Queued = queued;
            return;
        }
        var wasOpen = Card is not null;
        Card = new BrowserDialogCardViewModel(current, this) { Queued = queued };
        if (!wasOpen) _ = CoverAsync();
    }

    /// <summary>Takes the page's picture (while the web view still shows it), then hides the web view behind it.</summary>
    private async Task CoverAsync()
    {
        var generation = ++_generation;
        Bitmap? picture = null;
        if (CaptureSnapshot is { } capture)
        {
            using var cts = new CancellationTokenSource(SnapshotTimeout);
            try
            {
                var task = capture(cts.Token);
                if (await Task.WhenAny(task, Task.Delay(SnapshotTimeout, CancellationToken.None)) == task) picture = await task;
                else _ = task.ContinueWith(static t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
            }
            catch (Exception)
            {
                // No picture (the engine refused, or the page went away): the card shows over the plain surface.
            }
        }
        if (generation != _generation || Card is null)
        {
            picture?.Dispose();
            return;
        }
        Snapshot = picture;
        CoversPage = true;
    }

    private void Uncover()
    {
        _generation++;
        CoversPage = false;
        var old = Snapshot;
        Snapshot = null;
        old?.Dispose();
    }

    internal async Task<IReadOnlyList<string>?> PickFilesAsync(FileChooserRequest request) =>
        PickFiles is { } pick ? await pick(request) : null;

    public void Dispose()
    {
        Broker.Changed -= OnBrokerChanged;
        _own.Dispose();
        Card = null;
        Uncover();
    }
}

/// <summary>
/// One card: the title says who asks what ("localhost:3000 wants you to sign in"), the page's own text below, then the
/// answers. Enter = <see cref="PrimaryCommand"/>, Esc = <see cref="CancelCommand"/> (the answer nobody-answered gets).
/// </summary>
public sealed partial class BrowserDialogCardViewModel : ObservableObject
{
    private readonly BrowserDialogsViewModel _owner;

    internal BrowserDialogCardViewModel(BrowserDialog dialog, BrowserDialogsViewModel owner)
    {
        Dialog = dialog;
        _owner = owner;
        var r = dialog.Request;
        var site = Site(r.PageUrl);
        switch (r)
        {
            case CredentialRequest c:
                Title = c.IsProxy ? $"The proxy {HostPort(c.Host, c.Port, r.PageUrl)} wants you to sign in" : $"{HostPort(c.Host, c.Port, r.PageUrl)} wants you to sign in";
                Detail = c.Realm is { Length: > 0 } realm ? $"Realm: {realm}" : null;
                Error = c.FailedBefore ? "That username or password didn't work" : null;
                Warning = !c.IsProxy && r.PageUrl?.Scheme == "http" ? "This site doesn't use a secure connection." : null;
                Note = r.CausedByAgent ? "omp opened this page. Only you can sign in; omp doesn't see your password." : null;
                UserName = c.UserName ?? "";
                PrimaryLabel = "Sign in";
                SecondaryLabel = "Cancel";
                break;
            case AlertRequest a:
                Title = $"{site} says";
                Message = a.Message;
                PrimaryLabel = "OK";
                break;
            case ConfirmRequest cf:
                Title = $"{site} says";
                Message = cf.Message;
                PrimaryLabel = "OK";
                SecondaryLabel = "Cancel";
                break;
            case PromptRequest p:
                Title = $"{site} says";
                Message = p.Message;
                PromptText = p.Default ?? "";
                PrimaryLabel = "OK";
                SecondaryLabel = "Cancel";
                break;
            case BeforeUnloadRequest:
                // Browsers show their own words, never the page's (it could say anything)
                Title = "Leave this page?";
                Message = "Changes you made may not be saved.";
                PrimaryLabel = "Leave";
                SecondaryLabel = "Stay";
                break;
            case FileChooserRequest f:
                Title = f.AllowDirectories ? $"{site} wants you to choose a folder"
                    : f.Multiple ? $"{site} wants you to choose files" : $"{site} wants you to choose a file";
                Detail = f.Accept.Count > 0 ? $"Accepted types: {string.Join(", ", f.Accept)}" : null;
                PrimaryLabel = f.AllowDirectories ? "Choose folder…" : f.Multiple ? "Choose files…" : "Choose file…";
                SecondaryLabel = "Cancel";
                break;
            case DownloadRequest d:
                Title = $"Download {d.FileName}?";
                Detail = (d.Size is { } size ? FormatSize(size) + " from " : "From ") + (d.Url is { } u ? Site(u) : site);
                PrimaryLabel = "Save";
                TertiaryLabel = "Save as…";
                SecondaryLabel = "Cancel";
                break;
            case PermissionRequest pm:
                Title = $"{Site(pm.Origin)} wants to {Wants(pm.Permission)}";
                PrimaryLabel = "Allow once";
                TertiaryLabel = "Always allow on this site";
                SecondaryLabel = "Don't allow";
                break;
            case PopupNoticeRequest pop:
                Title = $"{site} wants to open a new window";
                Detail = pop.Url is { } pu ? PreviewViewModel.Display(pu) : null;
                PrimaryLabel = "Open";
                SecondaryLabel = "Don't open";
                break;
            default:
                Title = $"{site} is asking something this version can't show";
                PrimaryLabel = "Close";
                break;
        }
    }

    public BrowserDialog Dialog { get; }
    public BrowserDialogKind Kind => Dialog.Kind;

    public string Title { get; } = "";
    /// <summary>The page's own text (alert, confirm, prompt), shown as it is.</summary>
    public string? Message { get; }
    /// <summary>A secondary line: the realm, the accepted types, the size and site of a download, the popup's address.</summary>
    public string? Detail { get; }
    public string? Error { get; }
    public string? Warning { get; }
    public string? Note { get; }
    public string PrimaryLabel { get; } = "OK";
    public string? SecondaryLabel { get; }
    public string? TertiaryLabel { get; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasError => Error is not null;
    public bool HasWarning => Warning is not null;
    public bool HasNote => Note is not null;
    public bool HasSecondary => SecondaryLabel is not null;
    public bool HasTertiary => TertiaryLabel is not null;
    public bool IsCredentials => Kind == BrowserDialogKind.Credentials;
    public bool IsPrompt => Kind == BrowserDialogKind.Prompt;

    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _promptText = "";

    /// <summary>Cards waiting behind this one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueText), nameof(HasQueue))]
    private int _queued;

    public bool HasQueue => Queued > 0;
    public string QueueText => Queued == 1 ? "1 more" : $"{Queued} more";

    /// <summary>Which field takes the keyboard when the card opens: the user name (empty), else the password, the prompt, or the main button.</summary>
    public string FirstFocus => Kind switch
    {
        BrowserDialogKind.Credentials => UserName.Length == 0 ? "UserName" : "Password",
        BrowserDialogKind.Prompt => "Prompt",
        _ => "Primary",
    };

    [RelayCommand]
    private async Task Primary()
    {
        switch (Dialog.Request)
        {
            case CredentialRequest:
                Dialog.Complete(new CredentialAnswer(UserName, Password));
                break;
            case AlertRequest:
                Dialog.Complete(null);
                break;
            case ConfirmRequest or BeforeUnloadRequest or PopupNoticeRequest:
                Dialog.Complete(true);
                break;
            case PromptRequest:
                Dialog.Complete(PromptText);
                break;
            case FileChooserRequest f:
                if (Dialog.IsCompleted) return;
                var files = await _owner.PickFilesAsync(f);
                // Nothing chosen in the picker: the card stays, as Safari's input stays empty and can be tried again
                if (files is { Count: > 0 }) Dialog.Complete(files);
                break;
            case DownloadRequest:
                Dialog.Complete(DownloadAnswer.Save);
                break;
            case PermissionRequest:
                Dialog.Complete(PermissionAnswer.AllowOnce);
                break;
            default:
                Dialog.Cancel();
                break;
        }
    }

    [RelayCommand]
    private void Secondary()
    {
        switch (Dialog.Kind)
        {
            case BrowserDialogKind.Confirm or BrowserDialogKind.PopupNotice or BrowserDialogKind.BeforeUnload:
                Dialog.Complete(false);
                break;
            default:
                Dialog.Cancel();
                break;
        }
    }

    [RelayCommand]
    private void Tertiary()
    {
        switch (Dialog.Kind)
        {
            case BrowserDialogKind.Download:
                Dialog.Complete(DownloadAnswer.SaveAs);
                break;
            case BrowserDialogKind.Permission:
                Dialog.Complete(PermissionAnswer.AlwaysForSite);
                break;
        }
    }

    /// <summary>Esc: the answer nobody-answered gets (Cancel, Don't allow, Stay on a beforeunload card, OK on an alert).</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (Dialog.Kind == BrowserDialogKind.BeforeUnload) Dialog.Complete(false); // Esc keeps the user on the page
        else Dialog.Cancel();
    }

    /// <summary>"localhost:3000", "studio.example.com": who asks, as the address box shows the site.</summary>
    internal static string Site(Uri? url) => url is { IsAbsoluteUri: true, Host.Length: > 0 }
        ? url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}"
        : "This page";

    private static string Site(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) ? Site(u) : origin.Length > 0 ? origin : "This page";

    private static string HostPort(string host, int port, Uri? page)
    {
        var defaultPort = page?.Scheme == "http" ? 80 : 443;
        return port <= 0 || port == defaultPort ? host : $"{host}:{port}";
    }

    private static string Wants(PermissionKind kind) => kind switch
    {
        PermissionKind.Camera => "use your camera",
        PermissionKind.Microphone => "use your microphone",
        PermissionKind.CameraAndMicrophone => "use your camera and microphone",
        PermissionKind.Geolocation => "know your location",
        PermissionKind.Notifications => "show notifications",
        PermissionKind.Fullscreen => "go full screen",
        PermissionKind.StorageAccess => "use its cookies on this page",
        PermissionKind.Clipboard => "read your clipboard",
        _ => "use a feature of your computer",
    };

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
