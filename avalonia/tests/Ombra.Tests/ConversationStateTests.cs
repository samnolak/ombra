using System.Diagnostics;
using System.Text.Json;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class ConversationStateTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-24T12:00:00Z");

    private static RpcFrame F(string json)
    {
        var e = JsonDocument.Parse(json).RootElement.Clone();
        return new RpcFrame(e.GetProperty("type").GetString()!, null, e, Stopwatch.GetTimestamp(), json.Length + 1);
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    [InlineData("🇫🇷")]
    public void Shell_arguments_and_output_clip_only_between_visible_graphemes(string glyph)
    {
        // Try every UTF-16 position inside the cluster, not just the first surrogate.
        for (var cut = 1; cut < glyph.Length; cut++)
        {
            var state = new ConversationState();
            var commandHead = new string('a', 400 - cut);
            var command = commandHead + glyph + " remaining";
            var id = state.AddShellCommand(command, T);
            Assert.Equal(commandHead + $"… [{glyph.Length + 10} more chars]",
                Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Args);

            var head = new string('h', 1000 - cut);
            var tail = new string('t', 3000 - (glyph.Length - cut));
            var output = head + glyph + new string('m', 25) + glyph + tail;
            state.FinishShellCommand(id, output, 0, false, T);
            Assert.Equal(head + $"\n… [{25 + 2 * glyph.Length} chars omitted] …\n" + tail,
                Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);

            // A boundary exactly after/before the glyph keeps it rather than dropping it gratuitously.
            var aligned = new string('h', 1000 - glyph.Length) + glyph + new string('m', 25)
                + glyph + new string('t', 3000 - glyph.Length);
            state.FinishShellCommand(id, aligned, 0, false, T);
            Assert.Equal(new string('h', 1000 - glyph.Length) + glyph + "\n… [25 chars omitted] …\n"
                + glyph + new string('t', 3000 - glyph.Length),
                Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);
        }
    }

    [Fact]
    public void Shell_clipping_preserves_ascii_budgets_and_short_text()
    {
        var state = new ConversationState();
        var id = state.AddShellCommand(new string('a', 401), T);
        Assert.Equal(new string('a', 400) + "… [1 more chars]",
            Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Args);
        state.FinishShellCommand(id, new string('x', 4001), 0, false, T);
        Assert.Equal(new string('x', 1000) + "\n… [1 chars omitted] …\n" + new string('x', 3000),
            Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);
        state.FinishShellCommand(id, "e\u0301 👩‍💻", 0, false, T);
        Assert.Equal("e\u0301 👩‍💻", Assert.Single(state.Snapshot().Items.OfType<ToolItem>()).Output);
    }

    [Fact]
    public void Ownership_fork_notice_moves_visible_identity_without_discarding_the_conversation()
    {
        var state = new ConversationState { SessionFile = "/sessions/original.jsonl", SessionId = "original" };
        state.SetPhase(SessionPhase.Ready, T);
        state.AddUserPrompt("retained conversation");
        state.Apply(F("""{"type":"notice","level":"warning","source":"session-persistence","reason":"open-elsewhere","message":"Another writer owns the original; saving a fork.","previousSessionFile":"/sessions/original.jsonl","sessionFile":"/sessions/fork.jsonl","sessionId":"fork"}"""), T);
        var snapshot = state.Snapshot();
        Assert.Equal("/sessions/fork.jsonl", snapshot.SessionFile);
        Assert.Equal("fork", snapshot.SessionId);
        Assert.Single(snapshot.Items.OfType<UserItem>());
        Assert.Contains(snapshot.Items, item => item is NoticeItem { Level: NoticeLevel.Warning });
        Assert.True(state.StateRefreshWanted);
    }

    [Fact]
    public void A_reply_the_client_ended_before_its_message_end_is_settled_not_duplicated()
    {
        // Regression (real model, one run in six): omp answers abort after writing the reply's message_end, but the
        // answer reaches the controller directly while that event still waits in the event channel. The abort ended
        // the reply first; the late message_end then added a second copy of it ("Interrupted by user").
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Ready, T);
        s.AddUserPrompt("tell a long story");
        s.SetPhase(SessionPhase.Running, T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"tell a long story"}]}}"""), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"Once upon"}}"""), T);
        s.SetPhase(SessionPhase.Aborting, T);
        s.EndRun(interrupted: true, T); // the abort's answer, applied before the queued events
        s.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":" a time"}}"""), T);
        s.Apply(F("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Once upon a time"}],"stopReason":"aborted","errorMessage":"Interrupted by user"}}"""), T);
        s.Apply(F("""{"type":"agent_end","messages":[]}"""), T);

        var snap = s.Snapshot();
        var reply = Assert.Single(snap.Items.OfType<AssistantItem>());
        Assert.Equal("Once upon a time", reply.Text); // omp's final text, not what the deltas reached before the abort
        Assert.Equal("aborted", reply.StopReason);
        Assert.False(reply.Streaming);
        Assert.Equal(0, snap.StreamMismatches);
        Assert.Equal(SessionPhase.Ready, snap.Phase);

        // The next run's reply is its own row.
        s.AddUserPrompt("again");
        s.SetPhase(SessionPhase.Running, T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"again"}]}}"""), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}],"stopReason":"stop"}}"""), T);
        Assert.Equal(["Once upon a time", "Hello"], s.Snapshot().Items.OfType<AssistantItem>().Select(a => a.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Useful partial answer")]
    public void Retry_removes_only_empty_failed_assistant_rows(string partial)
    {
        var state = new ConversationState();
        state.SetPhase(SessionPhase.Running, T);
        state.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        state.Apply(F(JsonSerializer.Serialize(new
        {
            type = "message_update",
            assistantMessageEvent = new { type = "text_delta", delta = partial }
        })), T);
        state.Apply(F("""{"type":"message_end","message":{"role":"assistant","content":[],"stopReason":"error","errorMessage":"offline"}}"""), T);
        state.Apply(F("""{"type":"auto_retry_start","attempt":1,"maxAttempts":3,"errorMessage":"offline"}"""), T);
        var replies = state.Snapshot().Items.OfType<AssistantItem>();
        if (partial.Length == 0) Assert.Empty(replies);
        else Assert.Equal(partial, Assert.Single(replies).Text);
        Assert.Single(state.Snapshot().Items.OfType<NoticeItem>());
    }

    [Fact]
    public void Retry_progress_replaces_itself_and_disappears_on_success()
    {
        var state = new ConversationState();
        state.SetPhase(SessionPhase.Running, T);
        state.Apply(F("""{"type":"auto_retry_start","attempt":1,"maxAttempts":3,"errorMessage":"offline"}"""), T);
        var key = Assert.Single(state.Snapshot().Items.OfType<NoticeItem>()).Key;
        state.Apply(F("""{"type":"auto_retry_start","attempt":2,"maxAttempts":3,"errorMessage":"offline"}"""), T);
        Assert.Equal(key, Assert.Single(state.Snapshot().Items.OfType<NoticeItem>()).Key);
        state.Apply(F("""{"type":"auto_retry_end","success":true}"""), T);
        Assert.Empty(state.Snapshot().Items.OfType<NoticeItem>());
    }

    [Theory]
    [InlineData("error", false)]
    [InlineData("aborted", false)]
    [InlineData("aborted", true)]
    public void Interrupted_final_preserves_received_text_and_thinking(string reason, bool endedEarly)
    {
        var state = new ConversationState();
        state.SetPhase(SessionPhase.Running, T);
        state.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        state.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"Partial answer already visible"}}"""), T);
        state.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"thinking_delta","delta":"Partial reasoning already visible"}}"""), T);
        if (endedEarly) state.EndRun(interrupted: true, T);
        state.Apply(F(JsonSerializer.Serialize(new
        {
            type = "message_end",
            message = new { role = "assistant", content = Array.Empty<object>(), stopReason = reason, errorMessage = "Interrupted" }
        })), T);
        var reply = Assert.Single(state.Snapshot().Items.OfType<AssistantItem>());
        Assert.Equal("Partial answer already visible", reply.Text);
        Assert.Equal("Partial reasoning already visible", reply.Thinking);
        Assert.Equal(reason, reply.StopReason);
        Assert.False(reply.Streaming);
        state.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        state.Apply(F("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Successful retry"}],"stopReason":"stop"}}"""), T);
        Assert.Equal(["Partial answer already visible", "Successful retry"],
            state.Snapshot().Items.OfType<AssistantItem>().Select(item => item.Text));
    }

    [Fact]
    public void Ended_runs_are_counted_so_a_sampling_reader_sees_a_run_it_never_saw_running()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Ready, T);
        Assert.Equal(0, s.Snapshot().RunsEnded);
        s.SetPhase(SessionPhase.Running, T);
        s.SetPhase(SessionPhase.Ready, T);                  // finished
        s.SetPhase(SessionPhase.Running, T);
        s.SetPhase(SessionPhase.Aborting, T);
        s.SetPhase(SessionPhase.Ready, T);                  // stopped by the user
        s.SetPhase(SessionPhase.Running, T);
        s.SetPhase(SessionPhase.Faulted, T);                // omp died mid-run
        s.SetPhase(SessionPhase.Starting, T);
        s.SetPhase(SessionPhase.Ready, T);                  // a restart is not a run
        s.SetPhase(SessionPhase.Running, T);
        s.SetPhase(SessionPhase.Stopping, T);
        s.SetPhase(SessionPhase.Stopped, T);                // closing the app is not a run ending
        Assert.Equal(3, s.Snapshot().RunsEnded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pending_local_or_cancelled_input_does_not_count_as_an_ended_agent_turn(bool interrupted)
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Ready, T);
        s.BeginRun(T);
        if (interrupted) s.SetPhase(SessionPhase.Aborting, T.AddSeconds(1));
        s.EndPendingRun(interrupted, T.AddSeconds(5));

        var done = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, done.Phase);
        Assert.Equal(0, done.RunsEnded);
        Assert.Null(done.RunStartedAt);
        Assert.Empty(done.Items.OfType<TurnEndItem>());
    }

    [Fact]
    public void A_local_prompt_result_does_not_count_a_pending_start_as_an_agent_turn()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Ready, T);
        s.BeginRun(T);
        s.Apply(F("""{"type":"prompt_result","agentInvoked":false,"status":"completed"}"""), T.AddSeconds(5));

        var done = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, done.Phase);
        Assert.Equal(0, done.RunsEnded);
        Assert.Null(done.RunStartedAt);
        Assert.Empty(done.Items.OfType<TurnEndItem>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_agent_started_during_input_admission_survives_pending_settlement_and_counts_once(bool interrupted)
    {
        var clock = new PowerLifecycleTests.ManualClock();
        var s = new ConversationState(clock);
        s.SetPhase(SessionPhase.Ready, T);
        s.BeginRun(T);
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.EndPendingRun(interrupted, T.AddSeconds(1));

        var running = s.Snapshot();
        Assert.Equal(SessionPhase.Running, running.Phase);
        Assert.Equal(0, running.RunsEnded);
        Assert.Equal(T, running.RunStartedAt);
        Assert.Empty(running.Items.OfType<TurnEndItem>());

        clock.Advance(TimeSpan.FromSeconds(5));
        s.Apply(F("""{"type":"agent_end","messages":[],"isTerminal":true}"""), T.AddSeconds(5));
        s.Apply(F("""{"type":"prompt_result","agentInvoked":true,"status":"completed","sessionSettled":true}"""), T.AddSeconds(5));
        s.Apply(F("""{"type":"session_settled"}"""), T.AddSeconds(5));
        var done = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, done.Phase);
        Assert.Equal(1, done.RunsEnded);
        Assert.Null(done.RunStartedAt);
        var ended = Assert.Single(done.Items.OfType<TurnEndItem>());
        Assert.Equal(TimeSpan.FromSeconds(5), ended.Took);
        Assert.False(ended.Interrupted);
    }

    /// <summary>A stdout recording of real omp 18.2.0 (prompt → streamed text → read tool → final answer).</summary>
    [Fact]
    public void Folds_a_real_omp_recording_into_the_expected_transcript()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Ready, T);
        s.AddUserPrompt("TOOLCALL please");
        s.SetPhase(SessionPhase.Running, T);
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "omp-18.2.0-toolcall.jsonl")))
        {
            var f = F(line);
            if (f.Type is "ready" or "response") continue; // responses go to the awaiting caller, not the state
            s.Apply(f, T);
        }
        var snap = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, snap.Phase);
        Assert.Collection(snap.Items,
            // The echo confirms the prompt and brings the time omp saved with it
            i => Assert.Equal(new UserItem(1, "TOOLCALL please", true) { At = DateTimeOffset.FromUnixTimeMilliseconds(1790278392200) }, i),
            i => Assert.Equal(("Reading the file first.", true), (Assert.IsType<AssistantItem>(i).Text, ((AssistantItem)i).At is not null)),
            i =>
            {
                var t = Assert.IsType<ToolItem>(i);
                Assert.Equal("read", t.Name);
                Assert.Equal(ToolStatus.Succeeded, t.Status);
                Assert.Contains("hello readme", t.Output);
            },
            i => Assert.Equal("I read the file. It exists and has content. TOOLCALL done.", Assert.IsType<AssistantItem>(i).Text),
            i => Assert.Equal(1, Assert.IsType<TurnEndItem>(i).Tools));
        Assert.All(snap.Items.OfType<AssistantItem>(), a => Assert.False(a.Streaming));
        Assert.Equal(0, snap.StreamMismatches);
        Assert.Equal(2, snap.MessagesCompleted);
    }

    [Fact]
    public void Final_message_text_wins_and_a_lost_delta_is_counted()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"Hel"}}"""), T);
        // "lo" delta lost
        s.Apply(F("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}]}}"""), T);
        var snap = s.Snapshot();
        Assert.Equal("Hello", ((AssistantItem)snap.Items[0]).Text);
        Assert.Equal(1, snap.StreamMismatches);
    }

    [Fact]
    public void Non_terminal_agent_end_keeps_the_run_going()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.Apply(F("""{"type":"agent_end","messages":[],"isTerminal":false}"""), T);
        Assert.Equal(SessionPhase.Running, s.Phase);
        s.Apply(F("""{"type":"agent_end","messages":[]}"""), T);
        Assert.Equal(SessionPhase.Ready, s.Phase);
    }

    [Fact]
    public void Abort_and_crash_leave_no_row_spinning()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"tool_execution_start","toolCallId":"a","toolName":"bash","args":{}}"""), T);
        s.Fail("omp exited unexpectedly", T);
        var snap = s.Snapshot();
        Assert.Equal(SessionPhase.Faulted, snap.Phase);
        Assert.False(snap.Items.OfType<AssistantItem>().Single().Streaming);
        Assert.Equal(ToolStatus.Interrupted, snap.Items.OfType<ToolItem>().Single().Status);
        Assert.Contains(snap.Items, i => i is NoticeItem { Level: NoticeLevel.Error });
    }

    [Fact]
    public void Approval_and_dialog_requests_become_pending_dialogs_one_way_requests_do_not()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"extension_ui_request","id":"d1","method":"select","title":"Allow tool: bash\n$ rm -rf build","options":["Approve","Deny"]}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"d2","method":"select","title":"Pick one","options":["A (Recommended)","B"],"optionDetails":[{"description":"first"},{}],"timeout":30000}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"d3","method":"confirm","title":"Sure?","message":"This deletes files"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"d4","method":"input","title":"Name","placeholder":"type here"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"d5","method":"editor","title":"Edit","prefill":"line1\nline2"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"d1","method":"select","title":"duplicate","options":["Approve","Deny"]}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"w1","method":"setWidget","widgetKey":"autoresearch","widgetLines":["one","two"]}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"s1","method":"setStatus","statusKey":"git","statusText":"main*"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"n1","method":"notify","message":"hello","notifyType":"warning"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"e1","method":"set_editor_text","text":"prefilled"}"""), T);

        var snap = s.Snapshot();
        Assert.Collection(snap.Dialogs,
            d =>
            {
                Assert.Equal(DialogKind.Approval, d.Kind);
                Assert.Equal("Allow tool: bash", d.Headline);
                Assert.Equal("$ rm -rf build", d.Details);
                Assert.Null(d.Deadline);
            },
            d =>
            {
                Assert.Equal(DialogKind.Select, d.Kind);
                Assert.True(d.Options[0].Recommended);
                Assert.Equal("first", d.Options[0].Description);
                Assert.Null(d.Options[1].Description);
                Assert.Equal(T + TimeSpan.FromSeconds(30) - ConversationState.DialogDeadlineMargin, d.Deadline);
            },
            d => Assert.Equal((DialogKind.Confirm, "This deletes files"), (d.Kind, d.Message)),
            d => Assert.Equal((DialogKind.Input, "type here"), (d.Kind, d.Placeholder)),
            d => Assert.Equal((DialogKind.Editor, "line1\nline2"), (d.Kind, d.Prefill)));
        Assert.Equal(["d2"], s.NewDeadlines.Select(d => d.Id));
        Assert.Equal(["one", "two"], Assert.Single(snap.Widgets).Lines);
        Assert.Equal("main*", snap.ExtensionStatus["git"]);
        Assert.Equal("prefilled", snap.EditorText!.Text);
        Assert.Equal(NoticeLevel.Warning, Assert.Single(snap.Items.OfType<NoticeItem>()).Level);

        // Clearing: an empty status and a widget without lines remove them.
        s.Apply(F("""{"type":"extension_ui_request","id":"s2","method":"setStatus","statusKey":"git"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"w2","method":"setWidget","widgetKey":"autoresearch"}"""), T);
        Assert.Empty(s.Snapshot().ExtensionStatus);
        Assert.Empty(s.Snapshot().Widgets);
    }

    [Fact]
    public void Nulls_and_odd_types_in_dialog_fields_are_tolerated()
    {
        // Regression: omp may serialize an absent timeout as null; reading it as a number threw and stopped the pump.
        var s = new ConversationState();
        s.Apply(F("""{"type":"extension_ui_request","id":"a","method":"select","title":"Allow tool: bash","options":["Approve","Deny"],"timeout":null}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"b","method":"select","title":null,"options":[1,{"x":2}],"optionDetails":[null,"x"],"timeout":"soon"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"c","method":"confirm","title":"t","timeout":1e300}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"w","method":"setWidget","widgetKey":"k","widgetLines":"not an array"}"""), T);
        var d = s.Snapshot().Dialogs;
        Assert.Equal(3, d.Count);
        Assert.All(d, x => Assert.Null(x.Deadline));
        Assert.Equal(["1", """{"x":2}"""], d[1].Options.Select(o => o.Label));
    }

    [Fact]
    public void A_late_echo_of_the_prompt_does_not_consume_a_queued_message()
    {
        // Regression: the prompt's echo arrived after the client had already ended the run (abort acknowledged),
        // matched no pending row, took the queued follow-up and duplicated the prompt row.
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Ready, T);
        s.AddUserPrompt("long task");
        s.SetPhase(SessionPhase.Running, T);
        s.Enqueue(QueueKind.FollowUp, "later", []);
        s.EndRun(interrupted: true, T);
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"long task"}]}}"""), T);
        var snap = s.Snapshot();
        Assert.Equal("later", Assert.Single(snap.Queued).Text);
        Assert.Single(snap.Items.OfType<UserItem>());

        // Delivered with the same text: dequeued. Expanded by omp (other text): the oldest is taken.
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"later"}]}}"""), T);
        Assert.Empty(s.Snapshot().Queued);
        s.Enqueue(QueueKind.FollowUp, "/tmpl arg", []);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"expanded template text"}]}}"""), T);
        Assert.Empty(s.Snapshot().Queued);
        Assert.Equal(["long task", "later", "expanded template text"], s.Snapshot().Items.OfType<UserItem>().Select(u => u.Text));
    }

    [Fact]
    public void Dialogs_end_by_answer_withdrawal_deadline_or_shutdown_and_each_end_is_recorded()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"extension_ui_request","id":"a","method":"select","title":"Allow tool: bash","options":["Approve","Deny"],"approval":{"tool":"bash","toolCallId":"t1","risk":"exec","details":[],"command":"ls","scopes":["once","session","always"]}}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"b","method":"confirm","title":"Go?","message":"m","timeout":5000}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"c","method":"input","title":"Name"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"d","method":"input","title":"Other"}"""), T);

        var a = s.TakeDialog("a")!;
        s.RecordAnswer(a, new DialogAnswer.Value("Approve"), "by you");
        Assert.Null(s.TakeDialog("a")); // a second click finds nothing to answer

        s.ExpireDialogs(T + TimeSpan.FromSeconds(3)); // not yet
        Assert.Equal(3, s.Dialogs.Count);
        s.ExpireDialogs(T + TimeSpan.FromSeconds(4)); // 5 s minus the safety margin
        Assert.DoesNotContain(s.Dialogs, d => d.Id == "b");

        s.Apply(F("""{"type":"extension_ui_request","id":"x","method":"cancel","targetId":"c"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"y","method":"cancel","targetId":"unknown"}"""), T);
        Assert.Equal(["d"], s.Dialogs.Select(d => d.Id));

        Assert.Equal(["d"], s.CloseAllDialogs("omp was stopped").Select(d => d.Id));
        Assert.Empty(s.Snapshot().Dialogs);

        var notices = s.Snapshot().Items.OfType<NoticeItem>().Select(n => n.Text).ToList();
        Assert.Collection(notices,
            n => Assert.Equal("Approved by you — bash", n),
            n => Assert.StartsWith("Question timed out without an answer; omp applied its default — Go?", n),
            n => Assert.Equal("Question withdrawn by omp — Name", n),
            n => Assert.Equal("Question closed (omp was stopped) — Other", n));
    }

    [Fact]
    public void Async_prompt_failure_ends_the_run_with_an_error()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Running, T);
        s.Apply(F("""{"type":"_unmatched_response","command":"prompt","success":false,"error":"model unavailable"}"""), T);
        Assert.Equal(SessionPhase.Ready, s.Phase);
        Assert.Contains(s.Snapshot().Items, i => i is NoticeItem n && n.Text.Contains("model unavailable"));
    }

    [Fact]
    public void Hydrates_history_from_get_messages()
    {
        var s = new ConversationState();
        var messages = JsonDocument.Parse("""
            [{"role":"user","content":[{"type":"text","text":"hi"}]},
             {"role":"assistant","content":[{"type":"text","text":"calling"},{"type":"toolCall","id":"c1","name":"read","arguments":{"path":"a"}}]},
             {"role":"toolResult","toolCallId":"c1","toolName":"read","content":[{"type":"text","text":"body"}],"isError":false},
             {"role":"assistant","content":"plain string content"}]
            """).RootElement.Clone();
        s.Hydrate(messages);
        var items = s.Snapshot().Items;
        Assert.Equal(4, items.Count);
        Assert.Equal(ToolStatus.Succeeded, ((ToolItem)items[2]).Status);
        Assert.Equal("plain string content", ((AssistantItem)items[3]).Text);
    }

    [Fact]
    public void Visible_runtime_context_keeps_live_and_reopened_order_images_and_timestamps()
    {
        using var document = JsonDocument.Parse("""
            [
              {"role":"custom","customType":"extension-note","display":true,"timestamp":1000,"content":[{"type":"text","text":"**Visible**"},{"type":"image","mimeType":"image/png","data":"AQID"},{"type":"image","data":"blob:sha256:missing"}]},
              {"role":"hookMessage","customType":"legacy-note","display":true,"timestamp":2000,"content":"Legacy"},
              {"role":"branchSummary","summary":"Branch body","fromId":"prior","timestamp":3000},
              {"role":"compactionSummary","summary":"Compacted body","tokensBefore":9000,"timestamp":4000,"blocks":[{"type":"text","text":"Archive text"},{"type":"image","mimeType":"image/png","data":"BAU="}],"warning":"Compaction warning"},
              {"role":"fileMention","timestamp":5000,"files":[{"path":"photo.png","content":"private auto-read payload","image":{"type":"image","mimeType":"image/png","data":"Bgc="}},{"path":"large.bin","content":"","skippedReason":"tooLarge"}]},
              {"role":"pythonExecution","code":"print(7)","output":"7","exitCode":1,"cancelled":false,"timestamp":6000},
              {"role":"bashExecution","command":"echo done","output":"done","exitCode":0,"cancelled":false,"timestamp":7000,"images":[{"type":"image","mimeType":"image/png","data":"CAk="}]}
            ]
            """);
        var live = new ConversationState();
        foreach (var message in document.RootElement.EnumerateArray())
        {
            live.Apply(F($$"""{"type":"message_start","message":{{message.GetRawText()}}}"""), T);
            live.Apply(F($$"""{"type":"message_end","message":{{message.GetRawText()}}}"""), T);
        }
        var reopened = new ConversationState();
        reopened.Hydrate(document.RootElement);
        // Serialized row values include attachment bytes, order, stable keys, and timestamps.
        Assert.Equal(JsonSerializer.Serialize(reopened.Snapshot().Items.Select(item => (object)item)),
            JsonSerializer.Serialize(live.Snapshot().Items.Select(item => (object)item)));
        var rows = reopened.Snapshot().Items;
        Assert.Equal(9, rows.Count);
        var custom = Assert.IsType<CommandOutputItem>(rows[0]);
        Assert.Equal("extension-note", custom.Label);
        Assert.Equal("**Visible**", custom.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), custom.At);
        Assert.Equal(new byte[] { 1, 2, 3 }, custom.Images[0].Data);
        Assert.Empty(custom.Images[1].Data);
        Assert.Equal("Legacy", Assert.IsType<CommandOutputItem>(rows[1]).Text);
        Assert.Equal("Branch body", Assert.IsType<CommandOutputItem>(rows[2]).Text);
        var compacted = Assert.IsType<CommandOutputItem>(rows[3]);
        Assert.Equal("Compacted body\n\nArchive text\n\nCompaction warning", compacted.Text);
        Assert.Single(compacted.Images);
        Assert.Equal("photo.png", Assert.IsType<CommandOutputItem>(rows[4]).Text);
        Assert.Equal("large.bin (tooLarge)", Assert.IsType<CommandOutputItem>(rows[5]).Text);
        var python = Assert.IsType<ToolItem>(rows[6]);
        Assert.Equal(("eval", "print(7)", ToolStatus.Failed, "7\nexit code 1"), (python.Name, python.Summary, python.Status, python.Output));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(6000), python.StartedAt);
        Assert.Single(Assert.IsType<CommandOutputItem>(rows[8]).Images);
    }

    [Fact]
    public void Reload_replaces_persisted_context_without_duplicating_it_or_discarding_command_output()
    {
        using var document = JsonDocument.Parse("""[{"role":"custom","customType":"note","display":true,"content":"persisted"}]""");
        var state = new ConversationState();
        state.Hydrate(document.RootElement);
        state.Apply(F("""{"type":"command_output","text":"local command output"}"""), T);
        state.ReplaceHistory(document.RootElement);
        state.ReplaceHistory(document.RootElement);
        Assert.Equal(["persisted", "local command output"], state.Snapshot().Items.OfType<CommandOutputItem>().Select(item => item.Text));
    }

    [Fact]
    public async Task Visible_context_cannot_complete_a_background_command()
    {
        await using var controller = new SessionController(TestProcesses.Fake("history"));
        var before = controller.Snapshot();
        controller.Mutate(state =>
        {
            state.Hydrate(JsonDocument.Parse("""[{"role":"custom","customType":"extension","display":true,"content":"not command output"}]""").RootElement);
            state.Apply(F("""{"type":"command_output","text":"actual command output"}"""), T);
        });
        Assert.Equal("actual command output", await controller.WaitForCommandOutputAsync(before, _ => true, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("custom", "extension-note")]
    [InlineData("hookMessage", "legacy-note")]
    [InlineData("custom", "irc:incoming")]
    [InlineData("custom", "irc:relay")]
    public void Hidden_custom_content_and_details_never_become_transcript_rows(string role, string customType)
    {
        foreach (var display in new[] { ",\"display\":false", "" })
        {
            var message = $$$"""{"role":"{{{role}}}","customType":"{{{customType}}}"{{{display}}},"content":"secret content","details":{"from":"peer","message":"secret details"}}""";
            var state = new ConversationState();
            state.Hydrate(JsonDocument.Parse("[" + message + "]").RootElement);
            foreach (var kind in new[] { "message_start", "message_end", "irc_message" })
                state.Apply(F($$"""{"type":"{{kind}}","message":{{message}}}"""), T);
            Assert.Empty(state.Snapshot().Items);
        }
    }

    [Fact]
    public void Irc_live_observation_and_persisted_lifecycle_render_once_and_reset_for_the_next_session()
    {
        const string message = """{"role":"custom","customType":"irc:incoming","display":true,"content":"model envelope","details":{"id":"irc-1","from":"peer","message":"visible body"}}""";
        var state = new ConversationState();
        foreach (var kind in new[] { "irc_message", "message_start", "message_end" })
            state.Apply(F($$"""{"type":"{{kind}}","message":{{message}}}"""), T);
        Assert.Equal("visible body", Assert.IsType<AgentMessageItem>(Assert.Single(state.Snapshot().Items)).Text);
        state.ResetTranscript();
        state.Apply(F($$"""{"type":"message_end","message":{{message}}}"""), T);
        Assert.Single(state.Snapshot().Items);
    }

    [Fact]
    public void Context_row_is_not_command_output_or_a_compaction_marker()
    {
        using var row = new Ombra.App.ViewModels.CommandOutputRowViewModel(new CommandOutputItem(1, "Compaction complete. Tokens: 9 -> 3")
        {
            Label = "extension-note", At = T, Images = [new ImageAttachment("missing.png", "image/png", [])],
        });
        Assert.True(row.IsContext);
        Assert.False(row.IsOutput);
        Assert.False(row.IsMarker);
        Assert.True(row.HasTime);
        Assert.True(row.HasImages);
        Assert.Equal("missing.png", Assert.Single(row.Previews).Name);
    }

    [Fact]
    public void Streaming_a_long_reply_stays_linear()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        var delta = F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"0123456789"}}""");
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++) s.Apply(delta, T);
        Assert.Equal(1_000_000, ((AssistantItem)s.Snapshot().Items[0]).Text.Length);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"100k deltas took {sw.Elapsed}");
    }

    [Fact]
    public void Prompt_result_settles_an_aborting_run()
    {
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Aborting, T);
        s.Apply(F("""{"type":"prompt_result","agentInvoked":false}"""), T);
        Assert.Equal(SessionPhase.Ready, s.Phase);
    }

    [Fact]
    public void Expanded_prompt_echo_confirms_the_optimistic_row()
    {
        var s = new ConversationState();
        s.AddUserPrompt("/review");
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"Review the current diff carefully…"}]}}"""), T);
        var users = s.Snapshot().Items.OfType<UserItem>().ToList();
        Assert.Single(users);
        Assert.True(users[0].Confirmed);
        Assert.StartsWith("Review the current diff", users[0].Text);
    }

    [Fact]
    public void Long_tool_output_keeps_head_and_tail()
    {
        var s = new ConversationState();
        var body = "START" + new string('x', 20_000) + "FINAL ERROR LINE";
        s.Apply(F("""{"type":"tool_execution_start","toolCallId":"a","toolName":"bash","args":{}}"""), T);
        s.Apply(F(System.Text.Json.JsonSerializer.Serialize(new { type = "tool_execution_end", toolCallId = "a", toolName = "bash", result = body, isError = true })), T);
        var t = s.Snapshot().Items.OfType<ToolItem>().Single();
        Assert.StartsWith("START", t.Output);
        Assert.EndsWith("FINAL ERROR LINE", t.Output);
        Assert.True(t.Output!.Length < 4200);
    }

    [Fact]
    public void Non_terminal_agent_end_waits_for_settle_and_session_settled_ends_the_run()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.Apply(F("""{"type":"agent_end","messages":[],"isTerminal":false}"""), T);
        Assert.True(s.AwaitingSettle);
        Assert.Equal(SessionPhase.Running, s.Phase);
        s.Apply(F("""{"type":"agent_start"}"""), T);
        Assert.False(s.AwaitingSettle);
        s.Apply(F("""{"type":"agent_end","messages":[],"isTerminal":false}"""), T);
        s.Apply(F("""{"type":"session_settled"}"""), T);
        Assert.False(s.AwaitingSettle);
        Assert.Equal(SessionPhase.Ready, s.Phase);
    }

    /// <summary>
    /// omp 18.8.0 ends a run with agent_end, prompt_result and session_settled together. A prompt sent while the last
    /// two were still on their way (real-omp test, and a user who types fast) was shown as idle while omp worked.
    /// </summary>
    [Fact]
    public void The_previous_runs_end_does_not_end_the_next_prompt()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.Apply(F("""{"type":"agent_end","messages":[]}"""), T);
        Assert.Equal(SessionPhase.Ready, s.Phase);
        s.AddUserPrompt("next");
        s.BeginRun(T);
        s.Apply(F("""{"type":"prompt_result","id":"p1","agentInvoked":true,"status":"completed","sessionSettled":true}"""), T);
        s.Apply(F("""{"type":"session_settled"}"""), T);
        Assert.Equal(SessionPhase.Running, s.Phase);
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.Apply(F("""{"type":"agent_end","messages":[]}"""), T);
        s.Apply(F("""{"type":"session_settled"}"""), T);
        Assert.Equal(SessionPhase.Ready, s.Phase);

        // A prompt omp handles without a run (a slash command) still ends at once.
        s.BeginRun(T);
        s.Apply(F("""{"type":"prompt_result","id":"p3","agentInvoked":false,"status":"completed"}"""), T);
        Assert.Equal(SessionPhase.Ready, s.Phase);
    }

    [Fact]
    public void Late_run_events_do_not_leave_the_stopping_phase()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.SetPhase(SessionPhase.Stopping, T);
        s.Apply(F("""{"type":"agent_end","messages":[]}"""), T);
        Assert.Equal(SessionPhase.Stopping, s.Phase);
        s.SetPhase(SessionPhase.Stopped, T);
        Assert.Equal(SessionPhase.Stopped, s.Phase);
    }

    [Fact]
    public void A_call_omp_set_aside_for_a_message_that_came_first_is_skipped_not_failed()
    {
        // omp 18.8.0 (agent-loop.ts createSkippedToolResult): isError with details.source "interrupt_skipped"; its own
        // terminal shows it neutrally. A real error of the same tool still fails.
        const string skippedText = "Skipped due to a queued background completion (job or supervised process). Do not count this skipped result as completed work or verification.";
        var s = new ConversationState();
        foreach (var (id, details) in new[] { ("w1", """{"__interrupted":true,"source":"interrupt_skipped","execution":"started"}"""),
                     ("w2", """{"__synthetic":true,"source":"interrupt_skipped","executed":false}"""), ("w3", """{"source":"other"}""") })
        {
            s.Apply(F($$$"""{"type":"tool_execution_start","toolCallId":"{{{id}}}","toolName":"wait","args":{}}"""), T);
            s.Apply(F($$$"""{"type":"tool_execution_end","toolCallId":"{{{id}}}","toolName":"wait","isError":true,"result":{"content":[{"type":"text","text":"{{{skippedText}}}"}],"details":{{{details}}}}}"""), T);
        }
        Assert.Equal([ToolStatus.Skipped, ToolStatus.Skipped, ToolStatus.Failed], s.Snapshot().Items.OfType<ToolItem>().Select(t => t.Status));

        // The same from the history
        var h = new ConversationState();
        h.Hydrate(JsonDocument.Parse($$$"""
            [{"role":"assistant","content":[{"type":"toolCall","id":"w1","name":"wait","arguments":{}}]},
             {"role":"toolResult","toolCallId":"w1","toolName":"wait","isError":true,"content":[{"type":"text","text":"{{{skippedText}}}"}],
              "details":{"__interrupted":true,"source":"interrupt_skipped","execution":"started"}}]
            """).RootElement);
        Assert.Equal(ToolStatus.Skipped, h.Snapshot().Items.OfType<ToolItem>().Single().Status);
        Assert.Equal(Ombra.App.Localization.Loc.T("Skipped: a background job finished first"), Ombra.App.ViewModels.ToolRowViewModel.SkippedFor(skippedText));
    }

    [Fact]
    public void An_agents_message_is_a_message_row_with_its_words_not_omps_envelope()
    {
        // irc_message (omp 18.8.0 irc-bridge.ts): the content is the model's envelope ("<irc>\nIncoming IRC message from
        // agent `X`: …"); the sender and the words are in details. Relays between two others name both.
        var s = new ConversationState();
        s.Apply(F("""{"type":"irc_message","message":{"role":"custom","customType":"irc:incoming","display":true,"content":"<irc>\nIncoming IRC message from agent `GallerySolutions`:\n\n**Found** it\n","details":{"id":"m1","from":"GallerySolutions","message":"**Found** it"}}}"""), T);
        s.Apply(F("""{"type":"irc_message","message":{"role":"custom","customType":"irc:relay","display":true,"content":"[IRC `A` → `B`]\n\nhi","details":{"from":"A","to":"B","body":"hi"}}}"""), T);
        s.Apply(F("""{"type":"irc_message","message":{"role":"custom","customType":"irc:incoming","display":true,"content":"<irc>\nIncoming IRC message from agent `C` (reply to m1):\n\nok","details":{"id":"m2","from":"C","message":"ok","replyTo":"m1"}}}"""), T);
        Assert.Equal([("GallerySolutions", (string?)null, "**Found** it", false), ("A", "B", "hi", false), ("C", null, "ok", true)],
            s.Snapshot().Items.OfType<AgentMessageItem>().Select(m => (m.From, m.To, m.Text, m.IsReply)));
        Assert.Empty(s.Snapshot().Items.OfType<NoticeItem>());

        // From the history: the incoming ones omp keeps with the session
        var h = new ConversationState();
        h.Hydrate(JsonDocument.Parse("""[{"role":"custom","customType":"irc:incoming","display":true,"content":"<irc>…","details":{"id":"m1","from":"GallerySolutions","message":"**Found** it"}}]""").RootElement);
        Assert.Equal(("GallerySolutions", "**Found** it"), h.Snapshot().Items.OfType<AgentMessageItem>().Select(m => (m.From, m.Text)).Single());

        var row = new Ombra.App.ViewModels.AgentMessageRowViewModel(new AgentMessageItem(1, "C", null, "## Result\n**Found** `it`\n- one", true));
        Assert.Equal(("Result Found it - one", Ombra.App.Localization.Loc.T("→ omp") + " · " + Ombra.App.Localization.Loc.T("reply")), (row.Preview, row.Route));
    }

    [Fact]
    public void The_clients_own_failure_notice_reads_in_the_ui_language_and_keeps_omps_words()
    {
        // Regression (ru): "Starting omp failed: …" and "omp exited unexpectedly …" notices stayed English.
        var s = new ConversationState();
        var text = new ClientText("Starting omp failed: {0}", "spawn EACCES");
        s.AddNotice(NoticeLevel.Error, text + "\nbun: permission denied", text);
        var item = s.Snapshot().Items.OfType<NoticeItem>().Single();
        Assert.Equal("Starting omp failed: spawn EACCES\nbun: permission denied", item.Text); // the log keeps the English
        Assert.Equal(Ombra.App.Localization.Loc.F("Starting omp failed: {0}", "spawn EACCES") + "\nbun: permission denied",
            new Ombra.App.ViewModels.NoticeRowViewModel(item).Text);
    }
}
