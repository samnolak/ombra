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

    [Fact]
    public void Saved_frame_past_a_display_edge_stays_on_the_display_it_overlaps_most()
    {
        // The main display at the origin and a portrait one to its left (negative X), as AppKit reports them in points
        var main = new MainWindow.PlacementScreen(new PixelRect(0, 30, 2560, 1320), 1, true);
        var left = new MainWindow.PlacementScreen(new PixelRect(-1440, -994, 1440, 2560), 1, false);
        MainWindow.PlacementScreen[] screens = [main, left];
        // The top-left corner just past the left display's left edge: the window stays there, it does not jump to the main one
        var past = MainWindow.PlaceSavedWindow(new WindowLayoutOptions { X = -1460, Y = 80, Width = 1320, Height = 860 }, screens);
        Assert.Equal((-1440, 80, 1320.0, 860.0), (past.X, past.Y, past.Width, past.Height));
        // Mostly on the main display: placed on it
        var across = MainWindow.PlaceSavedWindow(new WindowLayoutOptions { X = -200, Y = 100, Width = 1320, Height = 860 }, screens);
        Assert.Equal((0, 100), (across.X, across.Y));
        // Its display is gone: the primary one takes it, at its size
        var gone = MainWindow.PlaceSavedWindow(new WindowLayoutOptions { X = 5000, Y = 80, Width = 1320, Height = 860 }, screens);
        Assert.Equal((2560 - 1320, 80, 1320.0), (gone.X, gone.Y, gone.Width));
        // No displays reported: kept as saved
        var kept = new WindowLayoutOptions { X = -1400, Y = 80, Width = 1320, Height = 860 };
        Assert.Equal(kept, MainWindow.PlaceSavedWindow(kept, []));
    }

    [AvaloniaFact]
    public async Task Saved_frame_is_the_frame_the_window_is_shown_and_maximized_from()
    {
        var dir = TestProcesses.TempDir("micro-window-first-frame");
        var settings = new ClientSettingsStore(Path.Combine(dir, "settings.json"));
        settings.Update(o => o with { WindowLayout = new WindowLayoutOptions { X = 120, Y = 90, Width = 900, Height = 650, Maximized = true } });
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), settings: settings);
        var log = new Ombra.App.Services.ClientLog(Path.Combine(dir, "client.log"));
        var window = new MainWindow { DataContext = vm, WindowLog = log };
        (Size Size, PixelPoint Position, WindowState State, WindowStartupLocation Startup)? atOpen = null;
        window.Opened += (_, _) => atOpen = (window.ClientSize, window.Position, window.WindowState, window.WindowStartupLocation);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            // Applied before the platform showed it: no XAML 1100×760 in the middle first, and maximized from the saved
            // frame (AppKit's Zoom goes back to the frame it zoomed from)
            Assert.Equal((new Size(900, 650), new PixelPoint(120, 90), WindowState.Maximized, WindowStartupLocation.Manual), atOpen);
            window.WindowState = WindowState.Normal;
            window.SaveWindowLayout();
            Assert.Equal((120, 90, 900.0, 650.0, false), settings.Load().WindowLayout is { } l ? (l.X, l.Y, l.Width, l.Height, l.Maximized) : default);
            var text = log.ReadTail(int.MaxValue)!;
            Assert.Contains("[window] restore saved=120,90 900×650 maximized placed=120,90 900×650 maximized screens=[0,0 1920×1280 x1 primary]", text);
            Assert.Contains("[window] shown at 120,90 900×650", text);
            Assert.Contains("[window] state Maximized -> Normal", text);
            Assert.Contains("[window] saved 120,90 900×650 state=Normal", text);
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Normal_frame_is_not_one_passed_through_on_the_way_to_maximized()
    {
        var settings = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("micro-window-transit"), "settings.json"));
        settings.Update(o => o with { WindowLayout = new WindowLayoutOptions { X = 120, Y = 90, Width = 900, Height = 650 } });
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), settings: settings);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            // AppKit's zoom: the window takes the display's frame while it still reports the normal state, then says maximized
            window.Width = 1900;
            window.Height = 1200;
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Maximized;
            await Task.Delay(MainWindow.PlacementSettle * 1.5);
            Dispatcher.UIThread.RunJobs();
            window.SaveWindowLayout();
            Assert.Equal((900.0, 650.0, true), settings.Load().WindowLayout is { } zoomed ? (zoomed.Width, zoomed.Height, zoomed.Maximized) : default);
            // A frame that holds in the normal state is the normal frame, also when the window quits right after it
            window.WindowState = WindowState.Normal;
            window.Width = 1000;
            window.Height = 700;
            Dispatcher.UIThread.RunJobs();
            window.SaveWindowLayout();
            Assert.Equal((1000.0, 700.0, false), settings.Load().WindowLayout is { } normal ? (normal.Width, normal.Height, normal.Maximized) : default);
            // Full screen keeps the state under it (maximized), so quitting in full screen restores that
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.FullScreen;
            window.SaveWindowLayout();
            Assert.True(settings.Load().WindowLayout!.Maximized);
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
