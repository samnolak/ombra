using System.Globalization;

namespace OmpGui.Tests;

/// <summary>
/// The window sizes the layout audits run at. By default the few a normal run can afford; with OMPGUI_MATRIX=1 the
/// whole design-review matrix: both themes at seven sizes, from the minimum window to a 1080p screen.
/// </summary>
public static class DesignMatrix
{
    public static readonly (double W, double H)[] Full =
        [(480, 360), (640, 600), (800, 600), (1024, 768), (1280, 800), (1440, 900), (1920, 1080)];

    public static bool IsFull => Environment.GetEnvironmentVariable("OMPGUI_MATRIX") is "1" or "true";

    /// <summary>Theory data (theme, width, height); <paramref name="defaults"/> like "light 1180 760".</summary>
    public static IEnumerable<object[]> Sizes(params string[] defaults)
    {
        if (!IsFull)
        {
            foreach (var d in defaults)
            {
                var p = d.Split(' ');
                yield return [p[0], double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture)];
            }
            yield break;
        }
        foreach (var theme in new[] { "light", "dark" })
            foreach (var (w, h) in Full)
                yield return [theme, w, h];
    }

    public static IEnumerable<object[]> Standard => Sizes("light 1180 760", "dark 1180 760", "light 640 600", "dark 640 600");
    public static IEnumerable<object[]> MainScreens => Sizes("light 1180 760", "dark 1180 760", "light 640 600");
    public static IEnumerable<object[]> Panes => Sizes("light 1180 760", "dark 1180 760", "light 640 640", "dark 640 640");
    public static IEnumerable<object[]> Extremes => Sizes("light 1180 760", "dark 1180 760", "light 640 600", "dark 900 600");
    public static IEnumerable<object[]> FilesPane => SizesWithPane("light 1180 760 400", "dark 1180 760 400", "light 640 600 320", "dark 640 600 320");

    /// <summary>The same with the Files pane's width: 400 where the window has room, 320 below 1000 px.</summary>
    public static IEnumerable<object[]> SizesWithPane(params string[] defaults) =>
        IsFull
            ? Sizes().Select(s => new object[] { s[0], s[1], s[2], (double)s[1] >= 1000 ? 400.0 : 320.0 })
            : defaults.Select(d => d.Split(' ')).Select(p => new object[] { p[0],
                double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture), double.Parse(p[3], CultureInfo.InvariantCulture) });
}
