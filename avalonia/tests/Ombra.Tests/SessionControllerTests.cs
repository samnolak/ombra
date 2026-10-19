using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class SessionControllerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Prompt_streams_to_a_final_transcript_and_returns_to_ready()
    {
        await using var s = new SessionController(TestProcesses.Fake("normal"));
        await s.StartAsync();
        var start = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, start.Phase);
        Assert.Equal(2, start.ProtocolVersion);
        Assert.Equal("fake/model", start.Model);

        await s.PromptAsync("hello");
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted == 1, Wait, "run end");
        Assert.Collection(done.Items,
            i => Assert.True(Assert.IsType<UserItem>(i).Confirmed),
            i => Assert.StartsWith("tok0 tok1", Assert.IsType<AssistantItem>(i).Text),
            i => Assert.Equal(ToolStatus.Succeeded, Assert.IsType<ToolItem>(i).Status),
            // the run's end stays in the conversation (one tool; a duration)
            i => Assert.Equal((1, false), (Assert.IsType<TurnEndItem>(i).Tools, ((TurnEndItem)i).Interrupted)));
        Assert.Equal(0, done.StreamMismatches);
    }

    [Fact]
    public async Task Abort_stops_a_streaming_run_and_marks_it_interrupted()
    {
        await using var s = new SessionController(TestProcesses.Fake("slow-stream"));
        await s.StartAsync();
        await s.PromptAsync("go");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<AssistantItem>().Any(a => a.Text.Length > 50), Wait, "streaming");
        await s.AbortAsync();
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "abort settles");
        var a = snap.Items.OfType<AssistantItem>().Single();
        Assert.False(a.Streaming);
        Assert.Equal("aborted", a.StopReason);
    }

    [Fact]
    public async Task Start_failure_faults_the_session_with_stderr()
    {
        await using var s = new SessionController(TestProcesses.Fake("crash-before-ready"), TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<Ombra.Rpc.OmpStartException>(() => s.StartAsync());
        var snap = s.Snapshot();
        Assert.Equal(SessionPhase.Faulted, snap.Phase);
        Assert.Contains("no model configured", snap.LastError);
    }

    [Fact]
    public async Task Crash_during_a_run_faults_and_finalizes_rows()
    {
        await using var s = new SessionController(TestProcesses.Fake("slow-stream"));
        await s.StartAsync();
        await s.PromptAsync("go");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<AssistantItem>().Any(a => a.Text.Length > 20), Wait, "streaming");
        var pid = s.ProcessId!.Value;
        System.Diagnostics.Process.GetProcessById(pid).Kill();
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Faulted, Wait, "fault");
        Assert.False(snap.Items.OfType<AssistantItem>().Single().Streaming);
        Assert.Contains("exited unexpectedly", snap.LastError);
    }

    [Theory]
    [InlineData(QueueKind.FollowUp)]
    [InlineData(QueueKind.Steer)]
    public async Task Input_queued_after_crash_finalization_is_recovered_without_resending(QueueKind kind)
    {
        await using var session = new SessionController(TestProcesses.Fake("slow-stream"));
        await session.StartAsync();
        await session.PromptAsync("running question");
        await TestProcesses.Eventually(session.Snapshot,
            snapshot => snapshot.Items.OfType<AssistantItem>().Any(), Wait, "streaming");
        using (var process = System.Diagnostics.Process.GetProcessById(session.ProcessId!.Value)) process.Kill();
        await TestProcesses.Eventually(session.Snapshot,
            snapshot => snapshot.Phase == SessionPhase.Faulted, Wait, "crash finalized");

        // A stale Running UI can submit after the fault has already recovered the existing queue.
        Assert.False(await session.QueueAsync(kind, "unsent input", []));
        var failed = session.Snapshot();
        Assert.Equal(SessionPhase.Faulted, failed.Phase);
        Assert.Equal("unsent input", Assert.Single(failed.Queued).Text);
        Assert.True(failed.Queued[0].Recovered);
        Assert.DoesNotContain(failed.Items, item => item is UserItem { Text: "unsent input" });

        await session.RecoverAsync();
        var recovered = session.Snapshot();
        Assert.Equal(SessionPhase.Ready, recovered.Phase);
        Assert.True(Assert.Single(recovered.Queued).Recovered);
        Assert.DoesNotContain(recovered.Items, item => item is UserItem { Text: "unsent input" });
    }

    [Fact]
    public async Task Malformed_engine_output_faults_every_chat_instead_of_guessing_the_owner()
    {
        await using var first = new SessionController(request => TestProcesses.Fake(request.ApprovalMode == "yolo" ? "garbage" : "normal"), new LaunchRequest());
        await first.StartAsync();
        await using var second = first.Sibling(new LaunchRequest(ApprovalMode: "yolo"));
        var openingError = await Record.ExceptionAsync(() => second.StartAsync());
        Assert.True(openingError is Ombra.Rpc.OmpStartException or Ombra.Rpc.RpcConnectionClosedException);
        var firstFault = await TestProcesses.Eventually(first.Snapshot, snapshot => snapshot.Phase == SessionPhase.Faulted, Wait, "first fault");
        var secondFault = await TestProcesses.Eventually(second.Snapshot, snapshot => snapshot.Phase == SessionPhase.Faulted, Wait, "second fault");
        Assert.NotNull(firstFault.LastError);
        Assert.NotNull(secondFault.LastError);
    }

    [Fact]
    public async Task Stop_during_generation_exits_cleanly()
    {
        var s = new SessionController(TestProcesses.Fake("slow-stream"));
        await s.StartAsync();
        await s.PromptAsync("go");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<AssistantItem>().Any(), Wait, "streaming");
        var pid = s.ProcessId!.Value;
        await s.DisposeAsync();
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        var snap = s.Snapshot();
        Assert.Equal(SessionPhase.Stopped, snap.Phase);
        Assert.All(snap.Items.OfType<AssistantItem>(), a => Assert.False(a.Streaming));
        Assert.All(snap.Items.OfType<ToolItem>(), t => Assert.NotEqual(ToolStatus.Running, t.Status));
    }

    [Fact]
    public async Task Oversized_prompt_is_reported_not_thrown()
    {
        await using var s = new SessionController(TestProcesses.Fake("normal"));
        await s.StartAsync();
        await s.PromptAsync(new string('ж', 600_000)); // 1.2 MB of UTF-8
        var snap = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, snap.Phase);
        Assert.Contains(snap.Items, i => i is NoticeItem n && n.Text.StartsWith("Prompt failed"));
        await s.PromptAsync("still works");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted == 1, Wait, "next run");
    }

    [Fact]
    public async Task Abort_settles_even_without_an_agent_end()
    {
        await using var s = new SessionController(TestProcesses.Fake("normal"));
        await s.StartAsync();
        // Pretend a run is active that omp never reports on (e.g. a local command): the abort ack must end it.
        s.Mutate(x => x.SetPhase(SessionPhase.Running, DateTimeOffset.UtcNow));
        await s.AbortAsync();
        Assert.Equal(SessionPhase.Ready, s.Snapshot().Phase);
    }

    [Fact]
    public async Task Non_terminal_agent_end_followed_by_silence_settles_via_get_state()
    {
        await using var s = new SessionController(TestProcesses.Fake("nonterminal-end")) { SettleQuietPeriod = TimeSpan.FromMilliseconds(300) };
        await s.StartAsync();
        await s.PromptAsync("hello");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted == 1, Wait, "settled");
        Assert.Contains(snap.DebugTail, d => d.Type == "settle");
        Assert.All(snap.Items.OfType<ToolItem>(), t => Assert.NotEqual(ToolStatus.Running, t.Status));
    }

    [Fact]
    public async Task Wall_clock_rollback_does_not_delay_settling_a_finished_run()
    {
        var clock = new UtcOffsetClock();
        await using var s = new SessionController(TestProcesses.Fake("nonterminal-end"), clock: clock)
        {
            SettleQuietPeriod = TimeSpan.FromMilliseconds(500),
        };
        await s.StartAsync();
        await s.PromptAsync("hello");
        await TestProcesses.Eventually(() => s.SettleProbeActive, active => active, Wait, "settle probe armed");
        clock.MoveUtc(TimeSpan.FromHours(-1));
        var snapshot = await TestProcesses.Eventually(s.Snapshot,
            state => state.Phase == SessionPhase.Ready && state.MessagesCompleted == 1,
            TimeSpan.FromSeconds(5), "finished run settles despite wall-clock rollback");
        Assert.Contains(snapshot.DebugTail, entry => entry.Type == "settle");
    }

    private sealed class UtcOffsetClock : TimeProvider
    {
        private long _offsetTicks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
        public void MoveUtc(TimeSpan delta) => Interlocked.Add(ref _offsetTicks, delta.Ticks);
    }

    [Fact]
    public async Task Settle_probe_stops_when_omp_dies_while_awaiting_settle()
    {
        await using var s = new SessionController(TestProcesses.Fake("nonterminal-end")) { SettleQuietPeriod = TimeSpan.FromSeconds(30) };
        await s.StartAsync();
        await s.PromptAsync("hello");
        // message_end is visible before the following agent_end{isTerminal:false} arms the probe: wait for it.
        await TestProcesses.Eventually(() => s.SettleProbeActive, active => active, Wait, "probe armed after non-terminal end");
        System.Diagnostics.Process.GetProcessById(s.ProcessId!.Value).Kill();
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Faulted, Wait, "fault");
        await TestProcesses.Eventually(() => s.SettleProbeActive, active => !active, TimeSpan.FromSeconds(5), "probe ends");
    }

    [Fact]
    public async Task A_failed_prompt_does_not_capture_the_next_prompts_echo()
    {
        await using var s = new SessionController(TestProcesses.Fake("normal"));
        await s.StartAsync();
        await s.PromptAsync(new string('ж', 600_000));
        await s.PromptAsync("second");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted == 1, Wait, "run end");
        var users = snap.Items.OfType<UserItem>().ToList();
        Assert.Equal(2, users.Count);
        Assert.StartsWith("жжж", users[0].Text);
        Assert.Equal("second", users[1].Text);
        Assert.All(users, u => Assert.True(u.Confirmed));
    }

    [Fact]
    public async Task A_broken_chunk_stream_stops_omp_and_says_so()
    {
        await using var s = new SessionController(TestProcesses.Fake("bad-chunk"));
        await s.StartAsync();
        var pid = s.ProcessId!.Value;
        await s.PromptAsync("go");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Faulted, Wait, "fault");
        Assert.IsType<Ombra.Rpc.RpcProtocolException>(s.Connection!.CloseReason!.InnerException);
        await TestProcesses.Eventually(() => { try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; } },
            alive => !alive, Wait, "omp stopped");
    }

    [Fact]
    public async Task Stopping_after_a_crash_keeps_the_fault()
    {
        await using var s = new SessionController(TestProcesses.Fake("slow-stream"));
        await s.StartAsync();
        await s.PromptAsync("go");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<AssistantItem>().Any(), Wait, "streaming");
        System.Diagnostics.Process.GetProcessById(s.ProcessId!.Value).Kill();
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Faulted, Wait, "fault");
        await s.StopAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionPhase.Faulted, s.Snapshot().Phase);
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var s = new SessionController(TestProcesses.Fake("normal"));
        await s.StartAsync();
        await s.DisposeAsync();
        await s.DisposeAsync();
        Assert.Equal(SessionPhase.Stopped, s.Snapshot().Phase);
    }

    [Fact]
    public async Task Invalid_prompt_admission_is_visible_and_does_not_leave_a_pending_run()
    {
        await using var session = new SessionController(TestProcesses.Fake("invalid-admission"));
        await session.StartAsync();
        Assert.False(await session.PromptAsync("keep this input", []));
        var snapshot = session.Snapshot();
        Assert.Equal(SessionPhase.Ready, snapshot.Phase);
        Assert.Contains(snapshot.Items, item => item is NoticeItem notice && notice.Text.Contains("invalid input acknowledgement"));
    }

    [Fact]
    public async Task Invalid_deferred_admission_recovers_the_entire_fifo_without_sending_its_tail()
    {
        await using var session = new SessionController(TestProcesses.Fake("invalid-admission"));
        await session.StartAsync();
        session.Mutate(state => state.SetPhase(SessionPhase.Running, DateTimeOffset.UtcNow));
        Assert.True(await session.QueueAsync(QueueKind.BuiltinCommand, "/rename first", []));
        Assert.True(await session.QueueAsync(QueueKind.FollowUp, "later input", []));
        session.Mutate(state => state.SetPhase(SessionPhase.Ready, DateTimeOffset.UtcNow));
        var snapshot = await TestProcesses.Eventually(session.Snapshot,
            state => state.Phase == SessionPhase.Ready && state.Queued.Count == 2 && state.Queued.All(input => input.Recovered),
            Wait, "the malformed admission recovered both FIFO entries");
        Assert.Equal(["/rename first", "later input"], snapshot.Queued.Select(input => input.Text));
        Assert.DoesNotContain(snapshot.Items, item => item is UserItem user && user.Text == "later input");
        Assert.Contains(snapshot.Items, item => item is NoticeItem notice && notice.Text.Contains("invalid input acknowledgement"));
    }
}
