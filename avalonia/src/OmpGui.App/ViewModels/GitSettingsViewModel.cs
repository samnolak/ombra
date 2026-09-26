using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>GitHub CLI state as the page shows it.</summary>
public enum GhState { Checking, NotInstalled, SignedOut, SignedIn, Problem }

/// <summary>
/// Settings → Git and worktrees. The project's git state comes from the git CLI (status, remotes, worktree list);
/// GitHub from the gh CLI (omp's github tool, github.enabled, works only through gh); new worktrees through omp's
/// <c>/wt &lt;branch&gt;</c> (it moves this session there, uncommitted changes included); worktree.* and task.isolation.*
/// through <c>omp config set</c>. omp has no "environments": a session runs in a folder (a worktree is one) on this
/// computer, and its ssh tool reaches other machines.
/// </summary>
public sealed partial class GitSettingsViewModel : WorkspacePageViewModel
{
    private bool _applying;
    private string _savedBase = "";
    private string? _agentDir;

    public GitSettingsViewModel(MainViewModel main) : base(main, "git") { }

    // ── This project ──

    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private bool _hasProject;
    [ObservableProperty] private bool _isRepo;
    [ObservableProperty] private bool _isNotRepo;
    [ObservableProperty] private bool _gitMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepoError))]
    private string _repoError = "";

    public bool HasRepoError => RepoError.Length > 0;

    [ObservableProperty] private string _branchLabel = "";
    [ObservableProperty] private bool _isLinkedWorktree;
    [ObservableProperty] private string _worktreeOf = "";
    [ObservableProperty] private string _changesLabel = "";
    [ObservableProperty] private bool _isClean = true;
    [ObservableProperty] private string _upstreamLabel = "";
    [ObservableProperty] private string _remoteLabel = "";

    // ── GitHub ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GhSignedIn), nameof(GhSignedOut), nameof(GhNotInstalled), nameof(GhProblem), nameof(GhChecking))]
    private GhState _gh = GhState.Checking;

    public bool GhChecking => Gh == GhState.Checking;
    public bool GhSignedIn => Gh == GhState.SignedIn;
    public bool GhSignedOut => Gh == GhState.SignedOut;
    public bool GhNotInstalled => Gh == GhState.NotInstalled;
    public bool GhProblem => Gh == GhState.Problem;

    [ObservableProperty] private string _ghLabel = "Checking…";
    [ObservableProperty] private string _ghDetail = "";
    private string? _ghPath;

    [ObservableProperty] private bool _githubEnabled;
    [ObservableProperty] private bool _settingsLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsError))]
    private string _settingsError = "";

    public bool HasSettingsError => SettingsError.Length > 0;

    // ── Worktrees ──

    public ObservableCollection<WorktreeRowViewModel> Worktrees { get; } = [];

    [ObservableProperty] private bool _isCreating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitWorktree))]
    private bool _creatingBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewBranchProblem), nameof(HasNewBranchProblem), nameof(CreateHint), nameof(CanSubmitWorktree))]
    private string _newBranch = "";

    public string NewBranchProblem => WorkspaceParsers.BranchNameProblem(NewBranch.Trim()) ?? "";
    public bool HasNewBranchProblem => NewBranchProblem.Length > 0;

    public bool CanCreateWorktree => IsRepo && IsOmpRunning && !Main.IsRunning;

    /// <summary>The form's Create button: omp can take /wt now, the name is usable, nothing is being created.</summary>
    public bool CanSubmitWorktree => CanCreateWorktree && !HasNewBranchProblem && !CreatingBusy;

    public string CreateUnavailableHint => !IsRepo ? "" : !IsOmpRunning ? "omp isn't running: start it to make a worktree." : Main.IsRunning ? "omp is replying: you can make a worktree when the reply ends." : "";

    public string CreateHint
    {
        get
        {
            var branch = NewBranch.Trim() is { Length: > 0 } b ? b : "wt/<date-time>";
            var clean = WorktreeCleanSource ? " Then omp resets this folder to its last commit (Clean the project folder is on)." : " This folder keeps its files and changes.";
            return $"omp makes branch {branch} from the current commit in a new folder under {BaseLabel}, copies your uncommitted changes there, and moves this conversation into it.{clean}";
        }
    }

    // ── Worktree settings ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BaseChanged), nameof(BaseLabel), nameof(CreateHint))]
    private string _worktreeBase = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BaseLabel), nameof(CreateHint))]
    private string _defaultBase = "~/.omp/wt";

    public bool BaseChanged => WorktreeBase.Trim() != _savedBase;
    public string BaseLabel => _savedBase.Length > 0 ? _savedBase : DefaultBase;

    [ObservableProperty] private bool _worktreeClone = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateHint))]
    private bool _worktreeCleanSource;

    // ── Subagent isolation ──

    [ObservableProperty] private bool _isolationEnabled;
    [ObservableProperty] private string _isolationMerge = "patch";
    [ObservableProperty] private bool _isolationApply = true;

    public const string EnvironmentsNote =
        "omp has no separate environments: a session works in a folder on this computer (the project, or one of its worktrees), and its SSH hosts are the other machines it can reach.";

    public override async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        var ct = Main.Lifetime;
        try
        {
            await Task.WhenAll(LoadRepoAsync(ct), LoadGitHubAsync(ct), LoadSettingsAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            IsLoading = false;
            NotifyCreate();
        }
    }

    private void NotifyCreate()
    {
        OnPropertyChanged(nameof(CanCreateWorktree));
        OnPropertyChanged(nameof(CanSubmitWorktree));
        OnPropertyChanged(nameof(CreateUnavailableHint));
    }

    protected override void OnPhaseChanged()
    {
        NotifyCreate();
        foreach (var w in Worktrees) w.NotifyCanOpen();
    }

    // ── Repository ──

    private async Task LoadRepoAsync(CancellationToken ct)
    {
        var folder = Main.ProjectFolder;
        Folder = folder is null ? "" : GitProbe.Tilde(folder);
        HasProject = folder is not null;
        RepoError = "";
        if (folder is null)
        {
            IsRepo = IsNotRepo = GitMissing = false;
            Worktrees.Clear();
            return;
        }
        var (state, head, error) = await GitProbe.ReadHeadAsync(Main.RunTool, folder, ct);
        GitMissing = state == GitProbeState.GitMissing;
        IsNotRepo = state == GitProbeState.NotARepo;
        IsRepo = state == GitProbeState.Ok;
        if (state == GitProbeState.Failed) RepoError = error ?? "git failed";
        if (head is null)
        {
            Worktrees.Clear();
            return;
        }
        IsLinkedWorktree = head.IsLinkedWorktree;
        WorktreeOf = head.MainCheckout is { } m ? GitProbe.Tilde(m) : head.IsLinkedWorktree ? GitProbe.Tilde(head.CommonDir) : "";

        var status = await Main.RunTool(new ToolCommand("git", ["status", "--porcelain=v2", "--branch"], folder, TimeSpan.FromSeconds(20)), ct);
        if (!status.Ok)
        {
            RepoError = "git status failed: " + status.Message;
            return;
        }
        var s = WorkspaceParsers.ParseGitStatus(status.Stdout);
        BranchLabel = s.Detached ? $"No branch (at {head.ShortCommit ?? s.Commit?[..Math.Min(7, s.Commit.Length)]})"
            : s.Unborn ? $"{s.Branch} (no commits yet)" : s.Branch ?? head.Label ?? "";
        IsClean = s.Clean;
        ChangesLabel = ChangesText(s);
        UpstreamLabel = s.Upstream is null ? "No upstream branch" : (s.Ahead, s.Behind) switch
        {
            (0, 0) => $"Up to date with {s.Upstream}",
            (var a, 0) => $"{a} {Commits(a)} ahead of {s.Upstream}",
            (0, var b) => $"{b} {Commits(b)} behind {s.Upstream}",
            var (a, b) => $"{a} ahead, {b} behind {s.Upstream}",
        };
        RemoteLabel = await RemoteAsync(folder, s.Upstream, ct);
        await LoadWorktreesAsync(folder, ct);
    }

    private static string Commits(int n) => n == 1 ? "commit" : "commits";

    internal static string ChangesText(GitStatusInfo s)
    {
        if (s.Clean) return "No uncommitted changes";
        var parts = new List<string> { $"{s.ChangedFiles} {(s.ChangedFiles == 1 ? "file" : "files")} changed" };
        if (s.Staged > 0) parts.Add($"{s.Staged} staged");
        if (s.Untracked > 0) parts.Add($"{s.Untracked} untracked");
        if (s.Conflicts > 0) parts.Add($"{s.Conflicts} with conflicts");
        return string.Join(" · ", parts);
    }

    /// <summary>The upstream's remote (else origin, else the first) and its URL without credentials.</summary>
    private async Task<string> RemoteAsync(string folder, string? upstream, CancellationToken ct)
    {
        var r = await Main.RunTool(new ToolCommand("git", ["remote", "-v"], folder, GitProbe.Timeout), ct);
        if (!r.Ok) return "Remotes unreadable: " + r.Message;
        var remotes = new List<(string Name, string Url)>();
        foreach (var line in r.Stdout.Split('\n'))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !remotes.Any(x => x.Name == parts[0])) remotes.Add((parts[0], parts[1]));
        }
        if (remotes.Count == 0) return "No remote";
        var wanted = upstream?.Split('/')[0];
        var pick = remotes.FirstOrDefault(x => x.Name == wanted);
        if (pick.Name is null) pick = remotes.FirstOrDefault(x => x.Name == "origin");
        if (pick.Name is null) pick = remotes[0];
        return $"{pick.Name}  {WorkspaceParsers.RedactRemoteUrl(pick.Url)}" + (remotes.Count > 1 ? $"  (+{remotes.Count - 1} more)" : "");
    }

    private async Task LoadWorktreesAsync(string folder, CancellationToken ct)
    {
        var r = await Main.RunTool(new ToolCommand("git", ["worktree", "list", "--porcelain"], folder, GitProbe.Timeout), ct);
        Worktrees.Clear();
        if (!r.Ok)
        {
            RepoError = "git worktree list failed: " + r.Message;
            return;
        }
        foreach (var w in WorkspaceParsers.ParseWorktrees(r.Stdout).Where(w => !w.Bare))
            Worktrees.Add(new WorktreeRowViewModel(this, w, GitProbe.SamePath(w.Path, folder)));
    }

    // ── GitHub ──

    private async Task LoadGitHubAsync(CancellationToken ct)
    {
        Gh = GhState.Checking;
        GhLabel = "Checking…";
        GhDetail = "";
        _ghPath = await Task.Run(() => WorkspaceTools.Which("gh"), ct);
        var r = await Main.RunTool(new ToolCommand("gh", ["auth", "status"], Main.ProjectFolder, TimeSpan.FromSeconds(20)), ct);
        if (r.NotFound)
        {
            Gh = GhState.NotInstalled;
            GhLabel = "GitHub CLI not installed";
            GhDetail = "omp works with GitHub through the GitHub CLI (gh). Install it, then sign in.";
            return;
        }
        if (r.TimedOut)
        {
            Gh = GhState.Problem;
            GhLabel = "No answer from gh";
            GhDetail = r.Stderr;
            return;
        }
        var info = WorkspaceParsers.ParseGhAuth(r.Stdout + "\n" + r.Stderr);
        if (info.Primary is { } a)
        {
            Gh = GhState.SignedIn;
            GhLabel = $"Signed in as {a.Login}";
            GhDetail = a.Host == "github.com" ? "GitHub CLI (gh), github.com" : $"GitHub CLI (gh), {a.Host}";
        }
        else if (info.Accounts.FirstOrDefault() is { } bad)
        {
            Gh = GhState.Problem;
            GhLabel = $"Sign-in to {bad.Host} failed";
            GhDetail = bad.Problem ?? r.Message;
        }
        else
        {
            Gh = GhState.SignedOut;
            GhLabel = "Not signed in";
            GhDetail = "Sign in with the GitHub CLI so omp can reach your repositories.";
        }
    }

    /// <summary>Opens a terminal running <c>gh auth login</c> (interactive: browser or token); checks again when it ends.</summary>
    [RelayCommand]
    private void SignInToGitHub()
    {
        var t = Main.RunInTerminal("gh auth login", _ghPath ?? "gh", ["auth", "login"]);
        t.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalViewModel.HasExited) && t.HasExited) _ = LoadGitHubAsync(Main.Lifetime);
        };
    }

    [RelayCommand]
    private void InstallGh() => Main.OpenExternal("https://cli.github.com/");

    /// <summary>A shell in the project folder (to run git init, or anything git needs by hand).</summary>
    [RelayCommand]
    private void OpenTerminal()
    {
        Main.NewShellCommand.Execute(null);
        Main.IsSettingsOpen = false;
    }

    [RelayCommand]
    private Task CheckGitHubAgain() => LoadGitHubAsync(Main.Lifetime);

    // ── omp's settings ──

    private async Task LoadSettingsAsync(CancellationToken ct)
    {
        SettingsError = "";
        var agentDirTask = _agentDir is null ? Main.RunOmpCliAsync(["config", "path"], TimeSpan.FromSeconds(30), ct) : null;
        var (config, error) = await WorkspaceConfig.LoadAsync(Main, ct);
        if (agentDirTask is not null && await agentDirTask is { Ok: true } p && p.Stdout.Trim() is { Length: > 0 } dir) _agentDir = dir;
        DefaultBase = DefaultWorktreeBase(_agentDir);
        if (config is null)
        {
            SettingsError = error ?? "omp's settings could not be read.";
            SettingsLoaded = false;
            return;
        }
        _applying = true;
        try
        {
            GithubEnabled = config.Bool("github.enabled") ?? false;
            _savedBase = config.String("worktree.base") ?? "";
            WorktreeBase = _savedBase;
            WorktreeClone = config.Bool("worktree.clone") ?? true;
            WorktreeCleanSource = config.Bool("worktree.cleanSource") ?? false;
            IsolationEnabled = config.Bool("task.isolation.enabled") ?? false;
            IsolationMerge = config.String("task.isolation.merge") is "branch" ? "branch" : "patch";
            IsolationApply = config.Bool("task.isolation.apply") ?? true;
        }
        finally { _applying = false; }
        OnPropertyChanged(nameof(BaseChanged));
        OnPropertyChanged(nameof(BaseLabel));
        SettingsLoaded = true;
    }

    /// <summary>Where omp puts worktrees when worktree.base is unset: OMP_WORKTREE_DIR, else "wt" next to omp's agent
    /// folder (~/.omp/wt; a profile's own folder with a profile).</summary>
    internal static string DefaultWorktreeBase(string? agentDir)
    {
        if (Environment.GetEnvironmentVariable("OMP_WORKTREE_DIR") is { Length: > 0 } env && Path.IsPathRooted(env)) return GitProbe.Tilde(env);
        if (agentDir is not null && Path.GetFileName(agentDir.TrimEnd('/', '\\')) == "agent" && Path.GetDirectoryName(agentDir.TrimEnd('/', '\\')) is { } root)
            return GitProbe.Tilde(Path.Combine(root, "wt"));
        return "~/.omp/wt";
    }

    partial void OnGithubEnabledChanged(bool value) => SaveBool("github.enabled", value, v => GithubEnabled = v, "the GitHub tool");
    partial void OnWorktreeCloneChanged(bool value) => SaveBool("worktree.clone", value, v => WorktreeClone = v, "worktree cloning");
    partial void OnWorktreeCleanSourceChanged(bool value) => SaveBool("worktree.cleanSource", value, v => WorktreeCleanSource = v, "this setting");
    partial void OnIsolationEnabledChanged(bool value) => SaveBool("task.isolation.enabled", value, v => IsolationEnabled = v, "subagent isolation");
    partial void OnIsolationApplyChanged(bool value) => SaveBool("task.isolation.apply", value, v => IsolationApply = v, "this setting");

    private void SaveBool(string key, bool value, Action<bool> revert, string what)
    {
        if (_applying) return;
        _ = SaveAsync(key, value ? "true" : "false", ok =>
        {
            if (ok) return;
            _applying = true;
            revert(!value);
            _applying = false;
        }, v => v is { ValueKind: JsonValueKind.True or JsonValueKind.False } e && e.GetBoolean() != value, what);
    }

    private async Task SaveAsync(string key, string value, Action<bool> done, Func<JsonElement?, bool> overridden, string what)
    {
        try
        {
            var (ok, echoed, error) = await WorkspaceConfig.SetAsync(Main, key, value, Main.Lifetime);
            done(ok);
            if (!ok) Say("Not saved: " + error, error: true);
            else if (overridden(echoed)) Say(WorkspaceConfig.OverriddenNote(what), error: true);
            else SavedForNextStart();
        }
        catch (OperationCanceledException) when (Main.Lifetime.IsCancellationRequested) { }
    }

    [RelayCommand]
    private Task SetIsolationMerge(string? merge)
    {
        if (merge is not ("patch" or "branch") || merge == IsolationMerge) return Task.CompletedTask;
        var old = IsolationMerge;
        IsolationMerge = merge;
        return SaveAsync("task.isolation.merge", merge, ok => { if (!ok) IsolationMerge = old; },
            v => v is { ValueKind: JsonValueKind.String } e && e.GetString() != merge, "the merge strategy");
    }

    [RelayCommand]
    private Task SaveBase()
    {
        var value = WorktreeBase.Trim();
        if (value.Length > 0 && !(value.StartsWith('~') || Path.IsPathRooted(value)))
        {
            Say("Use a full path or one that starts with ~: omp ignores a relative worktree folder.", error: true);
            return Task.CompletedTask;
        }
        return SaveBaseAsync(value);
    }

    private async Task SaveBaseAsync(string value)
    {
        // An empty value would be "" (not unset): reset puts omp's default back.
        var r = value.Length == 0
            ? await Main.RunOmpCliAsync(["config", "reset", "worktree.base", "--json"], TimeSpan.FromSeconds(45), Main.Lifetime)
            : null;
        if (r is { Ok: false }) { Say("Not saved: " + WorkspaceConfig.CliError(r), error: true); return; }
        if (value.Length > 0)
        {
            var (ok, _, error) = await WorkspaceConfig.SetAsync(Main, "worktree.base", value, Main.Lifetime);
            if (!ok) { Say("Not saved: " + error, error: true); return; }
        }
        _savedBase = value;
        WorktreeBase = value;
        OnPropertyChanged(nameof(BaseChanged));
        OnPropertyChanged(nameof(BaseLabel));
        OnPropertyChanged(nameof(CreateHint));
        SavedForNextStart();
    }

    // ── New worktree (/wt) ──

    /// <summary>Opens the "New worktree" form (also from the header's branch chip).</summary>
    public void StartNewWorktree()
    {
        NewBranch = "";
        IsCreating = true;
        Message = "";
    }

    [RelayCommand]
    private void BeginCreate() => StartNewWorktree();

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    [RelayCommand]
    private async Task CreateWorktreeAsync()
    {
        var branch = NewBranch.Trim();
        if (HasNewBranchProblem || CreatingBusy) return;
        CreatingBusy = true;
        try
        {
            var (ok, message) = await Main.MoveSessionToNewWorktreeAsync(branch);
            if (!ok)
            {
                Say(message, error: true);
                return;
            }
            IsCreating = false;
            Say(WorkspaceParsers.ParseMovedToWorktree(message) is { } m
                ? $"This conversation now works in {GitProbe.Tilde(m.Path)}, on branch {m.Branch}."
                : message);
            Main.BranchChip.Refresh();
            await LoadAsync();
        }
        finally { CreatingBusy = false; }
    }

    // ── Worktree rows ──

    internal async Task OpenWorktreeAsync(WorktreeRowViewModel row) => await Main.OpenFolderInNewSessionAsync(row.Path);

    internal bool CanOpenWorktree(WorktreeRowViewModel row) => !row.IsCurrent && !row.Info.Prunable && !Main.IsRunning;

    /// <summary>Before asking: a worktree with changes, a locked one or this session's folder is never removed.</summary>
    internal async Task<string?> RemoveProblemAsync(WorktreeRowViewModel row)
    {
        if (row.IsMain) return "This is the repository's main folder.";
        if (row.IsCurrent) return "This conversation works in it. Open another folder first.";
        if (row.Info.Locked) return "It is locked" + (row.Info.LockReason is { Length: > 0 } why ? $" ({why})" : "") + ". Unlock it first: git worktree unlock.";
        if (row.Info.Prunable) return null;
        var r = await Main.RunTool(new ToolCommand("git", ["status", "--porcelain"], row.Path, TimeSpan.FromSeconds(20)), Main.Lifetime);
        if (!r.Ok) return "git can't read it: " + r.Message;
        var changes = r.Stdout.Split('\n').Count(l => l.Trim().Length > 0);
        return changes == 0 ? null : $"It has {changes} uncommitted {(changes == 1 ? "change" : "changes")}. Commit or discard {(changes == 1 ? "it" : "them")} first.";
    }

    internal async Task<string?> RemoveWorktreeAsync(WorktreeRowViewModel row)
    {
        if (Main.ProjectFolder is not { } folder) return "No project folder.";
        var args = row.Info.Prunable ? new[] { "worktree", "prune" } : ["worktree", "remove", row.Path];
        var r = await Main.RunTool(new ToolCommand("git", args, folder, TimeSpan.FromSeconds(60)), Main.Lifetime);
        if (!r.Ok) return r.Message;
        Say(row.Info.Prunable ? "Cleaned up the missing worktree." : $"Removed the worktree {GitProbe.Tilde(row.Path)}." + (row.Info.Branch is { } b ? $" Branch {b} is still there." : ""));
        await LoadAsync();
        return null;
    }
}

