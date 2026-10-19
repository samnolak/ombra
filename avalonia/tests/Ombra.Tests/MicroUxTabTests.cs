using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class MicroUxTabTests
{
    private static async Task Settle()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
    }

    internal static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("OMBRA_REVIEW_DIR") is not { } dir) return;
        Directory.CreateDirectory(dir);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var file = File.Create(Path.Combine(dir, name + ".png"));
        frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static Button MenuButton(Flyout menu, string text) => ((Control)menu.Content!).GetVisualDescendants()
        .OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == Loc.T(text));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task Browser_keys_and_tab_menus_do_not_navigate_chat_history()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1280);
        try
        {
            vm.OpenBrowserPageCommand.Execute(null);
            var browser = vm.Browser;
            browser.NavigateCommand.Execute("https://example.invalid/first");
            browser.NewTabCommand.Execute(null);
            browser.NavigateCommand.Execute("https://example.invalid/second");
            browser.NewTabCommand.Execute(null);
            browser.NavigateCommand.Execute("https://example.invalid/third");
            await Settle();
            Assert.Equal(3, browser.Tabs.Count);
            var panel = w.FindControl<PreviewPanel>("BrowserPanel")!;
            Assert.True(panel.ShowTabStrip);
            var command = (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
            w.KeyPress(Key.L, command, PhysicalKey.L, "l");
            await Settle();
            Assert.True(panel.FindControl<TextBox>("AddressBox")!.IsFocused);
            var third = browser.ActiveTab;
            w.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            Assert.True(vm.ShowBrowserPage);
            Assert.Equal(0, browser.Tabs.IndexOf(browser.ActiveTab));
            w.KeyPress(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab, null);
            Assert.Same(third, browser.ActiveTab);
            var second = browser.Tabs[1];
            var tab = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("panel-tab") && b.DataContext == second);
            var menu = TabMenus.Build(tab)!;
            menu.ShowAt(tab);
            await Settle();
            Shot(w, "browser-tab-menu");
            Click(MenuButton(menu, "Close tabs to the right"));
            Assert.Equal(2, browser.Tabs.Count);
            Assert.DoesNotContain(third, browser.Tabs);
            browser.SelectTabCommand.Execute(second);
            panel.FindControl<TextBox>("AddressBox")!.Focus();
            w.KeyPress(Key.W, command, PhysicalKey.W, "w");
            Assert.Single(browser.Tabs);
            Assert.True(w.IsVisible);
            w.KeyPress(Key.W, command, PhysicalKey.W, "w");
            Assert.False(vm.ShowBrowserPage);
            Assert.True(w.IsVisible);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Terminal_right_click_is_a_menu_not_paste_and_copy_reads_real_pty()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1280);
        try
        {
            vm.ToggleTerminalCommand.Execute(null);
            var tab = Assert.Single(vm.Terminals);
            var terminal = w.TerminalOf(tab)!;
            await FileTestRepo.Until(() => terminal.IsLive, "real terminal");
            await terminal.SendInputAsync("echo micro-ux-terminal-copy\r", CancellationToken.None);
            await FileTestRepo.Until(() => terminal.FindInBuffer("micro-ux-terminal-copy", default) >= 2, "echo output");
            var clipboard = w.Clipboard!;
            await clipboard.SetTextAsync("echo unsafe-right-click\r");
            await Settle();
            var point = terminal.TranslatePoint(new Point(25, 25), w)!.Value;
            w.MouseDown(point, MouseButton.Right);
            w.MouseUp(point, MouseButton.Right);
            await Settle();
            var menu = Assert.IsType<Flyout>(terminal.ContextFlyout);
            Assert.True(menu.IsOpen);
            Assert.Equal(0, terminal.FindInBuffer("unsafe-right-click", default));
            Shot(w, "terminal-context-menu");
            Click(MenuButton(menu, "Copy all"));
            await Settle();
            Assert.Contains("micro-ux-terminal-copy", await clipboard.TryGetTextAsync());
            terminal.Terminal!.Selection.StartSelection(0, 0, XTerm.Selection.SelectionMode.Normal);
            terminal.Terminal.Selection.UpdateSelection(20, 2);
            terminal.Terminal.Selection.EndSelection();
            var selected = terminal.Terminal.Selection.GetSelectionText();
            Assert.NotEmpty(selected);
            menu.ShowAt(terminal);
            await Settle();
            Click(MenuButton(menu, "Copy selection"));
            await Settle();
            Assert.Equal(selected, await clipboard.TryGetTextAsync());
            terminal.Terminal.Selection.ClearSelection();
            await clipboard.SetTextAsync("echo micro-ux-pasted\r");
            menu.ShowAt(terminal);
            await Settle();
            Click(MenuButton(menu, "Paste"));
            await FileTestRepo.Until(() => terminal.FindInBuffer("micro-ux-pasted", default) >= 2, "menu paste reaches PTY");
            menu.ShowAt(terminal);
            await Settle();
            Click(MenuButton(menu, "Clear"));
            await Settle();
            Assert.Equal(0, terminal.FindInBuffer("micro-ux-pasted", default));
            vm.NewShellCommand.Execute(null);
            await Settle();
            var firstTab = w.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tab") && b.DataContext == tab);
            var tabMenu = TabMenus.Build(firstTab)!;
            tabMenu.ShowAt(firstTab);
            await Settle();
            Click(MenuButton(tabMenu, "Close other tabs"));
            Assert.Same(tab, Assert.Single(vm.Terminals));
            var closePoint = firstTab.TranslatePoint(new Point(8, 8), w)!.Value;
            w.MouseDown(closePoint, MouseButton.Middle);
            w.MouseUp(closePoint, MouseButton.Middle);
            Assert.Empty(vm.Terminals);
        }
        finally
        {
            foreach (var terminal in vm.Terminals.ToArray()) vm.CloseTerminalCommand.Execute(terminal);
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Explicit_unread_survives_settings_and_read_actions_update_the_filter()
    {
        var settings = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("micro-read"), "settings.json"));
        var session = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("micro-read-sessions")),
            new LaunchRequest(TestProcesses.TempDir("micro-read-project")));
        await using var vm = new MainViewModel(session, new AppArgs(), settings: settings);
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        try
        {
            await FileTestRepo.Until(() => vm.Phase == SessionPhase.Ready, "ready");
            await PanesUiTests.SendAsync(vm, "unread test", () => vm.Rows.OfType<AssistantRowViewModel>().Any());
            await FileTestRepo.Until(() => vm.Sessions.Count > 0, "saved chat row");
            var row = vm.Sessions.Single(s => s.IsCurrent);
            vm.ToggleReadItemCommand.Execute(row);
            Assert.True(row.IsMarkedUnread);
            Assert.Equal(SessionStatus.Unread, row.Status);
            Assert.Contains(row.Model.Path, settings.Load().MarkedUnreadSessions!);
            vm.SetStatusFilterCommand.Execute(SessionStatusFilter.Unread);
            Assert.Contains(row, vm.Sessions);
            Shot(w, "session-marked-unread");
            w.KeyPress(Key.Escape, RawInputModifiers.Shift, PhysicalKey.Escape, null);
            Assert.Empty(vm.Sessions);
            Assert.Null(settings.Load().MarkedUnreadSessions);
            vm.SetStatusFilterCommand.Execute(SessionStatusFilter.All);
            row = vm.Sessions.Single(s => s.IsCurrent);
            w.FindControl<TextBox>("Composer")!.Focus();
            var command = (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
            w.KeyPress(Key.U, command | RawInputModifiers.Shift, PhysicalKey.U, "u");
            Assert.True(row.IsMarkedUnread);
            await vm.OpenSessionCommand.ExecuteAsync(row);
            Assert.False(row.IsMarkedUnread);
            Assert.Null(settings.Load().MarkedUnreadSessions);
        }
        finally { w.Close(); }
    }
    [AvaloniaFact]
    public async Task Tool_card_menu_copies_whole_output_and_links_open_in_preview()
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var tool = new ToolRowViewModel(new ToolItem(1, "copy-tool", "bash", "{\"command\":\"echo menu-command\"}",
            ToolStatus.Succeeded, "whole output\nsecond line", "$ echo menu-command"));
        var card = new Border { DataContext = tool, Height = 90, Background = Avalonia.Media.Brushes.Gray,
            Child = new TextBlock { Text = tool.Command } };
        ConversationMenus.SetKind(card, ConversationMenuKind.Tool);
        var link = new Button { Content = "Local preview", ContextFlyout = ContextMenus.ForLink("http://localhost:4321/example") };
        var w = new Window { DataContext = vm, Content = new StackPanel { Children = { card, link } }, Width = 600, Height = 300 };
        PathLinks.SetHandler(w, vm);
        w.Show();
        try
        {
            await Settle();
            var menu = ConversationMenus.Create(card, vm)!;
            menu.ShowAt(card);
            await Settle();
            Shot(w, "tool-card-menu");
            Click(MenuButton(menu, "Copy output"));
            await Settle();
            Assert.Equal(tool.ShownOutput, await w.Clipboard!.TryGetTextAsync());
            menu.ShowAt(card);
            await Settle();
            Click(MenuButton(menu, "Copy command"));
            await Settle();
            Assert.Equal(tool.Command, await w.Clipboard!.TryGetTextAsync());
            var links = Assert.IsType<Flyout>(link.ContextFlyout);
            links.ShowAt(link);
            await Settle();
            Shot(w, "link-preview-menu");
            Click(MenuButton(links, "Open in preview"));
            Assert.Equal(SidePane.Browser, vm.ActivePane);
            Assert.Equal("http://localhost:4321/example", vm.Preview.CurrentUrl!.AbsoluteUri);
        }
        finally { w.Close(); }
    }
}
