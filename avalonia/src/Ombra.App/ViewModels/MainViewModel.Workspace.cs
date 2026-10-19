using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Computer use, Git and worktrees, SSH hosts (settings pages) and the branch chip in the header: git, gh and ssh on
/// this computer, omp's /computer, /wt and /ssh over RPC, and omp's settings through its CLI.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Runs git, gh, ssh and xdpyinfo (tests replace it with scripted answers).</summary>
    public Func<ToolCommand, CancellationToken, Task<ToolResult>> RunTool { get; init; } = WorkspaceTools.RunAsync;

    private ComputerSettingsViewModel? _computerSettings;
    private GitSettingsViewModel? _gitSettings;
    private SshSettingsViewModel? _sshSettings;
    private BranchChipViewModel? _branchChip;

    public ComputerSettingsViewModel ComputerSettings => _computerSettings ??= new ComputerSettingsViewModel(this);
    public GitSettingsViewModel GitSettings => _gitSettings ??= new GitSettingsViewModel(this);
    public SshSettingsViewModel SshSettings => _sshSettings ??= new SshSettingsViewModel(this);
    public BranchChipViewModel BranchChip => _branchChip ??= new BranchChipViewModel(this);

    // UI-thread operation scopes bridge asynchronous starts/moves and Git's asynchronous preparation.
    // A null admission path means /wt has not reported its destination yet.
    private readonly List<WorktreeOperation> _worktreeOperations = [];

    private sealed class WorktreeOperation(MainViewModel owner, string? path, bool removal) : IDisposable
    {
        public string? Path { get; } = path;
        public bool Removal { get; } = removal;
        public void Dispose() => owner._worktreeOperations.Remove(this);
    }

    internal static bool WorktreeContainsPath(string root, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        for (var directory = SessionCatalog.ResolveLinks(Path.GetFullPath(cwd));
             directory is not null; directory = Path.GetDirectoryName(directory))
            if (SessionCatalog.SamePath(directory, root)) return true;
        return false;
    }

    internal IDisposable? ReserveWorktreeRemoval(string path)
    {
        var canonical = SessionCatalog.ResolveLinks(Path.GetFullPath(path));
        // Raw builtins can be deferred behind an agent run. Do not infer their eventual
        // destination: wait for all hosted work and queued input to settle instead.
        if (OpenChats.Any(c => c.Snapshot() is var s
            && (!SessionHost.IsIdle(s) || s.Queued.Count > 0 || c.ShouldQueueSubmission("")))) return null;
        if (_worktreeOperations.Any(o => o.Path is null || WorktreeContainsPath(canonical, o.Path)
            || o.Removal && WorktreeContainsPath(o.Path, canonical))) return null;
        var operation = new WorktreeOperation(this, canonical, removal: true);
        _worktreeOperations.Add(operation);
        return operation;
    }

    private IDisposable? AdmitWorktreeChat(string? cwd)
    {
        var canonical = cwd is null ? null : SessionCatalog.ResolveLinks(Path.GetFullPath(cwd));
        if (_worktreeOperations.Any(o => o.Removal && (canonical is null || WorktreeContainsPath(o.Path!, canonical))))
        {
            ShowBrief("IconAlert", Loc.T("Worktree removal in progress"),
                Loc.T("Wait for worktree removal to finish before sending input, running commands or opening a conversation there."));
            return null;
        }
        // The bell's update holds the repository while git writes its files (MainViewModel.GitBell.cs)
        if (GitUpdating(cwd ?? Session.CurrentLaunch.WorkingDirectory))
        {
            ShowBrief("IconAlert", Loc.T("Updating from the remote"),
                Loc.T("The repository is being updated from its remote. Send when it finishes: your message stays in the box."));
            return null;
        }
        var operation = new WorktreeOperation(this, canonical, removal: false);
        _worktreeOperations.Add(operation);
        return operation;
    }

    /// <summary>Ends with the window: work started by the settings pages stops with it.</summary>
    internal CancellationToken Lifetime
    {
        get
        {
            try { return _cts.Token; }
            catch (ObjectDisposedException) { return new CancellationToken(true); }
        }
    }

    /// <summary>A command in a new terminal tab in the project folder (e.g. <c>gh auth login</c>), shown at once.</summary>
    internal TerminalViewModel RunInTerminal(string title, string file, IReadOnlyList<string> args)
    {
        var t = new TerminalViewModel(TerminalKind.Shell, title, file, args, ProjectDirectory(), new Dictionary<string, string>());
        Add(t);
        IsSettingsOpen = false;
        return t;
    }

    /// <summary>Copy feedback is emitted only after the view confirms the clipboard write.</summary>
    internal void CopyText(string text) => _ = CopyTextAsync(text);

    internal async Task<bool> CopyTextAsync(string text, string? title = null, string message = "", bool notify = true)
    {
        if (text.Length == 0 || CopyTextRequested is not { } copy || !await copy(text)) return false;
        if (notify) ShowBrief("IconCopy", title ?? Loc.T("Copied"), message);
        return true;
    }

    internal void OpenExternal(string url) => OpenUrlRequested?.Invoke(url);

    /// <summary>
    /// <c>/wt &lt;branch&gt;</c> (omp 18.2.0, builtin-lifecycle.ts): omp creates the branch from HEAD in a new linked
    /// worktree under worktree.base, carries the uncommitted changes over and moves this session (its file and working
    /// directory) there. The window then reloads the session from omp so it shows the new folder.
    /// </summary>
    internal async Task<(bool Ok, string Message)> MoveSessionToNewWorktreeAsync(string branch)
    {
        if (!CanRunOmpCommands) return (false, Loc.T("omp isn't running. Start it, then try again."));
        if (IsRunning) return (false, Loc.T("omp is replying. Try again when the reply ends."));
        using var admission = AdmitWorktreeChat(null);
        if (admission is null) return (false, Loc.T("Worktree removal is in progress. Try again when it finishes."));
        // This chat's omp throughout: another chat may be shown by the time omp has made the worktree
        var open = _open;
        var session = open.Controller;
        var r = await session.RunSlashCommandAsync(branch.Length == 0 ? "/wt" : "/wt " + branch, TimeSpan.FromMinutes(3), Lifetime);
        if (!r.Ok) return (false, r.Error ?? Loc.T("omp did not answer."));
        if (WorkspaceParsers.ParseMovedToWorktree(r.Output) is not { } moved)
            return (false, r.Output.Length > 0 ? r.Output : Loc.T("omp did not create the worktree."));
        try
        {
            await session.FollowSessionMoveAsync(moved.Path, Lifetime);
            await FollowTrustAfterMoveAsync(open, moved.Path); // MainViewModel.WorkspaceTrust.cs
            ApplyIfShown(open);
            RequestCatalogRefresh();
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        return (true, r.Output);
    }

    /// <summary>A new session in another folder (a worktree): a new omp starts there, beside the chats already open.</summary>
    internal async Task OpenFolderInNewSessionAsync(string folder)
    {
        IsSettingsOpen = false;
        await OpenProjectAsync(folder); // MainViewModel.OpenSessions.cs
    }
}
