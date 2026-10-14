using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Views;

namespace Ombra.Tests;

/// <summary>
/// macOS: the app's title band is the window's title bar (MainWindow.Chrome.cs). The headless platform has no title bar,
/// so the tests switch the traffic lights' insets on themselves: nothing the user reads or clicks may lie under the
/// lights, the band stays one row of <see cref="MainWindow.TitleBarHeight"/>, the rail's buttons start under it, and its
/// empty parts are the window's drag areas.
/// </summary>
public sealed class WindowChromeTests
{
    /// <summary>Where the traffic lights sit in the title band (three 14 pt buttons 23 pt apart from
    /// <see cref="MainWindow.TrafficLightsLeft"/>, centred in the band), with some air around them.</summary>
    private static readonly Rect Lights = new(0, 0, MainWindow.TrafficLightsLeft + 2 * 23 + 14 + 8, MainWindow.TitleBarHeight);

    private static async Task Settle(int ms = 200)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static Rect Box(Window w, Control c) => new(c.TranslatePoint(default, w)!.Value, c.Bounds.Size);

    /// <summary>What the user reads or clicks and the lights would cover.</summary>
    private static List<string> UnderTheLights(MainWindow w) =>
        [.. w.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Button or TextBox or TextBlock && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0
                && Box(w, c).Intersects(Lights))
            .Select(c => $"{c.GetType().Name} {c.Name} {(c as TextBlock)?.Text} at {Box(w, c)}")];

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public async Task Nothing_sits_under_the_traffic_lights_and_the_top_row_moves_the_window()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1180);
        w.TrafficLightsShown = true;
        await Settle();

        // The sidebar shown: the rail starts below the lights, the sidebar's top row after them
        Assert.True(vm.ShowSidebar);
        Assert.Empty(UnderTheLights(w));
        Assert.True(Box(w, Named<Button>(w, "RailPluginsButton")).Y >= MainWindow.TitleBarHeight - 0.5);
        Assert.True(Box(w, Named<Button>(w, "HideSidebarButton")).X >= Lights.Right);

        // The sidebar hidden: the header starts after the lights
        vm.ToggleSidebarCommand.Execute(null);
        await Settle();
        Assert.False(vm.ShowSidebar);
        Assert.Empty(UnderTheLights(w));

        // The settings page, its list beside it, and in a narrow window its header row: below the band or after the lights
        vm.OpenSettingsCommand.Execute(null);
        await Settle();
        Assert.Empty(UnderTheLights(w));
        w.Width = 700;
        await Settle(300);
        Assert.True(vm.IsSettingsNarrow);
        Assert.Empty(UnderTheLights(w));
        Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(Named<Border>(w, "SettingsNarrowHeader")));
        vm.CloseSettingsCommand.Execute(null);
        w.Width = 1180;
        await Settle();

        // The band is one row: the sidebar's top row, the header and the right panel's strip
        vm.ToggleSidebarCommand.Execute(null);
        vm.ShowPaneCommand.Execute(Ombra.App.ViewModels.SidePane.Files);
        await Settle(300);
        var header = Named<Border>(w, "HeaderBar");
        var strip = Named<Border>(w, "PanelStrip");
        var sidebarTop = Named<Grid>(w, "SidebarHeader");
        Assert.All(new Control[] { header, strip, sidebarTop }, c => Assert.Equal((0.0, MainWindow.TitleBarHeight), (Box(w, c).Y, Box(w, c).Height)));

        // Their empty parts (and the title, the brand and the rail) are the window's title bar; the buttons are not
        foreach (var c in new Control[] { header, strip, sidebarTop, Named<TextBlock>(w, "HeaderTitle"), Named<Border>(w, "IconRail"), Named<Border>(w, "SettingsPage") })
            Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(c));
        Assert.Equal(WindowDecorationsElementRole.None, WindowDecorationProperties.GetElementRole(Named<Button>(w, "HideSidebarButton")));
        // …and hit-testable, so that the platform finds them under the pointer
        Assert.All(new Control[] { header, strip, sidebarTop }, c => Assert.NotNull(c switch { Border b => b.Background, Panel p => p.Background, _ => null }));

        // Full screen (or another platform): no insets; the rail's buttons stay under the band (one L-shaped chrome)
        w.TrafficLightsShown = false;
        await Settle();
        Assert.Equal(8, sidebarTop.Margin.Left);
        Assert.True(Box(w, Named<Button>(w, "RailPluginsButton")).Y >= MainWindow.TitleBarHeight - 0.5);
        await vm.DisposeAsync();
        w.Close();
    }
}
