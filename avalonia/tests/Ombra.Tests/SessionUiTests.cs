using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>Sessions sidebar and header in the real window over the fake omp (sessions kept in a temp folder).</summary>
public sealed class SessionUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm, SessionController s)> OpenAsync(double width = 1200, double height = 760)
    {
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("ui-sessions")), new LaunchRequest(TestProcesses.TempDir("ui-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm, s);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static async Task SendAsync(MainWindow w, MainViewModel vm, string text)
    {
        w.FindControl<TextBox>("Composer")!.Focus();
        w.KeyTextInput(text);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == text) && vm.Rows.OfType<ToolRowViewModel>().Any(), "run " + text);
    }

    private static async Task<SessionItemViewModel> MissingFolderSessionAsync(int count = 1)
    {
        var root = TestProcesses.TempDir("missing-session-folder");
        var cwd = Path.Combine(root, "deleted-project");
        var file = Path.Combine(root, "saved.jsonl");
        var id = Guid.NewGuid().ToString();
        var lines = new List<string>
        {
            System.Text.Json.JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }),
        };
        for (var i = 0; i < count; i++)
            lines.Add(System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "message", id = $"m{i}", parentId = i == 0 ? null : $"m{i - 1}",
                message = new { role = "user", content = new[] { new { type = "text", text = $"Saved message {i}" } } },
            }));
        await File.WriteAllLinesAsync(file, lines);
        return new SessionItemViewModel(new SessionSummary(file, id, cwd, "Saved conversation", DateTimeOffset.UtcNow));
    }

    [AvaloniaFact]
    public async Task Read_only_never_claims_recovers_or_rewrites_the_source_draft()
    {
        var saved = await MissingFolderSessionAsync();
        var root = TestProcesses.TempDir("read-only-source-draft");
        using var drafts = new DraftStore(Path.Combine(root, "drafts.json"));
        drafts.Update("source", d => d with { Composer = DraftContent.Empty with { Text = "Unsent source input" } },
            saved.Cwd, saved.Model.Path, flush: true);
        var original = await File.ReadAllBytesAsync(drafts.Path!);
        var session = new SessionController(TestProcesses.FakeFactory("normal", root),
            new LaunchRequest(saved.Cwd, saved.Model.Path) { ReadOnly = true });
        var vm = new MainViewModel(session, new AppArgs(), drafts: drafts);
        try
        {
            Assert.True(vm.IsReadOnly);
            Assert.Equal("", vm.ComposerText);
            await session.StartAsync();
            vm.Apply(session.Snapshot());
            Assert.Equal("", vm.ComposerText);
            vm.ComposerText = "A direct binding assignment must not persist";
            await vm.SendCommand.ExecuteAsync(null);
            await vm.DisposeAsync();
            Assert.Equal(original, await File.ReadAllBytesAsync(drafts.Path!));
            Assert.Equal("Unsent source input", Assert.Single(drafts.All).Composer.Text);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Missing_folder_picker_cancellation_keeps_current_session()
    {
        var saved = await MissingFolderSessionAsync();
        var root = TestProcesses.TempDir("missing-folder-picker");
        var session = new SessionController(TestProcesses.FakeFactory("normal", root),
            new LaunchRequest(saved.Cwd, saved.Model.Path) { ReadOnly = true });
        await using var vm = new MainViewModel(session, new AppArgs());
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        var calls = 0;
        vm.PickFolderRequested += () => { calls++; return Task.FromResult<string?>(null); };
        var other = await MissingFolderSessionAsync();
        await vm.OpenSessionCommand.ExecuteAsync(other);
        var card = vm.SessionCard!;
        Assert.Equal(Loc.T("Choose project folder…"), card.Primary!.Label);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)card.Primary.Command).ExecuteAsync(null);
        Assert.Equal(1, calls);
        Assert.Same(session, vm.Session);
        Assert.Same(card, vm.SessionCard);
        Assert.Single(vm.OpenChats);
    }

    [AvaloniaFact]
    public async Task Offered_project_disappearing_does_not_fall_back_or_replace_live_chat()
    {
        var (window, vm, live) = await OpenAsync();
        try
        {
            vm.ComposerText = "Keep after refusal";
            var saved = await MissingFolderSessionAsync();
            await vm.OpenSessionCommand.ExecuteAsync(saved);
            var card = vm.SessionCard!;
            Directory.Delete(vm.ProjectFolder!);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)card.Primary!.Command).ExecuteAsync(null);
            Assert.Same(live, vm.Session);
            Assert.Same(card, vm.SessionCard);
            Assert.Equal("Keep after refusal", vm.ComposerText);
            Assert.Single(vm.OpenChats);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Missing_folder_choice_cancellation_keeps_current_chat_and_draft()
    {
        var (window, vm, live) = await OpenAsync();
        try
        {
            vm.ComposerText = "Keep this unsent draft";
            var saved = await MissingFolderSessionAsync();
            var original = await File.ReadAllBytesAsync(saved.Model.Path);
            await vm.OpenSessionCommand.ExecuteAsync(saved);
            Assert.Equal("missing-session-folder", vm.SessionCard?.Kind);
            Assert.Equal(Loc.T("Open in current project"), vm.SessionCard?.Primary?.Label);
            Assert.Equal(Loc.T("Read only"), vm.SessionCard?.Secondary?.Label);
            Assert.Same(live, vm.Session);
            vm.SessionCard!.DismissCommand!.Execute(null);
            Assert.Null(vm.SessionCard);
            Assert.Same(live, vm.Session);
            Assert.Equal("Keep this unsent draft", vm.ComposerText);
            Assert.Single(vm.OpenChats);
            Assert.Equal(original, await File.ReadAllBytesAsync(saved.Model.Path));
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Missing_folder_explicit_resume_uses_the_offered_project_and_keeps_live_draft()
    {
        var (window, vm, live) = await OpenAsync();
        try
        {
            vm.ComposerText = "My live draft";
            var target = vm.ProjectFolder;
            var saved = await MissingFolderSessionAsync();
            await vm.OpenSessionCommand.ExecuteAsync(saved);
            Assert.Contains(target!, vm.SessionCard!.Detail);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.SessionCard.Primary!.Command).ExecuteAsync(null);
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Session != live, "explicit resume");
            Assert.False(vm.IsReadOnly);
            Assert.Equal(target, vm.Session.CurrentLaunch.WorkingDirectory);
            Assert.Equal(target, vm.ProjectPath);
            Assert.Equal(saved.Model.Path, vm.Session.CurrentLaunch.ResumeSessionFile);
            Assert.Contains(live, vm.OpenChats);
            var original = new SessionItemViewModel(new SessionSummary(live.SessionFile!, "live", target!, "Live", DateTimeOffset.UtcNow));
            await vm.OpenSessionCommand.ExecuteAsync(original);
            Assert.Same(live, vm.Session);
            Assert.Equal("My live draft", vm.ComposerText);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Read_only_open_failure_returns_to_live_chat_without_consuming_draft()
    {
        var (window, vm, live) = await OpenAsync();
        try
        {
            vm.ComposerText = "Keep after failed read";
            var saved = await MissingFolderSessionAsync();
            await vm.OpenSessionCommand.ExecuteAsync(saved);
            var read = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.SessionCard!.Secondary!.Command;
            File.Delete(saved.Model.Path);
            await read.ExecuteAsync(null);
            await Until(() => vm.Session == live && vm.OpenChats.Count == 1, "return after failed read");
            Assert.Equal("Keep after failed read", vm.ComposerText);
            Assert.Equal("open-failed", vm.SessionCard?.Kind);
            Assert.False(vm.IsReadOnly);
            Assert.False(File.Exists(saved.Model.Path));
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Read_only_missing_folder_renders_pages_blocks_input_and_reopens_without_writer()
    {
        var (window, vm, live) = await OpenAsync();
        try
        {
            vm.ComposerText = "Do not consume this draft";
            var saved = await MissingFolderSessionAsync(240);
            var original = await File.ReadAllBytesAsync(saved.Model.Path);
            await vm.OpenSessionCommand.ExecuteAsync(saved);
            var read = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.SessionCard!.Secondary!.Command).ExecuteAsync(null);
            Assert.True(vm.IsReadOnly); // Guard before the asynchronous transcript load finishes.
            Assert.False(vm.SendCommand.CanExecute(null));
            await read;
            await Until(() => vm.IsReadOnly && vm.History.LoadedMessages == 100, "read-only recent page");
            var reader = vm.Session;
            Assert.Null(reader.Connection);
            Assert.Equal(SessionPhase.Stopped, vm.Phase);
            Assert.True(window.FindControl<Border>("ReadOnlySessionBanner")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<TextBox>("Composer")!.IsEffectivelyEnabled);
            Assert.Contains(saved.Cwd, vm.ReadOnlyExplanation);
            Assert.Null(vm.ProjectFolder);
            Assert.Equal("", vm.ComposerText);
            Assert.False(vm.CanRecover);
            Assert.False(vm.StartRenameCommand.CanExecute(null));
            Assert.False(vm.MoveSessionCommand.CanExecute(null));
            Assert.False(vm.CanQueue);
            Assert.True(vm.CanLoadOlderMessages);
            Assert.True(vm.CanReloadHistory);
            Assert.Contains(vm.Rows.OfType<UserRowViewModel>(), row => row.Text == "Saved message 239");
            await vm.SendCommand.ExecuteAsync(null); // Direct invocation must fail closed too.
            await vm.SteerCommand.ExecuteAsync(null);
            await vm.LoadOlderMessagesCommand.ExecuteAsync(null);
            Assert.Equal(200, vm.History.LoadedMessages);
            await vm.ReloadHistoryCommand.ExecuteAsync(null);
            Assert.Equal(100, vm.History.LoadedMessages);
            await vm.OpenSessionCommand.ExecuteAsync(saved);
            Assert.Same(reader, vm.Session);
            Assert.Equal(2, vm.OpenChats.Count);
            Assert.Equal(original, await File.ReadAllBytesAsync(saved.Model.Path));
            await vm.OpenSessionCommand.ExecuteAsync(new SessionItemViewModel(
                new SessionSummary(live.SessionFile!, "live", live.CurrentLaunch.WorkingDirectory!, "Live", DateTimeOffset.UtcNow)));
            Assert.Same(live, vm.Session);
            Assert.Equal("Do not consume this draft", vm.ComposerText);
            await vm.OpenSessionCommand.ExecuteAsync(saved);
            Assert.Same(reader, vm.Session);
            Assert.True(vm.IsReadOnly);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Missing_folder_without_current_project_asks_for_folder_and_cancel_keeps_reader()
    {
        var (window, vm, _) = await OpenAsync();
        try
        {
            var saved = await MissingFolderSessionAsync();
            await vm.OpenSessionCommand.ExecuteAsync(saved);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.SessionCard!.Secondary!.Command).ExecuteAsync(null);
            var reader = vm.Session;
            var other = await MissingFolderSessionAsync();
            await vm.OpenSessionCommand.ExecuteAsync(other);
            Assert.Equal(Loc.T("Choose project folder…"), vm.SessionCard!.Primary!.Label);
            vm.SessionCard.DismissCommand!.Execute(null);
            Assert.Same(reader, vm.Session);
            Assert.True(vm.IsReadOnly);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }
    [AvaloniaFact]
    public async Task Paged_history_keeps_the_original_title_and_explicit_rename_wins()
    {
        var root = TestProcesses.TempDir("paged-session-title");
        var file = Path.Combine(root, "history.jsonl");
        var lines = new List<string>
        {
            System.Text.Json.JsonSerializer.Serialize(new { type = "session", id = Guid.NewGuid().ToString(), cwd = root }),
        };
        for (var i = 0; i < 360; i++)
            lines.Add(System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "message", message = new
                {
                    role = i % 2 == 0 ? "user" : "assistant",
                    content = new[] { new { type = "text", text = i == 0 ? "Original topic" : $"Later message {i}" } },
                },
            }));
        await File.WriteAllLinesAsync(file, lines);
        var session = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(root, file));
        await using var vm = new MainViewModel(session, new AppArgs());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.OnWindowOpened();
        try
        {
            await Until(() => vm.Phase == SessionPhase.Ready && vm.History.LoadedMessages == 100, "recent page");
            Assert.DoesNotContain(session.Snapshot().Items.OfType<UserItem>(), item => item.Text == "Original topic");
            Assert.Equal("Original topic", vm.SessionTitle);
            await vm.LoadOlderMessagesCommand.ExecuteAsync(null);
            Assert.Equal(200, vm.History.LoadedMessages);
            Assert.Equal("Original topic", vm.SessionTitle);
            await session.RenameSessionAsync("Explicit name");
            await Until(() => vm.SessionTitle == "Explicit name", "explicit rename");
            await vm.LoadOlderMessagesCommand.ExecuteAsync(null);
            Assert.Equal("Explicit name", vm.SessionTitle);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Sidebar_lists_sessions_new_chat_and_switching_back()
    {
        var (w, vm, _) = await OpenAsync();
        Assert.True(w.FindControl<ListBox>("SessionList")!.IsEffectivelyVisible);
        await SendAsync(w, vm, "first topic");
        await Until(() => vm.Sessions.Count == 1 && vm.Sessions[0].Title == "first topic", "listed");
        Assert.True(vm.Sessions[0].IsCurrent);
        Assert.Equal("first topic", vm.SessionTitle);

        // ⌘N: a new chat outside the project
        var mod = w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
        w.KeyPress(Key.N, (RawInputModifiers)mod, PhysicalKey.N, null);
        await Until(() => vm.Rows.Count == 0 && vm.SessionTitle == Loc.T("New chat") && vm.IsProjectless && vm.Phase == SessionPhase.Ready, "new chat");
        await SendAsync(w, vm, "second topic");
        await Until(() => vm.Sessions.Count == 2 && vm.Sessions[0].Title == "second topic", "both listed");
        Shot(w, "ui-sessions-sidebar");

        vm.OpenSessionCommand.Execute(vm.Sessions.Single(x => x.Title == "first topic"));
        await Until(() => vm.SessionTitle == "first topic" && vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text).SequenceEqual(["first topic"]), "switched back");
        Assert.True(vm.Sessions.Single(x => x.Title == "first topic").IsCurrent);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Rename_in_the_header_with_keyboard()
    {
        var (w, vm, _) = await OpenAsync();
        await SendAsync(w, vm, "something");
        vm.StartRenameCommand.Execute(null);
        await Until(() => w.FindControl<TextBox>("RenameBox")!.IsFocused, "rename box focused");
        w.KeyPress(Key.A, (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers, PhysicalKey.A, null);
        w.KeyTextInput("Parser work");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.SessionTitle == "Parser work" && vm.Sessions.Any(x => x.Title == "Parser work"), "renamed");
        Assert.False(vm.IsRenaming);
        Assert.True(w.FindControl<TextBox>("Composer")!.IsFocused);

        vm.StartRenameCommand.Execute(null);
        await Until(() => w.FindControl<TextBox>("RenameBox")!.IsFocused, "rename box focused again");
        w.KeyTextInput("discarded");
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsRenaming);
        Assert.Equal("Parser work", vm.SessionTitle);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Sidebar_hides_in_narrow_windows_and_opens_on_demand()
    {
        var (w, vm, _) = await OpenAsync(640, 480);
        Assert.True(vm.IsNarrow);
        Assert.False(vm.ShowSidebar);
        var mod = (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
        w.KeyPress(Key.B, mod, PhysicalKey.B, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ShowSidebar);
        Shot(w, "ui-sessions-narrow-open");
        w.Width = 1200;
        Dispatcher.UIThread.RunJobs();
        await Until(() => !vm.IsNarrow, "wide");
        Assert.True(vm.ShowSidebar);
        w.KeyPress(Key.B, mod, PhysicalKey.B, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ShowSidebar);
        await vm.DisposeAsync();
        w.Close();
    }
}