/// <summary>One worktree of the project in the list: open it in a new session, or remove it (after a check and a confirm).</summary>
public sealed partial class WorktreeRowViewModel(GitSettingsViewModel owner, GitWorktreeInfo info, bool isCurrent) : ObservableObject
{
    public GitWorktreeInfo Info { get; } = info;
    public string Path => Info.Path;
    public bool IsCurrent { get; } = isCurrent;
    public bool IsMain => Info.IsMain;

    public string Title => Info.Branch ?? (Info.Head is { Length: >= 7 } h ? $"No branch (at {h[..7]})" : "No branch");
    public string Location => GitProbe.Tilde(Info.Path);
    public string? Tag => IsCurrent ? "This session" : IsMain ? "Main folder" : Info.Prunable ? "Folder missing" : Info.Locked ? "Locked" : null;
    public bool HasTag => Tag is not null;

    public bool CanOpen => owner.CanOpenWorktree(this);
    public bool CanRemove => !IsMain && !IsCurrent;
    public string RemoveLabel => Info.Prunable ? "Clean up" : "Remove";
    public string ConfirmText => Info.Prunable
        ? "Its folder is gone. Remove git's record of it?"
        : "Delete this worktree's folder?" + (Info.Branch is { } b ? $" Branch {b} stays." : "");

    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private bool _busy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _problem = "";

    public bool HasProblem => Problem.Length > 0;

    internal void NotifyCanOpen() => OnPropertyChanged(nameof(CanOpen));

    [RelayCommand]
    private Task Open() => owner.OpenWorktreeAsync(this);

    [RelayCommand]
    private async Task AskRemoveAsync()
    {
        Busy = true;
        Problem = await owner.RemoveProblemAsync(this) ?? "";
        Busy = false;
        IsConfirming = Problem.Length == 0;
    }

    [RelayCommand]
    private void CancelRemove()
    {
        IsConfirming = false;
        Problem = "";
    }

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        Busy = true;
        var error = await owner.RemoveWorktreeAsync(this);
        Busy = false;
        IsConfirming = false;
        Problem = error ?? "";
    }
}
