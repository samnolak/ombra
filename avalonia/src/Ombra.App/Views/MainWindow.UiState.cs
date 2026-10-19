using Avalonia;
using Avalonia.Controls;
using Ombra.ClientCore;

namespace Ombra.App.Views;

public sealed partial class MainWindow
{
    private bool _layoutRestored;
    private WindowLayoutOptions? _normalPlacement;
    private bool _wasMaximized;

    private void WireWindowLayout()
    {
        Opened += (_, _) => RestoreWindowLayout();
        PositionChanged += (_, _) => RememberNormalPlacement();
        SizeChanged += (_, _) => RememberNormalPlacement();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty || !_layoutRestored) return;
            if (WindowState != WindowState.Minimized) _wasMaximized = WindowState == WindowState.Maximized;
        };
    }

    private void RestoreWindowLayout()
    {
        if (_layoutRestored || Vm is not { } vm) return;
        var saved = vm.LoadWindowLayout();
        if (saved is not null)
        {
            var screen = Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(new PixelPoint(saved.X, saved.Y))) ?? Screens.Primary;
            var placement = screen is null ? saved : ClampWindowPlacement(saved, screen.WorkingArea, screen.Scaling, MinWidth, MinHeight);
            Width = placement.Width;
            Height = placement.Height;
            Position = new PixelPoint(placement.X, placement.Y);
            _normalPlacement = placement;
            _rightPanelWidth = PanelSize(saved.RightPanelWidth, RightPanelDefaultWidth, RightPanelMinWidth);
            _debugWidth = PanelSize(saved.EventsWidth, DebugDefaultWidth, DebugMinWidth);
            _terminalHeight = PanelSize(saved.TerminalHeight, TerminalDefaultHeight, 100);
            vm.RestorePanelVisibility(saved);
            _wasMaximized = saved.Maximized;
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        _layoutRestored = true;
        RememberNormalPlacement();
        FitPanels();
    }

    private static double PanelSize(double value, double fallback, double minimum) =>
        double.IsFinite(value) && value >= minimum ? Math.Min(value, 10000) : fallback;

    internal static WindowLayoutOptions ClampWindowPlacement(WindowLayoutOptions saved, PixelRect area, double scale,
        double minimumWidth = 640, double minimumHeight = 480)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var maxWidth = Math.Max(1, area.Width / scale);
        // Leave room for the native frame/title bar, which is outside the client size on Windows/Linux.
        var maxHeight = Math.Max(1, area.Height / scale - 32);
        var width = Math.Clamp(double.IsFinite(saved.Width) ? saved.Width : 1200, Math.Min(minimumWidth, maxWidth), maxWidth);
        var height = Math.Clamp(double.IsFinite(saved.Height) ? saved.Height : 800, Math.Min(minimumHeight, maxHeight), maxHeight);
        return saved with
        {
            Width = width,
            Height = height,
            X = Math.Clamp(saved.X, area.X, Math.Max(area.X, area.Right - (int)Math.Ceiling(width * scale))),
            Y = Math.Clamp(saved.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)Math.Ceiling((height + 32) * scale))),
        };
    }

    private void RememberNormalPlacement()
    {
        if (!_layoutRestored || WindowState != WindowState.Normal || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _normalPlacement = new WindowLayoutOptions { X = Position.X, Y = Position.Y, Width = Bounds.Width, Height = Bounds.Height };
    }

    internal void SaveWindowLayout()
    {
        if (!_layoutRestored || Vm is not { } vm || _normalPlacement is not { } normal) return;
        vm.SaveWindowLayout(normal with
        {
            Maximized = _wasMaximized,
            SidebarVisible = vm.IsSidebarVisible,
            RightPanelWidth = _rightPanelWidth,
            EventsWidth = _debugWidth,
            TerminalHeight = _terminalHeight,
            ActivePane = vm.ActivePane.ToString(),
            EventsVisible = vm.IsDebugVisible,
            TerminalVisible = vm.IsTerminalOpen,
        });
    }
}
