using System.Text.Json;
using Avalonia.Headless.XUnit;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class DraftDeletionTests
{
    private static ImageAttachment Image(string name) => new(name, "image/png",
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jf9sAAAAASUVORK5CYII="));

    private static string SessionFile(string root, string name)
    {
        var directory = Path.Combine(root, "sessions", "project");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "2026-10-15_" + name + ".jsonl");
        File.WriteAllLines(path, [
            JsonSerializer.Serialize(new { type = "title", v = 1, title = name }),
            JsonSerializer.Serialize(new { type = "session", version = 3, id = name, cwd = root }),
        ]);
        return path;
    }

    private static SessionItemViewModel Row(string root, string file) => new(new SessionSummary(file,
        Path.GetFileNameWithoutExtension(file), root, Path.GetFileNameWithoutExtension(file), DateTimeOffset.UtcNow));

    private static void Seed(ClientSettingsStore settings, string root, string file, string id, string text, bool image = false)
    {
        using var drafts = new DraftStore(DraftStore.PathFor(settings.Path));
        drafts.Update(id, d => d with
        {
            Composer = DraftContent.Empty with { Text = text, Images = image ? [new ImageAttachment("discarded-secret.png", "image/png", [31, 41, 59, 26])] : [] },
        }, root, file, flush: true);
    }

    private static MainViewModel Create(string root, ClientSettingsStore settings, string? resume = null, bool deleteSucceeds = true)
    {
        var commands = Path.Combine(root, "commands.json");
        File.WriteAllText(commands, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["/session delete"] = deleteSucceeds
                ? "Session deleted: " + resume + ". Use ACP `session/load` to switch to another session."
                : "Failed to delete session: permission denied",
        }));
        var factory = TestProcesses.FakeFactory("normal", Path.Combine(root, "sessions"));
        var session = new SessionController(request =>
        {
            var spec = factory(request);
            return spec with { Environment = new Dictionary<string, string?>(spec.Environment!) { ["FAKE_OMP_COMMANDS"] = commands } };
        }, new LaunchRequest(root, resume));
        return new MainViewModel(session, new AppArgs(), settings: settings)
        {
            Chats = new ChatFolders(Path.Combine(root, "chats")),
        };
    }

    private static async Task Start(MainViewModel vm)
    {
        var resume = vm.Session.CurrentLaunch.ResumeSessionFile;
        await vm.Session.StartAsync();
        vm.Apply(vm.Session.Snapshot());
        if (resume is not null) Assert.Equal(resume, vm.Session.SessionFile);
    }

    private static void AssertErased(ClientSettingsStore settings, params string[] files)
    {
        var drafts = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
        Assert.DoesNotContain(drafts.All, d => files.Any(f => SessionCatalog.SamePath(d.SessionFile, f)));
        var bytes = File.ReadAllText(DraftStore.PathFor(settings.Path));
        Assert.DoesNotContain("discarded-secret", bytes);
        Assert.DoesNotContain(Convert.ToBase64String([31, 41, 59, 26]), bytes);
    }

    [AvaloniaFact]
    public async Task Sidebar_delete_starts_only_the_shared_root_when_no_chat_has_started()
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("delete-with-idle-engine"));
        var file = SessionFile(root, "saved");
        var openedSessions = Path.Combine(root, "opened-sessions.jsonl");
        var factory = TestProcesses.FakeFactory("normal", Path.Combine(root, "sessions"));
        var controller = new SessionController(request =>
        {
            var spec = factory(request);
            return spec with { Environment = new Dictionary<string, string?>(spec.Environment!)
                { ["FAKE_OMP_SESSION_LOG"] = openedSessions } };
        }, new LaunchRequest(root));
        await using var vm = new MainViewModel(controller, new AppArgs(),
            settings: new ClientSettingsStore(Path.Combine(root, "config.json")))
        {
            Chats = new ChatFolders(Path.Combine(root, "chats")),
        };
        vm.ComposerText = "unstarted draft stays here";
        vm.DeleteSessionItemCommand.Execute(Row(root, file));
        await vm.SessionCard!.Primary.Run();
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(openedSessions)); // no engine_open, resume or unrelated logical chat
        Assert.Equal(SessionPhase.NotStarted, controller.Snapshot().Phase);
        Assert.Null(controller.SessionFile);
        Assert.Equal("unstarted draft stays here", vm.ComposerText);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Current_delete_obeys_runtime_result_and_preserves_only_active_composer_under_fresh_identity(bool succeeds)
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("delete-current-drafts"));
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        var file = SessionFile(root, "current");
        Seed(settings, root, file, "active", "original draft");
        Seed(settings, root, file, "orphan", "discarded-secret-current", image: true);
        var vm = Create(root, settings, file, succeeds);
        try
        {
            await Start(vm);
            vm.ComposerText = "keep my edited composer";
            Assert.True(vm.TryAddImage(Image("keep.png"), "test"));
            vm.DeleteSessionCommand.Execute(null);
            Assert.Equal(SessionCardState.Ask, vm.SessionCard!.State);
            await vm.SessionCard.Primary.Run();
            Assert.Equal("keep my edited composer", vm.ComposerText);
            Assert.Single(vm.Attachments);
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            if (succeeds)
            {
                Assert.NotEqual(file, vm.Session.SessionFile);
                AssertErased(settings, file);
                var saved = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
                Assert.NotEqual("active", Assert.Single(saved.All).Id);
            }
            else
            {
                Assert.Equal(file, vm.Session.SessionFile);
                Assert.Equal(Loc.T("Not deleted"), vm.SessionCard!.Title);
            }
        }
        finally { await vm.DisposeAsync(); }

        if (succeeds)
        {
            AssertErased(settings, file);
            await using var restarted = Create(root, settings);
            Assert.Equal("keep my edited composer", restarted.ComposerText);
            Assert.Equal("keep.png", Assert.Single(restarted.Attachments).Name);
            Assert.Empty(restarted.Session.Snapshot().Items.OfType<UserItem>());
            Assert.Equal(SessionPhase.NotStarted, restarted.Session.Snapshot().Phase);
        }
        else
        {
            var retained = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
            Assert.Equal("keep my edited composer", retained.Get("active")!.Composer.Text);
            Assert.Equal("discarded-secret-current", retained.Get("orphan")!.Composer.Text);
            Assert.All(retained.All, d => Assert.Equal(file, d.SessionFile));
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sidebar_delete_of_background_chat_removes_all_matching_records_only_after_filesystem_success(bool succeeds)
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("delete-sidebar-drafts"));
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        var file = SessionFile(root, "background");
        Seed(settings, root, file, "active", "discarded-secret-background");
        Seed(settings, root, file, "orphan", "discarded-secret-orphan", image: true);
        var vm = Create(root, settings, file);
        try
        {
            await Start(vm);
            var background = vm.Session;
            var lateCheckpoint = background.QueueCheckpoint!;
            var lateDelivery = background.SubmissionDelivered!;
            await vm.NewChatCommand.ExecuteAsync(null);
            vm.ComposerText = "unrelated active draft";
            if (!succeeds)
            {
                File.Delete(file);
                Directory.CreateDirectory(file); // File.Delete must fail on every supported platform, even as root.
            }
            vm.DeleteSessionItemCommand.Execute(Row(root, file));
            await vm.SessionCard!.Primary.Run();
            Assert.DoesNotContain(background, vm.OpenChats);
            Assert.Equal("unrelated active draft", vm.ComposerText);
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            if (succeeds)
            {
                Assert.False(File.Exists(file));
                lateCheckpoint([new QueuedMessage(99, QueueKind.FollowUp, "discarded-secret-late-callback", [])]);
                lateDelivery("old-submission");
                AssertErased(settings, file);
            }
            else Assert.Equal(Loc.T("Not deleted"), vm.SessionCard!.Title);
        }
        finally { await vm.DisposeAsync(); }
        if (succeeds) AssertErased(settings, file);
        else
        {
            var retained = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
            Assert.Equal("discarded-secret-background", retained.Get("active")!.Composer.Text);
            Assert.Equal("discarded-secret-orphan", retained.Get("orphan")!.Composer.Text);
        }
        await using var restarted = Create(root, settings);
        var afterRestart = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
        Assert.Equal(succeeds, afterRestart.Get("active") is null);
    }

    [AvaloniaFact]
    public async Task Project_delete_preserves_shown_composer_and_keeps_only_failed_sessions_recovery_records()
    {
        var root = SessionCatalog.ResolveLinks(TestProcesses.TempDir("delete-project-drafts"));
        var settings = new ClientSettingsStore(Path.Combine(root, "config.json"));
        var current = SessionFile(root, "current");
        var other = SessionFile(root, "other");
        var failed = SessionFile(root, "failed");
        Seed(settings, root, current, "active", "original active draft");
        Seed(settings, root, current, "orphan", "discarded-secret-current", image: true);
        Seed(settings, root, other, "other", "discarded-secret-other");
        Seed(settings, root, failed, "failed", "retain failed deletion");
        File.Delete(failed);
        Directory.CreateDirectory(failed);
        var vm = Create(root, settings, current);
        string replacementProject;
        try
        {
            await Start(vm);
            vm.ComposerText = "keep project edit";
            Assert.True(vm.TryAddImage(Image("project-keep.png"), "test"));
            vm.Sessions.Clear();
            foreach (var file in new[] { current, other, failed }) vm.Sessions.Add(Row(root, file));
            vm.RemoveProjectCommand.Execute(new SessionGroupViewModel(root, true));
            await vm.SessionCard!.Tertiary.Run();
            Assert.False(File.Exists(current));
            Assert.False(File.Exists(other));
            Assert.True(Directory.Exists(failed));
            Assert.Equal("keep project edit", vm.ComposerText);
            Assert.Equal("project-keep.png", Assert.Single(vm.Attachments).Name);
            Assert.Empty(vm.Session.Snapshot().Items.OfType<UserItem>());
            AssertErased(settings, current, other);
            var saved = DraftStore.ReadSnapshot(DraftStore.PathFor(settings.Path));
            Assert.Equal("retain failed deletion", saved.Get("failed")!.Composer.Text);
            Assert.NotEqual("active", Assert.Single(saved.All, d => d.Composer.Text == "keep project edit").Id);
            replacementProject = vm.Session.CurrentLaunch.WorkingDirectory!;
        }
        finally { await vm.DisposeAsync(); }
        AssertErased(settings, current, other);
        Assert.True(Directory.Exists(replacementProject), "A draft-only chat still needs its working directory after shutdown.");
        await using var restarted = Create(replacementProject, settings);
        await Start(restarted);
        Assert.Equal(SessionPhase.Ready, restarted.Phase);
        Assert.Equal("keep project edit", restarted.ComposerText);
        Assert.Single(restarted.Attachments);
        Assert.Empty(restarted.Session.Snapshot().Items.OfType<UserItem>());
    }
}
