using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;
using static Ombra.Tests.SessionAreaTests;

namespace Ombra.Tests;

/// <summary>Goal mode and side questions over the owned engine's RPC (FakeBtwGoal in FakeOmp): the goal bar and the BTW panel.</summary>
public sealed class BtwGoalUiTests
{
    private static T Named<T>(Control root, string name) where T : Control =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public async Task Slash_goal_sets_a_goal_that_the_bar_pauses_resumes_edits_and_clears()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        var bar = e.W.FindControl<Control>("GoalBarHost")!;
        await Until(() => vm.IsGoalAvailable, "omp reports goal mode");
        await Settle();
        Assert.False(bar.IsVisible);

        await Send(e, "/goal ship the release");
        await Until(() => vm.ShowGoalBar && vm.IsGoalActive, "goal set");
        await Settle();
        Assert.True(bar.IsVisible);
        Assert.Equal("ship the release", vm.GoalObjective);
        Assert.Equal(Loc.T("Working on it"), vm.GoalStatusText);
        Assert.Empty(vm.Rows.OfType<UserRowViewModel>()); // never sent to the model as a message

        await vm.PauseGoalCommand.ExecuteAsync(null);
        await Until(() => vm.IsGoalPaused, "paused");
        await Settle();
        Assert.Equal(Loc.T("Paused"), vm.GoalStatusText);
        Assert.Equal(Loc.F("{0}s", 5), vm.GoalElapsedText);
        Assert.True(Named<Button>(e.W, "GoalResumeButton").IsVisible);
        Assert.False(Named<Button>(e.W, "GoalPauseButton").IsVisible);

        // Edit: a new objective for the paused goal (omp: drop, create, and paused again)
        vm.StartGoalEditCommand.Execute(null);
        Assert.True(vm.IsEditingGoal);
        Assert.Equal("ship the release", vm.GoalDraft);
        vm.GoalDraft = "ship the release notes";
        await vm.SaveGoalCommand.ExecuteAsync(null);
        await Until(() => !vm.IsEditingGoal && vm.GoalObjective == "ship the release notes" && vm.IsGoalPaused, "edited");

        await vm.ResumeGoalCommand.ExecuteAsync(null);
        await Until(() => vm.IsGoalActive, "resumed");

