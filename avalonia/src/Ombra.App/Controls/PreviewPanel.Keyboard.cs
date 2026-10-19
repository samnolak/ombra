using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Ombra.App.Controls;

public partial class PreviewPanel
{
    public static readonly StyledProperty<bool> ShowTabStripProperty =
        AvaloniaProperty.Register<PreviewPanel, bool>(nameof(ShowTabStrip));
    public bool ShowTabStrip { get => GetValue(ShowTabStripProperty); set => SetValue(ShowTabStripProperty, value); }

    internal void HandleBrowserShortcut(KeyEventArgs e) => OnBrowserKeyDown(this, e);
    private void WireBrowserKeys() => AddHandler(KeyDownEvent, OnBrowserKeyDown, RoutingStrategies.Tunnel);

    private void OnBrowserKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm is not { } vm) return;
        if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.Control)
        {
            CycleTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }
        var command = Avalonia.VisualTree.VisualExtensions.GetPlatformSettings(this)?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (e.KeyModifiers == command)
        {
            switch (e.Key)
            {
                case Key.L: FocusAddress(); break;
                case Key.R: vm.ReloadCommand.Execute(null); break;
                case Key.T: vm.NewTabCommand.Execute(null); break;
                case Key.OemOpenBrackets: vm.BackCommand.Execute(null); break;
                case Key.OemCloseBrackets: vm.ForwardCommand.Execute(null); break;
                default: return;
            }
        }
        else if (e.KeyModifiers == (command | KeyModifiers.Shift) && e.Key == Key.C)
        {
            if (vm.CurrentUrl is not { } uri) return;
            _ = ContextMenus.CopyAsync(this, uri.AbsoluteUri);
        }
        else return;
        e.Handled = true;
    }

    private void CycleTab(int direction)
    {
        if (_vm is not { } vm || vm.Tabs.Count < 2) return;
        var index = vm.Tabs.IndexOf(vm.ActiveTab);
        vm.SelectTabCommand.Execute(vm.Tabs[(index + direction + vm.Tabs.Count) % vm.Tabs.Count]);
    }
}
