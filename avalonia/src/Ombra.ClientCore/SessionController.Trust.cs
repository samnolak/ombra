using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>How a logical session's project folder was treated when it last started (<see cref="WorkspaceTrust"/>).
/// A new instance for every start, so a window can tell one start from the next.</summary>
public sealed class ProjectLaunch(string folder, bool restricted)
{
    private volatile IReadOnlyList<OmpProjectItem>? _leftOut;

    /// <summary>The folder omp started in.</summary>
    public string Folder { get; } = folder;

    /// <summary>Started with <see cref="WorkspaceTrust.UntrustedProjectFlag"/>: none of the folder's own code loaded.</summary>
    public bool Restricted { get; } = restricted;

    /// <summary>What omp reported it left out of the folder (restricted starts, once omp answered; empty when it
    /// reported nothing or the start was not restricted). Null until then.</summary>
    public IReadOnlyList<OmpProjectItem>? LeftOut => Restricted ? _leftOut : [];

    internal void Report(IReadOnlyList<OmpProjectItem>? items) => _leftOut ??= items ?? [];
}

/// <summary>Workspace trust for one logical session: whether it runs restricted, and restarting it after a decision.</summary>
public sealed partial class SessionController
{
    private (OmpLaunchSpec Spec, ProjectLaunch Launch)? _projectLaunch;
    private readonly Lock _projectLaunchGate = new();

    /// <summary>What the running (or last) start did with the project folder; null before the first start.</summary>
    public ProjectLaunch? ProjectLaunch
    {
        get
        {
            if (Volatile.Read(ref _launchSpec) is not { } spec) return null;
            lock (_projectLaunchGate)
            {
                if (_projectLaunch is { } known && ReferenceEquals(known.Spec, spec)) return known.Launch;
                var launch = new ProjectLaunch(Path.GetFullPath(spec.WorkingDirectory ?? Environment.CurrentDirectory),
                    spec.SessionArguments.Contains(WorkspaceTrust.UntrustedProjectFlag));
                _projectLaunch = (spec, launch);
                return launch;
            }
        }
    }

    /// <summary>The first state of a start: what omp left out of an untrusted folder.</summary>
    private void NoteProjectReport(OmpSessionState state) => ProjectLaunch?.Report(state.UntrustedProject);

    /// <summary>
    /// Starts this logical session again on the same conversation after the folder was trusted or revoked, or after
    /// omp moved it to another folder; the launch builder decides anew whether the folder's own code loads. Other
    /// chats keep running as they are.
    /// </summary>
    public Task ReloadProjectTrustAsync(CancellationToken ct = default) =>
        RestartAsync(_request with { ResumeSessionFile = ExistingSessionFile() }, ct);
}
