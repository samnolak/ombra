using Avalonia.Headless.XUnit;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>Send now on a queued chip (<c>promote_queued_message</c>) and Interrupt and send (<c>abort_and_prompt</c>).</summary>
public sealed class SendNowTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static ImageAttachment Image() => new("keep.png", "image/png",
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf9sAAAAASUVORK5CYII="));

    private static async Task<(SessionController Session, MainViewModel Vm, ClientSettingsStore Settings)> CreateAsync(string scenario)
    {
        var root = TestProcesses.TempDir("send-now");
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        var session = new SessionController(TestProcesses.FakeFactory(scenario, Path.Combine(root, "sessions")), new LaunchRequest(root));
        var vm = new MainViewModel(session, new AppArgs(), settings: settings) { Chats = new ChatFolders(Path.Combine(root, "chats")) };
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        return (session, vm, settings);
    }

    private static SavedDraft Journal(ClientSettingsStore settings) =>
        Assert.Single(DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All);

    private static async Task StartRunningAsync(SessionController session, MainViewModel vm)
    {
        vm.ComposerText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.Apply(await TestProcesses.Eventually(session.Snapshot,
            s => s.Phase == SessionPhase.Running && s.Items.OfType<UserItem>().Any(u => u.Confirmed), Wait, "running"));
    }

    [AvaloniaFact]
    public async Task Send_now_delivers_exactly_that_queued_message_and_drops_its_chip_and_journal_entry()
    {
        // queue-recovery: omp holds follow-ups while idle, so only the promoted one can be delivered.
        var (session, vm, settings) = await CreateAsync("queue-recovery");
        try
        {
            Assert.True(await session.QueueAsync(QueueKind.FollowUp, "keep waiting", []));
            Assert.True(await session.QueueAsync(QueueKind.FollowUp, "send me now", [Image()]));
            vm.Apply(session.Snapshot());
            Assert.Equal(["keep waiting", "send me now"], Journal(settings).Queue.Select(q => q.Text));
            var chip = Assert.Single(vm.QueuedMessages, q => q.Text == "send me now");
            Assert.True(chip.CanSendNow);

            await chip.SendNowCommand.ExecuteAsync(null);

            var done = await TestProcesses.Eventually(session.Snapshot,
                s => s.Phase == SessionPhase.Ready && s.Items.OfType<UserItem>().Any(u => u.Text == "send me now" && u.Confirmed),
                Wait, "the promoted message to be delivered");
            var user = Assert.Single(done.Items.OfType<UserItem>());
            Assert.Equal("send me now", user.Text);
            Assert.Equal(Image().Data, Assert.Single(user.Images).Data);
            var left = Assert.Single(done.Queued);
            Assert.Equal(("keep waiting", QueueKind.FollowUp, false), (left.Text, left.Kind, left.Recovered));
            vm.Apply(done);
            Assert.Equal(["keep waiting"], vm.QueuedMessages.Select(q => q.Text));
            Assert.Equal(["keep waiting"], Journal(settings).Queue.Select(q => q.Text));
            Assert.Equal(1, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Promoted_message_waits_as_a_steer_and_stop_recovers_it_once()
    {
        var (session, vm, settings) = await CreateAsync("slow-stream");
        try
        {
            await StartRunningAsync(session, vm);
            vm.ComposerText = "later message";
            await vm.SendCommand.ExecuteAsync(null);
            var chip = Assert.Single(vm.QueuedMessages);
            Assert.True(chip.CanSendNow);

            await chip.SendNowCommand.ExecuteAsync(null);

            // omp reads it at its next step: until then it stays pending as a steer, in memory and in the journal.
            var promoted = Assert.Single(session.Snapshot().Queued);
            Assert.Equal((QueueKind.Steer, false), (promoted.Kind, promoted.Recovered));
            chip = Assert.Single(vm.QueuedMessages);
            Assert.Equal(QueueKind.Steer, chip.Model.Kind);
            Assert.False(chip.CanSendNow);
            var journaled = Assert.Single(Journal(settings).Queue);
            Assert.Equal(("later message", QueueKind.Steer), (journaled.Text, journaled.Kind));

            await vm.AbortCommand.ExecuteAsync(null);

            Assert.Equal(1, vm.ComposerText.Split("later message").Length - 1);
            Assert.Empty(vm.QueuedMessages);
            Assert.Equal(0, (await session.Connection!.GetStateAsync()).QueuedMessageCount);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Interrupt_and_send_stops_the_run_and_sends_text_with_images()
    {
        var (session, vm, settings) = await CreateAsync("slow-stream");
        try
        {
            await StartRunningAsync(session, vm);
            vm.ComposerText = "change of plan";
            vm.TryAddImage(Image(), "test");
            Assert.True(vm.InterruptAndSendCommand.CanExecute(null));

            await vm.InterruptAndSendCommand.ExecuteAsync(null);

            Assert.Empty(vm.ComposerText);
            Assert.Empty(vm.Attachments);
            var done = await TestProcesses.Eventually(session.Snapshot,
                s => s.Phase == SessionPhase.Ready && s.Items.OfType<UserItem>().Count(u => u.Confirmed) == 2,
                Wait, "the replacement prompt's run to finish");
            var users = done.Items.OfType<UserItem>().ToArray();
            Assert.Equal(["first", "change of plan"], users.Select(u => u.Text));
            Assert.Equal(Image().Data, Assert.Single(users[1].Images).Data);
            // Delivered: no submission is left pending and nothing returns to the composer (the draft may be gone entirely)
            await TestProcesses.Eventually(() => DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All,
                all => all.All(d => d.Pending.Length == 0 && d.Composer.IsEmpty), Wait, "the delivered submission to leave the journal");
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Failed_interrupt_and_send_restores_text_and_images_into_the_composer()
    {
        var (session, vm, settings) = await CreateAsync("slow-stream");
        try
        {
            await StartRunningAsync(session, vm);
            vm.ComposerText = "REJECT this plan";
            vm.TryAddImage(Image(), "test");

            await vm.InterruptAndSendCommand.ExecuteAsync(null);

            Assert.Contains("REJECT this plan", vm.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
            var after = session.Snapshot();
            Assert.Equal(SessionPhase.Ready, after.Phase);
            Assert.Contains(after.Items, i => i is NoticeItem { Level: NoticeLevel.Error });
            var journal = Journal(settings);
            Assert.Empty(journal.Pending);
            Assert.Contains("REJECT this plan", journal.Composer.Text);
            Assert.Equal(Image().Data, Assert.Single(journal.Composer.Images).Data);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Interrupt_while_the_previous_prompt_is_admitted_leaves_the_replacement_to_its_own_echo()
    {
        // held-admission: "HOLD" stays unadmitted until abort_and_prompt cancels it; the cancelled answer arrives before
        // the replacement's run starts, so a stale acknowledgement would settle the replacement's pending run.
        var (session, vm, settings) = await CreateAsync("held-admission");
        try
        {
            vm.ComposerText = "HOLD first";
            var first = vm.SendCommand.ExecuteAsync(null);
            await session.Connection!.GetStateAsync(); // omp has read the held prompt
            Assert.Equal(SessionPhase.Running, vm.Phase);
            vm.ComposerText = "change of plan";
            vm.TryAddImage(Image(), "test");

            await vm.InterruptAndSendCommand.ExecuteAsync(null);
            await first;

            var done = await TestProcesses.Eventually(session.Snapshot,
                s => s.Phase == SessionPhase.Ready && s.Items.OfType<UserItem>().Any(u => u.Text == "change of plan" && u.Confirmed) && s.MessagesCompleted > 0,
                Wait, "the replacement's run to finish");
            var user = Assert.Single(done.Items.OfType<UserItem>());
            Assert.Equal("change of plan", user.Text);
            Assert.Equal(Image().Data, Assert.Single(user.Images).Data);
            Assert.DoesNotContain(done.Items, i => i is NoticeItem { Text: var t } && t.Contains("cancelled before admission", StringComparison.Ordinal));
            // The cancelled original returns to the message box once; the delivered replacement leaves the journal.
            Assert.Equal(1, vm.ComposerText.Split("HOLD first").Length - 1);
            Assert.DoesNotContain("change of plan", vm.ComposerText);
            var journal = await TestProcesses.Eventually(() => Journal(settings), d => d.Pending.Length == 0, Wait, "the delivered replacement to leave the journal");
            Assert.Contains("HOLD first", journal.Composer.Text);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Identical_replacement_echoed_before_the_cancelled_original_confirms_only_the_replacement()
    {
        // LATE-CANCEL: omp runs and echoes the replacement before answering the superseded prompt as cancelled, and both
        // carry the same text: the echo must confirm the replacement, never the original omp is about to cancel.
        const string Same = "HOLD LATE-CANCEL same plan";
        var (session, vm, settings) = await CreateAsync("held-admission");
        try
        {
            vm.ComposerText = Same;
            var first = vm.SendCommand.ExecuteAsync(null);
            await session.Connection!.GetStateAsync(); // omp has read the held prompt
            vm.ComposerText = Same;

            await vm.InterruptAndSendCommand.ExecuteAsync(null);
            await first;

            var done = await TestProcesses.Eventually(session.Snapshot,
                s => s.Phase == SessionPhase.Ready && s.MessagesCompleted > 0 && s.Items.OfType<UserItem>().Count() == 1,
                Wait, "the replacement's run to finish and the cancelled original's row to go");
            Assert.True(Assert.Single(done.Items.OfType<UserItem>()).Confirmed);
            // The cancelled original returns once; the delivered replacement is not kept for recovery.
            Assert.Equal(1, vm.ComposerText.Split(Same).Length - 1);
            var journal = await TestProcesses.Eventually(() => Journal(settings), d => d.Pending.Length == 0, Wait, "the delivered replacement to leave the journal");
            Assert.Equal(1, journal.Composer.Text.Split(Same).Length - 1);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Replaced_run_ending_late_keeps_the_pending_replacement_running()
    {
        // ADMIT-LATE: the superseded prompt was admitted after all; its run's start, echo and end arrive after the
        // interrupt, and the replacement is admitted 1.5 s later. That end is the replaced run's, not the replacement's.
        var (session, vm, settings) = await CreateAsync("held-admission");
        try
        {
            vm.ComposerText = "HOLD first";
            var first = vm.SendCommand.ExecuteAsync(null);
            await session.Connection!.GetStateAsync(); // omp has read the held prompt
            vm.ComposerText = "ADMIT-LATE change of plan";

            var interrupt = vm.InterruptAndSendCommand.ExecuteAsync(null);
            var replacedEnded = await TestProcesses.Eventually(session.Snapshot,
                s => s.Items.OfType<NoticeItem>().Any(n => n.Text.Contains("replaced run ended", StringComparison.Ordinal)),
                Wait, "the replaced run's late end");
            Assert.Equal(SessionPhase.Running, replacedEnded.Phase);
            await first;
            var waiting = session.Snapshot();
            Assert.Equal(SessionPhase.Running, waiting.Phase);
            vm.Apply(waiting);
            Assert.True(vm.AbortCommand.CanExecute(null));
            Assert.Equal(["HOLD first", "ADMIT-LATE change of plan"], waiting.Items.OfType<UserItem>().Select(u => u.Text));

            await interrupt;
            var done = await TestProcesses.Eventually(session.Snapshot,
                s => s.Phase == SessionPhase.Ready && s.Items.OfType<UserItem>().All(u => u.Confirmed) && s.MessagesCompleted > 0,
                Wait, "the replacement's run to finish");
            Assert.Equal(["HOLD first", "ADMIT-LATE change of plan"], done.Items.OfType<UserItem>().Select(u => u.Text));
            Assert.Empty(vm.ComposerText);
            // Both were delivered: neither is kept for recovery (the draft may be gone entirely).
            await TestProcesses.Eventually(() => DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All,
                all => all.All(d => d.Pending.Length == 0 && d.Composer.IsEmpty), Wait, "both submissions to leave the journal");
        }
        finally { await vm.DisposeAsync(); }
    }
}
