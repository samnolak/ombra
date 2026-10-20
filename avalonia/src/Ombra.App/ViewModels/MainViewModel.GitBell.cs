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
            new GitBellHost(WatchedFolders, GitWatchAllowed, BeginGitUpdate, AgentFilesIn, AskAgentToUpdate, ShowIncomingAsync, ShowTeamInbox),
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
    internal void StartGitBell()
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
    /// while another update holds it, or while a chat in it is working or has input waiting unless
    /// <paramref name="underRunningTurn"/> (the bell's Update now, weighed against the turn's files); else a hold under
    /// which no new turn, command or queued input starts there (AdmitWorktreeChat, CanSend) until it is disposed.
    /// </summary>
    internal IDisposable? BeginGitUpdate(string top, bool underRunningTurn = false)
    {
        var canonical = SessionCatalog.ResolveLinks(Path.GetFullPath(top));
        if (GitUpdating(canonical)) return null;
        // Every chat of the shared engine, as worktree removal checks them (a run, input waiting, a deferred builtin)
        if (!underRunningTurn && OpenChats.Any(c => WorktreeContainsPath(canonical, c.CurrentLaunch.WorkingDirectory) && Working(c, c.Snapshot())))
            return null;
        _gitUpdates.Add(canonical);
        GitUpdateChanged();
        return new GitUpdateHold(this, canonical);
    }

    private static bool Working(SessionController c, SessionSnapshot s) => !SessionHost.IsIdle(s) || s.Queued.Count > 0 || c.ShouldQueueSubmission("");

    /// <summary>
    /// The files (relative to <paramref name="top"/>, '/') that the turns working now in that repository changed or
    /// opened: what their tool calls changed or were pointed at (a read's selector cut off). A subagent's own calls
    /// are not in its parent's chat; what it changed is uncommitted, and the update's risk counts it.
    /// </summary>
    internal IReadOnlyCollection<string> AgentFilesIn(string top)
    {
        var root = SessionCatalog.ResolveLinks(Path.GetFullPath(top));
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in OpenChats)
        {
            if (c.CurrentLaunch.WorkingDirectory is not { } folder || !WorktreeContainsPath(root, folder)) continue;
            var s = c.Snapshot();
            if (!Working(c, s)) continue;
            var cwd = SessionCatalog.ResolveLinks(Path.GetFullPath(s.Cwd ?? folder));
            // The run's calls (those since its last message while it waits on input without a run)
            var since = s.RunStartedAt is null ? LastIndexOfUser(s.Items) : -1;
            for (var i = since + 1; i < s.Items.Count; i++)
            {
                if (s.Items[i] is not ToolItem t || s.RunStartedAt is { } run && !(t.StartedAt >= run)) continue;
                foreach (var named in ConversationState.NamedFiles(t))
                {
                    string full;
                    try { full = Path.IsPathRooted(named) ? SessionCatalog.ResolveLinks(named) : Path.GetFullPath(Path.Combine(cwd, named)); }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                    foreach (var path in WithoutSelector(full))
                    {
                        var rel = Path.GetRelativePath(root, path);
                        if (rel != "." && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                            files.Add(rel.Replace('\\', '/'));
                    }
                }
            }
        }
        return files;
    }

    private static int LastIndexOfUser(IReadOnlyList<TranscriptItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i] is UserItem) return i;
        return -1;
    }

    /// <summary>The path, and without a read's selector (<c>a.cs:50-80</c>, <c>a.cs:raw</c>) when its name has one.</summary>
    private static IEnumerable<string> WithoutSelector(string path)
    {
        yield return path;
        var name = Path.GetFileName(path);
        if (name.IndexOf(':') is > 0 and var colon) yield return path[..(path.Length - name.Length + colon)];
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
