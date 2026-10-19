using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>
/// Plan mode (Claude Code's plan mode, omp's <c>/plan</c>; SessionController.PlanMode.cs): omp explores read-only
/// and writes a plan; the plan it proposes shows as a card over the message box with its document, Approve, and
/// Reject with what to change. The mode is a step of Shift+Tab (Ask → Accept edits → Plan → Bypass) and an entry of
/// the permission menu; the permission mode stays as it was under it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>This chat's omp has plan mode over RPC and the chat can change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApprovePlanCommand), nameof(RejectPlanCommand), nameof(TogglePlanModeCommand))]
    private bool _isPlanModeAvailable;
    [ObservableProperty] private bool _isPlanMode;
    [ObservableProperty] private bool _hasPlanReview;
    [ObservableProperty] private string _planReviewTitle = "";
    /// <summary>The proposed plan, Markdown.</summary>
    [ObservableProperty] private string _planReviewMarkdown = "";
    [ObservableProperty] private string _planReviewPath = "";
    [ObservableProperty] private bool _isWritingPlanFeedback;
    [ObservableProperty] private string _planFeedbackText = "";
    /// <summary>An approve, reject or mode change is on its way to omp: the card's buttons wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApprovePlanCommand), nameof(RejectPlanCommand), nameof(TogglePlanModeCommand))]
    private bool _isPlanActionRunning;

    private OmpPlanReview? _appliedPlanReview;

    private void ApplyPlanMode(SessionSnapshot s)
    {
        IsPlanModeAvailable = s.PlanModeSupported == true && !IsReadOnly;
        // While a change is on its way the toggle shows the choice; omp's answer confirms it
        if (!IsPlanActionRunning) IsPlanMode = s.PlanMode.Enabled;
        var review = s.PlanMode.Review;
        if (review == _appliedPlanReview) return;
        _appliedPlanReview = review;
        ApprovePlanCommand.NotifyCanExecuteChanged();
        RejectPlanCommand.NotifyCanExecuteChanged();
        HasPlanReview = review is not null;
        PlanReviewTitle = review is null ? "" : PlanTitle(review.Title);
        PlanReviewMarkdown = review?.Content ?? "";
        PlanReviewPath = review?.PlanFilePath ?? "";
        // Another plan (a revision, or none): the words written for the previous one do not apply to it
        IsWritingPlanFeedback = false;
        PlanFeedbackText = "";
    }

    /// <summary>omp's plan slug ("auth-token-refresh") as a title, as omp names the session after an approved plan.</summary>
    internal static string PlanTitle(string slug)
    {
        var spaced = slug.Replace('-', ' ').Replace('_', ' ').Trim();
        return spaced.Length == 0 ? Loc.T("Plan") : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private bool CanChangePlan() => !IsPlanActionRunning && IsPlanModeAvailable;

    /// <summary>The permission menu's Plan mode entry, and the plan chip (leaves plan mode).</summary>
    [RelayCommand(CanExecute = nameof(CanChangePlan))]
    private Task TogglePlanModeAsync() => SetPlanModeAsync(!IsPlanMode);

    private async Task SetPlanModeAsync(bool on)
    {
        if (!IsPlanModeAvailable || IsPlanMode == on) return;
        // Shown at once, so a quick second Shift+Tab steps on from it; omp's answer (or the next state) confirms it
        IsPlanMode = on;
        if (!await RunPlanActionAsync(Loc.T("Could not change plan mode"), ct => Session.SetPlanModeAsync(on, ct)))
            IsPlanMode = !on;
    }

    /// <summary>
    /// Approve and reject act on the plan shown, and never while omp works: a planning turn still running may propose
    /// another version (omp refuses both then too, and any other revision than the one shown).
    /// </summary>
    private bool CanReviewPlan() => CanChangePlan() && !IsRunning && _appliedPlanReview is not null;

    [RelayCommand(CanExecute = nameof(CanReviewPlan))]
    private Task ApprovePlanAsync()
    {
        if (_appliedPlanReview is not { } shown) return Task.CompletedTask;
        return RunPlanActionAsync(Loc.T("Could not approve the plan"), ct => Session.ApprovePlanAsync(shown.Revision, ct));
    }

    [RelayCommand]
    private void StartPlanFeedback() => IsWritingPlanFeedback = true;

    [RelayCommand]
    private void CancelPlanFeedback()
    {
        IsWritingPlanFeedback = false;
        PlanFeedbackText = "";
    }

    /// <summary>Rejects the plan; the written feedback (if any) becomes omp's next planning turn.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewPlan))]
    private Task RejectPlanAsync()
    {
        if (_appliedPlanReview is not { } shown) return Task.CompletedTask;
        var feedback = PlanFeedbackText.Trim();
        return RunPlanActionAsync(Loc.T("Could not reject the plan"), ct => Session.RejectPlanAsync(shown.Revision, feedback.Length > 0 ? feedback : null, ct));
    }

    /// <summary>False when omp refused or could not be asked (a brief note says why).</summary>
    private async Task<bool> RunPlanActionAsync(string failureTitle, Func<CancellationToken, Task<string?>> action)
    {
        if (IsClosing || IsReadOnly) return false;
        IsPlanActionRunning = true;
        try
        {
            if (await action(_cts.Token) is not { } error) return true;
            ShowUrgentBrief("IconAlert", failureTitle, error);
            return false;
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { return false; }
        finally { IsPlanActionRunning = false; }
    }
}
