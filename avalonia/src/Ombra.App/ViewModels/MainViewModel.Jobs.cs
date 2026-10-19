using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>Listing, stopping and reading omp's background jobs from the Background tasks pane.</summary>
public sealed partial class MainViewModel
{
    internal async Task<(IReadOnlyList<JobInfo>? Jobs, string? Error)> GetJobsAsync(CancellationToken ct)
    {
        if (IsClosing) return (null, Loc.T("the window is closing"));
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            return await Session.GetJobsAsync(linked.Token);
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { return (null, Loc.T("the window is closing")); }
    }

    internal async Task<string?> CancelJobAsync(string id)
    {
        if (IsClosing) return Loc.T("the window is closing");
        try { return await Session.CancelJobAsync(id, _cts.Token); }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { return Loc.T("the window is closing"); }
    }

    internal async Task<(JobOutputPage? Page, string? Error)> GetJobOutputAsync(string id, long? fromByte, CancellationToken ct)
    {
        if (IsClosing) return (null, Loc.T("the window is closing"));
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            return await Session.GetJobOutputAsync(id, fromByte, linked.Token);
        }
        catch (ObjectDisposedException) { return (null, Loc.T("the window is closing")); }
    }
}
