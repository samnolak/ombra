using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>Stopping and messaging omp's subagents from the Background tasks pane.</summary>
public sealed partial class MainViewModel
{
    internal async Task<string?> CancelSubagentAsync(string id)
    {
        if (IsClosing) return Loc.T("the window is closing");
        try { return await Session.CancelSubagentAsync(id, _cts.Token); }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { return Loc.T("the window is closing"); }
    }

    internal async Task<string?> SteerSubagentAsync(string id, string message)
    {
        if (IsClosing) return Loc.T("the window is closing");
        try { return await Session.SteerSubagentAsync(id, message, _cts.Token); }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { return Loc.T("the window is closing"); }
    }
}
