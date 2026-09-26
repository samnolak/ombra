using System.Diagnostics;
using System.Text.Json;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// The side panes' state: subagent frames folded into the snapshot, the plan with its history, the background tasks and
/// their count (the Views menu), and the subagent RPC against the fake omp — with and without support for it.
/// </summary>
public sealed class PanesTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static RpcFrame F(string json)
    {
        var e = JsonDocument.Parse(json).RootElement.Clone();
        return new RpcFrame(e.GetProperty("type").GetString()!, null, e, Stopwatch.GetTimestamp(), json.Length + 1);
    }

    private static string Lifecycle(string id, string status, string agent = "explore", string description = "Find the readers") =>
        """{"type":"subagent_lifecycle","payload":{"id":"ID","agent":"AGENT","agentSource":"bundled","description":"DESC","status":"STATUS","index":0,"detached":false}}"""
            .Replace("ID", id).Replace("AGENT", agent).Replace("DESC", description).Replace("STATUS", status);

    private static string Progress(string id, string status, string? tool = "grep", long ms = 4200) =>
        """{"type":"subagent_progress","payload":{"index":0,"agent":"explore","agentSource":"bundled","task":"Find where todos are read","progress":{"index":0,"id":"ID","agent":"explore","agentSource":"bundled","status":"STATUS","task":"Find where todos are read","lastIntent":"Searching","currentTool":TOOL,"currentToolArgs":"todoPhases src/","recentTools":[{"tool":"read","args":"a.cs","endMs":1}],"recentOutput":["newest","older"],"toolCount":4,"requests":2,"tokens":12345,"cost":0.01,"durationMs":MS,"resolvedModel":"fake/model"}}}"""
            .Replace("ID", id).Replace("STATUS", status).Replace("TOOL", tool is null ? "null" : $"\"{tool}\"").Replace("MS", ms.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ── Subagents in the conversation state ──

    [Fact]
    public void Subagent_frames_become_a_list_that_keeps_finished_agents()
    {
        var s = new ConversationState { SessionFile = "/s/one.jsonl" };
        s.Apply(F(Lifecycle("a1", "started")), T);
        s.Apply(F(Progress("a1", "running")), T.AddSeconds(4));
        var running = Assert.Single(s.Snapshot().Subagents);
        Assert.True(running.IsRunning);
        Assert.Equal("Find the readers", running.Description);
        Assert.Equal("Find where todos are read", running.Task);
        Assert.Equal("grep", running.CurrentTool);
        Assert.Equal(["newest", "older"], running.RecentOutput);
        Assert.Equal(12345, running.Tokens);
        Assert.Equal(TimeSpan.FromMilliseconds(4200), running.Duration);
        Assert.True(s.Snapshot().SubagentsSupported);

        s.Apply(F(Progress("a1", "completed", tool: null, ms: 9000)), T.AddSeconds(9));
        s.Apply(F(Lifecycle("a1", "completed")), T.AddSeconds(9));
        // A late progress frame does not bring a finished agent back
        s.Apply(F(Progress("a1", "running")), T.AddSeconds(10));
        var done = Assert.Single(s.Snapshot().Subagents);
        Assert.Equal("completed", done.Status);
        Assert.Null(done.CurrentTool);
        Assert.Equal(T.AddSeconds(9), done.EndedAt);
        Assert.Empty(s.Snapshot().Items); // nothing in the conversation itself
    }

    [Fact]
    public void The_list_is_the_same_instance_until_it_changes_and_belongs_to_its_session()
    {
        var s = new ConversationState { SessionFile = "/s/one.jsonl" };
        s.Apply(F(Lifecycle("a1", "started")), T);
        var first = s.Snapshot().Subagents;
        s.AddNotice(NoticeLevel.Info, "something else");
        Assert.Same(first, s.Snapshot().Subagents);
        s.SessionFile = "/s/two.jsonl"; // another session: its own (empty) list
        Assert.Empty(s.Snapshot().Subagents);
    }

    [Fact]
    public void Get_subagents_marks_an_agent_omp_no_longer_runs_as_ended()
    {
        var s = new ConversationState { SessionFile = "/s/one.jsonl" };
        s.Apply(F(Lifecycle("a1", "started")), T);
        s.Apply(F(Lifecycle("a2", "started", agent: "task", description: "Watch the server")), T);
        var list = JsonDocument.Parse("""[{"id":"a2","index":1,"agent":"task","status":"running","task":"Keep it up","progress":{"id":"a2","status":"running","currentTool":"bash","durationMs":1000,"toolCount":1,"tokens":10,"cost":0}}]""").RootElement.Clone();
        s.MergeSubagents(list, T.AddSeconds(5));
        var agents = s.Snapshot().Subagents;
        Assert.Equal("ended", agents.Single(a => a.Id == "a1").Status);
        var a2 = agents.Single(a => a.Id == "a2");
        Assert.True(a2.IsRunning);
        Assert.Equal("bash", a2.CurrentTool);
        Assert.Equal("Keep it up", a2.Task);
    }

    [Fact]
    public void An_omp_without_subagents_answering_unknown_command_is_no_error_in_the_conversation()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"_unmatched_response","command":"set_subagent_subscription","success":false,"error":"Unknown command: set_subagent_subscription"}"""), T);
        Assert.Empty(s.Snapshot().Items);
        Assert.False(s.Snapshot().SubagentsSupported);
        // Other failures still say so
        s.Apply(F("""{"type":"_unmatched_response","command":"frobnicate","success":false,"error":"Unknown command: frobnicate"}"""), T);
        Assert.Single(s.Snapshot().Items.OfType<NoticeItem>());
    }

    [Fact]
    public void A_subagents_conversation_reads_as_text_and_tool_calls()
    {
        var data = JsonDocument.Parse("""{"messages":[{"role":"user","content":"Find it"},{"role":"assistant","content":[{"type":"text","text":"Looking."},{"type":"toolCall","id":"c","name":"grep","arguments":{"pattern":"todo"}}]},{"role":"toolResult","content":[{"type":"text","text":"a.cs:1"}],"isError":true}]}""").RootElement;
        var m = ConversationState.ParseSubagentMessages(data);
        Assert.Equal([("user", "Find it"), ("assistant", "Looking."), ("tool", "grep · todo"), ("error", "a.cs:1")], m.Select(x => (x.Role, x.Text)));
    }

    // ── Against the fake omp ──

    [Fact]
    public async Task Subagents_are_reported_live_and_their_conversation_can_be_read()
    {
        await using var s = new SessionController(TestProcesses.FakeFactory("subagents", TestProcesses.TempDir("panes-sa")), new LaunchRequest(TestProcesses.TempDir("panes-sa-project")));
        await s.StartAsync();
        await TestProcesses.Eventually(s.Snapshot, x => x.SubagentsSupported == true, Wait, "subscribed");
        await s.PromptAsync("look into it");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Subagents.Count == 3, Wait, "three subagents");
        Assert.Equal("completed", snap.Subagents.Single(a => a.Id == "sa-1").Status);
        Assert.Equal("failed", snap.Subagents.Single(a => a.Id == "sa-2").Status);
        var bg = snap.Subagents.Single(a => a.Id == "sa-3");
        Assert.True(bg.IsRunning);
        Assert.True(bg.Detached);
        Assert.Equal("Watch the dev server", bg.Description);

        Assert.True(await s.RefreshSubagentsAsync());
        Assert.True(s.Snapshot().Subagents.Single(a => a.Id == "sa-3").IsRunning); // omp still lists it

        var (messages, error) = await s.GetSubagentMessagesAsync("sa-1");
        Assert.Null(error);
        Assert.Contains(messages, m => m.Role == "tool" && m.Text.StartsWith("grep", StringComparison.Ordinal));
        var (_, unknown) = await s.GetSubagentMessagesAsync("nope");
        Assert.Contains("Unknown subagent", unknown);
        Assert.DoesNotContain(s.Snapshot().Items, i => i is NoticeItem { Level: NoticeLevel.Error });
    }

    [Fact]
    public async Task An_omp_that_does_not_know_the_subagent_commands_starts_as_before()
    {
        await using var s = new SessionController(TestProcesses.Fake("normal"));
        await s.StartAsync();
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.SubagentsSupported == false, Wait, "support known");
        Assert.Empty(snap.Items); // no "set_subagent_subscription failed" notice
        Assert.False(await s.RefreshSubagentsAsync());
        await s.PromptAsync("hello");
        snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Items.OfType<AssistantItem>().Any(), Wait, "a reply");
        Assert.DoesNotContain(snap.Items, i => i is NoticeItem);
        Assert.Empty(snap.Subagents);
    }

    [Fact]
    public async Task A_real_omp_takes_the_subscription_lists_its_subagents_and_its_jobs()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        await using var s = new SessionController(options.ToLaunchSpec(), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        await TestProcesses.Eventually(s.Snapshot, x => x.SubagentsSupported is not null, TimeSpan.FromSeconds(30), "subscription answered");
        Assert.True(s.Snapshot().SubagentsSupported);
        Assert.True(await s.RefreshSubagentsAsync());
        Assert.Empty(s.Snapshot().Subagents); // nothing started yet
        Assert.Contains(s.Snapshot().Commands, c => c.Name == "jobs"); // omp lists /jobs: the pane may ask it
        var jobs = await s.RunSlashCommandAsync("/jobs");
        Assert.True(jobs.Ok, jobs.Error);
        Assert.NotNull(TasksViewModel.ParseJobs(jobs.Output)); // omp's own words, read as rows (none here)
        Assert.DoesNotContain(s.Snapshot().Items, i => i is NoticeItem);
    }

    // ── The plan's history in the conversation state ──

    [Fact]
    public void Every_list_the_todo_tool_committed_is_kept_with_its_time_also_from_a_saved_session()
    {
        var s = new ConversationState();
        s.ResetTranscript();
        var at = DateTimeOffset.Parse("2026-09-20T09:30:00Z");
        var messages = JsonDocument.Parse($$$"""
            [{"role":"toolResult","toolCallId":"a","toolName":"todo","content":[{"type":"text","text":"ok"}],"isError":false,"timestamp":{{{at.ToUnixTimeMilliseconds()}}},
              "details":{"op":"init","phases":[{"name":"Todos","tasks":[{"content":"Read","status":"in_progress"},{"content":"Ship","status":"blocked","blocker":"a key"}]}]}},
             {"role":"toolResult","toolCallId":"b","toolName":"todo","content":[],"isError":false,"details":{"op":"view","phases":[]}},
             {"role":"toolResult","toolCallId":"c","toolName":"todo","content":[],"isError":true,"details":{"op":"done","phases":[]}},
             {"role":"toolResult","toolCallId":"d","toolName":"read","content":[],"isError":false,"details":{"phases":[]}}]
            """).RootElement;
        s.Hydrate(messages);
        var saved = Assert.Single(s.Snapshot().PlanHistory); // a view, a failed call and another tool commit nothing
        Assert.Equal(at, saved.At);
        Assert.Equal("a key", saved.Phases[0].Tasks[1].Blocker);

        // Live: the tool's result as it ends
        s.Apply(F("""{"type":"tool_execution_start","toolCallId":"e","toolName":"todo","args":{"op":"done","task":"Read"}}"""), T);
        s.Apply(F("""{"type":"tool_execution_end","toolCallId":"e","toolName":"todo","result":{"content":[],"details":{"op":"done","phases":[{"name":"Todos","tasks":[{"content":"Read","status":"completed"}]}]}}}"""), T);
        var history = s.Snapshot().PlanHistory;
        Assert.Equal(2, history.Count);
        Assert.Equal(T, history[1].At);
        Assert.Same(history, s.Snapshot().PlanHistory);

        s.ResetTranscript(); // another session, or omp restarted: the history is read again from the session
        Assert.Empty(s.Snapshot().PlanHistory);
    }

    [Fact]
    public void A_saved_sessions_plan_history_becomes_earlier_plans_and_changes_with_their_times()
    {
        var day = DateTimeOffset.Now.Date;
        var t1 = new DateTimeOffset(day.AddHours(9));
        IReadOnlyList<PlanSnapshot> recorded =
        [
            new(t1, [P("Todos", ("Read", "in_progress"), ("Write", "pending"))]),
            new(t1.AddMinutes(5), [P("Todos", ("Read", "completed"), ("Write", "in_progress"))]),
            new(t1.AddMinutes(9), [P("Todos", ("Deploy", "in_progress"))]),
        ];
        var plan = new PlanViewModel();
        plan.Apply([P("Todos", ("Deploy", "in_progress"))], recorded, "/s/old", T);
        Assert.Equal("0 of 1 done", plan.ProgressText);
        Assert.Equal(["New plan · 1 task", "Started", "Completed", "New plan · 2 tasks"], plan.Changes.Select(c => c.What));
        Assert.Equal(t1.AddMinutes(5).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), plan.Changes[1].Time);
        var earlier = Assert.Single(plan.EarlierPlans);
        Assert.Equal("1 of 2 done", earlier.Summary);
        Assert.Equal("Replaced at " + t1.AddMinutes(9).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), earlier.When);

        // get_state answering late with a state already recorded is no change; a new one (e.g. /todo) is
        plan.Apply([P("Todos", ("Read", "completed"), ("Write", "in_progress"))], recorded, "/s/old", T.AddMinutes(1));
        Assert.Equal(4, plan.Changes.Count);
        plan.Apply([P("Todos", ("Deploy", "completed"))], recorded, "/s/old", T.AddMinutes(2));
        Assert.Equal("Completed", plan.Changes[0].What);
    }

    // ── The plan ──

    private static TodoPhase P(string name, params (string Content, string Status)[] tasks) => new(name, [.. tasks.Select(t => new TodoTask(t.Content, t.Status))]);

    [Fact]
    public void The_plan_groups_by_phase_and_shows_every_status()
    {
        var plan = new PlanViewModel();
        plan.Apply([], "/s/one", T);
        IReadOnlyList<TodoPhase> todos =
        [
            P("Understand", ("Read", "completed"), ("Map", "completed")),
            new("Build", [new TodoTask("Draw", "in_progress"), new TodoTask("Test", "pending"), new TodoTask("Ship", "blocked", "a key"), new TodoTask("Port", "abandoned")]),
        ];
        plan.Apply(todos, "/s/one", T);
        Assert.True(plan.HasPlan);
        Assert.Equal(["Understand", "Build"], plan.Phases.Select(p => p.Name));
        Assert.Equal("2/2", plan.Phases[0].Count);
        var build = plan.Phases[1].Tasks;
        Assert.True(build[0].IsActive && build[0].IsCurrent);
        Assert.True(build[1].IsOpen && !build[1].IsCurrent);
        Assert.True(build[2].IsBlocked);
        Assert.Equal("Waiting on: a key", build[2].BlockerText);
        Assert.True(build[3].IsDropped);
        // Done of what is still to do: the dropped task counts neither way
        Assert.Equal("0/3", plan.Phases[1].Count);
        Assert.Equal(2, plan.DoneCount);
        Assert.Equal(5, plan.TotalCount);
        Assert.Equal("2 of 5 done", plan.ProgressText);
        Assert.Equal("2/5", plan.ShortProgress);

        // One phase: no phase header (it is just the list)
        plan.Apply([P("Todos", ("Draw", "in_progress"))], "/s/one", T);
        Assert.False(plan.Phases.Single().HasName);
    }

    [Fact]
    public void Plan_changes_are_kept_and_a_replaced_plan_stays_readable()
    {
        var plan = new PlanViewModel();
        plan.Apply([], "/s/one", T);
        plan.Apply([P("Todos", ("Read", "in_progress"), ("Write", "pending"))], "/s/one", T);
        plan.Apply([P("Todos", ("Read", "completed"), ("Write", "in_progress"), ("Test", "pending"))], "/s/one", T.AddMinutes(1));
        // Newest first
        Assert.Equal(["Added", "Started", "Completed", "New plan · 2 tasks"], plan.Changes.Select(c => c.What));
        Assert.Equal("History · 4 changes", plan.HistoryTitle);

        // A plan with nothing in common replaces it: the old one moves under "Earlier plan", folded
        plan.Apply([P("Todos", ("Deploy", "pending"))], "/s/one", T.AddMinutes(2));
        var earlier = Assert.Single(plan.EarlierPlans);
        Assert.False(earlier.IsExpanded);
        Assert.Equal("1 of 3 done", earlier.Summary);
        Assert.StartsWith("Replaced at ", earlier.When);
        Assert.Equal(["Read", "Write", "Test"], earlier.Phases.SelectMany(p => p.Tasks).Select(t => t.Content));
        Assert.Equal("New plan · 1 task", plan.Changes[0].What);

        // Cleared: nothing current, the last plan opens so it can still be read
        plan.Apply([], "/s/one", T.AddMinutes(3));
        Assert.False(plan.HasPlan);
        Assert.Equal(2, plan.EarlierPlans.Count);
        Assert.True(plan.EarlierPlans[0].IsExpanded);
        Assert.False(plan.ShowEmptyHint);
        Assert.Equal("Plan cleared", plan.Changes[0].What);

        // Another session starts its own history
        plan.Apply([P("Todos", ("Other", "pending"))], "/s/two", T.AddMinutes(4));
        Assert.Empty(plan.EarlierPlans);
        Assert.Empty(plan.Changes);
        Assert.True(plan.HasPlan);
    }

    [Fact]
    public void Blocking_and_dropping_are_logged_with_what_a_task_waits_on()
    {
        var plan = new PlanViewModel();
        plan.Apply([P("Todos", ("Ship", "in_progress"), ("Port", "pending"))], "/s", T);
        plan.Apply([new TodoPhase("Todos", [new TodoTask("Ship", "blocked", "a key"), new TodoTask("Port", "abandoned")])], "/s", T);
        Assert.Contains(plan.Changes, c => c.What == "Blocked" && c.Content == "Ship (waiting on: a key)");
        Assert.Contains(plan.Changes, c => c.What == "Dropped" && c.Content == "Port");
        plan.Apply([P("Todos", ("Ship", "in_progress"), ("Port", "abandoned"))], "/s", T);
        Assert.Equal("Unblocked", plan.Changes[0].What);
    }

    // ── Background tasks ──

    private static SubagentInfo Agent(string id, string status, DateTimeOffset seen, TimeSpan? took = null, bool detached = false) => new()
    {
        Id = id, Agent = "explore", Status = status, Description = "Agent " + id, Task = "Do " + id, FirstSeenAt = seen, UpdatedAt = seen,
        EndedAt = status is "running" ? null : seen + (took ?? TimeSpan.Zero), Duration = took, Detached = detached, ToolCount = 3, Tokens = 12_345, Model = "fake/model",
    };

    [Fact]
    public void Running_subagents_come_first_then_the_latest_finished_and_the_count_adds_jobs()
    {
        var tasks = new TasksViewModel();
        tasks.ApplySubagents([Agent("old", "completed", T, TimeSpan.FromSeconds(8)), Agent("run", "running", T.AddSeconds(1), TimeSpan.FromSeconds(3)),
            Agent("new", "failed", T.AddSeconds(2), TimeSpan.FromSeconds(9))], T.AddSeconds(20));
        Assert.Equal(["run", "new", "old"], tasks.Subagents.Select(r => r.Id));
        var run = tasks.Subagents[0];
        Assert.Equal("Running", run.StatusText);
        Assert.Equal("3 tool calls · 12.3k tokens · fake/model", run.MetaText);
        Assert.Equal("Failed", tasks.Subagents[1].StatusText);
        Assert.Equal("8s", tasks.Subagents[2].ElapsedText);
        Assert.Equal(1, tasks.RunningCount);

        // Jobs: a bash job counts; an async task job is the subagents it runs, listed once (as subagents) when omp
        // reports them, and as a job when it does not
        tasks.ApplyJobs("Background Jobs\nRunning: 2\n\nRunning Jobs\n  [bg-1] bash (running) — 2m\n    npm run dev\n  [bg-2] task (running) — 1m\n    Watch it\n\nRecent Jobs\n  [bg-0] bash (completed) — 4m\n    npm test");
        Assert.Equal(["npm run dev", "Watch it", "npm test"], tasks.Jobs.Select(j => j.Title));
        Assert.Equal(3, tasks.RunningCount);
        tasks.SetSupport(true);
        Assert.Equal(["npm run dev", "npm test"], tasks.Jobs.Select(j => j.Title));
        Assert.True(tasks.Jobs[0].IsRunning);
        Assert.Equal("bash · running for 2m", tasks.Jobs[0].Detail);
        Assert.Equal("bash · started 4m ago", tasks.Jobs[1].Detail);
        Assert.Equal("Done", tasks.Jobs[1].StatusText);
        Assert.Equal(2, tasks.RunningCount);
        Assert.Equal("2 running · 3 finished", tasks.Summary);

        // An update keeps the row (and its opened state)
        run.IsExpanded = true;
        tasks.ApplySubagents([Agent("old", "completed", T, TimeSpan.FromSeconds(8)), Agent("run", "completed", T.AddSeconds(1), TimeSpan.FromSeconds(12)),
            Agent("new", "failed", T.AddSeconds(2), TimeSpan.FromSeconds(9))], T.AddSeconds(30));
        Assert.Same(run, tasks.Subagents.Single(r => r.Id == "run"));
        Assert.True(run.IsExpanded);
        Assert.Equal("Done", run.StatusText);
        Assert.Equal(1, tasks.RunningCount);
    }

    [Fact]
    public void Jobs_output_is_read_as_rows_or_else_shown_as_printed()
    {
        Assert.Empty(TasksViewModel.ParseJobs("No background jobs running. (Background jobs run async tools …)")!);
        Assert.Null(TasksViewModel.ParseJobs("Something omp prints in a later version"));
        var tasks = new TasksViewModel();
        tasks.ApplyJobs("Something omp prints in a later version");
        Assert.True(tasks.HasJobsText);
        Assert.False(tasks.IsEmpty);
        tasks.ApplyJobs("No background jobs running.");
        Assert.True(tasks.IsEmpty);
    }

    [Fact]
    public void Elapsed_time_reads_like_a_clock()
    {
        Assert.Equal("8s", TasksViewModel.Duration(TimeSpan.FromSeconds(8.4)));
        Assert.Equal("1m 05s", TasksViewModel.Duration(TimeSpan.FromSeconds(65)));
        Assert.Equal("2h 03m", TasksViewModel.Duration(TimeSpan.FromMinutes(123)));
    }
}
