using System.Diagnostics;
using System.Text.Json;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

public sealed class ConversationStateTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-24T12:00:00Z");

    private static RpcFrame F(string json)
    {
        var e = JsonDocument.Parse(json).RootElement.Clone();
        return new RpcFrame(e.GetProperty("type").GetString()!, null, e, Stopwatch.GetTimestamp(), json.Length + 1);
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

    [Fact]
    public void Text_omp_drops_from_a_failed_message_is_not_counted_as_lost_stream_data()
    {
        // Real model (SmolLM2): omp 18.2.0 detected a "thinking loop", ended that message with stopReason error and
        // empty content, and retried. The deltas it had streamed are omp's choice to drop, not the client losing data.
        var s = new ConversationState();
        s.SetPhase(SessionPhase.Ready, T);
        s.AddUserPrompt("hi");
        s.SetPhase(SessionPhase.Running, T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"hi"}]}}"""), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"la la la la la la"}}"""), T);
        s.Apply(F("""{"type":"message_end","message":{"role":"assistant","content":[],"stopReason":"error","errorMessage":"Thinking loop detected: retrying."}}"""), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"Hello"}}"""), T);
        s.Apply(F("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}],"stopReason":"stop"}}"""), T);
        var snap = s.Snapshot();
        Assert.Equal(0, snap.StreamMismatches);
        Assert.Collection(snap.Items.OfType<AssistantItem>(),
            failed => { Assert.Equal("", failed.Text); Assert.Equal("error", failed.StopReason); Assert.StartsWith("Thinking loop", failed.Error); },
            ok => Assert.Equal("Hello", ok.Text));

        // A normally finished message whose deltas do not add up is still counted: that is the data-loss detector.
        s.Apply(F("""{"type":"message_start","message":{"role":"assistant","content":[]}}"""), T);
        s.Apply(F("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"Hel"}}"""), T);
        s.Apply(F("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}],"stopReason":"stop"}}"""), T);
        Assert.Equal(1, s.Snapshot().StreamMismatches);
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
            i => Assert.Equal(new UserItem(1, "TOOLCALL please", true), i),
            i => Assert.Equal("Reading the file first.", Assert.IsType<AssistantItem>(i).Text),
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
        s.Enqueue(QueueKind.FollowUp, "later", 0);
        s.EndRun(interrupted: true, T);
        s.Apply(F("""{"type":"agent_start"}"""), T);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"long task"}]}}"""), T);
        var snap = s.Snapshot();
        Assert.Equal("later", Assert.Single(snap.Queued).Text);
        Assert.Single(snap.Items.OfType<UserItem>());

        // Delivered with the same text: dequeued. Expanded by omp (other text): the oldest is taken.
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"later"}]}}"""), T);
        Assert.Empty(s.Snapshot().Queued);
        s.Enqueue(QueueKind.FollowUp, "/tmpl arg", 0);
        s.Apply(F("""{"type":"message_start","message":{"role":"user","content":[{"type":"text","text":"expanded template text"}]}}"""), T);
        Assert.Empty(s.Snapshot().Queued);
        Assert.Equal(["long task", "later", "expanded template text"], s.Snapshot().Items.OfType<UserItem>().Select(u => u.Text));
    }

    [Fact]
    public void Dialogs_end_by_answer_withdrawal_deadline_or_shutdown_and_each_end_is_recorded()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"extension_ui_request","id":"a","method":"select","title":"Allow tool: bash","options":["Approve","Deny"]}"""), T);
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
            n => Assert.Equal("Approved by you — Allow tool: bash", n),
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
}
