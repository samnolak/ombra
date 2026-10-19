using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class DraftFolderRecoveryTests
{
    private static ImageAttachment Image() => new("kept.png", "image/png",
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf9sAAAAASUVORK5CYII="));

    private static MainViewModel Create(string root, string project, ClientSettingsStore settings, ChatFolders chats) =>
        new(new SessionController(TestProcesses.FakeFactory("normal", Path.Combine(root, "sessions")), new LaunchRequest(project)),
            new AppArgs(), settings: settings) { Chats = chats };

    private static void Seed(ClientSettingsStore settings, string id, string project, string text)
    {
        using var store = new DraftStore(DraftStore.PathFor(settings.Path));
        store.Update(id, d => d with { Composer = new DraftContent(text, [Image()], [], []) }, project, flush: true);
    }

    private static async Task Until(Func<bool> done, string outcome)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Assert.True(done(), outcome);
    }

    [AvaloniaFact]
    public async Task Draft_only_chat_keeps_its_folder_until_the_reopened_composer_is_cleared()
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("draft-folder-lifetime"));
        var chats = new ChatFolders(Path.Combine(root, "chats"));
        var project = chats.Create();
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        var vm = Create(root, project, settings, chats);
        try
        {
            await vm.Session.StartAsync();
            vm.Apply(vm.Session.Snapshot());
            vm.ComposerText = "never submitted";
            Assert.True(vm.TryAddImage(Image(), "test"));
            Assert.False(File.Exists(vm.Session.SessionFile));
        }
        finally { await vm.DisposeAsync(); }
        Assert.True(Directory.Exists(project));

        var reopened = Create(root, project, settings, chats);
        try
        {
            await reopened.Session.StartAsync();
            reopened.Apply(reopened.Session.Snapshot());
            Assert.Equal(SessionPhase.Ready, reopened.Phase);
            Assert.Equal("never submitted", reopened.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(reopened.Attachments).Model.Data);
            Assert.Empty(reopened.Session.Snapshot().Items.OfType<UserItem>());
            Assert.Empty(reopened.Session.Snapshot().Queued);
            reopened.ComposerText = "";
            Assert.Single(reopened.Attachments).RemoveCommand.Execute(null);
        }
        finally { await reopened.DisposeAsync(); }
        Assert.False(Directory.Exists(project));
        var stored = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
        Assert.Empty(stored.All);
    }

    [AvaloniaFact]
    public async Task Window_start_recreates_missing_owned_draft_folders_for_shown_and_background_chats_without_sending()
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("draft-orphan-folders"));
        var chats = new ChatFolders(Path.Combine(root, "chats"));
        var first = chats.Create();
        var second = chats.Create();
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        Seed(settings, "first", first, "first recovered draft");
        Seed(settings, "second", second, "second recovered draft");
        Directory.Delete(first);
        Directory.Delete(second);
        var vm = Create(root, first, settings, chats);
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();
        try
        {
            vm.OnWindowOpened();
            await Until(() => vm.OpenChats.Count == 2 && vm.OpenChats.All(c => c.Snapshot().Phase == SessionPhase.Ready), "both recovered chats started");
            await Until(() => vm.Phase == SessionPhase.Ready, "shown chat is ready");
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
            Assert.Equal("first recovered draft", vm.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
            var background = Assert.Single(vm.OpenChats, c => c != vm.Session);
            await vm.OpenSessionCommand.ExecuteAsync(new SessionItemViewModel(new SessionSummary(
                background.SessionFile!, "second", second, "Second draft", DateTimeOffset.UtcNow)));
            Assert.Same(background, vm.Session);
            Assert.Equal("second recovered draft", vm.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
            Assert.All(vm.OpenChats, c =>
            {
                Assert.Empty(c.Snapshot().Items.OfType<UserItem>());
                Assert.Empty(c.Snapshot().Queued);
            });
        }
        finally { await vm.DisposeAsync(); window.Close(); }
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_external_project_or_blocked_owned_folder_keeps_recovery_visible_without_creating_an_external_directory(bool owned)
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("draft-folder-start-failure"));
        var chats = new ChatFolders(Path.Combine(root, "chats"));
        var project = owned ? Path.Combine(chats.Root, "blocked-chat") : Path.Combine(root, "missing-project");
        if (owned) File.WriteAllText(chats.Root, "keep this blocking file");
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        Seed(settings, "recoverable", project, "keep input after failed start");
        var vm = Create(root, project, settings, chats);
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();
        try
        {
            vm.OnWindowOpened();
            await Until(() => vm.Phase == SessionPhase.Faulted, "failed start remains visible");
            Assert.True(vm.Session.Snapshot().StartFailed);
            Assert.False(string.IsNullOrEmpty(vm.Session.Snapshot().LastError));
            Assert.False(Directory.Exists(project));
            if (owned) Assert.Equal("keep this blocking file", File.ReadAllText(chats.Root));
            Assert.Equal("keep input after failed start", vm.ComposerText);
            Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            Assert.True(vm.CanRecover);
            Assert.Null(vm.Session.ProcessId);
            if (owned)
            {
                File.Delete(chats.Root);
                await vm.RecoverCommand.ExecuteAsync(null);
                await Until(() => vm.Phase == SessionPhase.Ready, "retry recreates the owned folder inside the core start lifecycle");
                Assert.True(Directory.Exists(project));
                Assert.Equal("keep input after failed start", vm.ComposerText);
                Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
                Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            }
            else
            {
                await vm.RecoverCommand.ExecuteAsync(null);
                Assert.Equal(SessionPhase.Faulted, vm.Phase);
                Assert.False(Directory.Exists(project));
                Assert.Null(vm.Session.ProcessId);
                Directory.CreateDirectory(project); // Only the user restores an external project.
                await vm.RecoverCommand.ExecuteAsync(null);
                await Until(() => vm.Phase == SessionPhase.Ready, "retry uses the externally restored project");
                Assert.Equal("keep input after failed start", vm.ComposerText);
                Assert.Equal(Image().Data, Assert.Single(vm.Attachments).Model.Data);
                Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
                Assert.Empty(vm.Session.Snapshot().Queued);
            }
        }
        finally { await vm.DisposeAsync(); window.Close(); }
        var stored = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
        var draft = Assert.Single(stored.All);
        Assert.Equal("keep input after failed start", draft.Composer.Text);
        Assert.Equal(Image().Data, Assert.Single(draft.Composer.Images).Data);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleting_background_owned_chat_retries_folder_cleanup_only_after_success(bool succeeds)
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("delete-owned-draft-folder"));
        var chats = new ChatFolders(Path.Combine(root, "chats"));
        var project = chats.Create();
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        var vm = Create(root, project, settings, chats);
        try
        {
            await vm.Session.StartAsync();
            vm.Apply(vm.Session.Snapshot());
            vm.ComposerText = "saved conversation";
            await vm.SendCommand.ExecuteAsync(null);
            await Until(() => vm.Session.Snapshot().Phase == SessionPhase.Ready && File.Exists(vm.Session.SessionFile), "conversation file was saved");
            vm.Apply(vm.Session.Snapshot());
            var file = vm.Session.SessionFile!;
            var background = vm.Session;
            vm.ComposerText = "discard this private draft";
            Assert.True(vm.TryAddImage(Image(), "test"));
            await vm.NewChatCommand.ExecuteAsync(null);
            vm.ComposerText = "keep the replacement draft";
            if (!succeeds)
            {
                File.Delete(file);
                Directory.CreateDirectory(file);
            }
            vm.DeleteSessionItemCommand.Execute(new SessionItemViewModel(
                new SessionSummary(file, "owned", project, "Owned chat", DateTimeOffset.UtcNow)));
            await vm.SessionCard!.Primary.Run();
            Assert.DoesNotContain(background, vm.OpenChats);
            Assert.Equal(!succeeds, Directory.Exists(project));
            Assert.True(Directory.Exists(vm.Session.CurrentLaunch.WorkingDirectory));
            Assert.Equal("keep the replacement draft", vm.ComposerText);
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            var saved = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
            Assert.Equal(!succeeds, saved.HasDraftInProject(project));
        }
        finally { await vm.DisposeAsync(); }
    }

    [Fact]
    public async Task Launch_preparation_follows_sibling_and_recovery_uses_normal_start_failure_boundary()
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("prepare-draft-launch"));
        var project = Path.Combine(root, "recovered-chat");
        var nextProject = Path.Combine(root, "another-owned-chat");
        var blocked = true;
        await using var parent = new SessionController(TestProcesses.FakeFactory("normal", Path.Combine(root, "sessions")), new LaunchRequest(root))
        {
            PrepareLaunch = request =>
            {
                if (!SessionCatalog.SamePath(request.WorkingDirectory, project)
                    && !SessionCatalog.SamePath(request.WorkingDirectory, nextProject)) return;
                if (blocked) throw new IOException("Working folder preparation failed");
                Directory.CreateDirectory(request.WorkingDirectory!);
            },
        };
        await using var sibling = parent.Sibling(new LaunchRequest(project));
        await Assert.ThrowsAsync<IOException>(() => sibling.StartAsync());
        Assert.Equal(SessionPhase.Faulted, sibling.Snapshot().Phase);
        Assert.True(sibling.Snapshot().StartFailed);
        Assert.Contains("Working folder preparation failed", sibling.Snapshot().LastError);
        Assert.Null(sibling.ProcessId);
        Assert.False(Directory.Exists(project));
        await sibling.RecoverAsync(); // still blocked: normal startup failure, not an unhandled preparation exception
        Assert.Equal(SessionPhase.Faulted, sibling.Snapshot().Phase);
        Assert.True(sibling.Snapshot().StartFailed);
        Assert.Null(sibling.ProcessId);
        blocked = false;
        await sibling.RecoverAsync();
        Assert.Equal(SessionPhase.Ready, sibling.Snapshot().Phase);
        Assert.True(Directory.Exists(project));
        Assert.Equal(project, sibling.Snapshot().Cwd);
        Assert.Empty(sibling.Snapshot().Items.OfType<UserItem>());
        var pid = sibling.ProcessId;
        Assert.NotNull(pid);
        blocked = true;
        await sibling.OpenFolderAsync(nextProject);
        Assert.Equal(pid, sibling.ProcessId);
        Assert.Equal(SessionPhase.Ready, sibling.Snapshot().Phase);
        Assert.Equal(project, sibling.CurrentLaunch.WorkingDirectory);
        Assert.False(Directory.Exists(nextProject));

        var external = Path.Combine(root, "missing-external-project");
        await sibling.OpenFolderAsync(external);
        Assert.Equal(pid, sibling.ProcessId);
        Assert.Equal(SessionPhase.Ready, sibling.Snapshot().Phase);
        Assert.Equal(project, sibling.CurrentLaunch.WorkingDirectory);
        Assert.False(Directory.Exists(external));

        blocked = false;
        await sibling.OpenFolderAsync(nextProject);
        Assert.Equal(SessionPhase.Ready, sibling.Snapshot().Phase);
        Assert.True(Directory.Exists(nextProject));
        Assert.Equal(nextProject, sibling.Snapshot().Cwd);
        Assert.Empty(sibling.Snapshot().Items.OfType<UserItem>());
        Assert.Empty(sibling.Snapshot().Queued);
    }
}
