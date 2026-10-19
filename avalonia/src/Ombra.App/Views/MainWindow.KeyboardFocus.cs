using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

public sealed partial class MainWindow
{
    private Control? _modelFocusReturn;
    private bool _modelMenuEscaped;

    private void WireKeyboardFocus()
    {
        WatchFocusReturn(ImageViewer);
        WatchFocusReturn(ShortcutsSheet);
        WatchFocusReturn(SessionCardHost);
        foreach (var button in new[] { ThinkingBox, AttachButton, ProjectButton, ApprovalButton })
            if (button.Flyout is Flyout flyout) Controls.ChoiceMenu.RestoreFocusOnClose(flyout);
        WatchFocusReturn(DialogHost);
        ModelPopup.Opened += (_, _) => _modelFocusReturn = FocusManager?.GetFocusedElement() as Control;
        AddHandler(KeyDownEvent, OnFocusShortcut, RoutingStrategies.Tunnel);
        SessionList.AddHandler(KeyDownEvent, OnSessionListKey, RoutingStrategies.Tunnel);
        SessionList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount != 2 || !e.GetCurrentPoint(SessionList).Properties.IsLeftButtonPressed
                || SessionButton(e.Source as Visual) is not { DataContext: SessionItemViewModel row }) return;
            if (row.RenameCommand?.CanExecute(row) == true) row.RenameCommand.Execute(row);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        SidebarGrip.Focusable = true;
        AutomationProperties.SetName(SidebarGrip, Loc.T("Resize sidebar"));
        AutomationProperties.SetHelpText(SidebarGrip, Loc.T("Left and Right resize; Home resets; End maximizes the width"));
        SidebarGrip.KeyDown += (_, e) =>
        {
            if (Vm is not { } vm || Sidebar.Classes.Contains("drawer") || e.KeyModifiers != KeyModifiers.None) return;
            var width = e.Key switch
            {
                Key.Left => vm.SidebarWidth - 20,
                Key.Right => vm.SidebarWidth + 20,
                Key.Home => MainViewModel.SidebarDefaultWidth,
                Key.End => MainViewModel.SidebarMaxWidth,
                _ => double.NaN,
            };
            if (double.IsNaN(width)) return;
            vm.SidebarWidth = Math.Min(width, Math.Max(MainViewModel.SidebarMinWidth, Bounds.Width - ChatMinWidth));
            vm.SaveSidebarWidth();
            e.Handled = true;
        };
    }

    private void WatchFocusReturn(Control host)
    {
        Control? opener = null;
        host.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (host.IsVisible) opener = FocusManager?.GetFocusedElement() as Control;
            else
            {
                var target = opener;
                opener = null;
                Dispatcher.UIThread.Post(() => ReturnFocus(target, host), DispatcherPriority.Background);
            }
        };
    }

    private void ReturnFocus(Control? target, Visual? closing = null)
    {
        if (!IsActive || Vm is { IsImageViewerOpen: true } || IsModelMenuOpenNow()) return;
        var focused = FocusManager?.GetFocusedElement() as Visual;
        // A click outside a transient or a command that deliberately focused a new editor wins.
        if (focused is { IsEffectivelyVisible: true } && TopLevel.GetTopLevel(focused) == this
            && (closing is null || !WithinHost(closing, focused))) return;
        if (target is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true }
            && TopLevel.GetTopLevel(target) == this) target.Focus();
        else if (Composer.IsEffectivelyVisible && Composer.IsEffectivelyEnabled) Composer.Focus();
    }

    private static bool WithinHost(Visual host, Visual? focused) => focused is not null
        && (ReferenceEquals(host, focused) || host.IsVisualAncestorOf(focused));

    private void OnFocusShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Vm is not { } vm) return;
        var command = this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var browser = vm.ShowBrowserPage ? BrowserPanel
            : focused?.GetSelfAndVisualAncestors().OfType<Controls.PreviewPanel>().FirstOrDefault();
        browser?.HandleBrowserShortcut(e);
        if (e.Handled) return;
        if (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.Escape && !vm.ShowBrowserPage)
        {
            vm.MarkAllReadCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == command && e.Key == Key.W)
        {
            CloseActiveSurface();
            e.Handled = true;
        }
        else if (e.KeyModifiers == command && e.Key is Key.L or Key.Escape)
        {
            // The browser owns its address shortcut, including when web content has focus.
            if (e.Key == Key.L && vm.ShowBrowserPage)
            {
                BrowserPanel.FocusAddress();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.L && focused?.GetSelfAndVisualAncestors().OfType<Controls.PreviewPanel>().Any() == true) return;
            vm.CloseImageViewerCommand.Execute(null);
            vm.CloseShortcutsCommand.Execute(null);
            vm.ClosePaletteCommand.Execute(null);
            vm.ShowChatsCommand.Execute(null);
            if (Composer.IsEffectivelyVisible && Composer.IsEffectivelyEnabled) Composer.Focus();
            e.Handled = true;
        }
        else if (e.KeyModifiers == command && e.Key is Key.OemPipe or Key.OemBackslash)
        {
            if (WithinHost(RightPanel, focused)) vm.ClosePaneCommand.Execute(null);
            else if (WithinHost(TerminalPanel, focused)) vm.ToggleTerminalCommand.Execute(null);
            else if (WithinHost(DebugPanel, focused)) vm.ToggleDebugCommand.Execute(null);
            else if (WithinHost(Sidebar, focused)) vm.ToggleSidebarCommand.Execute(null);
            else return;
            Dispatcher.UIThread.Post(() => Composer.Focus(), DispatcherPriority.Background);
            e.Handled = true;
        }
        else if (e.KeyModifiers == (command | KeyModifiers.Shift)
            && vm is { IsSettingsOpen: false, IsImageViewerOpen: false, IsShortcutsOpen: false, IsPaletteOpen: false })
        {
            if (e.Key == Key.I && ModelButton.IsEffectivelyEnabled) vm.OpenModelMenuCommand.Execute(null);
            else if (e.Key == Key.E && ThinkingBox.IsEffectivelyVisible && ThinkingBox.IsEffectivelyEnabled)
                ThinkingBox.Flyout?.ShowAt(ThinkingBox);
            else if (e.Key == Key.U && !vm.ShowBrowserPage)
                vm.ToggleReadItemCommand.Execute(SessionButton(focused)?.DataContext as SessionItemViewModel ?? vm.Sessions.FirstOrDefault(s => s.IsCurrent));
            else return;
            e.Handled = true;
        }
    }

    private static Button? SessionButton(Visual? source) => source?.GetSelfAndVisualAncestors().OfType<Button>()
        .FirstOrDefault(b => b.Classes.Contains("session"));

    private void OnSessionListKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox) return;
        var focused = SessionButton(FocusManager?.GetFocusedElement() as Visual);
        if (e.Key is Key.Up or Key.Down)
        {
            var rows = Vm?.SessionGroups.Where(g => g.IsExpanded).SelectMany(g => g.Items).ToList();
            if (rows is not { Count: > 0 }) return;
            var index = focused?.DataContext is SessionItemViewModel current ? rows.IndexOf(current) : -1;
            var next = index < 0 ? (e.Key == Key.Down ? 0 : rows.Count - 1)
                : Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, rows.Count - 1);
            var target = rows[next];
            SessionList.ScrollIntoView(Vm!.SessionGroups.First(g => g.Items.Contains(target)));
            Dispatcher.UIThread.Post(() =>
            {
                var button = SessionList.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Classes.Contains("session") && ReferenceEquals(b.DataContext, target) && b.IsEffectivelyVisible);
                button?.Focus(NavigationMethod.Directional);
                button?.BringIntoView();
            }, DispatcherPriority.Background);
            e.Handled = true;
        }
        else if (focused?.DataContext is SessionItemViewModel row)
        {
            var action = e.Key switch { Key.Enter => row.OpenCommand, Key.F2 => row.RenameCommand, Key.Delete => row.DeleteCommand, _ => null };
            if (action is null) return;
            if (action.CanExecute(row)) action.Execute(row);
            e.Handled = true;
        }
    }
}
