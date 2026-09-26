using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

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

    /// <summary>Puts <paramref name="text"/> on the clipboard (the view does it).</summary>
    internal void CopyText(string text) => CopyTextRequested?.Invoke(text);

    internal void OpenExternal(string url) => OpenUrlRequested?.Invoke(url);

    /// <summary>
    /// <c>/wt &lt;branch&gt;</c> (omp 18.2.0, builtin-lifecycle.ts): omp creates the branch from HEAD in a new linked
    /// worktree under worktree.base, carries the uncommitted changes over and moves this session (its file and working
    /// directory) there. The window then reloads the session from omp so it shows the new folder.
    /// </summary>
    internal async Task<(bool Ok, string Message)> MoveSessionToNewWorktreeAsync(string branch)
    {
        if (!CanRunOmpCommands) return (false, "omp isn't running. Start it, then try again.");
        if (IsRunning) return (false, "omp is replying. Try again when the reply ends.");
        var r = await RunOmpCommandAsync(branch.Length == 0 ? "/wt" : "/wt " + branch, TimeSpan.FromMinutes(3), Lifetime);
        if (!r.Ok) return (false, r.Error ?? "omp did not answer.");
        if (WorkspaceParsers.ParseMovedToWorktree(r.Output) is not { } moved)
            return (false, r.Output.Length > 0 ? r.Output : "omp did not create the worktree.");
        try
        {
            await _session.FollowSessionMoveAsync(moved.Path, Lifetime);
            AfterSessionChange();
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        return (true, r.Output);
    }

    /// <summary>A new session in another folder (a worktree): omp restarts there.</summary>
    internal async Task OpenFolderInNewSessionAsync(string folder)
    {
        try
        {
            IsSettingsOpen = false;
            await _session.OpenFolderAsync(folder, Lifetime);
            AfterSessionChange();
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
    }
}
