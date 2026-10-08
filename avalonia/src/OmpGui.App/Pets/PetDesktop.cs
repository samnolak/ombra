using Avalonia;
using Avalonia.Controls;
using OmpGui.ClientCore;

namespace OmpGui.App.Pets;

/// <summary>
/// A monitor as the pet sees it, in desktop pixels (the units of <see cref="Window.Position"/>: points on macOS,
/// physical pixels on Windows): its bounds, the work area the menu bar, Dock or taskbar leave, and how many desktop
/// pixels one layout unit takes on it.
/// </summary>
public sealed record PetScreen(string? Name, PixelRect Bounds, PixelRect WorkingArea, double Scaling, bool IsPrimary = false);

/// <summary>The monitors, and word when they change (plugged, unplugged, rearranged, the Dock moved).</summary>
public interface IPetScreens
{
    IReadOnlyList<PetScreen> All { get; }
    event EventHandler? Changed;
}

/// <summary>Avalonia's <see cref="Screens"/> of a window, as <see cref="PetScreen"/>s (read again only when they change).</summary>
public sealed class AvaloniaPetScreens : IPetScreens
{
    private readonly Screens _screens;
    private IReadOnlyList<PetScreen>? _all;

    public AvaloniaPetScreens(Screens screens)
    {
        _screens = screens;
        // Subscribed first: whoever is told of a change reads the new list
        _screens.Changed += (_, _) => _all = null;
    }

    public IReadOnlyList<PetScreen> All =>
        _all ??= [.. _screens.All.Select(s => new PetScreen(s.DisplayName, s.Bounds, s.WorkingArea, s.Scaling, s.IsPrimary))];

    public event EventHandler? Changed
    {
        add => _screens.Changed += value;
        remove => _screens.Changed -= value;
    }
}

/// <summary>
/// Where a pet may be on the desktop: wholly on the monitors' work areas, never under the menu bar, the Dock or the
/// taskbar. It may straddle two monitors only where they touch (the part over the edge must be on the other one);
/// anywhere else it is pulled onto the monitor its middle is on, or the nearest one when its middle is on none (a gap
/// between monitors, a monitor that was unplugged). Pure: the monitors are passed in.
/// </summary>
public static class PetDesktop
{
    /// <summary>The pet's size in desktop pixels on <paramref name="screen"/>.</summary>
    public static PixelSize SizeOn(PetScreen? screen, Size size)
    {
        var scale = screen?.Scaling is > 0 and var s ? s : 1;
        return new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
    }

    /// <summary>The monitor the pet at <paramref name="at"/> is on: the one its middle is on, else the nearest one.</summary>
    public static PetScreen? Home(IReadOnlyList<PetScreen> screens, PixelPoint at, Size size)
    {
        PetScreen? nearest = null;
        var best = double.MaxValue;
        foreach (var s in screens)
        {
            var px = SizeOn(s, size);
            var middle = new Point(at.X + px.Width / 2.0, at.Y + px.Height / 2.0);
            var d = Distance(s.WorkingArea, middle);
            if (d == 0) return s;
            if (d < best)
            {
                best = d;
                nearest = s;
            }
        }
        return nearest;
    }

    /// <summary>The nearest allowed place for a pet whose top-left would be <paramref name="at"/>.</summary>
    public static PixelPoint Clamp(IReadOnlyList<PetScreen> screens, PixelPoint at, Size size)
    {
        if (Home(screens, at, size) is not { } home) return at;
        var px = SizeOn(home, size);
        var rect = new PixelRect(at, px);
        if (Covered(screens, rect)) return at;
        var area = home.WorkingArea;
        return new PixelPoint(
            Math.Clamp(at.X, area.X, Math.Max(area.X, area.Right - px.Width)),
            Math.Clamp(at.Y, area.Y, Math.Max(area.Y, area.Bottom - px.Height)));
    }

    /// <summary>True when every pixel of <paramref name="rect"/> is on some monitor's work area.</summary>
    public static bool Covered(IReadOnlyList<PetScreen> screens, PixelRect rect)
    {
        // Cut the rectangle along every work area edge inside it: each cell is then wholly on a work area or wholly off
        var xs = new SortedSet<int> { rect.X, rect.Right };
        var ys = new SortedSet<int> { rect.Y, rect.Bottom };
        foreach (var s in screens)
        {
            var a = s.WorkingArea;
            if (!a.Intersects(rect)) continue;
            foreach (var x in (int[])[a.X, a.Right]) if (x > rect.X && x < rect.Right) xs.Add(x);
            foreach (var y in (int[])[a.Y, a.Bottom]) if (y > rect.Y && y < rect.Bottom) ys.Add(y);
        }
        var cx = xs.ToArray();
        var cy = ys.ToArray();
        for (var i = 1; i < cx.Length; i++)
            for (var j = 1; j < cy.Length; j++)
            {
                var mx = (cx[i - 1] + cx[i]) / 2.0;
                var my = (cy[j - 1] + cy[j]) / 2.0;
                if (!screens.Any(s => s.WorkingArea.X <= mx && mx < s.WorkingArea.Right && s.WorkingArea.Y <= my && my < s.WorkingArea.Bottom))
                    return false;
            }
        return true;
    }

    /// <summary>The place to save for a pet at <paramref name="at"/>: relative to the monitor it is on.</summary>
    public static PetDesktopPlace Place(IReadOnlyList<PetScreen> screens, PixelPoint at, Size size)
    {
        var home = Home(screens, at, size);
        var b = home?.Bounds ?? default;
        return new PetDesktopPlace
        {
            Screen = home?.Name,
            ScreenX = b.X,
            ScreenY = b.Y,
            ScreenWidth = b.Width,
            ScreenHeight = b.Height,
            X = at.X - b.X,
            Y = at.Y - b.Y,
        };
    }

    /// <summary>
    /// Where a saved place is now: on its monitor (found by name and size, else by name, else by its old bounds),
    /// wherever that monitor now is in the arrangement; a monitor that is gone gives the pet to the nearest one.
    /// </summary>
    public static PixelPoint Resolve(IReadOnlyList<PetScreen> screens, PetDesktopPlace place, Size size)
    {
        var bounds = new PixelRect(place.ScreenX, place.ScreenY, place.ScreenWidth, place.ScreenHeight);
        var screen = screens.FirstOrDefault(s => s.Name == place.Screen && s.Bounds.Size == bounds.Size)
                     ?? (place.Screen is null ? null : screens.FirstOrDefault(s => s.Name == place.Screen))
                     ?? screens.FirstOrDefault(s => s.Bounds == bounds);
        var origin = screen?.Bounds.Position ?? bounds.Position;
        return Clamp(screens, new PixelPoint(origin.X + place.X, origin.Y + place.Y), size);
    }

    private static double Distance(PixelRect r, Point p)
    {
        var dx = p.X < r.X ? r.X - p.X : p.X >= r.Right ? p.X - r.Right + 1 : 0;
        var dy = p.Y < r.Y ? r.Y - p.Y : p.Y >= r.Bottom ? p.Y - r.Bottom + 1 : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
