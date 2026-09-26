namespace OmpGui.ClientCore;

/// <summary>Following omp when it moves the open session to another folder (<c>/wt &lt;branch&gt;</c>, a worktree).</summary>
public sealed partial class SessionController
{
    /// <summary>
    /// omp moved the session (its file and working directory) to <paramref name="folder"/>: reload the session from omp
    /// in place (no restart) and remember the folder, so a later restart (recovery, approval mode) starts there too.
    /// </summary>
    public Task FollowSessionMoveAsync(string folder, CancellationToken ct = default) =>
        SessionChangeAsync(async conn =>
        {
            _request = _request with { WorkingDirectory = folder, ResumeSessionFile = null };
            await LoadSessionAsync(conn, folder, ct, newProcess: false).ConfigureAwait(false);
        }, ct);
}
