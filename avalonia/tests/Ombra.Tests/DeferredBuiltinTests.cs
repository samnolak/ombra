using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class DeferredBuiltinTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private const string Rename = "/rename Deferred builtin smoke";

    private static (SessionController Session, MainViewModel Vm, ClientSettingsStore Settings, string Log) Create(string project, string? resume = null)
    {
        var log = Path.Combine(project, "commands.log");
        var factory = TestProcesses.FakeFactory("deferred-input", Path.Combine(project, "sessions"));
        var session = new SessionController(request =>
        {
            var spec = factory(request);
            return spec with { Environment = new Dictionary<string, string?>(spec.Environment)
            {
                ["FAKE_OMP_COMMAND_LOG"] = log,
                ["FAKE_OMP_LONG_DELTAS"] = "160",
                ["FAKE_OMP_INPUT_LOG"] = Path.Combine(project, "inputs.log"),
            } };
        }, new LaunchRequest(project, resume));
        var settings = new ClientSettingsStore(Path.Combine(project, "config.json"));
        return (session, new MainViewModel(session, new AppArgs(), settings: settings), settings, log);
    }

    private static string[] Sent(string log) => File.Exists(log) ? File.ReadAllLines(log) : [];

    private static async Task StartRun(SessionController session, MainViewModel vm, string text = "FOREVER")
    {
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        vm.ComposerText = text;
        await vm.SendCommand.ExecuteAsync(null);
        vm.Apply(await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Running && s.Items.OfType<UserItem>().Any(u => u.Confirmed), Wait, "initial prompt delivered"));
    }

    [Theory]
    [InlineData("/model provider/id", true)]
    [InlineData("/models:provider/id", true)]
    [InlineData("/effort\thigh", true)]
    [InlineData("/intensity:high", true)]
    [InlineData("/skill:review", false)]
    [InlineData("/template argument", false)]
    [InlineData("/mcp:review argument", false)]
    [InlineData("/custom", false)]
    [InlineData("/file argument", false)]
    [InlineData("/unknown", false)]
    [InlineData("/Model provider/id", false)]
    [InlineData(" /model provider/id", false)]
    public void Classification_uses_runtime_sources_aliases_and_pinned_colon_parser(string text, bool builtin)
    {
        SlashCommand[] catalog =
        [
            new("model", "", null, "builtin", ["models"], []),
            new("effort", "", null, "builtin", ["intensity"], []),
            new("skill:review", "", null, "skill", [], []),
            new("template", "", null, "extension", [], []),
            new("mcp:review", "", null, "mcp_prompt", [], []),
            new("custom", "", null, "custom", [], []),
            new("file", "", null, "file", [], []),
        ];
        Assert.Equal(builtin, OmpBuiltins.RunsOverRpc(text, catalog));
        Assert.False(OmpBuiltins.RunsOverRpc("/model", [new("model", "", null, "skill", [], [])]));
        Assert.False(OmpBuiltins.RunsOverRpc("/rename", catalog));
    }

    [AvaloniaFact]
    public async Task Builtins_and_later_followups_execute_in_FIFO_order_without_reclassifying_slash_text()
    {
        var (session, vm, settings, log) = Create(TestProcesses.TempDir("deferred-builtin"));
        await using var owned = vm;
        await StartRun(session, vm, "LONG");
        const string before = "/skill:systematic-debugging before the builtin";
        const string after = "/skill:systematic-debugging after the builtin";
        const string ordinary = "/unknown ordinary slash text";
        vm.ComposerText = before;
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(1, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        foreach (var input in new[] { Rename, after, "continue after rename", ordinary, "/stats" })
        {
            vm.ComposerText = input;
            await vm.SendCommand.ExecuteAsync(null);
        }
        Assert.Equal([QueueKind.FollowUp, QueueKind.BuiltinCommand, QueueKind.DeferredFollowUp,
            QueueKind.DeferredFollowUp, QueueKind.DeferredFollowUp, QueueKind.BuiltinCommand],
            vm.QueuedMessages.Select(q => q.Model.Kind));
        Assert.Empty(Sent(log));
        Assert.Equal(1, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        Assert.Null(session.Snapshot().SessionName);
        Assert.DoesNotContain(session.Snapshot().Items.OfType<UserItem>(), u => u.Text == Rename);
        vm.ComposerText = "new edit must stay";

        var done = await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Ready && s.Queued.Count == 0 && s.SessionName == "Deferred builtin smoke"
                 && s.Items.OfType<CommandOutputItem>().Any(), Wait, "deferred builtins executed at idle");
        vm.Apply(done);
        vm.Apply(session.Snapshot());
        Assert.Equal([Rename, "/stats"], Sent(log));
        Assert.Equal(["LONG", before, Rename, after, "continue after rename", ordinary, "/stats"],
            done.Items.OfType<UserItem>().Select(u => u.Text));
        Assert.Equal(["prompt LONG", "follow_up " + before, "prompt " + Rename, "follow_up " + after,
            "follow_up continue after rename", "follow_up " + ordinary, "prompt /stats"],
            File.ReadAllLines(Path.Combine(Path.GetDirectoryName(log)!, "inputs.log")));
        Assert.Equal("new edit must stay", vm.ComposerText);
        Assert.Empty(vm.QueuedMessages);
        var saved = Assert.Single(DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All);
        Assert.Empty(saved.Queue);
        Assert.Empty(saved.Pending);
    }

    [AvaloniaTheory]
    [InlineData("input-hook:handled")]
    [InlineData("input-hook:empty")]
    public async Task Locally_consumed_deferred_followup_settles_without_an_echo_and_releases_the_next_builtin(string input)
    {
        var (session, vm, settings, log) = Create(TestProcesses.TempDir("deferred-local"));
        await using var owned = vm;
        await StartRun(session, vm, "LONG");
        foreach (var text in new[] { Rename, input, "/stats" })
        {
            vm.ComposerText = text;
            await vm.SendCommand.ExecuteAsync(null);
        }
        var done = await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Ready && s.Queued.Count == 0
                 && s.Items.OfType<CommandOutputItem>().Any(), Wait, "local follow-up settled and next builtin executed");
        vm.Apply(done);
        Assert.Equal([Rename, "/stats"], Sent(log));
        Assert.Equal(["LONG", Rename, "/stats"], done.Items.OfType<UserItem>().Select(u => u.Text));
        Assert.Equal(1, done.RunsEnded); // local input and builtins do not spend another agent turn
        Assert.Equal(["prompt LONG", "prompt " + Rename, "follow_up " + input, "prompt /stats"],
            File.ReadAllLines(Path.Combine(Path.GetDirectoryName(log)!, "inputs.log")));
        Assert.Empty(vm.ComposerText);
        var draftPath = DraftStore.PathFor(settings.Path);
        Assert.True(File.Exists(draftPath));
        var saved = DraftStore.ReadSnapshot(draftPath);
        Assert.Null(saved.LastError);
        Assert.Empty(saved.All); // no composer, queued input or uncertain submission remains to recover
        Assert.Equal(0, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
    }

    [AvaloniaTheory]
    [InlineData("input-hook:handled")]
    [InlineData("input-hook:empty")]
    public async Task Locally_consumed_runtime_queue_input_does_not_end_the_existing_run(string input)
    {
        var (session, vm, _, _) = Create(TestProcesses.TempDir("running-local"));
        await using var owned = vm;
        await StartRun(session, vm);
        foreach (var kind in new[] { QueueKind.Steer, QueueKind.FollowUp })
        {
            Assert.True(await session.QueueAsync(kind, input, []));
            Assert.Equal(SessionPhase.Running, session.Snapshot().Phase);
            Assert.Empty(session.Snapshot().Queued);
            Assert.Equal(0, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        }
        await vm.AbortCommand.ExecuteAsync(null);
        Assert.DoesNotContain(session.Snapshot().Items.OfType<UserItem>(), u => u.Text == input);
    }

    [AvaloniaFact]
    public async Task A_handled_input_with_extension_agent_work_keeps_the_next_builtin_behind_that_turn()
    {
        var (session, vm, settings, log) = Create(TestProcesses.TempDir("deferred-local-agent"));
        await using var owned = vm;
        await StartRun(session, vm, "LONG");
        const string after = "/rename After extension turn";
        foreach (var input in new[] { Rename, "input-hook:agent", after })
        {
            vm.ComposerText = input;
            await vm.SendCommand.ExecuteAsync(null);
        }
        var nested = await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Running && s.Items.OfType<UserItem>().Any(u => u.Text == "extension-created turn"),
            Wait, "input extension's own turn started");
        Assert.Equal("Deferred builtin smoke", nested.SessionName);
        Assert.Equal([Rename], Sent(log));
        Assert.Contains(nested.Queued, q => q.Text == after && q.Deferred);
        Assert.DoesNotContain(nested.Items.OfType<UserItem>(), u => u.Text == "input-hook:agent");
        var done = await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Ready && s.Queued.Count == 0 && s.SessionName == "After extension turn",
            Wait, "extension turn ended before the next builtin");
        vm.Apply(done);
        Assert.Equal([Rename, after], Sent(log));
        Assert.Equal(["LONG", Rename, "extension-created turn", after], done.Items.OfType<UserItem>().Select(u => u.Text));
        Assert.Equal(2, done.RunsEnded); // the extension-created turn counts; neither local builtin does
        Assert.Empty(vm.ComposerText);
        var draftPath = DraftStore.PathFor(settings.Path);
        Assert.True(File.Exists(draftPath));
        var saved = DraftStore.ReadSnapshot(draftPath);
        Assert.Null(saved.LastError);
        Assert.Empty(saved.All);
    }

    [AvaloniaFact]
    public async Task Cancelled_input_ack_recovers_the_waiting_FIFO_without_executing_it()
    {
        var (session, vm, _, log) = Create(TestProcesses.TempDir("deferred-cancelled-ack"));
        await using var owned = vm;
        await StartRun(session, vm, "LONG");
        foreach (var input in new[] { Rename, "input-hook:cancelled", "/stats" })
        {
            vm.ComposerText = input;
            await vm.SendCommand.ExecuteAsync(null);
        }
        vm.ComposerText = "new edit";
        var done = await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Ready && s.SessionName == "Deferred builtin smoke" && s.Queued.All(q => q.Recovered),
            Wait, "cancelled admission recovered local FIFO");
        vm.Apply(done);
        vm.Apply(session.Snapshot());
        Assert.Equal("new edit\n\ninput-hook:cancelled\n\n/stats", vm.ComposerText);
        Assert.Equal([Rename], Sent(log));
        Assert.Equal(1, done.RunsEnded); // cancellation before admission did not start another agent turn
        await session.PromptAsync("explicit next task");
        vm.Apply(await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Ready, Wait, "next explicit task"));
        Assert.Equal([Rename], Sent(log));
        Assert.DoesNotContain(session.Snapshot().Items.OfType<UserItem>(), u => u.Text is "input-hook:cancelled" or "/stats");
    }

    [AvaloniaFact]
    public async Task Cancelling_the_builtin_keeps_later_input_ordered_and_explicit_steering_can_interrupt()
    {
        var (session, vm, _, log) = Create(TestProcesses.TempDir("deferred-order-cancel"));
        await using var owned = vm;
        await StartRun(session, vm, "LONG");
        foreach (var input in new[] { Rename, "/unknown keep this text", "discard this follow-up" })
        {
            vm.ComposerText = input;
            await vm.SendCommand.ExecuteAsync(null);
        }
        var builtin = vm.QueuedMessages[0];
        await vm.WithdrawQueuedAsync(vm.QueuedMessages[2], edit: false);
        await vm.WithdrawQueuedAsync(vm.QueuedMessages[1], edit: true);
        Assert.Equal("/unknown keep this text", vm.ComposerText);
        await vm.SendCommand.ExecuteAsync(null);
        await vm.WithdrawQueuedAsync(builtin, edit: false);
        vm.ComposerText = "later text";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.All(vm.QueuedMessages, q => Assert.Equal(QueueKind.DeferredFollowUp, q.Model.Kind));
        Assert.Equal(0, (await session.Connection!.GetStateAsync()).QueuedMessageCount);

        // Steer explicitly means interrupt the current turn, not join the FIFO after its pending follow-ups.
        vm.ComposerText = "urgent steer";
        await vm.SteerCommand.ExecuteAsync(null);
        Assert.Equal(QueueKind.Steer, vm.QueuedMessages[^1].Model.Kind);
        Assert.Equal(1, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        var done = await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Ready && s.Queued.Count == 0, Wait, "remaining FIFO drained after steering");
        vm.Apply(done);
        Assert.Equal(["LONG", "urgent steer", "/unknown keep this text", "later text"],
            done.Items.OfType<UserItem>().Select(u => u.Text));
        Assert.Equal(["prompt LONG", "steer urgent steer", "follow_up /unknown keep this text", "follow_up later text"],
            File.ReadAllLines(Path.Combine(Path.GetDirectoryName(log)!, "inputs.log")));
        Assert.Empty(Sent(log));
        Assert.Null(done.SessionName);
        Assert.Empty(vm.ComposerText);
    }

    [AvaloniaFact]
    public async Task Stop_recovers_text_and_images_once_and_requires_explicit_resubmission()
    {
        var (session, vm, settings, log) = Create(TestProcesses.TempDir("deferred-stop"));
        await using var owned = vm;
        await StartRun(session, vm);
        vm.ComposerText = Rename;
        await vm.SendCommand.ExecuteAsync(null);
        vm.ComposerText = "continue after rename";
        var image = new ImageAttachment("saved.png", "image/png",
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf9sAAAAASUVORK5CYII="));
        Assert.True(vm.TryAddImage(image, "test"));
        await vm.SendCommand.ExecuteAsync(null);
        vm.ComposerText = "new edit";
        await vm.AbortCommand.ExecuteAsync(null);
        vm.Apply(session.Snapshot());
        vm.Apply(session.Snapshot());
        Assert.Equal("new edit\n\n" + Rename + "\n\ncontinue after rename", vm.ComposerText);
        Assert.Equal(image.Data, Assert.Single(vm.Attachments).Model.Data);
        Assert.Empty(vm.QueuedMessages);
        Assert.Empty(Sent(log));
        Assert.Null(session.Snapshot().SessionName);
        Assert.Equal(0, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        var saved = Assert.Single(DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All);
        Assert.Empty(saved.Queue);
        Assert.Empty(saved.Pending);
        Assert.Equal(vm.ComposerText, saved.Composer.Text);
        Assert.Single(saved.Composer.Images);

        // An unrelated run, and its terminal event, must not resurrect the cancelled builtin.
        await session.PromptAsync("explicit next task");
        vm.Apply(await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Ready, Wait, "next run ended"));
        Assert.Empty(Sent(log));
        Assert.Equal("new edit\n\n" + Rename + "\n\ncontinue after rename", vm.ComposerText);
        vm.ComposerText = Rename;
        await vm.SendCommand.ExecuteAsync(null);
        vm.Apply(await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Ready && s.Queued.Count == 0 && s.SessionName == "Deferred builtin smoke", Wait, "explicit builtin retry"));
        Assert.Equal([Rename], Sent(log));
    }

    [AvaloniaFact]
    public async Task Edit_and_cancel_withdraw_only_the_selected_local_command_without_sending_it()
    {
        var (session, vm, _, log) = Create(TestProcesses.TempDir("deferred-cancel"));
        await using var owned = vm;
        await StartRun(session, vm);
        for (var i = 0; i < 2; i++)
        {
            vm.ComposerText = Rename;
            await vm.SendCommand.ExecuteAsync(null);
        }
        var first = vm.QueuedMessages[0];
        var second = vm.QueuedMessages[1];
        await vm.WithdrawQueuedAsync(second, edit: true);
        Assert.Equal(Rename, vm.ComposerText);
        Assert.Equal(first.Model.Seq, Assert.Single(vm.QueuedMessages).Model.Seq);
        await vm.WithdrawQueuedAsync(first, edit: false);
        Assert.Empty(vm.QueuedMessages);
        await vm.AbortCommand.ExecuteAsync(null);
        vm.Apply(session.Snapshot());
        Assert.Equal(Rename, vm.ComposerText);
        Assert.Empty(Sent(log));
        Assert.Equal(0, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
    }

    [AvaloniaFact]
    public async Task Crash_and_reopen_recover_deferred_command_once_without_automatic_execution()
    {
        var project = TestProcesses.TempDir("deferred-crash");
        var (session, vm, _, log) = Create(project);
        string? file;
        try
        {
            await StartRun(session, vm);
            vm.ComposerText = Rename;
            await vm.SendCommand.ExecuteAsync(null);
            vm.ComposerText = "unfinished edit";
            using (var process = Process.GetProcessById(session.ProcessId!.Value)) process.Kill();
            vm.Apply(await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Faulted, Wait, "runtime crash"));
            vm.Apply(session.Snapshot());
            Assert.Equal("unfinished edit\n\n" + Rename, vm.ComposerText);
            file = session.SessionFile is { } path && File.Exists(path) ? path : null;
            await session.RecoverAsync();
            vm.Apply(session.Snapshot());
            Assert.Equal("unfinished edit\n\n" + Rename, vm.ComposerText);
            Assert.Empty(session.Snapshot().Queued);
            Assert.Empty(Sent(log));
        }
        finally { await vm.DisposeAsync(); }
        var (restarted, recovered, _, _) = Create(project, file);
        await using var reopened = recovered;
        Assert.Equal("unfinished edit\n\n" + Rename, recovered.ComposerText);
        await restarted.StartAsync();
        recovered.Apply(restarted.Snapshot());
        Assert.Empty(restarted.Snapshot().Queued);
        Assert.Empty(Sent(log));
    }

    [Fact]
    public async Task Steer_submission_of_builtin_is_deferred_and_force_stop_cannot_replay_it()
    {
        var project = TestProcesses.TempDir("deferred-force-stop");
        await using var session = new SessionController(TestProcesses.FakeFactory("multi", Path.Combine(project, "sessions")), new LaunchRequest(project));
        await session.StartAsync();
        await session.PromptAsync("FOREVER");
        Assert.True(await session.QueueAsync(QueueKind.Steer, Rename, []));
        Assert.Equal(QueueKind.BuiltinCommand, Assert.Single(session.Snapshot().Queued).Kind);
        Assert.True(await session.QueueAsync(QueueKind.FollowUp, "continue after rename", []));
        Assert.Equal(QueueKind.DeferredFollowUp, session.Snapshot().Queued[1].Kind);
        Assert.Equal(0, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        await session.ForceStopAsync();
        Assert.Equal(2, session.Snapshot().Queued.Count);
        Assert.All(session.Snapshot().Queued, q => Assert.True(q.Recovered));
        Assert.Null(session.Snapshot().SessionName);
        await session.PromptAsync("explicit next task");
        var done = await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Ready, Wait, "next task finished");
        Assert.Equal(2, done.Queued.Count);
        Assert.All(done.Queued, q => Assert.True(q.Recovered));
        Assert.Null(done.SessionName);
        Assert.DoesNotContain(done.Items.OfType<UserItem>(), u => u.Text == Rename);
        Assert.DoesNotContain(done.Items.OfType<UserItem>(), u => u.Text == "continue after rename");
    }

    [Fact]
    public async Task Cancelled_admission_and_failed_checkpoint_never_dispatch_a_builtin()
    {
        await using var session = new SessionController(TestProcesses.Fake("normal"));
        await session.StartAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueueAsync(QueueKind.FollowUp, Rename, [], cancelled.Token));
        Assert.Empty(session.Snapshot().Queued);
        session.QueueCheckpoint = _ => throw new IOException("disk unavailable");
        Assert.False(await session.QueueAsync(QueueKind.FollowUp, Rename, []));
        Assert.True(Assert.Single(session.Snapshot().Queued).Recovered);
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
        Assert.Null((await session.Connection!.GetStateAsync()).SessionName);
    }

    [Fact]
    public async Task A_builtin_that_starts_an_agent_run_holds_the_next_builtin_until_that_run_finishes()
    {
        var project = TestProcesses.TempDir("deferred-agent-builtin");
        var answers = Path.Combine(project, "commands.json");
        var log = Path.Combine(project, "commands.log");
        File.WriteAllText(answers, """{"/retry":{"text":"Retrying","agentInvoked":true}}""");
        await using var session = new SessionController(TestProcesses.FakeWithCommands("multi", answers, log));
        await session.StartAsync();
        await session.PromptAsync("LONG");
        Assert.True(await session.QueueAsync(QueueKind.FollowUp, "/retry", []));
        Assert.True(await session.QueueAsync(QueueKind.FollowUp, Rename, []));
        var done = await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Ready && s.Queued.Count == 0 && s.SessionName == "Deferred builtin smoke",
            Wait, "agent builtin followed by deferred rename");
        Assert.Equal(["/retry", Rename], Sent(log));
        var retried = Assert.Single(done.Items.OfType<UserItem>(), u => u.Text == "(retried turn)");
        var renamed = Assert.Single(done.Items.OfType<UserItem>(), u => u.Text == Rename);
        Assert.Contains(done.Items.OfType<AssistantItem>(), a => a.Key > retried.Key && a.Key < renamed.Key && !a.Streaming);
    }
}
