using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class OversizedInputFileTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static string LargeText => new('"', 600_000);
    private static ImageAttachment Image() => new("keep.png", "image/png",
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf9sAAAAASUVORK5CYII="));
    private static string FilesDirectory(ClientSettingsStore settings) => DraftStore.PathFor(settings.Path) + ".input-files";

    private static async Task<(SessionController Session, MainViewModel Vm, ClientSettingsStore Settings)> CreateAsync(string scenario = "normal")
    {
        var root = TestProcesses.TempDir("input files with spaces");
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        var session = new SessionController(TestProcesses.FakeFactory(scenario, Path.Combine(root, "sessions")), new LaunchRequest(root));
        var vm = new MainViewModel(session, new AppArgs(), settings: settings) { Chats = new ChatFolders(Path.Combine(root, "chats")) };
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        return (session, vm, settings);
    }

    [AvaloniaFact]
    public async Task Visible_explicit_action_sends_real_bounded_reference_and_preserves_all_file_text_and_images()
    {
        var (session, vm, settings) = await CreateAsync();
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();
        try
        {
            var text = "  начало\n  ";
            var paste = "\n pasted\nsecond\nthird\nfourth\n" + LargeText + "  \n";
            vm.ComposerText = text;
            Assert.True(vm.TryAddPastedText(paste));
            Assert.True(vm.AddCodeMark(new CodeAnchor("source.cs", 4, false, "source text"), "keep this comment"));
            Assert.True(vm.TryAddImage(Image(), "test"));
            var expected = text + "\n\n" + paste + "\n\n" + LocalMarks.CodeBlock(vm.Marks.Models());
            await vm.SendCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var button = window.FindControl<Button>("SendAsFileButton")!;
            Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
            Assert.Equal(text, vm.ComposerText);
            Assert.False(Directory.Exists(FilesDirectory(settings)));
            Assert.Empty(session.Snapshot().Items.OfType<UserItem>());

            await ((IAsyncRelayCommand)button.Command!).ExecuteAsync(null);
            var delivered = await TestProcesses.Eventually(session.Snapshot,
                s => s.Items.OfType<UserItem>().Any(u => u.Confirmed), Wait, "file reference delivered");
            var path = Assert.Single(Directory.GetFiles(FilesDirectory(settings), "*.txt"));
            Assert.Equal(expected, File.ReadAllText(path, Encoding.UTF8));
            Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path));
            var user = Assert.Single(delivered.Items.OfType<UserItem>());
            Assert.Contains(FilesViewModel.Mention(path), user.Text);
            Assert.DoesNotContain(LargeText, user.Text);
            SessionController.ValidateMessage(user.Text, user.Images);
            Assert.Equal(Image().Data, Assert.Single(user.Images).Data);
            Assert.Empty(vm.ComposerText);
            Assert.Empty(vm.PastedTexts);
            Assert.Empty(vm.Marks.Items);
            Assert.Empty(vm.Attachments);
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEffectivelyVisible);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(FilesDirectory(settings)));
            }
            await vm.DisposeAsync();
            Assert.Equal(expected, File.ReadAllText(path)); // closing cannot orphan persisted conversation references
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task File_write_failure_keeps_original_chips_images_and_can_be_retried_explicitly()
    {
        var (session, vm, settings) = await CreateAsync();
        await using var _ = vm;
        vm.ComposerText = LargeText;
        vm.TryAddPastedText("one\ntwo\nthree\nfour");
        vm.AddCodeMark(new CodeAnchor("a.cs", 1, false, "a"), "keep");
        vm.TryAddImage(Image(), "test");
        await vm.SendCommand.ExecuteAsync(null);
        File.WriteAllText(FilesDirectory(settings), "blocks directory creation");
        await vm.SendAsFileCommand.ExecuteAsync(null);
        Assert.Equal(LargeText, vm.ComposerText);
        Assert.Single(vm.PastedTexts);
        Assert.Single(vm.Marks.Items);
        Assert.Single(vm.Attachments);
        Assert.True(vm.HasOversizedInput);
        Assert.NotEmpty(vm.ComposerMessage);
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
        File.Delete(FilesDirectory(settings));
        await vm.SendAsFileCommand.ExecuteAsync(null);
        Assert.Single(Directory.GetFiles(FilesDirectory(settings), "*.txt"));
        Assert.Empty(vm.ComposerText);
    }

    [AvaloniaFact]
    public async Task Images_that_still_exceed_wire_limit_do_not_write_or_clear_any_input()
    {
        var (session, vm, settings) = await CreateAsync();
        await using var _ = vm;
        vm.ComposerText = LargeText;
        vm.TryAddPastedText("one\ntwo\nthree\nfour");
        vm.TryAddImage(new ImageAttachment("large.png", "image/png", new byte[900_000]), "test");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.HasOversizedInput);
        await vm.SendAsFileCommand.ExecuteAsync(null);
        Assert.Equal(LargeText, vm.ComposerText);
        Assert.Single(vm.PastedTexts);
        Assert.Equal(900_000, Assert.Single(vm.Attachments).Model.Data.Length);
        Assert.NotEmpty(vm.ComposerMessage);
        Assert.False(Directory.Exists(FilesDirectory(settings)));
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
    }

    [AvaloniaFact]
    public async Task Draft_checkpoint_failure_after_file_write_preserves_original_input()
    {
        var (session, vm, settings) = await CreateAsync();
        await using var _ = vm;
        vm.ComposerText = LargeText;
        vm.TryAddImage(Image(), "test");
        await vm.SendCommand.ExecuteAsync(null);
        // Block the journal's atomic replacement after it was loaded successfully.
        var journal = DraftStore.PathFor(settings.Path);
        if (File.Exists(journal)) File.Delete(journal);
        Directory.CreateDirectory(journal);
        try
        {
            await vm.SendAsFileCommand.ExecuteAsync(null);
            Assert.Equal(LargeText, vm.ComposerText);
            Assert.Single(vm.Attachments);
            Assert.NotEmpty(vm.ComposerMessage);
            Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
            var file = Assert.Single(Directory.GetFiles(FilesDirectory(settings), "*.txt"));
            Assert.Equal(LargeText, File.ReadAllText(file));
        }
        finally { Directory.Delete(journal); }
    }

    [AvaloniaFact]
    public async Task Runtime_failure_after_staging_restores_original_chips_and_images()
    {
        var (session, vm, settings) = await CreateAsync();
        await using var _ = vm;
        vm.ComposerText = LargeText;
        vm.TryAddPastedText("one\ntwo\nthree\nfour");
        vm.AddCodeMark(new CodeAnchor("a.cs", 1, false, "a"), "keep");
        vm.TryAddImage(Image(), "test");
        await vm.SendCommand.ExecuteAsync(null);
        var delivered = 0;
        var checkpoint = session.SubmissionDelivered;
        session.SubmissionDelivered = id => { delivered++; checkpoint?.Invoke(id); };
        // No UI pump: the button still reflects Ready. Stop retains the closed connection object, so PromptAsync
        // stages an optimistic row before RequestAsync rejects the request without writing to the transport.
        await session.StopAsync();
        Assert.NotNull(session.Connection!.CloseReason);
        await vm.SendAsFileCommand.ExecuteAsync(null);
        Assert.Equal(LargeText, vm.ComposerText);
        Assert.Single(vm.PastedTexts);
        Assert.Single(vm.Marks.Items);
        Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
        Assert.Equal(0, delivered);
        var file = Assert.Single(Directory.GetFiles(FilesDirectory(settings), "*.txt"));
        Assert.Contains(LargeText, File.ReadAllText(file));
        // EndRun finalizes even failed optimistic rows as Confirmed; that flag is not a delivery acknowledgement.
        Assert.Contains(FilesViewModel.Mention(file), Assert.Single(session.Snapshot().Items.OfType<UserItem>()).Text);
        var saved = Assert.Single(DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All);
        Assert.Empty(saved.Pending);
        Assert.Equal(LargeText, saved.Composer.Text);
        Assert.Single(saved.Composer.Pastes);
        Assert.Single(saved.Composer.Marks);
        Assert.Equal(Image().Data, Assert.Single(saved.Composer.Images).Data);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_file_follow_up_has_one_recovery_source_in_memory_and_after_reopen(bool failQueueCheckpoint)
    {
        var (session, vm, settings) = await CreateAsync("invalid-admission");
        try
        {
            vm.ComposerText = LargeText;
            vm.TryAddPastedText("one\ntwo\nthree\nfour");
            vm.AddCodeMark(new CodeAnchor("a.cs", 1, false, "a"), "keep");
            vm.TryAddImage(Image(), "test");
            var original = LargeText + "\n\none\ntwo\nthree\nfour\n\n" + LocalMarks.CodeBlock(vm.Marks.Models());
            await vm.SendCommand.ExecuteAsync(null);
            var checkpoint = session.QueueCheckpoint;
            var checkpointFailed = false;
            if (failQueueCheckpoint)
                session.QueueCheckpoint = queue =>
                {
                    // Leave the durable Pending copy in place while runtime admission creates a recovered row.
                    if (queue.Count > 0) { checkpointFailed = true; throw new IOException("queue checkpoint unavailable"); }
                    checkpoint?.Invoke(queue);
                };
            // The UI can still show the previous run as Running when the engine has become idle. This takes the
            // real follow_up path; the fixture rejects its acknowledgement instead of creating an agent turn.
            vm.Phase = SessionPhase.Running;
            await vm.SendAsFileCommand.ExecuteAsync(null);
            session.QueueCheckpoint = checkpoint;
            Assert.Equal(failQueueCheckpoint, checkpointFailed);
            var file = Assert.Single(Directory.GetFiles(FilesDirectory(settings), "*.txt"));
            Assert.Equal(original, File.ReadAllText(file));
            var reference = vm.ComposerText;
            Assert.Contains(FilesViewModel.Mention(file), reference);
            Assert.DoesNotContain(LargeText, reference);
            SessionController.ValidateMessage(reference, vm.Attachments.Select(a => a.Model).ToArray());
            Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
            Assert.Empty(vm.PastedTexts);
            Assert.Empty(vm.Marks.Items);
            Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
            Assert.Empty(session.Snapshot().Queued);
            vm.Apply(session.Snapshot());
            vm.Apply(session.Snapshot());
            Assert.Equal(reference, vm.ComposerText);
            Assert.Single(vm.Attachments);
            var saved = Assert.Single(DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All);
            Assert.Equal(reference, saved.Composer.Text);
            Assert.Equal(Image().Data, Assert.Single(saved.Composer.Images).Data);
            Assert.Empty(saved.Pending);
            Assert.Empty(saved.Queue);
            await vm.DisposeAsync();
            var reopenedSession = new SessionController(TestProcesses.FakeFactory("normal", Path.Combine(saved.Project, "sessions")),
                new LaunchRequest(saved.Project, saved.SessionFile));
            await using var reopened = new MainViewModel(reopenedSession, new AppArgs(), settings: settings);
            Assert.Equal(reference, reopened.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(reopened.Attachments).Model.Data);
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Empty(reopened.PastedTexts);
            Assert.Empty(reopened.Marks.Items);
            Assert.Equal(SessionPhase.NotStarted, reopenedSession.Snapshot().Phase);
            Assert.Empty(reopenedSession.Snapshot().Items.OfType<UserItem>());
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Queued_file_reference_survives_stop_and_reopen_without_automatic_resend()
    {
        var (session, vm, settings) = await CreateAsync("slow-stream");
        try
        {
            vm.ComposerText = "first";
            await vm.SendCommand.ExecuteAsync(null);
            vm.Apply(await TestProcesses.Eventually(session.Snapshot,
                s => s.Phase == SessionPhase.Running && s.Items.OfType<UserItem>().Any(u => u.Confirmed), Wait, "running"));
            vm.ComposerText = LargeText;
            vm.TryAddImage(Image(), "test");
            await vm.SendCommand.ExecuteAsync(null);
            await vm.SendAsFileCommand.ExecuteAsync(null);
            var queued = Assert.Single(session.Snapshot().Queued);
            var file = Assert.Single(Directory.GetFiles(FilesDirectory(settings), "*.txt"));
            Assert.Contains(FilesViewModel.Mention(file), queued.Text);
            var saved = Assert.Single(DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All);
            Assert.Equal(queued.Text, Assert.Single(saved.Queue).Text);
            Assert.Empty(vm.ComposerText);
            vm.ComposerText = "new edit";
            await vm.AbortCommand.ExecuteAsync(null);
            Assert.Contains("new edit", vm.ComposerText);
            Assert.Contains(FilesViewModel.Mention(file), vm.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
            await vm.DisposeAsync();
            var reopenedSession = new SessionController(TestProcesses.FakeFactory("normal", Path.Combine(Path.GetDirectoryName(settings.Path)!, "sessions")),
                new LaunchRequest(session.CurrentLaunch.WorkingDirectory, session.SessionFile));
            await using var reopened = new MainViewModel(reopenedSession, new AppArgs(), settings: settings);
            Assert.Contains(FilesViewModel.Mention(file), reopened.ComposerText);
            Assert.Equal(LargeText, File.ReadAllText(file));
            Assert.Equal(Image().Data, Assert.Single(reopened.Attachments).Model.Data);
            Assert.Equal(SessionPhase.NotStarted, reopenedSession.Snapshot().Phase);
            Assert.Empty(reopenedSession.Snapshot().Items.OfType<UserItem>());
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Cleaning_requires_review_and_slash_commands_are_not_converted_to_prose()
    {
        var (session, vm, settings) = await CreateAsync();
        await using var _ = vm;
        vm.ComposerText = "\u200b" + LargeText;
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(LargeText, vm.ComposerText);
        Assert.False(vm.HasOversizedInput);
        await vm.SendAsFileCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(FilesDirectory(settings)));
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.HasOversizedInput);
        vm.ComposerText = "/compact " + LargeText;
        await vm.SendAsFileCommand.ExecuteAsync(null); // a stale button action cannot convert the edited command
        await vm.SendCommand.ExecuteAsync(null);
        Assert.False(vm.HasOversizedInput);
        Assert.StartsWith("/compact ", vm.ComposerText);
        Assert.False(Directory.Exists(FilesDirectory(settings)));
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
    }

    [AvaloniaFact]
    public async Task Edits_while_file_is_being_written_cancel_submission_without_clearing_new_input()
    {
        var (session, vm, settings) = await CreateAsync();
        await using var _ = vm;
        vm.ComposerText = LargeText;
        vm.TryAddImage(Image(), "test");
        await vm.SendCommand.ExecuteAsync(null);
        var sending = vm.SendAsFileCommand.ExecuteAsync(null);
        vm.ComposerText = "newer edit";
        await sending;
        Assert.Equal("newer edit", vm.ComposerText);
        Assert.Single(vm.Attachments);
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
    }

    [AvaloniaFact]
    public async Task Session_navigation_invalidates_offer_and_leaves_original_draft_usable()
    {
        var (session, vm, settings) = await CreateAsync();
        await using var _ = vm;
        vm.ComposerText = LargeText;
        vm.TryAddImage(Image(), "test");
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.HasOversizedInput);
        await vm.NewChatCommand.ExecuteAsync(null);
        Assert.NotSame(session, vm.Session);
        Assert.False(vm.HasOversizedInput);
        await vm.SendAsFileCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(FilesDirectory(settings)));
        Assert.Empty(vm.ComposerText);
        Assert.Empty(vm.Attachments);
        await vm.DisposeAsync();
        var source = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All.Single(d => d.Project == session.CurrentLaunch.WorkingDirectory);
        Assert.Equal(LargeText, source.Composer.Text);
        Assert.Equal(Image().Data, Assert.Single(source.Composer.Images).Data);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigation_or_close_during_file_write_cannot_send_to_either_session(bool close)
    {
        var (session, vm, settings) = await CreateAsync();
        try
        {
            vm.ComposerText = LargeText;
            vm.TryAddImage(Image(), "test");
            await vm.SendCommand.ExecuteAsync(null);
            var sending = vm.SendAsFileCommand.ExecuteAsync(null);
            if (close) await vm.DisposeAsync();
            else await vm.NewChatCommand.ExecuteAsync(null);
            await sending;
            Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            await vm.DisposeAsync();
            var source = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path)).All.Single(d => d.Project == session.CurrentLaunch.WorkingDirectory);
            Assert.Equal(LargeText, source.Composer.Text);
            Assert.Equal(Image().Data, Assert.Single(source.Composer.Images).Data);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Read_only_command_guard_does_not_write_even_with_stale_offer_state()
    {
        var root = TestProcesses.TempDir("read-only-input-file");
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        var session = new SessionController(TestProcesses.FakeFactory("normal", Path.Combine(root, "sessions")),
            new LaunchRequest(root) { ReadOnly = true });
        await using var vm = new MainViewModel(session, new AppArgs(), settings: settings);
        vm.ComposerText = LargeText;
        vm.Phase = SessionPhase.Ready;
        vm.HasOversizedInput = true;
        Assert.False(vm.SendAsFileCommand.CanExecute(null));
        await vm.SendAsFileCommand.ExecuteAsync(null);
        Assert.Equal(LargeText, vm.ComposerText);
        Assert.False(Directory.Exists(FilesDirectory(settings)));
        Assert.Empty(session.Snapshot().Items.OfType<UserItem>());
    }
}
