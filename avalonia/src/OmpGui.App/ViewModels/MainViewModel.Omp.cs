using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Shared ways to reach omp beyond the conversation, used by the panes and settings sections: its builtin slash
/// commands over RPC (output returned, not shown in the conversation) and its CLI subcommands.
/// </summary>
public partial class MainViewModel
{
    /// <summary>omp's command line for a CLI subcommand (same binary, profile and environment as the session).</summary>
    public Func<IReadOnlyList<string>, string?, OmpLaunchSpec>? OmpCliLaunch { get; init; }

    /// <summary>The open session's project folder, when it exists (null before omp reports it).</summary>
    public string? ProjectFolder => ProjectDirectory() ?? (Session.CurrentLaunch.WorkingDirectory is { } d && Directory.Exists(d) ? d : null);

    /// <summary>True while omp runs and can take commands.</summary>
    public bool CanRunOmpCommands => Session.Connection is not null && Phase is not (SessionPhase.Starting or SessionPhase.Stopped or SessionPhase.Faulted);

    /// <summary>Runs an omp builtin such as "/mcp list" or "/usage" and returns what it printed (see <see cref="OmpBuiltins"/>).</summary>
    public Task<SlashCommandResult> RunOmpCommandAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default) =>
        Session.RunSlashCommandAsync(command, timeout, ct);

    /// <summary>Runs <c>omp &lt;args&gt;</c> (e.g. "plugin", "list", "--json") in the project folder.</summary>
    public Task<OmpCliResult> RunOmpCliAsync(IReadOnlyList<string> args, TimeSpan? timeout = null, CancellationToken ct = default) =>
        OmpCliLaunch is { } launch
            ? OmpCli.RunAsync(launch(args, ProjectFolder), timeout, ct)
            : Task.FromResult(new OmpCliResult(-1, "", "The app couldn't start omp's command-line tool."));

    /// <summary>
    /// Starts every chat's omp again so it reads changed settings files (omp reads config.yml, mcp.json and plugins
    /// only at start): the idle ones now, each one that is working once its run ends, so a change never cuts a turn
    /// short (MainViewModel.OpenSessions.cs). True when the shown chat's omp restarted now; false when it is replying
    /// and restarts after that.
    /// </summary>
    public Task<bool> RestartOmpToApplyAsync() => RestartAllSessionsAsync();
}
