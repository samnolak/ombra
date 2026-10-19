using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using static Ombra.Tests.SessionAreaTests;

namespace Ombra.Tests;

public sealed class KeyboardFocusUiTests
{
    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static RawInputModifiers Command(Window w) =>
        w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers.HasFlag(KeyModifiers.Meta)
            ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static void Press(Window w, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.KeyPress(key, modifiers, PhysicalKey.None, null);
        w.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    [AvaloniaFact]
    public async Task Composer_shortcuts_leave_browser_address_keys_alone_and_escape_returns_from_the_browser_page()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1400);
        try
        {
            var composer = Named<TextBox>(w, "Composer");
            Named<Button>(w, "ModelButton").Focus();
            Press(w, Key.L, Command(w));
            Assert.True(composer.IsFocused);
            vm.OpenBrowserPageCommand.Execute(null);
            await Settle(100);
            var address = Named<TextBox>(Named<Control>(w, "BrowserPanel"), "AddressBox");
            address.Focus();
            Press(w, Key.L, Command(w));
            Assert.True(address.IsFocused);
            Assert.True(vm.IsBrowserPageOpen);
            Named<Button>(w, "RailBrowserButton").Focus();
            Press(w, Key.L, Command(w));
            Assert.True(address.IsFocused);
            Press(w, Key.Escape, Command(w));
            await Settle(100);
            Assert.False(vm.IsBrowserPageOpen);
            Assert.True(composer.IsFocused);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Viewer_card_and_flyout_return_to_the_opener_without_stealing_a_new_focus()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1180);
        try
        {
            var opener = Named<Button>(w, "ModelButton");
            opener.Focus();
            vm.IsImageViewerOpen = true;
            await Settle(100);
            Assert.True(Named<Control>(w, "ImageViewer").IsKeyboardFocusWithin);
            vm.CloseImageViewerCommand.Execute(null);
            await Settle(100);
            Assert.True(opener.IsFocused);

            vm.ShowCard(new SessionCardViewModel("test", "IconInfo", "Card", "Message"));
            await Settle(100);
            var cardButton = Named<Control>(w, "SessionCardHost").GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled);
            cardButton.Focus();
            vm.CloseSessionCard();
            await Settle(100);
            Assert.True(opener.IsFocused);

            var thinking = Named<Button>(w, "ThinkingBox");
            var flyout = Assert.IsType<Flyout>(thinking.Flyout);
            flyout.ShowAt(thinking);
            await Settle(100);
            var choice = Assert.IsAssignableFrom<Control>(flyout.Content).GetVisualDescendants().OfType<Button>().First();
            choice.Focus();
            flyout.Hide();
            await Settle(100);
            Assert.True(opener.IsFocused);

