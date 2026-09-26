using System.Diagnostics;
using System.Text.Json;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>Regression tests for the findings of the independent review of the M3 slices (round 3).</summary>
public sealed class ReviewRegressionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-25T12:00:00Z");

    private static RpcFrame F(string json)
    {
        var e = JsonDocument.Parse(json).RootElement.Clone();
        return new RpcFrame(e.GetProperty("type").GetString()!, null, e, Stopwatch.GetTimestamp(), json.Length + 1);
    }

    private static async Task<SessionController> StartAsync(string scenario)
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("rr-sessions")), new LaunchRequest(TestProcesses.TempDir("rr-project"), ApprovalMode: "write"));
        await s.StartAsync();
        return s;
    }

    [Fact]
    public async Task R1_a_stop_that_never_finishes_can_be_forced_and_the_session_resumes()
    {
        await using var s = await StartAsync("abort-hangs");
        await s.PromptAsync("work");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<AssistantItem>().Any(a => a.Text.Length > 10), Wait, "streaming");
        var file = s.Snapshot().SessionFile;
        var pid = s.ProcessId;
        var abort = s.AbortAsync(); // omp never answers
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Aborting, Wait, "aborting");

        await s.ForceStopAsync().WaitAsync(Wait);
        Assert.Equal(SessionPhase.Ready, s.Snapshot().Phase);
        Assert.NotEqual(pid, s.ProcessId);
        Assert.Equal(file, s.Snapshot().SessionFile);
        await abort.WaitAsync(Wait); // the pending abort ends with the old connection, reported not thrown
    }

    [Fact]
    public async Task R2_a_switch_whose_reload_fails_resyncs_to_the_session_omp_has()
    {
        await using var s = await StartAsync("reload-fails-once");
        await s.PromptAsync("first");
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.MessagesCompleted == 1, Wait, "run");
        var first = s.Snapshot().SessionFile!;
        await s.NewSessionAsync();
        var second = s.Snapshot().SessionFile;
        Assert.NotEqual(first, second);

        await s.OpenSessionAsync(first, s.Snapshot().Cwd!); // switch succeeds, then get_messages fails once
        var snap = s.Snapshot();
        Assert.Equal(first, snap.SessionFile);
        Assert.Contains(snap.Items, i => i is UserItem { Text: "first" });
        Assert.Contains(snap.Items, i => i is NoticeItem n && n.Text.StartsWith("omp changed sessions after all", StringComparison.Ordinal));
        Assert.Equal(SessionPhase.Ready, snap.Phase);
    }

    [Fact]
    public async Task R3_other_commands_wait_while_omp_signs_in_and_cancel_restarts_omp()
    {
        await using var s = await StartAsync("normal");
        var login = s.LoginAsync("fakeauth");
        await TestProcesses.Eventually(s.Snapshot, x => x.SigningIn == "fakeauth" && x.Dialogs.Count == 1, Wait, "signing in");
        await s.PromptAsync("while signing in");
        Assert.DoesNotContain(s.Snapshot().Items, i => i is UserItem);
        Assert.Contains(s.Snapshot().Items, i => i is NoticeItem n && n.Text.StartsWith("omp is busy with the sign-in", StringComparison.Ordinal));

        await s.ForceStopAsync();
        Assert.False(await login.WaitAsync(Wait));
        Assert.Null(s.Snapshot().SigningIn);
        Assert.Equal(SessionPhase.Ready, s.Snapshot().Phase);
        Assert.Empty(s.Snapshot().Dialogs);
    }

    [Theory]
    [InlineData(null, "write")]
    [InlineData("ask", "write")]
    [InlineData("Write", "write")]
    [InlineData("always-ask", "always-ask")]
    [InlineData("yolo", "yolo")]
    public void R4_the_approval_flag_is_always_valid_and_never_left_out(string? configured, string expected)
    {
        var spec = new OmpRuntimeOptions { ApprovalMode = configured }.ToLaunchSpec();
        var i = spec.Arguments.ToList().IndexOf("--approval-mode");
        Assert.True(i >= 0);
        Assert.Equal(expected, spec.Arguments[i + 1]);
        Assert.Equal(configured is "ask" or "Write", new OmpRuntimeOptions { ApprovalMode = configured }.ApprovalModeWarning is not null);
    }

    [Fact]
    public void R5_open_url_keeps_the_launch_url_and_the_instructions()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"extension_ui_request","id":"u","method":"open_url","url":"https://github.com/login/device","launchUrl":"http://127.0.0.1:5000/l/1","instructions":"Enter code: ABCD-1234"}"""), T);
        var u = s.Snapshot().OpenUrl!;
        Assert.Equal(("http://127.0.0.1:5000/l/1", "Enter code: ABCD-1234"), (u.Target, u.Instructions));
        Assert.Contains(s.Snapshot().Items, i => i is NoticeItem n && n.Text.Contains("ABCD-1234"));
    }

    [Fact]
    public async Task R6_a_question_that_arrives_while_stopping_is_declined_not_shown()
    {
        await using var s = await StartAsync("approval-chain");
        await s.PromptAsync("two tools");
        await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "first approval");
        await s.AbortAsync().WaitAsync(Wait); // cancels the first; the second arrives while stopping
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "stopped");
        Assert.Empty(snap.Dialogs);
        Assert.Contains(snap.Items, i => i is NoticeItem n && n.Text.StartsWith("Approval request declined (the run is being stopped)", StringComparison.Ordinal));
        Assert.DoesNotContain(snap.Items, i => i is ToolItem { Status: ToolStatus.Succeeded });
    }

    [Fact]
    public void R7_switching_inside_the_same_omp_keeps_extension_status_widgets_and_dialogs()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"extension_ui_request","id":"s","method":"setStatus","statusKey":"git","statusText":"main"}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"w","method":"setWidget","widgetKey":"k","widgetLines":["x"]}"""), T);
        s.Apply(F("""{"type":"extension_ui_request","id":"d","method":"input","title":"code"}"""), T);
        s.AddUserPrompt("old");
        s.ResetTranscript(newProcess: false);
        var snap = s.Snapshot();
        Assert.Empty(snap.Items);
        Assert.Equal("main", snap.ExtensionStatus["git"]);
        Assert.Single(snap.Widgets);
        Assert.Single(snap.Dialogs);
        s.ResetTranscript(newProcess: true);
        Assert.Empty(s.Snapshot().Dialogs);
        Assert.Empty(s.Snapshot().ExtensionStatus);
    }

    [Fact]
    public async Task R9_an_answer_over_the_line_limit_keeps_the_question_open()
    {
        await using var s = await StartAsync("ask");
        await s.PromptAsync("ask me");
        var d = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1, Wait, "select")).Dialogs[0];
        Assert.True(await s.AnswerDialogAsync(d.Id, new DialogAnswer.Value("Blue")));
        var input = (await TestProcesses.Eventually(s.Snapshot, x => x.Dialogs.Count == 1 && x.Dialogs[0].Kind == DialogKind.Input, Wait, "input")).Dialogs[0];
        Assert.False(await s.AnswerDialogAsync(input.Id, new DialogAnswer.Value(new string('x', 1_100_000))));
        Assert.Equal(input.Id, Assert.Single(s.Snapshot().Dialogs).Id);
        Assert.Contains(s.Snapshot().Items, i => i is NoticeItem n && n.Text.StartsWith("The answer is too long", StringComparison.Ordinal));
        Assert.True(await s.AnswerDialogAsync(input.Id, new DialogAnswer.Value("short")));
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready, Wait, "run end");
    }
}
