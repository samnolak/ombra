using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ombra.App.Services;
using Ombra.ClientCore;

namespace Ombra.App.Views;

/// <summary>
/// Where the main window went, in client.log (source <c>window</c>), for a window found where nobody put it: the saved
/// layout, the displays and the frame chosen at launch, the frame it was shown with, every state change and display
/// change, the frame saved at quit, and each resize that neither the user's drag nor Ombra's layout asked for (AppKit's
/// zoom, full screen, a display going away …) or move onto another display, once it has settled: from where to where,
/// AppKit's state and the event it was handling, and while <see cref="FrameLogStackLimit"/> lasts the native and managed
/// stacks of its first frame. Each is a rare event; a drag within one display and Ombra's own resizes write nothing.
/// </summary>
public sealed partial class MainWindow
{
    internal const int FrameLogStackLimit = 10;

    /// <summary>A burst of frame changes ends after this long without another.</summary>
    internal static readonly TimeSpan FrameBurstQuiet = TimeSpan.FromMilliseconds(600);

    /// <summary>Where these lines go: the app's client.log (App); tests give their own; without one nothing is written.</summary>
    internal ClientLog? WindowLog { get; init; }

    private int _frameStacks;
    private PixelPoint _framePosition;
    private Size _frameSize;
    private string? _frameBurst;
    private DispatcherTimer? _frameBurstTimer;

    private void WireFrameLog()
    {
        Resized += (_, e) =>
        {
            if (e.Reason is not (WindowResizeReason.User or WindowResizeReason.Layout or WindowResizeReason.Application))
                FrameChanged(FormattableString.Invariant($"resized to {e.ClientSize.Width:0.#}×{e.ClientSize.Height:0.#} ({e.Reason})"));
            _frameSize = e.ClientSize;
        };
        PositionChanged += (_, e) =>
        {
            if (ScreenAt(e.Point) is var to && to != ScreenAt(_framePosition) && !UserDragsWindow())
                FrameChanged($"moved to {e.Point.X},{e.Point.Y} on display {to?.ToString() ?? "none"}");
            _framePosition = e.Point;
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && _layoutRestored) LogFrame($"state {e.OldValue} -> {e.NewValue} at {FrameText()}{NativeState()}");
        };
        Opened += (_, _) =>
        {
            (_framePosition, _frameSize) = (Position, ClientSize);
            if (WindowLog is not null && Screens is { } screens) screens.Changed += OnDisplaysChanged;
        };
        Closed += (_, _) =>
        {
            _frameBurstTimer?.Stop();
            _placementTimer?.Stop();
            if (Screens is { } screens) screens.Changed -= OnDisplaysChanged;
        };
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) =>
        LogFrame($"displays changed: [{Describe(Screens?.All.Select(s => new PlacementScreen(s.WorkingArea, s.Scaling, s.IsPrimary)) ?? [])}] window at {FrameText()}");

    private void LogFrame(string text) => WindowLog?.Write("window", text);

    private PixelRect? ScreenAt(PixelPoint point) => Screens?.ScreenFromPoint(point)?.Bounds;

    /// <summary>A change that starts a burst records what started it; the burst is written once it has settled.</summary>
    private void FrameChanged(string change)
    {
        if (WindowLog is null || !_layoutRestored || !IsVisible) return;
        if (_frameBurst is null)
        {
            var origin = "";
            if (OperatingSystem.IsMacOS()) origin = $" event={AppKitEvent()}";
            if (_frameStacks < FrameLogStackLimit)
            {
                _frameStacks++;
                var native = "(not macOS)";
                if (OperatingSystem.IsMacOS()) native = NativeStack();
                origin += $"\nnative stack:\n{native}\nmanaged stack:\n{new StackTrace(1, false)}";
            }
            _frameBurst = $"{change} from {FormattableString.Invariant($"{_framePosition.X},{_framePosition.Y} {_frameSize.Width:0.#}×{_frameSize.Height:0.#}")} (state {WindowState}){origin}";
            if (_frameBurstTimer is null)
            {
                _frameBurstTimer = new DispatcherTimer { Interval = FrameBurstQuiet };
                _frameBurstTimer.Tick += (_, _) =>
                {
                    _frameBurstTimer.Stop();
                    if (_frameBurst is { } burst) LogFrame($"settled at {FrameText()} state={WindowState}{NativeState()} after {burst}");
                    _frameBurst = null;
                };
            }
        }
        _frameBurstTimer!.Stop();
        _frameBurstTimer.Start();
    }

    private string FrameText() => FormattableString.Invariant($"{Position.X},{Position.Y} {ClientSize.Width:0.#}×{ClientSize.Height:0.#}");

    private static string Describe(WindowLayoutOptions l) =>
        FormattableString.Invariant($"{l.X},{l.Y} {l.Width:0.#}×{l.Height:0.#}{(l.Maximized ? " maximized" : "")}");

    private static string Describe(IEnumerable<PlacementScreen> screens) => string.Join("; ", screens.Select(s =>
        FormattableString.Invariant($"{s.WorkingArea.X},{s.WorkingArea.Y} {s.WorkingArea.Width}×{s.WorkingArea.Height} x{s.Scaling:0.##}{(s.IsPrimary ? " primary" : "")}")));

    /// <summary>macOS: what AppKit says of the window (the managed state can lag behind it).</summary>
    private string NativeState()
    {
        if (!OperatingSystem.IsMacOS() || NativeWindow() is not (var w and not 0)) return "";
        try
        {
            var mask = Platform.Mac.MacObjC.SendNUInt(w, "styleMask");
            return $" (AppKit zoomed={Platform.Mac.MacObjC.SendBool(w, "isZoomed")} fullscreen={(mask & (1 << 14)) != 0} "
                + $"minimized={Platform.Mac.MacObjC.SendBool(w, "isMiniaturized")} visible={Platform.Mac.MacObjC.SendBool(w, "isVisible")})";
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return " (AppKit: " + e.Message + ")"; }
    }

    /// <summary>The user drags the window (AppKit's title-bar drag runs on left-mouse-dragged events).</summary>
    private static bool UserDragsWindow()
    {
        if (!OperatingSystem.IsMacOS() || CurrentAppKitEvent() is not (var ev and not 0)) return false;
        return Platform.Mac.MacObjC.SendNUInt(ev, "type") is 1 or 6; // NSEventTypeLeftMouseDown, NSEventTypeLeftMouseDragged
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static nint CurrentAppKitEvent()
    {
        var app = Platform.Mac.MacObjC.Send(Platform.Mac.MacObjC.Cls("NSApplication"), "sharedApplication");
        return app == 0 ? 0 : Platform.Mac.MacObjC.Send(app, "currentEvent");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static string AppKitEvent()
    {
        try { return CurrentAppKitEvent() is var ev and not 0 ? Platform.Mac.MacObjC.ToStr(Platform.Mac.MacObjC.Send(ev, "description")) ?? "?" : "none"; }
        catch (Exception e) when (e is not OutOfMemoryException) { return e.Message; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static string NativeStack()
    {
        try
        {
            var symbols = Platform.Mac.MacObjC.Strings(Platform.Mac.MacObjC.Send(Platform.Mac.MacObjC.Cls("NSThread"), "callStackSymbols"));
            return string.Join('\n', symbols.Take(40));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return e.Message; }
    }
}