        await vm.ClearGoalCommand.ExecuteAsync(null);
        await Until(() => !vm.ShowGoalBar, "cleared");
        await Settle();
        Assert.False(bar.IsVisible);
        Assert.Null(vm.Session.Snapshot().Goal);
        Assert.Equal(["goal create ship the release", "goal pause", "goal drop", "goal create ship the release notes", "goal pause", "goal resume", "goal drop"],
            Sent(e).Where(l => l.StartsWith("goal ", StringComparison.Ordinal)));
        await Close(e);
    }

    /// <summary>The bar's own buttons, pressed with the mouse where they are drawn, in English and in Russian (whose
    /// longer labels change the bar's layout): Pause pauses, Resume resumes, the status says so in the app's language.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task Pause_and_resume_clicked_on_the_bar_change_the_goal_in_each_language(string language)
    {
        var settings = Path.Combine(TestProcesses.TempDir("goal-lang-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settings);
        try
        {
            var e = await OpenAsync();
            var vm = e.Vm;
            await Until(() => vm.IsGoalAvailable, "omp reports goal mode");
            await Send(e, "/goal ship the release");
            await Until(() => vm.ShowGoalBar && vm.IsGoalActive, "goal set");
            await Settle();
            Assert.Equal(Loc.T("Working on it"), Named<TextBlock>(e.W, "GoalStatusLabel").Text);

            var pause = Named<Button>(e.W, "GoalPauseButton");
            Assert.Equal(Loc.T("Pause"), pause.Content);
            Click(e.W, pause);
            await Until(() => vm.IsGoalPaused, "paused by the click");
            await Settle();
            Assert.Equal(Loc.T("Paused"), vm.GoalStatusText);
            Assert.Equal(Loc.T("Paused"), Named<TextBlock>(e.W, "GoalStatusLabel").Text);

            var resume = Named<Button>(e.W, "GoalResumeButton");
            Assert.Equal(Loc.T("Resume"), resume.Content);
            Click(e.W, resume);
            await Until(() => vm.IsGoalActive, "resumed by the click");
            await Settle();
            Assert.Equal(Loc.T("Working on it"), Named<TextBlock>(e.W, "GoalStatusLabel").Text);
            Assert.Equal(["goal create ship the release", "goal pause", "goal resume"], Sent(e).Where(l => l.StartsWith("goal ", StringComparison.Ordinal)));
            await Close(e);
        }
        finally { Loc.Load(previous, settings); }
    }

    /// <summary>A left click at the button's centre, which must be the button the window hits there.</summary>
    private static void Click(Window w, Button b)
    {
        Assert.True(b.IsEffectivelyVisible && b.IsEffectivelyEnabled, $"{b.Name} visible={b.IsEffectivelyVisible} enabled={b.IsEffectivelyEnabled}");
        var at = b.TranslatePoint(new Avalonia.Point(b.Bounds.Width / 2, b.Bounds.Height / 2), w)!.Value;
        var hit = w.InputHitTest(at) as Avalonia.Visual;
        Assert.True(hit is not null && (ReferenceEquals(hit, b) || b.IsVisualAncestorOf(hit)), $"{b.Name} at {at} is covered by {hit}");
        w.MouseMove(at);
        w.MouseDown(at, Avalonia.Input.MouseButton.Left);
        w.MouseUp(at, Avalonia.Input.MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task A_side_question_is_answered_while_omp_works_and_can_be_stopped_and_followed_up()
    {
        var e = await OpenAsync("slow-stream");
        var vm = e.Vm;
        await Until(() => vm.IsBtwAvailable, "omp reports side questions");
        await Send(e, "a long reply");
        await Until(() => vm.Phase == SessionPhase.Running, "turn runs");
        await Settle();
        Assert.True(vm.ShowBtwChip);
        Assert.True(e.W.FindControl<Button>("BtwChip")!.IsVisible);

        // The chip opens the panel; the session's topics are read once
        vm.ToggleBtwCommand.Execute(null);
        await Until(() => vm.Session.Snapshot().BtwHistoryLoaded, "history read");
        await Settle();
        Assert.True(e.W.FindControl<Control>("BtwPanelHost")!.IsVisible);
        Assert.False(vm.HasBtwTopics);

        vm.BtwQuestion = "what file is this";
        await vm.AskBtwCommand.ExecuteAsync(null);
        await Until(() => vm.BtwTopics.Count == 1 && !vm.IsBtwRunning && vm.BtwTopics[0].Turns[0].Answer.Contains("Side answer about what file is this"), "answered");
        Assert.Equal("", vm.BtwQuestion);
        Assert.Equal(SessionPhase.Running, vm.Phase); // the main turn went on
        Assert.DoesNotContain(vm.Rows.OfType<UserRowViewModel>(), r => r.Text.Contains("what file is this"));

        // A long answer, stopped: the topic says so
        vm.BtwQuestion = "SLOW tell me everything";
        await vm.AskBtwCommand.ExecuteAsync(null);
        await Until(() => vm.IsBtwRunning && vm.BtwTopics.Count == 2 && vm.BtwTopics[^1].Turns[0].HasAnswer, "streaming");
        // Read top to bottom like the conversation: the new topic under the earlier one
        Assert.Equal(["what file is this", "SLOW tell me everything"], vm.BtwTopics.Select(t => t.Title));
        Assert.False(vm.AskBtwCommand.CanExecute(null));
        Assert.True(Named<Button>(e.W, "BtwCancelButton").IsVisible);
        await vm.CancelBtwCommand.ExecuteAsync(null);
        await Until(() => !vm.IsBtwRunning && vm.BtwTopics[^1].Turns[0].Outcome == Loc.T("Cancelled"), "stopped");

        // A follow-up in the first topic
        var first = vm.BtwTopics.Single(t => t.Title == "what file is this");
        first.FollowUpCommand.Execute(null);
        Assert.Same(first, vm.BtwFollowUp);
        vm.BtwQuestion = "and why";
        await vm.AskBtwCommand.ExecuteAsync(null);
        await Until(() => first.Turns.Count == 2 && !first.IsRunning && first.Turns[1].Answer.Contains("Side answer about and why"), "follow-up answered");
        Assert.Null(vm.BtwFollowUp);

        Assert.Equal(["get_btw_history", "btw what file is this", "btw SLOW tell me everything", "btw_cancel", "btw and why"],
            Sent(e).Where(l => l.StartsWith("btw", StringComparison.Ordinal) || l == "get_btw_history"));
        vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "stopped turn");
        await Close(e);
    }

    [AvaloniaFact]
    public async Task The_side_question_panel_and_its_draft_stay_with_the_chat_they_were_opened_in()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        await Until(() => vm.IsBtwAvailable, "omp reports side questions");
        await Send(e, "first chat");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Session.SessionFile is not null, "first chat has a file");
        var a = vm.Session;
        var fileA = a.SessionFile!;
        vm.ToggleBtwCommand.Execute(null);
        vm.BtwQuestion = "half a side question";
        await Settle();
        Assert.True(e.W.FindControl<Control>("BtwPanelHost")!.IsVisible);

        // Another chat: no panel, no draft of A's
        await vm.NewChatCommand.ExecuteAsync(null);
        await Until(() => vm.Session != a && vm.Phase == SessionPhase.Ready, "new chat");
        await Settle();
        Assert.False(vm.IsBtwOpen);
        Assert.Equal("", vm.BtwQuestion);
        Assert.False(e.W.FindControl<Control>("BtwPanelHost")!.IsVisible);

        // Back in A: its panel open again with its draft
        await Until(() => vm.Sessions.Any(x => x.Model.Path == fileA), "first chat listed");
        await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.First(x => x.Model.Path == fileA));
        await Until(() => vm.Session == a, "first chat shown");
        await Settle();
        Assert.True(vm.IsBtwOpen);
        Assert.Equal("half a side question", vm.BtwQuestion);
        Assert.True(e.W.FindControl<Control>("BtwPanelHost")!.IsVisible);
        await Close(e);
    }

    [AvaloniaFact]
    public async Task Without_goal_mode_over_rpc_slash_goal_and_btw_stay_in_omps_terminal()
    {
        var e = await OpenAsync("no-goal");
        var vm = e.Vm;
        await Settle();
        Assert.False(vm.IsGoalAvailable);
        Assert.False(vm.IsBtwAvailable);
        await Send(e, "/goal ship it");
        Assert.Equal(Loc.F("/{0} runs in omp's terminal", "goal"), vm.SessionCard!.Title);
        vm.CloseSessionCard();
        await Send(e, "/btw what now");
        Assert.Equal(Loc.F("/{0} runs in omp's terminal", "btw"), vm.SessionCard!.Title);
        Assert.DoesNotContain(Sent(e), l => l.StartsWith("goal", StringComparison.Ordinal) || l.StartsWith("btw", StringComparison.Ordinal));
        await Close(e);
    }

    [Fact]
    public void An_active_goals_time_goes_on_from_its_last_reading_and_a_paused_ones_stands()
    {
        var read = DateTimeOffset.UnixEpoch.AddHours(1);
        var active = new OmpGoal("g", "x", "active", 0, null, 60);
        Assert.Equal(TimeSpan.FromSeconds(75), MainViewModel.GoalElapsed(active, read, read.AddSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(60), MainViewModel.GoalElapsed(active with { Status = "paused" }, read, read.AddSeconds(15)));
    }

    [Fact]
    public void Wire_goal_and_btw_records_parse_and_a_dropped_goal_is_none()
    {
        using var state = System.Text.Json.JsonDocument.Parse("""
            {"goal":{"enabled":true,"mode":"active","goal":{"id":"1","objective":"o","status":"budget-limited","tokensUsed":1200,"tokenBudget":1000,"timeUsedSeconds":9,"createdAt":1,"updatedAt":2}}}
            """);
        var parsed = OmpSessionState.Parse(state.RootElement);
        Assert.True(parsed.GoalSupported);
        Assert.Equal(new OmpGoal("1", "o", "budget-limited", 1200, 1000, 9), parsed.Goal);
        using var none = System.Text.Json.JsonDocument.Parse("""{"goal":null}""");
        Assert.True(OmpSessionState.Parse(none.RootElement).GoalSupported);
        using var old = System.Text.Json.JsonDocument.Parse("{}");
        Assert.False(OmpSessionState.Parse(old.RootElement).GoalSupported);
        using var dropped = System.Text.Json.JsonDocument.Parse("""{"id":"1","objective":"o","status":"dropped","tokensUsed":0,"timeUsedSeconds":0}""");
        Assert.Null(OmpGoal.Parse(dropped.RootElement));

        using var record = System.Text.Json.JsonDocument.Parse("""
            {"id":"b1","question":"q1","answer":"a1","status":"complete","createdAt":5,"updatedAt":6,"leafId":null,
             "followUps":[{"question":"q2","answer":"","status":"error","error":"boom","createdAt":7,"updatedAt":8}]}
            """);
        var r = OmpBtwRecord.Parse(record.RootElement)!;
        Assert.Equal(["q1", "q2"], r.Turns.Select(t => t.Question));
        Assert.Equal("boom", r.Latest.Error);
        Assert.Equal("a1+", r.WithLatestAnswer("x").Turns[0].Answer + "+");
        Assert.Equal("x", r.WithLatestAnswer("x").Latest.Answer);
    }
}
