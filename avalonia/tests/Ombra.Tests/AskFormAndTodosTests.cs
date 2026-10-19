using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>
/// The ask form (<c>set_ask_dialog</c>: every question of one ask tool call as one <c>ask</c> request, answered in
/// question order) and Plan pane edits (<c>set_todos</c>), over the fake omp that answers like rpc-mode.ts.
/// </summary>
public sealed class AskFormAndTodosTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-18T12:00:00Z");

    private static RpcFrame F(string json)
    {
        var e = JsonDocument.Parse(json).RootElement.Clone();
        return new RpcFrame(e.GetProperty("type").GetString()!, null, e, Stopwatch.GetTimestamp(), json.Length + 1);
    }

    private const string AskFrame = """
        {"type":"extension_ui_request","id":"a1","method":"ask","timeout":30000,"questions":[
          {"id":"color","question":"Which color?","header":"Color","options":[{"label":"Red","description":"warm"},{"label":"Blue"}],"recommended":0},
          {"id":"extras","question":"Which extras?","options":[{"label":"Tests"},{"label":"Docs","preview":"README.md"}],"multi":true}]}
        """;

    private static (SessionController Session, string Log) Fake(string scenario)
    {
        var log = Path.Combine(TestProcesses.TempDir("ask-todos"), "commands.log");
        var spec = TestProcesses.Fake(scenario);
        return (new SessionController(spec with { Environment = new Dictionary<string, string?> { ["FAKE_OMP_COMMAND_LOG"] = log } }), log);
    }

    private static string Read(string log) => File.Exists(log) ? File.ReadAllText(log) : "";

    // ── ClientCore ──

    [Fact]
    public void An_ask_request_is_one_dialog_with_every_question()
    {
        var s = new ConversationState();
        s.Apply(F(AskFrame), T);
        var d = Assert.Single(s.Snapshot().Dialogs);
        Assert.Equal(DialogKind.Ask, d.Kind);
        Assert.Equal("Which color?", d.Headline);
        Assert.NotNull(d.Deadline);
        Assert.Equal(["color", "extras"], d.Questions.Select(q => q.Id));
        var (color, extras) = (d.Questions[0], d.Questions[1]);
        Assert.Equal(("Color", false, (int?)0), (color.Header, color.Multi, color.Recommended));
        Assert.Equal([new AskOption("Red", "warm", null), new AskOption("Blue", null, null)], color.Options);
        Assert.Equal((null, true, (int?)null), (extras.Header, extras.Multi, extras.Recommended));
        Assert.Equal("README.md", extras.Options[1].Preview);
    }

    [Fact]
    public void An_ask_request_with_a_question_it_cannot_read_is_declined_not_shown()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"extension_ui_request","id":"bad","method":"ask","questions":[{"id":"x","question":"ok?","options":[]},{"question":"no id","options":[]}]}"""), T);
        Assert.Empty(s.Snapshot().Dialogs);
        Assert.Contains("bad", s.DialogsToCancel);
    }

    [Fact]
    public async Task The_session_opts_in_and_sends_every_answer_in_question_order()
    {
        var (session, log) = Fake("ask-form");
        await using var s = session;
        await s.StartAsync();
        await s.PromptAsync("ask me");
        var d = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "ask form")).Dialogs[0];
        Assert.Equal(DialogKind.Ask, d.Kind); // the fake sends the form only after set_ask_dialog (else a select)
        Assert.True(await s.AnswerDialogAsync(d.Id, new DialogAnswer.Asked(
            [new AskAnswer("color", ["Blue"], null), new AskAnswer("extras", ["Tests", "Docs"], "Lint")])));
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Items.OfType<ToolItem>().Any(t => t.Status == ToolStatus.Succeeded), Wait, "tool done");
        Assert.Equal("color=[Blue] extras=[Tests,Docs]+Lint", done.Items.OfType<ToolItem>().Single().Output);
        var line = Read(log).Split('\n').Single(l => l.StartsWith("ask ", StringComparison.Ordinal));
        using var wire = JsonDocument.Parse(line["ask ".Length..]);
        Assert.Equal("""[{"id":"color","selectedOptions":["Blue"]},{"id":"extras","selectedOptions":["Tests","Docs"],"customInput":"Lint"}]""",
            wire.RootElement.GetProperty("answers").GetRawText());
        Assert.Contains(done.Items.OfType<NoticeItem>(), n => n.Text == "Answered by you — Which color?");
    }

    [Fact]
    public async Task Todos_set_here_replace_omps_list_and_keep_each_tasks_details_and_notes()
    {
        var (session, log) = Fake("normal");
        await using var s = session;
        await s.StartAsync();
        IReadOnlyList<TodoPhase> plan =
        [
            new("Build", [new TodoTask("Write the form", "completed", Details: "tabs and multi-select", Notes: ["from the review"]), new TodoTask("Ship", "pending")]),
        ];
        Assert.True(await s.SetTodosAsync(plan));
        Assert.Equal(plan[0].Tasks, s.Snapshot().Todos.Single().Tasks);
        Assert.Contains("""set_todos [{"name":"Build","tasks":[{"content":"Write the form","status":"completed","details":"tabs and multi-select","notes":["from the review"]},{"content":"Ship","status":"pending"}]}]""", Read(log));
        // get_state confirms omp's list afterwards (no older answer replaces it)
        await Task.Delay(300);
        Assert.Equal(plan[0].Tasks, s.Snapshot().Todos.Single().Tasks);
    }

    // ── The ask form in the window ──

    private static async Task<(MainWindow W, MainViewModel Vm)> OpenAsync(string scenario)
    {
        var s = new SessionController(TestProcesses.Fake(scenario));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Window w, Func<T, bool> where) where T : Control
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var found = w.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && where(c));
            if (found is not null) return found;
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"no visible {typeof(T).Name} matched");
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Thread.Sleep(10);
        }
    }

    private static void Click(Button b)
    {
        Assert.True(b.IsEffectivelyVisible && b.IsEffectivelyEnabled, $"button {b.Name} not clickable");
        b.Command!.Execute(b.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_ask_form_has_a_tab_per_question_takes_several_extras_and_sends_all_answers_at_once()
    {
        var (w, vm) = await OpenAsync("ask-form");
        vm.ComposerText = "ask me";
        vm.SendCommand.Execute(null);
        await Until(() => vm.CurrentDialog?.IsAsk == true, "ask form");
        var d = vm.CurrentDialog!;
        var form = d.Ask!;
        Assert.Equal(Loc.F("omp has {0} questions", 2), d.Title);
        Assert.Equal(["Color", "Extras"], form.Questions.Select(q => q.TabLabel));
        Assert.Equal(Loc.F("Question {0} of {1}", 1, 2), form.Position);
        Assert.True(form.Questions[0].Options[0].Recommended);
        Assert.False(form.Questions[0].Options[0].IsChecked); // a suggestion, never preselected
        Assert.Equal(Loc.T("Choose one"), form.Questions[0].Hint);
        Assert.Equal(Loc.T("Choose any that apply"), form.Questions[1].Hint);

        // Submit with the first question open: nothing is sent, the form says what is missing
        Click(Find<Button>(w, b => b.Name == "AskNextButton"));
        Assert.Same(form.Questions[1], form.Current);
        Click(Find<Button>(w, b => b.Name == "AskSubmitButton"));
        Assert.Same(form.Questions[0], form.Current);
        Assert.Equal(Loc.F("Answer “{0}” too, or type your own answer.", "Color"), form.Error);
        Assert.Same(d, vm.CurrentDialog);

        // One choice moves on; the extras take several, and the user's own words besides
        Click(Find<Button>(w, b => b.DataContext is AskChoiceViewModel { Label: "Blue" }));
        Assert.Same(form.Questions[1], form.Current);
        Assert.False(form.HasError);
        Click(Find<Button>(w, b => b.DataContext is AskChoiceViewModel { Label: "Tests" }));
        Click(Find<Button>(w, b => b.DataContext is AskChoiceViewModel { Label: "Docs" }));
        Find<TextBox>(w, t => t.Name == "AskOwnAnswer").Text = "Lint";
        Assert.True(form.Questions.All(q => q.IsAnswered));
        Click(Find<Button>(w, b => b.Name == "AskSubmitButton"));

        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        Assert.Contains("color=[Blue] extras=[Tests,Docs]+Lint", vm.Rows.OfType<ToolRowViewModel>().Single().Output);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public void A_single_choice_takes_one_answer_and_digits_pick_options_of_the_question_shown()
    {
        var form = new AskFormViewModel([
            new AskQuestion("q", "Which?", null, [new AskOption("A", null, null), new AskOption("B", null, null)], false, null),
            new AskQuestion("m", "Which ones?", null, [new AskOption("X", null, null), new AskOption("Y", null, null)], true, null),
        ], _ => Task.CompletedTask);
        Assert.Equal(Loc.F("Question {0}", 1), form.Questions[0].TabLabel);
        Assert.True(form.TryPick(1));
        Assert.Same(form.Questions[1], form.Current); // a one-choice answer moves on
        form.PreviousCommand.Execute(null);
        Assert.True(form.TryPick(2));
        Assert.Equal(["B"], form.Questions[0].Options.Where(o => o.IsChecked).Select(o => o.Label));
        form.Questions[0].CustomText = "C";
        Assert.DoesNotContain(form.Questions[0].Options, o => o.IsChecked); // own words replace the option
        form.Questions[0].Options[0].ChooseCommand.Execute(null);
        Assert.Equal("", form.Questions[0].CustomText);
        Assert.True(form.TryPick(1) && form.TryPick(2)); // the multi-select shown now
        Assert.Equal(["X", "Y"], form.Questions[1].Options.Where(o => o.IsChecked).Select(o => o.Label));
        Assert.False(form.TryPick(3));
    }

    [AvaloniaFact]
    public async Task Cancel_closes_the_form_and_omp_hears_no_answer()
    {
        var (w, vm) = await OpenAsync("ask-form");
        vm.ComposerText = "ask me";
        vm.SendCommand.Execute(null);
        await Until(() => vm.CurrentDialog?.IsAsk == true, "ask form");
        Click(Find<Button>(w, b => b.Name == "AskCancelButton"));
        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        Assert.Contains("cancelled", vm.Rows.OfType<ToolRowViewModel>().Single().Output);
        await vm.DisposeAsync();
        w.Close();
    }

    // ── Plan edits ──

    private static TodoPhase P(string name, params (string Content, string Status)[] tasks) =>
        new(name, [.. tasks.Select(t => new TodoTask(t.Content, t.Status))]);

    [AvaloniaFact]
    public async Task Plan_edits_send_the_whole_list_and_the_pane_shows_omps_answer()
    {
        var sent = new List<IReadOnlyList<TodoPhase>>();
        var gate = new TaskCompletionSource<bool>();
        var plan = new PlanViewModel(save: async phases => { sent.Add(phases); return await gate.Task; });
        plan.Apply([P("Build", ("Read", "completed"), ("Write", "in_progress")), P("Check", ("Test", "pending"))], "/s", T);
        Assert.True(plan.IsEditable);
        var write = plan.Phases[0].Tasks[1];
        Assert.True(write.CanEdit);

        // Done: sent, and nothing else is taken while omp has not answered
        var done = write.ToggleDoneCommand.ExecuteAsync(null);
        Assert.False(plan.CanEdit);
        await plan.Phases[1].Tasks[0].DeleteCommand.ExecuteAsync(null);
        Assert.Single(sent);
        Assert.Equal([("Read", "completed"), ("Write", "completed")], sent[0][0].Tasks.Select(t => (t.Content, t.Status)));
        Assert.Equal("Check", sent[0][1].Name);
        gate.SetResult(true);
        await done;
        Assert.True(plan.CanEdit);
        // The pane shows what omp answers, not the request
        Assert.Equal(Loc.T("In progress"), plan.Phases[0].Tasks[1].StatusName);

        plan.Apply([P("Build", ("Read", "completed"), ("Write", "completed")), P("Check", ("Test", "pending"))], "/s", T.AddMinutes(1));
        await plan.Phases[0].Tasks[1].MoveUpCommand.ExecuteAsync(null);
        Assert.Equal(["Write", "Read"], sent[^1][0].Tasks.Select(t => t.Content));
        await plan.Phases[1].Tasks[0].DeleteCommand.ExecuteAsync(null);
        Assert.Equal(["Build"], sent[^1].Select(p => p.Name)); // an emptied phase is not sent
        await plan.Phases[1].Tasks[0].SetStatusCommand.ExecuteAsync("abandoned");
        Assert.Equal("abandoned", sent[^1][1].Tasks[0].Status);

        var read = plan.Phases[0].Tasks[0];
        read.StartEditCommand.Execute(null);
        Assert.True(read.IsEditing);
        Assert.Equal("Read", read.EditText);
        read.EditText = "  Read the loader  ";
        await read.CommitEditCommand.ExecuteAsync(null);
        Assert.Equal("Read the loader", sent[^1][0].Tasks[0].Content);
        var before = sent.Count;
        read.StartEditCommand.Execute(null);
        read.EditText = "Read";
        await read.CommitEditCommand.ExecuteAsync(null); // unchanged: nothing sent
        Assert.Equal(before, sent.Count);

        plan.NewTaskText = "Ship it";
        await plan.AddTaskCommand.ExecuteAsync(null);
        Assert.Equal(("Ship it", "pending"), (sent[^1][1].Tasks[^1].Content, sent[^1][1].Tasks[^1].Status));
        Assert.Equal("", plan.NewTaskText);

        // Earlier plans are a record: not editable
        plan.Apply([P("New", ("Other", "pending"))], "/s", T.AddMinutes(2));
        Assert.False(Assert.Single(plan.EarlierPlans).Phases[0].Tasks[0].CanEdit);
    }

    [AvaloniaFact]
    public async Task A_change_omp_did_not_take_says_so_and_a_new_plan_can_be_started()
    {
        var sent = new List<IReadOnlyList<TodoPhase>>();
        var ok = false;
        var plan = new PlanViewModel(save: phases => { sent.Add(phases); return Task.FromResult(ok); });
        plan.Apply([], "/s", T);
        plan.NewTaskText = "First";
        await plan.AddTaskCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("The change did not reach omp. Try again."), plan.EditError);
        Assert.Equal("First", plan.NewTaskText); // kept for the retry
        Assert.Equal(("Todos", "First"), (sent[0].Single().Name, sent[0].Single().Tasks.Single().Content));
        ok = true;
        await plan.AddTaskCommand.ExecuteAsync(null);
        Assert.False(plan.HasEditError);
        Assert.False(new PlanViewModel().IsEditable);
    }

    [AvaloniaFact]
    public async Task The_plan_pane_adds_and_checks_off_tasks_through_omp()
    {
        var (w, vm) = await OpenAsync("normal");
        vm.ActivePane = SidePane.Plan;
        await Until(() => vm.Plan.IsEditable, "editable plan");
        Find<TextBox>(w, t => t.Name == "PlanNewTaskBox").Text = "Write the tests";
        Click(Find<Button>(w, b => b.Name == "PlanAddTaskButton"));
        await Until(() => vm.Plan.Phases.SelectMany(p => p.Tasks).Any(t => t.Content == "Write the tests"), "task from omp");
        Click(Find<Button>(w, b => b.Classes.Contains("plan-check") && b.DataContext is PlanTaskViewModel { Content: "Write the tests" }));
        await Until(() => vm.Plan.Phases.SelectMany(p => p.Tasks).Any(t => t.Content == "Write the tests" && t.IsDone), "checked off");
        Assert.Equal(Loc.F("{0} of {1} done", 1, 1), vm.Plan.ProgressText);
        Assert.Contains(vm.Plan.Changes, c => c.What == Loc.T("Completed") && c.Content == "Write the tests");
        await vm.DisposeAsync();
        w.Close();
    }
}
