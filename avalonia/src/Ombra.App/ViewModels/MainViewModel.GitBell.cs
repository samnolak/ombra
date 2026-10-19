using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// The header's bell (<see cref="GitBellViewModel"/>): teammates' pushes to the open projects' remotes, the careful
/// update, and the team Inbox in one list; and the composer's hint when the shown chat's branch is behind.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Settings › General: watch the open projects' git remotes (git fetch every few minutes; default on).</summary>
    [ObservableProperty] private bool _watchGitRemotes = true;

    /// <summary>The watch runs in the window only (tests that build the view model alone never fetch).</summary>
    private bool _bellStarted;

    public GitBellViewModel Bell => field ??= NewBell();

    private GitBellViewModel NewBell()
    {
        var bell = new GitBellViewModel(
            new GitBellHost(WatchedFolders, GitWatchAllowed, BeginGitUpdate, AskAgentToUpdate, ShowIncomingAsync, ShowTeamInbox),
            Share.Inbox);
        bell.Changed += () => OnPropertyChanged(nameof(UpdateFirstHint));
        PropertyChanged += OnBellHintInputChanged;
        return bell;
    }

    private void OnBellHintInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComposerText) or nameof(ProjectFolder) or nameof(SessionTitle)) OnPropertyChanged(nameof(UpdateFirstHint));
    }

    /// <summary>The watch starts with the window: a first check shortly after, then every few minutes.</summary>
    private void StartGitBell()
    {
        _bellStarted = true;
        _ = Bell.RunAsync(Lifetime);
    }

    private void GitBellWindowFocused()
    {
        if (_bellStarted) Bell.OnWindowFocused();
    }

    private void GitBellTurnEnded(OpenSession open)
    {
        if (_bellStarted && open.Controller.CurrentLaunch.WorkingDirectory is { } folder) Bell.OnTurnEnded(folder);
    }

    partial void OnWatchGitRemotesChanged(bool value)
    {
        if (IsSettingsOpen) Persist(o => o with { WatchGitRemotes = value });
        if (_bellStarted) Bell.Check();
    }

    /// <summary>On, and strict network privacy off (it lets nothing out but the providers and the user's addresses).</summary>
    internal bool GitWatchAllowed() => WatchGitRemotes && LoadSettingsOrDefault().StrictNetworkPrivacy != true;

    /// <summary>The open chats' folders; only a trusted one (Workspace trust) is fetched without the user asking.</summary>
    private IReadOnlyList<WatchedFolder> WatchedFolders() =>
        [.. OpenChats
            .Where(c => c.CurrentLaunch.WorkingDirectory is not null)
            .Select(c => c.CurrentLaunch.WorkingDirectory!)
            .Distinct()
            .Select(f => new WatchedFolder(f, WorkspaceTrust is not { } trust || trust.Check(f).Status == ProjectTrustStatus.Trusted))];

    /// <summary>The tops (canonical) of the repositories being updated now.</summary>
    private readonly List<string> _gitUpdates = [];

    /// <summary>Whether <paramref name="folder"/> is inside a repository being updated now.</summary>
    private bool GitUpdating(string? folder) => _gitUpdates.Any(top => WorktreeContainsPath(top, folder));

    /// <summary>The shown chat's repository is being updated: Send waits (the hint over the message box says so).</summary>
    public bool GitUpdatingHere => GitUpdating(Session.CurrentLaunch.WorkingDirectory);

    /// <summary>
    /// Admission for an update of the repository at <paramref name="top"/>, on the UI thread before any await: null
    /// while a chat in it is working or has input waiting (or another update holds it); else a hold under which no new
    /// turn, command or queued input starts there (AdmitWorktreeChat, CanSend) until it is disposed.
    /// </summary>
    internal IDisposable? BeginGitUpdate(string top)
    {
        var canonical = SessionCatalog.ResolveLinks(Path.GetFullPath(top));
        if (GitUpdating(canonical)) return null;
        // Every chat of the shared engine, as worktree removal checks them (a run, input waiting, a deferred builtin)
        if (OpenChats.Any(c => WorktreeContainsPath(canonical, c.CurrentLaunch.WorkingDirectory)
            && c.Snapshot() is var s && (!SessionHost.IsIdle(s) || s.Queued.Count > 0 || c.ShouldQueueSubmission(""))))
            return null;
        _gitUpdates.Add(canonical);
        GitUpdateChanged();
        return new GitUpdateHold(this, canonical);
    }

    private void GitUpdateChanged()
    {
        OnPropertyChanged(nameof(GitUpdatingHere));
        OnPropertyChanged(nameof(UpdateFirstHint));
        SendCommand.NotifyCanExecuteChanged();
    }

    private sealed class GitUpdateHold(MainViewModel owner, string top) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._gitUpdates.Remove(top);
            owner.GitUpdateChanged();
        }
    }

    /// <summary>The careful update the agent should do, in the composer of a chat in that repository (shown first).</summary>
    private void AskAgentToUpdate(string top, string text)
    {
        if (!Bell.InRepo(Session.CurrentLaunch.WorkingDirectory, top)
            && _opens.Values.FirstOrDefault(o => Bell.InRepo(o.Controller.CurrentLaunch.WorkingDirectory, top)) is { } other)
            ShowSession(other);
        if (IsReadOnly) return;
        ComposerText = text;
        FocusComposerRequested?.Invoke();
    }

    /// <summary>The incoming commits and their diff, opened in the Files viewer.</summary>
    private async Task ShowIncomingAsync(string top, string target)
    {
        var text = await GitUpstream.IncomingPatchAsync(top, target, Lifetime);
        var dir = Path.Combine(Path.GetTempPath(), "ombra-incoming");
        Directory.CreateDirectory(dir);
        var name = string.Concat($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(top))}-{target}".Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-'));
        var file = Path.Combine(dir, name + ".diff");
        await File.WriteAllTextAsync(file, text, Lifetime);
        OpenTool(SidePane.Files);
        await Files.OpenFileAsync(file);
    }

    /// <summary>
    /// Over the composer: "Updating this repository from its remote…" while the bell's update holds it (Send waits), else
    /// "Branch main is 3 commits behind origin/main — update first?" while a message is typed.
    /// </summary>
    public string? UpdateFirstHint =>
        GitUpdatingHere ? Loc.T("Updating this repository from its remote: Send is available again when it finishes.")
        : !string.IsNullOrWhiteSpace(ComposerText) && Bell.BehindIn(Session.CurrentLaunch.WorkingDirectory) is { State: { } s } && _dismissedHint != (s.Top, s.Behind)
            ? Loc.Plural(s.Behind, "Branch {1} is {0} commit behind {2} — update first?", "Branch {1} is {0} commits behind {2} — update first?", s.Branch, s.Upstream)
            : null;

    private (string, int)? _dismissedHint;

    [RelayCommand]
    private void OpenBellFromHint() => Bell.IsOpen = true;

    [RelayCommand]
    private void DismissUpdateFirstHint()
    {
        if (Bell.BehindIn(Session.CurrentLaunch.WorkingDirectory) is { State: { } s }) _dismissedHint = (s.Top, s.Behind);
        OnPropertyChanged(nameof(UpdateFirstHint));
    }
}
