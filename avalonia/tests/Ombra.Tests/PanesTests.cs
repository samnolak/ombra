using System.Diagnostics;
using System.Text.Json;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

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

        var (page, error) = await s.GetSubagentMessagesAsync("sa-1");
        Assert.Null(error);
        Assert.NotNull(page);
        Assert.Contains(page.Messages, m => m.Role == "tool" && m.Text.StartsWith("grep", StringComparison.Ordinal));
        var (_, unknown) = await s.GetSubagentMessagesAsync("nope");
        Assert.Contains("Unknown subagent", unknown);
        Assert.DoesNotContain(s.Snapshot().Items, i => i is NoticeItem { Level: NoticeLevel.Error });
    }

    [Fact]
    public async Task Subagent_transcript_cursors_read_deltas_empty_polls_and_truncation_without_replaying()
    {
        await using var s = new SessionController(TestProcesses.Fake("subagents"));
        await s.StartAsync();
        var (first, _) = await s.GetSubagentMessagesAsync("sa-3");
        Assert.NotNull(first);
        Assert.NotEmpty(first.Messages);
        var (empty, _) = await s.GetSubagentMessagesAsync("sa-3", first.NextByte);
        Assert.NotNull(empty);
        Assert.Empty(empty.Messages);
        Assert.Equal(first.NextByte, empty.NextByte);

        await SubagentControl(s, """{"append":[{"role":"assistant","content":"é漢😀 delta"},{"role":"assistant","content":"é漢😀 delta"}]}""");
        var (delta, _) = await s.GetSubagentMessagesAsync("sa-3", first.NextByte);
        Assert.NotNull(delta);
        Assert.Equal(["é漢😀 delta", "é漢😀 delta"], delta.Messages.Select(m => m.Text));
        Assert.Equal(first.NextByte, delta.FromByte);
        Assert.True(delta.NextByte > first.NextByte);

        await SubagentControl(s, """{"replace":[{"role":"user","content":"reset"}]}""");
        var (reset, _) = await s.GetSubagentMessagesAsync("sa-3", delta.NextByte);
        Assert.NotNull(reset);
        Assert.True(reset.Reset);
        Assert.Equal(0, reset.FromByte);
        Assert.Equal("reset", Assert.Single(reset.Messages).Text);
    }

    [Fact]
    public async Task Subagent_read_failures_and_cancellation_leave_the_same_bytes_available()
    {
        await using var s = new SessionController(TestProcesses.Fake("subagents"));
        await s.StartAsync();
        var (first, _) = await s.GetSubagentMessagesAsync("sa-3");
        Assert.NotNull(first);
        await SubagentControl(s, """{"append":[{"role":"assistant","content":"not consumed"}],"malformed":true}""");
        var (invalid, invalidError) = await s.GetSubagentMessagesAsync("sa-3", first.NextByte);
        Assert.Null(invalid);
        Assert.NotNull(invalidError);
        await SubagentControl(s, """{"malformed":false,"error":"unavailable"}""");
        var (failed, error) = await s.GetSubagentMessagesAsync("sa-3", first.NextByte);
        Assert.Null(failed);
        Assert.Equal("unavailable", error);
        await SubagentControl(s, """{"error":null,"hold":true}""");
        using var cancellation = new CancellationTokenSource();
        var reading = s.GetSubagentMessagesAsync("sa-3", first.NextByte, cancellation.Token);
        var held = await SubagentControl(s, "{}");
        Assert.Equal(1, held.GetProperty("held").GetInt32());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await SubagentControl(s, """{"hold":false,"release":true}""");
        var (retried, retryError) = await s.GetSubagentMessagesAsync("sa-3", first.NextByte);
        Assert.Null(retryError);
        Assert.Equal("not consumed", Assert.Single(retried!.Messages).Text);
        Assert.DoesNotContain(s.Snapshot().Items, i => i is NoticeItem { Level: NoticeLevel.Error });
    }

    [Fact]
    public async Task Stopping_and_messaging_a_subagent_report_why_they_could_not()
    {
        await using var idle = new SessionController(TestProcesses.Fake("subagents"));
        Assert.Equal("omp is not running", await idle.CancelSubagentAsync("sa-3"));
        Assert.Equal("omp is not running", await idle.SteerSubagentAsync("sa-3", "hi"));

        await using var s = new SessionController(TestProcesses.Fake("subagents"));
        await s.StartAsync();
        await TestProcesses.Eventually(s.Snapshot, x => x.SubagentsSupported == true, Wait, "subscribed");
        await s.PromptAsync("look into it");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Subagents.Count == 3, Wait, "three subagents");
        Assert.Equal("Subagent sa-1 is not running", await s.SteerSubagentAsync("sa-1", "too late"));
        await SubagentControl(s, """{"cancelError":"refused"}""");
        Assert.Equal("refused", await s.CancelSubagentAsync("sa-3"));
        Assert.True(s.Snapshot().Subagents.Single(a => a.Id == "sa-3").IsRunning);
        await SubagentControl(s, """{"cancelError":null}""");
        Assert.Null(await s.CancelSubagentAsync("sa-3"));
        Assert.Equal("aborted", s.Snapshot().Subagents.Single(a => a.Id == "sa-3").Status);
        Assert.DoesNotContain(s.Snapshot().Items, i => i is NoticeItem { Level: NoticeLevel.Error });
    }

    [Fact]
    public async Task Subagents_running_when_omp_crashes_end_as_failed()
    {
        await using var s = new SessionController(TestProcesses.Fake("subagents"));
        await s.StartAsync();
        await TestProcesses.Eventually(s.Snapshot, x => x.SubagentsSupported == true, Wait, "subscribed");
        await s.PromptAsync("look into it");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Subagents.Count == 3, Wait, "three subagents");
        try { await s.Connection!.RequestAsync("test_subagent_control", w => w.WriteBoolean("crash", true), TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException) { } // it dies instead of answering
        var snap = await TestProcesses.Eventually(s.Snapshot, x => !x.Subagents.Single(a => a.Id == "sa-3").IsRunning, Wait, "sa-3 ended with omp");
        Assert.Equal("failed", snap.Subagents.Single(a => a.Id == "sa-3").Status);
        Assert.Equal("completed", snap.Subagents.Single(a => a.Id == "sa-1").Status); // what had ended keeps its end
    }

    internal static async Task<JsonElement> SubagentControl(SessionController session, string json)
    {
        using var fields = JsonDocument.Parse(json);
        var response = await session.Connection!.RequestAsync("test_subagent_control", writer =>
        {
            foreach (var field in fields.RootElement.EnumerateObject()) field.WriteTo(writer);
        });
        Assert.True(response.Success, response.Error);
        return response.Data!.Value;
    }

    [Fact]
    public async Task An_omp_without_the_job_commands_lists_none_quietly_and_is_not_asked_again()
    {
        await using var s = new SessionController(TestProcesses.Fake("no-jobs"));
        await s.StartAsync();
        // Its id-less "Unknown command" answer ends the wait (no 15 s request timeout, no error)
        var clock = Stopwatch.StartNew();
        var (jobs, error) = await s.GetJobsAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
        Assert.Null(error);
        Assert.Empty(jobs!);
        Assert.False(s.Snapshot().JobsSupported);
        var pending = s.Connection!.PendingRequestCount;
        Assert.Empty((await s.GetJobsAsync()).Jobs!); // answered here, nothing sent
        Assert.Equal(pending, s.Connection.PendingRequestCount);
        Assert.DoesNotContain(s.Snapshot().Items, i => i is NoticeItem); // not "get_jobs failed" in the conversation
    }

    /// <summary>The fake's <c>test_job_control</c> (FakeJobs.cs): replaces jobs, appends output, cans refusals; answers the job requests made.</summary>
    internal static async Task<JsonElement> JobControl(SessionController session, string json)
    {
        using var fields = JsonDocument.Parse(json);
        var response = await session.Connection!.RequestAsync("test_job_control", writer =>
        {
            foreach (var field in fields.RootElement.EnumerateObject()) field.WriteTo(writer);
        });
        Assert.True(response.Success, response.Error);
        return response.Data!.Value.Clone();
    }

    [Fact]
    public async Task Jobs_are_listed_stopped_and_read_by_byte_cursor_over_rpc()
    {
        await using var idle = new SessionController(TestProcesses.Fake("normal"));
        Assert.Equal("omp is not running", (await idle.GetJobsAsync()).Error);
        Assert.Equal("omp is not running", await idle.CancelJobAsync("bg_1"));

        await using var s = new SessionController(TestProcesses.Fake("normal"));
        await s.StartAsync();
        Assert.Empty((await s.GetJobsAsync()).Jobs!);
        await JobControl(s, """{"jobs":[{"id":"bg_1","label":"npm run dev","command":"npm run dev","output":"é漢😀 ready\n","ageMs":5000},{"id":"bg_0","status":"failed","label":"npm test","exitCode":1}]}""");
        var (jobs, error) = await s.GetJobsAsync();
        Assert.Null(error);
        Assert.NotNull(jobs);
        Assert.Equal(["bg_1", "bg_0"], jobs.Select(j => j.Id));
        Assert.True(jobs[0].IsRunning);
        Assert.Equal(1, jobs[1].ExitCode);

        var (tail, _) = await s.GetJobOutputAsync("bg_1");
        Assert.Equal("é漢😀 ready\n", tail!.Text);
        Assert.Equal(16, tail.NextByte); // UTF-8 bytes, not .NET characters
        await JobControl(s, """{"jobId":"bg_1","append":"more\n"}""");
        var (delta, _) = await s.GetJobOutputAsync("bg_1", tail.NextByte);
        Assert.Equal(("more\n", 16L, 21L), (delta!.Text, delta.FromByte, delta.NextByte));
        var (unknown, unknownError) = await s.GetJobOutputAsync("bg_404");
        Assert.Null(unknown);
        Assert.Equal("Unknown background job: bg_404", unknownError);

        Assert.Null(await s.CancelJobAsync("bg_1"));
        Assert.Equal("cancelled", (await s.GetJobsAsync()).Jobs!.Single(j => j.Id == "bg_1").Status);
        await JobControl(s, """{"cancelError":"the job is busy"}""");
        Assert.Equal("the job is busy", await s.CancelJobAsync("bg_1"));
        var asked = (await JobControl(s, "{}")).GetProperty("controls").EnumerateArray().Select(c => c.GetProperty("type").GetString()).ToList();
        Assert.Equal(["get_jobs", "get_jobs", "get_job_output", "get_job_output", "get_job_output", "cancel_job", "get_jobs", "cancel_job"], asked);
        Assert.DoesNotContain(s.Snapshot().Items, i => i is NoticeItem);
    }

    [Theory]
    [InlineData("""{"sessionFile":"/s","fromByte":10,"nextByte":20,"reset":false,"messages":"wrong"}""")]
    [InlineData("""{"sessionFile":"/s","fromByte":9,"nextByte":20,"reset":false,"messages":[]}""")]
    [InlineData("""{"sessionFile":"/s","fromByte":10,"nextByte":9,"reset":false,"messages":[]}""")]
    [InlineData("""{"sessionFile":"/s","fromByte":10,"nextByte":20,"reset":true,"messages":[]}""")]
    [InlineData("""{"sessionFile":"/s","fromByte":10,"nextByte":10,"reset":false,"messages":[{"role":"user","content":"replay"}]}""")]
    [InlineData("""{"sessionFile":"/s","fromByte":10,"nextByte":20,"reset":false,"messages":[{"role":"assistant","content":[{"type":"text","text":4}]}]}""")]
    public void Malformed_transcript_ranges_cannot_supply_a_consumable_cursor(string json)
    {
        using var data = JsonDocument.Parse(json);
        Assert.Null(ConversationState.ParseSubagentMessagesPage(data.RootElement, 10));
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
            Assert.Skip("OMBRA_TEST_CONFIG is not set");
            return;
        }
        await using var s = new SessionController(options.ToLaunchSpec(), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        await TestProcesses.Eventually(s.Snapshot, x => x.SubagentsSupported is not null, TimeSpan.FromSeconds(30), "subscription answered");
        Assert.True(s.Snapshot().SubagentsSupported);
        Assert.True(await s.RefreshSubagentsAsync());
        Assert.Empty(s.Snapshot().Subagents); // nothing started yet
        var (jobs, jobsError) = await s.GetJobsAsync(); // the owned engine lists them over RPC (none here)
        Assert.Null(jobsError);
        Assert.Empty(jobs!);
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
        Assert.Equal(Loc.F("{0} of {1} done", 0, 1), plan.ProgressText);
        Assert.Equal([Loc.F("New plan · {0}", Loc.T("1 task")), Loc.T("Started"), Loc.T("Completed"), Loc.F("New plan · {0}", Loc.F("{0} tasks", 2))], plan.Changes.Select(c => c.What));
        Assert.Equal(t1.AddMinutes(5).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), plan.Changes[1].Time);
        var earlier = Assert.Single(plan.EarlierPlans);
        Assert.Equal(Loc.F("{0} of {1} done", 1, 2), earlier.Summary);
        Assert.Equal(Loc.T("Replaced") + Loc.T(" at ") + t1.AddMinutes(9).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), earlier.When);

        // get_state answering late with a state already recorded is no change; a new one (e.g. /todo) is
        plan.Apply([P("Todos", ("Read", "completed"), ("Write", "in_progress"))], recorded, "/s/old", T.AddMinutes(1));
        Assert.Equal(4, plan.Changes.Count);
        plan.Apply([P("Todos", ("Deploy", "completed"))], recorded, "/s/old", T.AddMinutes(2));
        Assert.Equal(Loc.T("Completed"), plan.Changes[0].What);
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
        Assert.Equal(Loc.T("Waiting on: ") + "a key", build[2].BlockerText);
        Assert.True(build[3].IsDropped);
        // Done of what is still to do: the dropped task counts neither way
        Assert.Equal("0/3", plan.Phases[1].Count);
        Assert.Equal(2, plan.DoneCount);
        Assert.Equal(5, plan.TotalCount);
        Assert.Equal(Loc.F("{0} of {1} done", 2, 5), plan.ProgressText);
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
        Assert.Equal([Loc.T("Added"), Loc.T("Started"), Loc.T("Completed"), Loc.F("New plan · {0}", Loc.F("{0} tasks", 2))], plan.Changes.Select(c => c.What));
        Assert.Equal(Loc.Plural(4, "History · {0} change", "History · {0} changes"), plan.HistoryTitle);

        // A plan with nothing in common replaces it: the old one moves under "Earlier plan", folded
        plan.Apply([P("Todos", ("Deploy", "pending"))], "/s/one", T.AddMinutes(2));
        var earlier = Assert.Single(plan.EarlierPlans);
        Assert.False(earlier.IsExpanded);
        Assert.Equal(Loc.F("{0} of {1} done", 1, 3), earlier.Summary);
        Assert.StartsWith(Loc.T("Replaced") + Loc.T(" at "), earlier.When);
        Assert.Equal(["Read", "Write", "Test"], earlier.Phases.SelectMany(p => p.Tasks).Select(t => t.Content));
        Assert.Equal(Loc.F("New plan · {0}", Loc.T("1 task")), plan.Changes[0].What);

        // Cleared: nothing current, the last plan opens so it can still be read
        plan.Apply([], "/s/one", T.AddMinutes(3));
        Assert.False(plan.HasPlan);
        Assert.Equal(2, plan.EarlierPlans.Count);
        Assert.True(plan.EarlierPlans[0].IsExpanded);
        Assert.False(plan.ShowEmptyHint);
        Assert.Equal(Loc.T("Plan cleared"), plan.Changes[0].What);

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
        Assert.Contains(plan.Changes, c => c.What == Loc.T("Blocked") && c.Content == "Ship" + Loc.F(" (waiting on: {0})", "a key"));
        Assert.Contains(plan.Changes, c => c.What == Loc.T("Dropped") && c.Content == "Port");
        plan.Apply([P("Todos", ("Ship", "in_progress"), ("Port", "abandoned"))], "/s", T);
        Assert.Equal(Loc.T("Unblocked"), plan.Changes[0].What);
    }

    [Fact]
    public void A_plan_change_from_another_day_reads_its_date_in_the_users_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        var russian = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        System.Globalization.CultureInfo.CurrentCulture = russian;
        try
        {
            var plan = new PlanViewModel();
            plan.Apply([], "/s", T);
            plan.Apply([P("Todos", ("Ship", "in_progress"))], "/s", T);
            // Not "Sep 24 15:00" in a Russian sentence: the day and month as the sidebar's dates
            Assert.Equal(T.ToLocalTime().ToString("d MMM HH:mm", russian), Assert.Single(plan.Changes).Time);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    // ── Background tasks ──

    private static SubagentInfo Agent(string id, string status, DateTimeOffset seen, TimeSpan? took = null, bool detached = false) => new()
    {
        Id = id, Agent = "explore", Status = status, Description = "Agent " + id, Task = "Do " + id, FirstSeenAt = seen, UpdatedAt = seen,
        EndedAt = status is "running" ? null : seen + (took ?? TimeSpan.Zero), Duration = took, Detached = detached, ToolCount = 3, Tokens = 12_345, Model = "fake/model",
        RunningTime = MonotonicTime.Start(TimeProvider.System, took ?? TimeSpan.Zero).Stop(),
    };

    [Fact]
    public void A_subagents_token_count_uses_the_users_decimal_separator()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            var tasks = new TasksViewModel();
            tasks.ApplySubagents([Agent("run", "running", T)]);
            // Not "12.3" in a Russian sentence
            Assert.Contains(Loc.F("{0}k tokens", "12,3"), Assert.Single(tasks.Subagents).MetaText);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    public void Subagent_title_task_and_activity_keep_whole_graphemes(string glyph)
    {
        for (var cut = 1; cut < glyph.Length; cut++)
        {
            var tasks = new TasksViewModel();
            var titleHead = new string('a', 120 - cut);
            var taskHead = new string('b', 2000 - cut);
            var activityHead = new string('c', 200 - cut);
            tasks.ApplySubagents([Agent("unicode", "running", T) with
            {
                Description = titleHead + glyph + " remainder",
                CurrentTool = "read", CurrentToolArgs = activityHead + glyph + " remainder",
            }]);
            var row = Assert.Single(tasks.Subagents);
            Assert.Equal(titleHead + "…", row.Title);
            Assert.Equal("read: " + activityHead + "…", row.ActivityText);
            tasks.ApplySubagents([row.Info with { Task = taskHead + glyph + " remainder" }]);
            Assert.Equal(taskHead + "…", row.TaskText);
        }
    }

    [Fact]
    public void Subagent_shortening_trims_whitespace_without_leaving_its_prepend_character()
    {
        var tasks = new TasksViewModel();
        var head = new string('a', 118);
        tasks.ApplySubagents([Agent("unicode", "running", T) with
        {
            Description = head + "\u0600 remainder",
        }]);
        Assert.Equal(head + "…", Assert.Single(tasks.Subagents).Title);
        tasks.ApplySubagents([Agent("ascii", "running", T) with
        {
            Description = head + "  remainder",
        }]);
        Assert.Equal(head + "…", Assert.Single(tasks.Subagents).Title);
    }

    [Fact]
    public void A_subagent_without_a_description_is_titled_by_its_name_and_shows_its_own_assignment()
    {
        var tasks = new TasksViewModel();
        // omp's task text is the rendered subagent prompt; the assignment is what the parent wrote
        tasks.ApplySubagents([Agent("Main.AuthRefactor", "running", T) with
        {
            Description = null, Task = "Complete assignment thoroughly:\n\nRefactor the token refresh", Assignment = "Refactor the token refresh",
        }]);
        var row = Assert.Single(tasks.Subagents);
        Assert.Equal("AuthRefactor", row.Title);
        Assert.Equal("Refactor the token refresh", row.TaskText);
    }

    [Fact]
    public void Running_subagents_come_first_then_the_latest_finished_and_the_count_adds_jobs()
    {
        var tasks = new TasksViewModel();
        tasks.ApplySubagents([Agent("old", "completed", T, TimeSpan.FromSeconds(8)), Agent("run", "running", T.AddSeconds(1), TimeSpan.FromSeconds(3)),
            Agent("new", "failed", T.AddSeconds(2), TimeSpan.FromSeconds(9))]);
        Assert.Equal(["run", "new", "old"], tasks.Subagents.Select(r => r.Id));
        var run = tasks.Subagents[0];
        Assert.Equal(Loc.T("Running"), run.StatusText);
        Assert.Equal(Loc.F("{0} tool calls", 3) + " · " + Loc.F("{0}k tokens", 12.3.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)) + " · fake/model", run.MetaText);
        Assert.Equal(Loc.T("Failed"), tasks.Subagents[1].StatusText);
        Assert.Equal(Loc.F("{0}s", 8), tasks.Subagents[2].ElapsedText);
        Assert.Equal(1, tasks.RunningCount);

        // Jobs: a bash job counts; an async task job is the subagents it runs, listed once (as subagents) when omp
        // reports them, and as a job when it does not
        var now = T.AddMinutes(10);
        tasks.ApplyJobs([Job("bg-1", "running", "npm run dev", now.AddMinutes(-2)), Job("bg-2", "running", "Watch it", now.AddMinutes(-1)) with { Type = "task" },
            Job("bg-0", "completed", "npm test", now.AddMinutes(-4), now.AddMinutes(-3))], now);
        Assert.Equal(["npm run dev", "Watch it", "npm test"], tasks.Jobs.Select(j => j.Title));
        Assert.Equal(3, tasks.RunningCount);
        var bash = tasks.Jobs[0];
        tasks.SetSupport(true);
        Assert.Equal(["npm run dev", "npm test"], tasks.Jobs.Select(j => j.Title));
        Assert.Same(bash, tasks.Jobs[0]); // rows are kept by id
        Assert.True(tasks.Jobs[0].IsRunning);
        Assert.Equal(Loc.T("Done"), tasks.Jobs[1].StatusText);
        Assert.Equal(2, tasks.RunningCount);
        Assert.Equal(Loc.F("{0} running · {1} finished", 2, 3), tasks.Summary);

        // An update keeps the row (and its opened state)
        run.IsExpanded = true;
        tasks.ApplySubagents([Agent("old", "completed", T, TimeSpan.FromSeconds(8)), Agent("run", "completed", T.AddSeconds(1), TimeSpan.FromSeconds(12)),
            Agent("new", "failed", T.AddSeconds(2), TimeSpan.FromSeconds(9))]);
        Assert.Same(run, tasks.Subagents.Single(r => r.Id == "run"));
        Assert.True(run.IsExpanded);
        Assert.Equal(Loc.T("Done"), run.StatusText);
        Assert.Equal(1, tasks.RunningCount);
    }


    [Fact]
    public void Subagent_and_job_widget_text_displays_terminal_controls_inertly()
    {
        var source = Agent("ansi", "bad\u001b[31m", T) with
        {
            Agent = "explore\u001b[32m",
            Description = "desc\u001b[31m",
            Task = "task\u001b[31m",
            LastIntent = "activity\u001b[35m",
            RecentOutput = ["output\u001b[31m"],
            RecentTools = [new("bash", "arg\u001b[33m")],
            RetryNote = "retry\u001b[34m",
        };
        var tasks = new TasksViewModel();
        tasks.ApplySubagents([source]);
        var row = Assert.Single(tasks.Subagents);
        Assert.Equal("desc␛[31m", row.Title);
        Assert.Equal("explore␛[32m", row.Agent);
        Assert.Equal("task␛[31m", row.TaskText);
        Assert.Equal("Bad␛[31m", row.StatusText);
        Assert.Equal("activity␛[35m", row.ActivityText);
        Assert.Equal("output␛[31m", row.OutputText);
        Assert.Equal("bash: arg␛[33m", row.ToolsText);
        Assert.Equal("retry␛[34m", row.RetryNote);
        var injected = new JobRowViewModel(null, Job("id", "unknown\u001b[33m", "label\u001b[32m", T) with { Type = "bash\u001b[31m" }, T.AddMinutes(2));
        Assert.Equal("label␛[32m", injected.Title);
        Assert.Equal("Unknown␛[33m", injected.StatusText);
        Assert.Equal(Loc.F("{0} · started {1} ago", "bash␛[31m", Loc.F("{0}m {1:00}s", 2, 0)), injected.Detail);
    }

    private static JobInfo Job(string id, string status, string label, DateTimeOffset started, DateTimeOffset? ended = null) =>
        new(id, "bash", status, label, label, started, ended, null, null, 0, false);

    [Fact]
    public void A_job_row_shows_how_long_it_runs_how_it_ended_and_whether_it_waits_for_input()
    {
        var started = T;
        var row = new JobRowViewModel(null, Job("bg-1", "running", "npm run dev", started), started.AddSeconds(12));
        Assert.Equal(Loc.F("{0} · running for {1}", "bash", Loc.F("{0}s", 12)), row.Detail);
        Assert.Equal(Loc.T("Running"), row.StatusText);
        Assert.True(row.CanStop);
        row.Tick(started.AddSeconds(75));
        Assert.Equal(Loc.F("{0} · running for {1}", "bash", Loc.F("{0}m {1:00}s", 1, 15)), row.Detail);
        row.Update(row.Info with { WaitingForInput = true }, started.AddSeconds(80));
        Assert.True(row.IsWaitingForInput);
        Assert.Equal(Loc.T("Waiting for input"), row.StatusText);
        row.Update(row.Info with { Status = "failed", ExitCode = 3, EndedAt = started.AddSeconds(90) }, started.AddMinutes(5));
        Assert.False(row.IsRunning);
        Assert.False(row.IsWaitingForInput); // an ended job waits for nothing
        Assert.False(row.CanStop);
        Assert.True(row.IsFailed);
        Assert.Equal(Loc.F("Failed (exit {0})", 3), row.StatusText);
        Assert.Equal(Loc.F("{0} · started {1} ago", "bash", Loc.F("{0}m {1:00}s", 5, 0)), row.Detail);
    }

    [Fact]
    public void Job_lists_and_output_reads_are_parsed_strictly()
    {
        using var list = JsonDocument.Parse("""{"jobs":[{"id":"bg_1","type":"bash","status":"running","label":"npm run dev","command":"npm run dev","startTime":1000,"outputBytes":12,"waitingForInput":true},{"status":"running"},{"id":"bg_0","type":"bash","status":"failed","label":"x","startTime":1,"endTime":2000,"exitCode":3,"outputBytes":0,"waitingForInput":false}]}""");
        var jobs = OmpJobs.ParseJobs(list.RootElement)!;
        Assert.Equal(["bg_1", "bg_0"], jobs.Select(j => j.Id)); // a row without an id is skipped
        Assert.True(jobs[0].WaitingForInput);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), jobs[0].StartedAt);
        Assert.Equal(3, jobs[1].ExitCode);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2000), jobs[1].EndedAt);
        using var notList = JsonDocument.Parse("""{"jobs":{}}""");
        Assert.Null(OmpJobs.ParseJobs(notList.RootElement));
        using var page = JsonDocument.Parse("""{"jobId":"bg_1","status":"running","waitingForInput":false,"fromByte":4,"nextByte":9,"totalBytes":9,"startByte":0,"skippedBytes":2,"reset":false,"snapshot":false,"text":"héllo"}""");
        Assert.Equal(new JobOutputPage("bg_1", "running", false, 4, 9, 9, 2, false, false, "héllo"), OmpJobs.ParseOutput(page.RootElement));
        using var backwards = JsonDocument.Parse("""{"jobId":"bg_1","text":"","fromByte":9,"nextByte":4}""");
        Assert.Null(OmpJobs.ParseOutput(backwards.RootElement));
    }

    [Fact]
    public void Elapsed_time_reads_like_a_clock()
    {
        Assert.Equal(Loc.F("{0}s", 8), TasksViewModel.Duration(TimeSpan.FromSeconds(8.4)));
        Assert.Equal(Loc.F("{0}m {1:00}s", 1, 5), TasksViewModel.Duration(TimeSpan.FromSeconds(65)));
        Assert.Equal(Loc.F("{0}h {1:00}m", 2, 3), TasksViewModel.Duration(TimeSpan.FromMinutes(123)));
    }
}
