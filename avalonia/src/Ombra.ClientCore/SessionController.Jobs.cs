using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Background jobs over the owned engine's RPC (rpc-jobs.ts): what runs (<c>get_jobs</c>), stopping one
/// (<c>cancel_job</c>) and a bounded read of its output from a byte cursor (<c>get_job_output</c>).
/// </summary>
public sealed partial class SessionController
{
    private static readonly TimeSpan JobRequestTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Bytes one output read asks for: the pane keeps a bounded tail, never the whole output.</summary>
    public const int JobOutputReadBytes = 64 * 1024;
    /// <summary>Completed once an omp answered a job command "Unknown command" without an id (ConversationState.Jobs.cs).</summary>
    private readonly TaskCompletionSource _jobsUnsupported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// This chat's background jobs, running first; an error when omp could not list them. An omp without the job
    /// commands lists none, quietly: its id-less "Unknown command" ends the wait at once and later calls send nothing.
    /// </summary>
    public async Task<(IReadOnlyList<JobInfo>? Jobs, string? Error)> GetJobsAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return (null, "omp is not running");
        if (Snapshot() is { JobsSupported: false }) return ([], null);
        if (Snapshot().SigningIn is { } provider) return (null, $"omp is busy with the sign-in to {provider}");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var answer = conn.RequestAsync("get_jobs", timeout: JobRequestTimeout, ct: request.Token);
        if (await Task.WhenAny(answer, _jobsUnsupported.Task).ConfigureAwait(false) != answer)
        {
            request.Cancel();
            try { await answer.ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or RpcConnectionClosedException) { }
            return ([], null);
        }
        try
        {
            var r = await answer.ConfigureAwait(false);
            if (!r.Success && r.Error?.StartsWith("Unknown command", StringComparison.Ordinal) == true)
            {
                Mutate(s => s.JobsSupported = false);
                return ([], null);
            }
            if (!r.Success) return (null, r.Error ?? "omp could not list them");
            return r.Data is { } d && OmpJobs.ParseJobs(d) is { } jobs ? (jobs, null) : (null, "omp returned an invalid job list");
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Stops one running job. Null on success (also when it had already ended), else why not.</summary>
    public async Task<string?> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        if (_omp?.Connection is not { } conn) return "omp is not running";
        if (Snapshot().SigningIn is { } provider) return $"omp is busy with the sign-in to {provider}";
        try
        {
            var r = await conn.RequestAsync("cancel_job", w => w.WriteString("jobId", jobId), JobRequestTimeout, ct).ConfigureAwait(false);
            return r.Success ? null : r.Error ?? "omp could not stop it";
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Reads up to <see cref="JobOutputReadBytes"/> of a job's output from <paramref name="fromByte"/>, or its tail when
    /// null. Errors leave the caller's cursor unchanged.
    /// </summary>
    public async Task<(JobOutputPage? Page, string? Error)> GetJobOutputAsync(string jobId, long? fromByte = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        if (fromByte is { } f) ArgumentOutOfRangeException.ThrowIfNegative(f);
        if (_omp?.Connection is not { } conn) return (null, "omp is not running");
        if (Snapshot().SigningIn is { } provider) return (null, $"omp is busy with the sign-in to {provider}");
        try
        {
            var r = await conn.RequestAsync("get_job_output", w =>
            {
                w.WriteString("jobId", jobId);
                if (fromByte is { } from) w.WriteNumber("fromByte", from);
                w.WriteNumber("maxBytes", JobOutputReadBytes);
            }, JobRequestTimeout, ct).ConfigureAwait(false);
            if (!r.Success) return (null, r.Error ?? "omp could not read it");
            return r.Data is { } d && OmpJobs.ParseOutput(d) is { } page && page.JobId == jobId
                ? (page, null) : (null, "omp returned an invalid job output");
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return (null, e.Message);
        }
    }
}
