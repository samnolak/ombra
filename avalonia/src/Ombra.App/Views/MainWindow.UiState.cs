using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ombra.ClientCore;

namespace Ombra.App.Views;

/// <summary>
/// The window's frame and panels across runs (<see cref="WindowLayoutOptions"/>, saved by the close sequence).
/// <para>The saved frame is applied when the window starts to show (<see cref="Window.WindowOpenedEvent"/>, raised before
/// Avalonia sizes, places and shows it), so the platform's first frame is the saved one, never the XAML 1100×760 in the
/// middle of the main display. On macOS this also decides what Zoom goes back to: AppKit's zoom keeps the frame it zoomed
/// from, and a window maximized after it was shown had been zoomed from the XAML size at the saved corner.</para>
/// <para>The normal frame is taken once the window has rested in the normal state (<see cref="PlacementSettle"/>):
/// AppKit resizes a window to the zoomed or full-screen frame, and through the zoom animation, before the window reports
/// the new state, and those frames are not the one to come back to.</para>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>How long a frame must hold in the normal state to be the normal frame (longer than AppKit's zoom).</summary>
    internal static readonly TimeSpan PlacementSettle = TimeSpan.FromMilliseconds(600);

    private bool _layoutRestored;
    private WindowLayoutOptions? _normalPlacement;
    private bool _wasMaximized;
    private DispatcherTimer? _placementTimer;

    private void WireWindowLayout()
    {
        if (Platform.TestFocus.NoActivate)
        {
            // A harness run never takes the owner's focus, nor covers his windows (Platform/TestFocus.cs)
            ShowActivated = false;
            Opened += (_, _) => Platform.TestFocus.SendBehind(this);
        }
        AddHandler(WindowOpenedEvent, (_, _) => RestoreWindowLayout());
        Opened += (_, _) =>
        {
            RestoreWindowLayout(); // when there was no view model to restore from before the show
            RememberNormalPlacement();
            FitPanels();
            LogFrame($"shown at {FrameText()} state={WindowState}{NativeState()}");
        };
        PositionChanged += (_, _) => SettleNormalPlacement();
        SizeChanged += (_, _) => SettleNormalPlacement();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty || !_layoutRestored) return;
            // A change of state drops what the window went through on the way; full screen keeps the state under it
            _placementTimer?.Stop();
            if (WindowState is WindowState.Normal or WindowState.Maximized) _wasMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) SettleNormalPlacement();
        };
        WireFrameLog(); // MainWindow.FrameLog.cs
    }

    private void RestoreWindowLayout()
    {
        if (_layoutRestored || Vm is not { } vm) return;
        var saved = vm.LoadWindowLayout();
        var screens = Screens?.All.Select(s => new PlacementScreen(s.WorkingArea, s.Scaling, s.IsPrimary)).ToList() ?? [];
        if (saved is not null)
        {
            var placement = PlaceSavedWindow(saved, screens, MinWidth, MinHeight);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(placement.X, placement.Y);
            Width = placement.Width;
            Height = placement.Height;
            _normalPlacement = placement;
            _rightPanelWidth = PanelSize(saved.RightPanelWidth, RightPanelDefaultWidth, RightPanelMinWidth);
            _debugWidth = PanelSize(saved.EventsWidth, DebugDefaultWidth, DebugMinWidth);
            _terminalHeight = PanelSize(saved.TerminalHeight, TerminalDefaultHeight, 100);
            vm.RestorePanelVisibility(saved);
            _wasMaximized = saved.Maximized;
            if (saved.Maximized) WindowState = WindowState.Maximized;
            LogFrame($"restore saved={Describe(saved)} placed={Describe(placement)} screens=[{Describe(screens)}]");
        }
        else if (Platform.TestFocus.LeftDisplay(this) is { } left)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = left;
            LogFrame($"restore: no saved layout, harness run on the left display at {left} screens=[{Describe(screens)}]");
        }
        else LogFrame($"restore: no saved layout (XAML default, centered) screens=[{Describe(screens)}]");
        _layoutRestored = true;
    }

    private static double PanelSize(double value, double fallback, double minimum) =>
        double.IsFinite(value) && value >= minimum ? Math.Min(value, 10000) : fallback;

    /// <summary>A display as placement needs it: its working area (desktop pixels) and scale.</summary>
    internal readonly record struct PlacementScreen(PixelRect WorkingArea, double Scaling, bool IsPrimary);

    /// <summary>
    /// The saved frame fitted to the display it overlaps most: a frame partly past a display's edge (a window dragged
    /// beyond the left edge of a display at negative X) stays there. The primary display takes a frame that overlaps none
    /// (its display is gone); without displays the frame is kept.
    /// </summary>
    internal static WindowLayoutOptions PlaceSavedWindow(WindowLayoutOptions saved, IReadOnlyList<PlacementScreen> screens,
        double minimumWidth = 640, double minimumHeight = 480)
    {
        PlacementScreen? best = null;
        long bestOverlap = 0;
        foreach (var screen in screens)
        {
            var scale = double.IsFinite(screen.Scaling) && screen.Scaling > 0 ? screen.Scaling : 1;
            var width = double.IsFinite(saved.Width) && saved.Width > 0 ? saved.Width : 1200;
            var height = double.IsFinite(saved.Height) && saved.Height > 0 ? saved.Height : 800;
            var frame = new PixelRect(saved.X, saved.Y, (int)Math.Ceiling(Math.Min(width * scale, 100000)), (int)Math.Ceiling(Math.Min(height * scale, 100000)));
            var overlap = frame.Intersect(screen.WorkingArea);
            var area = (long)overlap.Width * overlap.Height;
            if (area > bestOverlap) (best, bestOverlap) = (screen, area);
        }
        best ??= screens.Where(s => s.IsPrimary).Cast<PlacementScreen?>().FirstOrDefault() ?? screens.Cast<PlacementScreen?>().FirstOrDefault();
        return best is { } s2 ? ClampWindowPlacement(saved, s2.WorkingArea, s2.Scaling, minimumWidth, minimumHeight) : saved;
    }

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

    /// <summary>A move or resize in the normal state becomes the normal frame once it has held for <see cref="PlacementSettle"/>.</summary>
    private void SettleNormalPlacement()
    {
        if (!_layoutRestored) return;
        if (_placementTimer is null)
        {
            _placementTimer = new DispatcherTimer { Interval = PlacementSettle };
            _placementTimer.Tick += (_, _) =>
            {
                _placementTimer.Stop();
                RememberNormalPlacement();
            };
        }
        _placementTimer.Stop();
        _placementTimer.Start();
    }

    private void RememberNormalPlacement()
    {
        if (!_layoutRestored || WindowState != WindowState.Normal || Bounds.Width <= 0 || Bounds.Height <= 0 || NativelyNotNormal()) return;
        _normalPlacement = new WindowLayoutOptions { X = Position.X, Y = Position.Y, Width = Bounds.Width, Height = Bounds.Height };
    }

    /// <summary>
    /// macOS: AppKit's own word on a window the managed state still calls normal: zoomed (its frame is the display's
    /// standard frame), in or entering full screen, or minimized.
    /// </summary>
    private bool NativelyNotNormal()
    {
        if (!OperatingSystem.IsMacOS() || NativeWindow() is not (var window and not 0)) return false;
        return MacNotNormal(window);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool MacNotNormal(nint window)
    {
        const nuint FullScreen = 1 << 14; // NSWindowStyleMaskFullScreen, set as the transition starts
        return Platform.Mac.MacObjC.SendBool(window, "isZoomed") || Platform.Mac.MacObjC.SendBool(window, "isMiniaturized")
            || (Platform.Mac.MacObjC.SendNUInt(window, "styleMask") & FullScreen) != 0;
    }

    /// <summary>macOS: the NSWindow; 0 elsewhere and before the platform made one.</summary>
    private nint NativeWindow()
    {
        if (!OperatingSystem.IsMacOS() || TryGetPlatformHandle() is not { Handle: not 0 } handle) return 0;
        return Nightly.NightlyCocoa.WindowOf(handle.Handle);
    }

    internal void SaveWindowLayout()
    {
        // A move or resize just before quitting counts once it is a normal frame
        if (_placementTimer?.IsEnabled == true)
        {
            _placementTimer.Stop();
            RememberNormalPlacement();
        }
        if (!_layoutRestored || Vm is not { } vm || _normalPlacement is not { } normal) return;
        var layout = normal with
        {
            Maximized = _wasMaximized,
            SidebarVisible = vm.IsSidebarVisible,
            RightPanelWidth = _rightPanelWidth,
            EventsWidth = _debugWidth,
            TerminalHeight = _terminalHeight,
            ActivePane = vm.ActivePane.ToString(),
            EventsVisible = vm.IsDebugVisible,
            TerminalVisible = vm.IsTerminalOpen,
        };
        vm.SaveWindowLayout(layout);
        LogFrame($"saved {Describe(layout)} state={WindowState}");
    }
}
