using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Send now: a queued follow-up's chip asks omp to read it at its next step (SessionController.SendNow.cs), and
/// Interrupt and send stops the running reply and sends the message box in its place.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly HashSet<(SessionController Controller, long Sequence)> _sendingNow = [];

    /// <summary>The chip stays until omp delivers the message; when omp already has it, the chip follows omp's events.</summary>
    internal async Task SendQueuedNowAsync(QueuedItemViewModel item)
    {
        var open = _open;
        if (!_sendingNow.Add((open.Controller, item.Model.Seq))) return;
        try
        {
            bool promoted;
            try { promoted = await open.Controller.SendQueuedNowAsync(item.Model, _cts.Token); }
            catch (Ombra.Rpc.RpcCommandException)
            {
                if (open == _open) ComposerMessage = Loc.T("This omp can’t send queued messages early (omp 18.4.6 or later can).");
                return;
            }
            catch (OmpNotRunningException e)
            {
                if (open == _open) ComposerMessage = e.Message;
                return;
            }
            ApplyIfShown(open);
            if (open == _open) ComposerMessage = promoted ? "" : Loc.T("omp already has that message.");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        finally
        {
            _sendingNow.Remove((open.Controller, item.Model.Seq));
        }
    }

    private bool CanInterruptAndSend() => !IsReadOnly && Phase == SessionPhase.Running && HasContent && !IsSigningIn;

    /// <summary>
    /// Stops the running reply and sends the message box instead (<c>abort_and_prompt</c>). The input is staged in the
    /// recovery journal first; when omp does not take it, text and images return to the message box.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInterruptAndSend))]
    private async Task InterruptAndSendAsync()
    {
        if (IsReadOnly) return;
        if (!PrepareComposerForSend()) return;
        if (HandleTerminalOnlyCommand(ComposerText)) return;
        var open = _open;
        if (open.Controller.IsRuntimeCommand(ComposerText.Trim()))
        {
            ComposerMessage = Loc.T("Commands can’t interrupt omp. Queue them, or stop omp first.");
            return;
        }
        using var admission = AdmitWorktreeChat(null);
        if (admission is null) return;
        string? submission = null;
        try
        {
            var content = TakeComposer();
            submission = StageSubmission(open, content, fromComposer: true);
            open.OptimisticAfterVersion = open.Controller.Snapshot().Version;
            Phase = SessionPhase.Running;
            var sent = await open.Controller.InterruptAndSendAsync(content.Text, content.Images, _cts.Token, submission);
            FinishSubmission(open, submission, sent, waitForDelivery: sent);
            SettleOptimisticPhase(open);
        }
        catch (Exception e) when (e is OmpNotRunningException or ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (submission is not null) FinishSubmission(open, submission, accepted: false);
            if (open == _open) ComposerMessage = e.Message;
            SettleOptimisticPhase(open);
        }
    }
}
