using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Tool approvals and dialogs through a scripted omp that behaves like 18.2.0 (approval not tied to abort).</summary>
public sealed class ApprovalTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static async Task<(SessionController s, PendingDialog d)> StartWithPendingApprovalAsync(string scenario = "approval")
    {
        var s = new SessionController(TestProcesses.Fake(scenario));
        await s.StartAsync();
        await s.PromptAsync("run a command");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "approval request");
        return (s, snap.Dialogs[0]);
    }

    [Fact]
    public async Task Approve_runs_the_tool_and_records_the_decision()
    {
        var (s, d) = await StartWithPendingApprovalAsync();
        await using var _ = s;
        Assert.Equal(DialogKind.Approval, d.Kind);
        Assert.Equal("Allow tool: bash", d.Headline);
        Assert.Equal("$ echo hi", d.Details);
        Assert.Equal(SessionPhase.Running, s.Snapshot().Phase);

        Assert.True(await s.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Approve")));
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "run end");
        var tool = done.Items.OfType<ToolItem>().Single();
        Assert.Equal((ToolStatus.Succeeded, "hi"), (tool.Status, tool.Output));
        Assert.Contains(done.Items, i => i is NoticeItem { Text: "Approved by you — bash" });
        Assert.Empty(done.Dialogs);
        Assert.False(await s.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Deny")), "a second answer is not sent");
    }

    [Fact]
    public async Task Deny_blocks_the_tool()
    {
        var (s, d) = await StartWithPendingApprovalAsync();
        await using var _ = s;
        Assert.True(await s.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Deny")));
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "run end");
        Assert.Equal(ToolStatus.Failed, done.Items.OfType<ToolItem>().Single().Status);
        Assert.Contains(done.Items, i => i is NoticeItem { Level: NoticeLevel.Warning, Text: "Denied by you — bash" });
    }

    [Fact]
    public async Task Dismissing_the_dialog_counts_as_no_and_the_tool_does_not_run()
    {
        var (s, d) = await StartWithPendingApprovalAsync();
        await using var _ = s;
        Assert.True(await s.AnswerDialogAsync(d.Id, new DialogAnswer.Cancelled()));
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "run end");
        Assert.Equal(ToolStatus.Failed, done.Items.OfType<ToolItem>().Single().Status);
    }

    [Fact]
    public async Task Stop_while_an_approval_is_pending_does_not_hang()
    {
        var (s, _) = await StartWithPendingApprovalAsync();
        await using var __ = s;
        // omp 18.2.0 would wait for the approval before answering abort; the client answers it first.
        await s.AbortAsync().WaitAsync(Wait);
        var snap = s.Snapshot();
        Assert.Equal(SessionPhase.Ready, snap.Phase);
        Assert.Empty(snap.Dialogs);
        Assert.Contains(snap.Items, i => i is NoticeItem { Text: "Approval request closed (the run was stopped) — bash" });
        Assert.NotEqual(ToolStatus.Running, snap.Items.OfType<ToolItem>().Single().Status);
    }

    [Fact]
    public async Task An_expired_approval_closes_and_a_late_answer_is_not_sent()
    {
        var (s, d) = await StartWithPendingApprovalAsync("approval-timeout");
        await using var _ = s;
        Assert.NotNull(d.Deadline);
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 0, Wait, "expiry");
        Assert.Contains(snap.Items, i => i is NoticeItem n && n.Text.StartsWith("Approval request timed out", StringComparison.Ordinal));
        Assert.False(await s.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Approve")));
        var done = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "run end");
        Assert.Equal(ToolStatus.Failed, done.Items.OfType<ToolItem>().Single().Status);
    }

    /// <summary>Timers that fire a little before their due time by the wall clock (as real timers may).</summary>
    private sealed class EarlyTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime > TimeSpan.FromMilliseconds(40) ? dueTime - TimeSpan.FromMilliseconds(40) : dueTime, period);
    }

    [Fact]
    public async Task Expiry_does_not_depend_on_the_timer_firing_after_the_wall_clock_deadline()
    {
        // Regression (CI run 9): a timer that fired just before the deadline expired nothing and never retried.
        await using var s = new SessionController(TestProcesses.Fake("approval-timeout"), clock: new EarlyTimers());
        await s.StartAsync();
        await s.PromptAsync("run a command");
        await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "approval request");
        await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 0, TimeSpan.FromSeconds(10), "expiry with early timers");
    }

    [Fact]
    public async Task Crash_while_an_approval_is_pending_closes_it()
    {
        var (s, d) = await StartWithPendingApprovalAsync();
        await using var _ = s;
        System.Diagnostics.Process.GetProcessById(s.ProcessId!.Value).Kill();
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Faulted, Wait, "fault");
        Assert.Empty(snap.Dialogs);
        Assert.Contains(snap.Items, i => i is NoticeItem { Text: "Approval request closed (omp is no longer running) — bash" });
        Assert.False(await s.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Approve")));
    }

    [Fact]
    public async Task Closing_the_session_while_an_approval_is_pending_stops_omp()
    {
        var (s, _) = await StartWithPendingApprovalAsync();
        await s.StopAsync(TimeSpan.FromSeconds(5)).WaitAsync(Wait);
        var snap = s.Snapshot();
        Assert.Equal(SessionPhase.Stopped, snap.Phase);
        Assert.Empty(snap.Dialogs);
        await s.DisposeAsync();
    }
}
