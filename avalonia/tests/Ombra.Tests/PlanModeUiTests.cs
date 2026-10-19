using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using static Ombra.Tests.SessionAreaTests;

namespace Ombra.Tests;

/// <summary>Plan mode over the owned engine's RPC (FakePlanMode in FakeOmp): the toggle, the plan card, approve and reject.</summary>
public sealed class PlanModeUiTests
{
    [AvaloniaFact]
    public async Task The_proposed_plan_shows_as_a_card_and_is_rejected_with_feedback_then_approved()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        var card = e.W.FindControl<Border>("PlanReviewCard")!;
        await Until(() => vm.IsPlanModeAvailable, "omp reports plan mode");
        Assert.False(card.IsVisible);

        // The permission menu's entry turns plan mode on; the pill says so
        await vm.TogglePlanModeCommand.ExecuteAsync(null);
        await Until(() => vm.IsPlanMode && e.Vm.Session.Snapshot().PlanMode.Enabled, "plan mode on");
        Assert.True(e.W.FindControl<Button>("PlanModeChip")!.IsVisible);

        // A message in plan mode: omp drafts a plan and proposes it; the card shows its document
        await Send(e, "add a greeting");
        await Until(() => vm.HasPlanReview, "plan proposed");
        await Settle();
        Assert.True(card.IsVisible);
        Assert.Equal("Demo", vm.PlanReviewTitle);
        Assert.Equal("local://demo-plan.md", vm.PlanReviewPath);
        Assert.Contains("1. Add the greeting.", vm.PlanReviewMarkdown);
        Assert.True(e.W.FindControl<Ombra.App.Controls.MarkdownView>("PlanReviewDocument")!.IsVisible);

        // Reject with what to change: omp keeps planning and proposes a revised plan
        e.W.FindControl<Button>("RejectPlanButton")!.Command!.Execute(null);
        Assert.True(vm.IsWritingPlanFeedback);
        vm.PlanFeedbackText = "Document it.";
        await vm.RejectPlanCommand.ExecuteAsync(null);
        await Until(() => vm.HasPlanReview && vm.PlanReviewMarkdown.Contains("3. Document it."), "revised plan");
        Assert.True(vm.IsPlanMode);
        Assert.False(vm.IsWritingPlanFeedback);

        // Approve: plan mode ends and omp carries the plan out
        await vm.ApprovePlanCommand.ExecuteAsync(null);
        await Until(() => !vm.HasPlanReview && !vm.IsPlanMode && vm.Phase == SessionPhase.Ready
            && vm.Rows.OfType<AssistantRowViewModel>().Any(r => r.Text.Contains("Executing the approved plan")), "plan executed");
        await Settle();
        Assert.False(card.IsVisible);
        Assert.False(e.W.FindControl<Button>("PlanModeChip")!.IsVisible);
        Assert.Equal(["plan_mode enter", "prompt (planning) add a greeting", "plan_mode reject Document it.", "plan_mode approve"],
            Sent(e).Where(l => l.StartsWith("plan_mode", StringComparison.Ordinal) || l.StartsWith("prompt (planning)", StringComparison.Ordinal)));
        await Close(e);
    }

    [AvaloniaFact]
    public async Task Approve_waits_while_omp_works_and_acts_only_on_the_plan_shown()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        await Until(() => vm.IsPlanModeAvailable, "omp reports plan mode");
        await vm.TogglePlanModeCommand.ExecuteAsync(null);
        await Until(() => vm.Session.Snapshot().PlanMode.Enabled, "plan mode on");
        await Send(e, "add a greeting");
        await Until(() => vm.HasPlanReview && vm.Phase == SessionPhase.Ready, "plan proposed");
        var shown = vm.Session.Snapshot().PlanMode.Review!;

        // Another planning message while the card shows: omp works, and the card cannot approve or reject meanwhile
        await Send(e, "SLOW look further");
        await Until(() => vm.Phase == SessionPhase.Running, "planning turn runs");
        Assert.True(vm.HasPlanReview);
        Assert.False(vm.ApprovePlanCommand.CanExecute(null));
        Assert.False(vm.RejectPlanCommand.CanExecute(null));
        Assert.False(e.W.FindControl<Button>("ApprovePlanButton")!.IsEffectivelyEnabled);
        // Even if it got through, omp refuses: the turn could still propose another version
        Assert.Contains("still working", await vm.Session.ApprovePlanAsync(shown.Revision));
        await Until(() => vm.Phase == SessionPhase.Ready && vm.ApprovePlanCommand.CanExecute(null), "turn over, approve again");

        // A plan other than the one waiting is refused: approval never runs a version the user did not see
        Assert.Contains("no longer the one", await vm.Session.ApprovePlanAsync("r-old"));
        Assert.True(vm.Session.Snapshot().PlanMode.Enabled);

        await vm.ApprovePlanCommand.ExecuteAsync(null);
        await Until(() => !vm.IsPlanMode && vm.Rows.OfType<AssistantRowViewModel>().Any(r => r.Text.Contains("Executing the approved plan")), "approved");
        Assert.Contains($"plan_mode approve", Sent(e));
        await Close(e);
    }

    [AvaloniaFact]
    public async Task Without_plan_mode_over_rpc_slash_plan_stays_in_omps_terminal_and_shift_tab_skips_it()
    {
        var e = await OpenAsync("no-plan-mode");
        var vm = e.Vm;
        await Settle();
        Assert.False(vm.IsPlanModeAvailable);
        Assert.False(e.W.FindControl<Button>("PlanModeMenuItem")!.IsVisible);
        await Send(e, "/plan add a cache");
        Assert.Equal(Loc.F("/{0} runs in omp's terminal", "plan"), vm.SessionCard!.Title);
        Assert.DoesNotContain(Sent(e), l => l.StartsWith("plan_mode", StringComparison.Ordinal));
        await Close(e);
    }
}
