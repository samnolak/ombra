using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>The Plan pane's edits go to the shown session's omp as its new todo list (SessionController.AskTodos.cs).</summary>
public sealed partial class MainViewModel
{
    /// <summary>False when omp did not take the list (the session's notice says why); the pane keeps omp's list.</summary>
    internal async Task<bool> SaveTodosAsync(IReadOnlyList<TodoPhase> phases)
    {
        var open = _open;
        try
        {
            var saved = await open.Controller.SetTodosAsync(phases, _cts.Token);
            ApplyIfShown(open);
            return saved;
        }
        catch (OmpNotRunningException) { return false; }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return false; }
    }
}