            vm.IsImageViewerOpen = true;
            await Settle(100);
            var composer = Named<TextBox>(w, "Composer");
            vm.CloseImageViewerCommand.Execute(null);
            composer.Focus();
            await Settle(100);
            Assert.True(composer.IsFocused);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Model_thinking_pane_and_sidebar_grip_shortcuts_have_local_effects_and_sheet_entries()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1400);
        try
        {
            var composer = Named<TextBox>(w, "Composer");
            composer.Focus();
            Press(w, Key.I, Command(w) | RawInputModifiers.Shift);
            await Until(() => vm.IsModelMenuOpen && Named<TextBox>(w, "ModelFilterBox").IsFocused, "model shortcut");
            Named<TextBox>(w, "ModelFilterBox").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Settle(100);
            Assert.True(composer.IsFocused);
            Press(w, Key.E, Command(w) | RawInputModifiers.Shift);
            var thinking = Named<Button>(w, "ThinkingBox").Flyout!;
            Assert.True(thinking.IsOpen);
            MicroUxTabTests.Shot(w, "keyboard-thinking-menu");
            thinking.Hide();
            vm.ToggleDebugCommand.Execute(null);
            await Settle(100);
            Named<Control>(w, "DebugPanel").GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible).Focus();
            Press(w, Key.OemPipe, Command(w));
            await Settle(100);
            Assert.False(vm.IsDebugVisible);
            Assert.True(composer.IsFocused);
            vm.ShowPaneCommand.Execute(SidePane.Files);
            await Settle(100);
            Named<Control>(w, "RightPanel").GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled).Focus();
            Press(w, Key.OemPipe, Command(w));
            await Settle(100);
            Assert.Equal(SidePane.None, vm.ActivePane);

            var grip = Named<Control>(w, "SidebarGrip");
            Assert.Equal(Loc.T("Resize sidebar"), AutomationProperties.GetName(grip));
            Assert.True(grip.Focus());
            vm.SidebarWidth = 300;
            Press(w, Key.Right);
            Assert.Equal(320, vm.SidebarWidth);
            Press(w, Key.Left);
            Assert.Equal(300, vm.SidebarWidth);
            Press(w, Key.Home);
            Assert.Equal(MainViewModel.SidebarDefaultWidth, vm.SidebarWidth);
            Press(w, Key.End);
            Assert.Equal(MainViewModel.SidebarMaxWidth, vm.SidebarWidth);
            vm.SidebarWidth = MainViewModel.SidebarMinWidth;
            Press(w, Key.Left);
            Assert.Equal(MainViewModel.SidebarMinWidth, vm.SidebarWidth);
            Press(w, Key.OemPipe, Command(w));
            await Settle(100);
            Assert.False(vm.ShowSidebar);
            Assert.True(composer.IsFocused);
            foreach (var mac in new[] { false, true })
            {
                var sheet = KeyboardShortcuts.For(mac).SelectMany(g => g.Items).ToList();
                foreach (var label in new[] { "Focus the message box", "Choose model", "Thinking effort", "Close the focused pane", "Resize sidebar" })
                    Assert.Contains(sheet, row => row.Action == Loc.T(label));
            }
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Session_arrows_only_move_focus_and_enter_rename_delete_double_click_use_the_focused_row_commands()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        var actions = new List<string>();
        SessionItemViewModel Row(string id) => new(new SessionSummary("/" + id, id, "/project", id, DateTimeOffset.Now))
        {
            OpenCommand = new RelayCommand(() => actions.Add("open:" + id)),
            RenameCommand = new RelayCommand(() => actions.Add("rename:" + id)),
            DeleteCommand = new RelayCommand(() => actions.Add("delete:" + id)),
        };
        var group = new SessionGroupViewModel("/project", true);
        group.Items.Add(Row("first"));
        group.Items.Add(Row("second"));
        vm.SessionGroups.Add(group);
        try
        {
            w.Show();
            await Settle(150);
            var list = Named<ListBox>(w, "SessionList");
            var first = list.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("session") && b.DataContext == group.Items[0]);
            first.Focus();
            Press(w, Key.Down);
            await Settle(100);
            Assert.Empty(actions);
            Assert.Same(group.Items[1], (w.FocusManager!.GetFocusedElement() as Control)?.DataContext);
            Press(w, Key.Enter);
            Press(w, Key.F2);
            Press(w, Key.Delete);
            Assert.Equal(["open:second", "rename:second", "delete:second"], actions);
            Press(w, Key.Up);
            await Settle(100);
            var at = first.TranslatePoint(new Point(first.Bounds.Width / 2, first.Bounds.Height / 2), w)!.Value;
            w.MouseDown(at, MouseButton.Left);
            w.MouseUp(at, MouseButton.Left);
            w.MouseDown(at, MouseButton.Left);
            w.MouseUp(at, MouseButton.Left);
            Assert.Equal("rename:first", actions.Last());
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Help_menu_routes_to_existing_shortcuts_diagnostics_and_release_notes()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1180);
        try
        {
            var help = w.MenuBar(vm).Items.OfType<NativeMenuItem>().Single(i => i.Header == Loc.T("Help")).Menu!;
            NativeMenuItem Item(string name) => help.Items.OfType<NativeMenuItem>().Single(i => i.Header == Loc.T(name));
            Item("Keyboard shortcuts").Command!.Execute(null);
            Assert.True(vm.IsShortcutsOpen);
            vm.CloseShortcutsCommand.Execute(null);
            await ((IAsyncRelayCommand)Item("Report a problem").Command!).ExecuteAsync(null);
            Assert.True(vm.IsSettingsOpen);
            Assert.Equal("diagnostics", vm.SettingsCategory);
            await ((IAsyncRelayCommand)Item("What's new").Command!).ExecuteAsync(null);
            Assert.Equal("updates", vm.SettingsCategory);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }
}
