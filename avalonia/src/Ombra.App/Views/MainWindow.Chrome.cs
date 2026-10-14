using Avalonia;
using Avalonia.Controls;

namespace Ombra.App.Views;

/// <summary>
/// The frame (the ChatGPT app's): the window is the chrome — a title band across the top and the icon rail down the left
/// (GuiChrome) — and the sidebar, the conversation and the right panel lie on sheets under the band (Styles.axaml
/// "Surfaces"). The band holds the header, the sidebar's top row and the right panel's tabs. On macOS it is the window's
/// title bar: the client area extends under it, the traffic lights sit over the rail and the band's start, and the empty
/// parts of the header, the sidebar's top row, the right panel's strip, the rail and the settings page's band move the
/// window, a double-click zooms it (Avalonia's own title-bar handling for elements marked
/// <c>WindowDecorationProperties.ElementRole="TitleBar"</c>). What sits in the band under the lights starts after them
/// while they show (the window's <c>traffic-lights</c> class, Styles.Workspace); in full screen they are hidden and the
/// insets go. Windows and Linux keep their system title bar over the same band.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The band's height (Tokens.axaml: GuiChromeBand): the header, the sidebar's top row, the panel's tabs.</summary>
    internal const double TitleBarHeight = 44;

    private void SetUpChrome()
    {
        if (!OperatingSystem.IsMacOS()) return;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        ExtendClientAreaToDecorationsHint = true;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowDecorationMarginProperty || e.Property == IsExtendedIntoWindowDecorationsProperty || e.Property == WindowStateProperty)
                FitChrome();
        };
        FitChrome();
        // AppKit puts the lights back at the top of its own, lower title bar after a resize or a full-screen change
        Opened += (_, _) => PlaceTrafficLights();
        SizeChanged += (_, _) => PlaceTrafficLights();
    }

    /// <summary>The traffic lights' close button starts this far from the window's left edge (the ChatGPT app's).</summary>
    internal const double TrafficLightsLeft = 16;

    /// <summary>The traffic lights in the middle of the band, as the header's buttons and title are.</summary>
    private void PlaceTrafficLights()
    {
        if (!OperatingSystem.IsMacOS() || !TrafficLightsShown || TryGetPlatformHandle() is not { Handle: not 0 } handle) return;
        Platform.Mac.MacTrafficLights.Place(handle.Handle, TitleBarHeight, TrafficLightsLeft);
    }

    /// <summary>The traffic lights are over the content: extended into the title bar, and not in full screen (where
    /// the platform hides them and reports no title-bar margin).</summary>
    private void FitChrome()
    {
        TrafficLightsShown = IsExtendedIntoWindowDecorations && WindowDecorationMargin.Top > 0 && WindowState != WindowState.FullScreen;
        PlaceTrafficLights();
    }

    /// <summary>What sits under the traffic lights starts after them (tests set it: the headless platform has no title bar).</summary>
    internal bool TrafficLightsShown
    {
        get => Classes.Contains("traffic-lights");
        set => Classes.Set("traffic-lights", value);
    }
}
