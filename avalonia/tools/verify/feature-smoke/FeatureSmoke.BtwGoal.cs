#if OMBRA_FEATURE_SMOKE
using Avalonia.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] BtwGoalSections() => [("btw", Btw), ("goal", Goal)];

        /// <summary>A button inside the goal bar or the BTW panel (their own name scopes).</summary>
        private Button? Named(string name) =>
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>().FirstOrDefault(b => b.Name == name);

        /// <summary>
        /// /btw against the real engine: a side question asked through the BTW chip while a run works, its answer
        /// streamed by the stand-in model (BTWQ), a long one stopped, a follow-up, and the session's history read back.
        /// </summary>
        private async Task Btw()
        {
            Check("btw-available", await Until(() => vm.IsBtwAvailable, 20), $"available={vm.IsBtwAvailable}");
            await Send("LONGTASK 0.6 id=btw");
            Check("btw-run-started", await Until(() => vm.IsRunning, 20));
            var chip = window.FindControl<Button>("BtwChip");
            Check("btw-chip-while-running", await Until(() => chip?.IsEffectivelyVisible == true, 10), $"show={vm.ShowBtwChip}");
            if (chip is not null) Click(chip);
            Check("btw-panel-opens", await Until(() => vm.IsBtwOpen && vm.Session.Snapshot().BtwHistoryLoaded && FocusedIs("BtwQuestionBox"), 15),
                $"open={vm.IsBtwOpen} loaded={vm.Session.Snapshot().BtwHistoryLoaded} focus={Focused()}");

            vm.BtwQuestion = "BTWQ heron what is the agent reading";
            await Task.Delay(200);
            Key("\r", 0x24);
            var answered = await Until(() => vm.BtwTopics.FirstOrDefault() is { IsRunning: false } t && t.Turns[0].Answer.Contains("BTW-ANSWER heron", StringComparison.Ordinal), 60);
            Check("btw-answered", answered, $"topics={vm.BtwTopics.Count} answer='{vm.BtwTopics.FirstOrDefault()?.Turns[0].Answer}' notices=[{Notices()}]");
            Check("btw-main-run-goes-on", vm.IsRunning && !UserRowsContain("BTWQ heron"), $"running={vm.IsRunning}");
            await Task.Delay(400);
            Shot("20-btw-answered-dark");

            vm.BtwQuestion = "BTWQ egret SLOW";
            var ask = ButtonFor(vm.AskBtwCommand);
            if (Check("btw-ask-button", ask is not null)) Click(ask!);
            Check("btw-streaming", await Until(() => vm.IsBtwRunning && vm.BtwTopics.FirstOrDefault()?.Turns[0].Answer.Length > 20, 40),
                $"running={vm.IsBtwRunning} answer='{vm.BtwTopics.FirstOrDefault()?.Turns[0].Answer}'");
            await Task.Delay(300);
            Shot("20-btw-streaming-dark");
            var stop = Named("BtwCancelButton");
            if (Check("btw-stop-button", stop?.IsEffectivelyVisible == true)) Click(stop!);
            Check("btw-cancelled", await Until(() => !vm.IsBtwRunning && vm.BtwTopics.FirstOrDefault()?.Turns[0].Outcome == Loc.T("Cancelled"), 15),
                $"running={vm.IsBtwRunning} outcome='{vm.BtwTopics.FirstOrDefault()?.Turns[0].Outcome}'");

            var heron = vm.BtwTopics.FirstOrDefault(t => t.Title.Contains("heron", StringComparison.Ordinal));
            var follow = heron is null ? null : ButtonFor(heron.FollowUpCommand);
            if (Check("btw-follow-up-button", follow is not null)) Click(follow!);
            Check("btw-follow-up-armed", await Until(() => ReferenceEquals(vm.BtwFollowUp, heron), 5));
            vm.BtwQuestion = "BTWQ ibis and then";
            await Task.Delay(200);
            FocusBtw();
            Key("\r", 0x24);
            Check("btw-follow-up-answered", await Until(() => heron is { IsRunning: false, Turns.Count: 2 } && heron.Turns[1].Answer.Contains("BTW-ANSWER ibis", StringComparison.Ordinal), 60),
                $"turns={heron?.Turns.Count} answer='{heron?.Turns.LastOrDefault()?.Answer}'");
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            Shot("20-btw-follow-up-light");

            // The session's history, read back from omp (its BTW sidecar): both topics, the follow-up in the first
            var error = await vm.Session.LoadBtwHistoryAsync();
            var records = vm.Session.Snapshot().Btw;
            Check("btw-history", error is null && records.Count >= 2 && records.Any(r => r.Turns.Count == 2 && r.Turns[0].Question.Contains("heron", StringComparison.Ordinal)),
                $"error={error} records=[{string.Join(" | ", records.Select(r => $"{r.Id}:{r.Turns.Count}:{r.Latest.Status}"))}]");

            // Esc in the panel closes it and never stops the run
            FocusBtw();
            await Task.Delay(200);
            Key("\u001b", 0x35);
            Check("btw-esc-closes-panel", await Until(() => !vm.IsBtwOpen, 5) && vm.IsRunning, $"open={vm.IsBtwOpen} running={vm.IsRunning}");
        }

        private void FocusBtw() => window.FindControl<Views.Session.BtwPanel>("BtwPanelHost")?.FindControl<TextBox>("BtwQuestionBox")?.Focus();

        /// <summary>
        /// /goal against the real engine: set from the message box, the bar's time goes on, Pause/Resume, Edit, a restart
        /// of omp brings the goal back from get_state (paused, as omp reattaches it), Clear.
        /// </summary>
        private async Task Goal()
        {
            Check("goal-available", await Until(() => vm.IsGoalAvailable, 20), $"available={vm.IsGoalAvailable}");
            await Send("/goal ship the smoke release");
            Check("goal-set", await Until(() => vm.ShowGoalBar && vm.IsGoalActive && vm.GoalObjective == "ship the smoke release", 20),
                $"bar={vm.ShowGoalBar} status='{vm.GoalStatusText}' objective='{vm.GoalObjective}' notices=[{Notices()}]");
            var elapsed = vm.GoalElapsedText;
            Check("goal-time-goes-on", await Until(() => vm.GoalElapsedText != elapsed, 5), $"{elapsed} -> {vm.GoalElapsedText}");
            await Task.Delay(300);
            Shot("21-goal-active-dark");

            var pause = Named("GoalPauseButton");
            if (Check("goal-pause-button", pause?.IsEffectivelyVisible == true)) Click(pause!);
            Check("goal-paused", await Until(() => vm.IsGoalPaused && vm.GoalStatusText == Loc.T("Paused"), 10), $"status='{vm.GoalStatusText}'");
            var resume = Named("GoalResumeButton");
            if (Check("goal-resume-button", await Until(() => resume?.IsEffectivelyVisible == true && resume.IsEffectivelyEnabled, 5))) Click(resume!);
            Check("goal-resumed", await Until(() => vm.IsGoalActive, 10), $"status='{vm.GoalStatusText}'");

            var edit = Named("GoalEditButton");
            if (Check("goal-edit-button", await Until(() => edit?.IsEffectivelyVisible == true && edit.IsEffectivelyEnabled, 5))) Click(edit!);
            Check("goal-editor", await Until(() => vm.IsEditingGoal && FocusedIs("GoalDraftBox"), 5), $"editing={vm.IsEditingGoal} focus={Focused()}");
            vm.GoalDraft = "ship the smoke release notes";
            await Task.Delay(300);
            Shot("21-goal-edit-dark");
            Key("\r", 0x24);
            Check("goal-edited", await Until(() => !vm.IsEditingGoal && vm.GoalObjective == "ship the smoke release notes" && vm.IsGoalActive, 15),
                $"objective='{vm.GoalObjective}' status='{vm.GoalStatusText}'");

            // omp restarts on this session: the goal comes back from get_state, paused as omp reattaches a goal it
            // was not running (it was active before)
            var pid = vm.Session.ProcessId;
            await Send("/restart");
            Check("goal-after-restart", await Until(() => vm.Phase == SessionPhase.Ready && vm.IsGoalPaused && vm.GoalObjective == "ship the smoke release notes", 90),
                $"pid {pid} -> {vm.Session.ProcessId} phase={vm.Phase} status='{vm.GoalStatusText}'");
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            Shot("21-goal-after-restart-light");

            var clear = Named("GoalClearButton");
            if (Check("goal-clear-button", await Until(() => clear?.IsEffectivelyVisible == true && clear.IsEffectivelyEnabled, 5))) Click(clear!);
            Check("goal-cleared", await Until(() => !vm.ShowGoalBar && vm.Session.Snapshot().Goal is null, 10), $"bar={vm.ShowGoalBar}");
            await Task.Delay(400);
            Shot("21-goal-cleared-light");
        }
    }
}
#endif
