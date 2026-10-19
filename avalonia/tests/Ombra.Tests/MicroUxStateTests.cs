using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class MicroUxStateTests
{
    [Fact]
    public void Window_placement_clamps_disconnected_and_scaled_screens_and_invalid_sizes()
    {
        var area = new PixelRect(-1920, 40, 1920, 1040);
        var saved = new WindowLayoutOptions { X = 9000, Y = -9000, Width = 4000, Height = 3000, Maximized = true };
        var fit = MainWindow.ClampWindowPlacement(saved, area, 2);
        Assert.Equal(-1920, fit.X);
        Assert.Equal(40, fit.Y);
        Assert.Equal(960, fit.Width);
        Assert.Equal(488, fit.Height);
        Assert.True(fit.Maximized);
        var invalid = MainWindow.ClampWindowPlacement(saved with { Width = double.NaN, Height = -10 }, new PixelRect(0, 0, 800, 600), 1);
        Assert.Equal(800, invalid.Width);
        Assert.Equal(480, invalid.Height);
        Assert.InRange(invalid.Y, 0, 88);
        var tiny = MainWindow.ClampWindowPlacement(saved, new PixelRect(0, 0, 400, 300), 1);
        Assert.Equal(400, tiny.Width);
        Assert.Equal(268, tiny.Height);
    }

    [AvaloniaFact]
    public async Task Window_and_panel_layout_round_trip_without_starting_a_terminal()
    {
        var settings = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("micro-layout"), "settings.json"));
        var saved = new WindowLayoutOptions
        {
            X = 100000, Y = -100000, Width = 900, Height = 650, SidebarVisible = false,
            RightPanelWidth = 520, EventsWidth = 460, TerminalHeight = 280,
            ActivePane = nameof(SidePane.Plan), EventsVisible = true, TerminalVisible = true,
        };
        settings.Update(o => o with { WindowLayout = saved, SidebarWidth = 310 });
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), settings: settings);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsSidebarVisible);
            Assert.True(vm.IsDebugVisible);
            Assert.True(vm.IsTerminalOpen);
            Assert.Equal(SidePane.Plan, vm.ActivePane);
            Assert.Equal(310, vm.SidebarWidth);
            Assert.Empty(vm.Terminals);
            if (window.Screens.Primary is { } screen) Assert.True(screen.WorkingArea.Contains(window.Position));
            MicroUxTabTests.Shot(window, "restored-window-panels");
            window.SaveWindowLayout();
            var normal = settings.Load().WindowLayout!;
            Assert.Equal(520, normal.RightPanelWidth);
            Assert.Equal(460, normal.EventsWidth);
            Assert.Equal(280, normal.TerminalHeight);
            Assert.False(normal.SidebarVisible);
            Assert.True(normal.EventsVisible);
            Assert.True(normal.TerminalVisible);
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            window.SaveWindowLayout();
            var minimized = settings.Load().WindowLayout!;
            Assert.True(minimized.Maximized);
            Assert.Equal(normal.Width, minimized.Width);
            Assert.Equal(normal.Height, minimized.Height);
            settings.Update(o => o with { Theme = "dark" });
            Assert.Equal(minimized, settings.Load().WindowLayout);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Initial_catalog_load_has_feedback_but_refresh_keeps_existing_rows_without_flash()
    {
        var root = TestProcesses.TempDir("micro-loading");
        var controller = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(TestProcesses.TempDir("micro-loading-project")));
        var vm = new MainViewModel(controller, new AppArgs());
        var release = new TaskCompletionSource<SessionCatalogScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ScanCatalogAsync = (_, ct) => release.Task.WaitAsync(ct);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            vm.OnWindowOpened();
            await SessionAreaTests.Until(() => vm.IsLoadingSessions, "initial sessions loading");
            Assert.True(window.FindControl<TextBlock>("SessionsLoadingText")!.IsVisible);
            Assert.Equal(Loc.T("Loading sessions…"), window.FindControl<TextBlock>("SessionsLoadingText")!.Text);
            MicroUxTabTests.Shot(window, "initial-sessions-loading");
            vm.SessionFilter = "no match while loading";
            Assert.False(vm.ShowNoSessionMatch);
            vm.SessionFilter = "";
            release.SetResult(await SessionCatalog.ScanAsync(root));
            await vm.CatalogRefreshTask;
            Assert.False(vm.IsLoadingSessions);
            var refresh = new TaskCompletionSource<SessionCatalogScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.ScanCatalogAsync = (_, ct) => refresh.Task.WaitAsync(ct);
            var rows = vm.Sessions.ToArray();
            vm.RequestCatalogRefresh();
            Assert.False(vm.IsLoadingSessions);
            Assert.Equal(rows, vm.Sessions);
            refresh.SetResult(await SessionCatalog.ScanAsync(root));
            await vm.CatalogRefreshTask;
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Opening_paste_and_rename_feedback_are_localized_and_clear_after_completion()
    {
        var env = await SessionAreaTests.OpenAsync();
        try
        {
            var vm = env.Vm;
            var window = env.W;
            var pasting = false;
            var renaming = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.IsPasting)) pasting |= vm.IsPasting;
                if (e.PropertyName == nameof(vm.IsSavingRename)) renaming |= vm.IsSavingRename;
            };
            await window.Clipboard!.SetTextAsync("pasted text");
            await window.PasteAsync();
            Assert.True(pasting);
            Assert.False(vm.IsPasting);
            Assert.Equal("pasted text", vm.ComposerText);
            vm.StartRenameCommand.Execute(null);
            vm.RenameText = "Renamed conversation";
            await vm.CommitRenameCommand.ExecuteAsync(null);
            Assert.True(renaming);
            Assert.False(vm.IsSavingRename);
            Assert.Equal("Renamed conversation", vm.SessionTitle);
            Assert.Equal(Loc.T("Session renamed"), vm.SessionCard!.Title);
            Assert.Equal(Loc.T("Pasting…"), window.FindControl<TextBlock>("PastingText")!.Text);
            MicroUxTabTests.Shot(window, "rename-paste-feedback");
            Assert.Equal(Loc.T("Renaming…"), window.FindControl<TextBlock>("RenamingText")!.Text);
        }
        finally { await SessionAreaTests.Close(env); }

        var controller = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("micro-opening")),
            new LaunchRequest(TestProcesses.TempDir("micro-opening-project"), "/saved/session.jsonl"));
        await using var opening = new MainViewModel(controller, new AppArgs());
        opening.Apply(controller.Snapshot() with { Phase = SessionPhase.Starting });
        Assert.True(opening.IsOpeningSession);
        Assert.Equal(Loc.T("Opening…"), opening.StatusText);
        opening.Apply(controller.Snapshot() with { Phase = SessionPhase.Ready });
        Assert.False(opening.IsOpeningSession);
    }

    [AvaloniaFact]
    public async Task Sidebar_folder_drop_operation_adds_once_and_restores_a_hidden_project()
    {
        var settings = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("micro-drop"), "settings.json"));
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), settings: settings);
        var folder = TestProcesses.TempDir("micro-drop-project");
        vm.AddProjectFolder(folder);
        vm.AddProjectFolder(folder + Path.DirectorySeparatorChar);
        Assert.Single(settings.Load().SidebarProjects!);
        var group = Assert.Single(vm.SessionGroups, g => g.Cwd == folder);
        vm.RemoveProjectCommand.Execute(group);
        Assert.Contains(folder, settings.Load().HiddenProjects!);
        vm.AddProjectFolder(folder);
        Assert.DoesNotContain(folder, settings.Load().HiddenProjects ?? []);
        Assert.Single(vm.SessionGroups, g => g.Cwd == folder);
        vm.AddProjectFolder(Path.Combine(folder, "missing"));
        Assert.Single(settings.Load().SidebarProjects!);
    }

    [AvaloniaFact]
    public async Task Deleting_current_chat_selects_neighbor_and_unpin_undo_restores_exact_pin()
    {
        var env = await SessionAreaTests.OpenAsync();
        try
        {
            var vm = env.Vm;
            await SessionAreaTests.Send(env, "first neighbor");
            await SessionAreaTests.Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "first reply");
            var first = vm.Session.SessionFile;
            vm.RequestCatalogRefresh();
            await vm.CatalogRefreshTask;
            await vm.NewSessionInProjectCommand.ExecuteAsync(vm.SessionGroups.Single(g => g.Cwd == env.Project));
            await SessionAreaTests.Send(env, "second current");
            await SessionAreaTests.Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "second reply");
            vm.RequestCatalogRefresh();
            await vm.CatalogRefreshTask;
            var item = vm.Sessions.Single(s => s.Model.Path == first);
            await vm.TogglePinItemCommand.ExecuteAsync(item);
            await vm.TogglePinItemCommand.ExecuteAsync(item);
            Assert.False(item.IsPinned);
            Assert.Equal(Loc.T("Unpinned"), vm.SessionCard!.Title);
            await vm.SessionCard.Primary!.Run();
            Assert.Contains(item.Model.Id, SessionPins.Read(SessionPins.PathFor(first)));
            vm.DeleteSessionCommand.Execute(null);
            await vm.SessionCard!.Primary!.Run();
            Assert.Equal(first, vm.Session.SessionFile);
            Assert.Equal(Loc.T("Session deleted"), vm.SessionCard!.Title);
        }
        finally { await SessionAreaTests.Close(env); }
    }

    [AvaloniaFact]
    public async Task Removed_permission_rule_undo_preserves_scope_and_project()
    {
        var settings = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("micro-rule-undo"), "settings.json"));
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), settings: settings);
        var grant = vm.ApprovalRules.Add(ApprovalRule.Parse("bash(npm test:*)")!, ApprovalScope.Project, "/original/project", null);
        await vm.Permissions.LoadAsync();
        vm.Permissions.Rules.Single().RemoveCommand.Execute(null);
        Assert.Empty(vm.ApprovalRules.Grants);
        Assert.True(vm.Permissions.CanUndoRemove);
        vm.Permissions.UndoRemoveCommand.Execute(null);
        Assert.Equal(grant, Assert.Single(vm.ApprovalRules.Grants));
        Assert.False(vm.Permissions.CanUndoRemove);
        Assert.Equal(Loc.T("Permission rule restored."), vm.Permissions.Message);
        Assert.Equal("/original/project", Assert.Single(settings.Load().ApprovalRules!).Project);
    }

    [AvaloniaFact]
    public async Task Removed_queue_undo_recovers_input_without_resending_or_overwriting_the_draft()
    {
        var env = await SessionAreaTests.OpenAsync("queue-stream-3");
        try
        {
            var vm = env.Vm;
            vm.ComposerText = "long task";
            await vm.SendCommand.ExecuteAsync(null);
            await SessionAreaTests.Until(() => vm.Phase == SessionPhase.Running, "running");
            vm.ComposerText = "removed message";
            await vm.SendCommand.ExecuteAsync(null);
            await SessionAreaTests.Until(() => vm.QueuedMessages.Count == 1, "queued");
            await vm.QueuedMessages[0].RemoveCommand.ExecuteAsync(null);
            Assert.Empty(vm.QueuedMessages);
            Assert.Equal(Loc.T("Message removed from queue"), vm.SessionCard!.Title);
            var undo = vm.SessionCard.Primary!;
            vm.ComposerText = "keep this draft";
            await undo.Run();
            await undo.Run();
            Assert.Equal("keep this draft\n\nremoved message", vm.ComposerText);
            Assert.Empty(vm.QueuedMessages);
            Assert.DoesNotContain(vm.Rows.OfType<UserRowViewModel>(), row => row.Text == "removed message");
        }
        finally { await SessionAreaTests.Close(env); }
    }
}
