using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OmpGui.App.Platform;

/// <summary>
/// macOS: Avalonia.Native renders on a CVDisplayLink over the active displays. With none active (the screen locked and
/// asleep: a login item, an update's relaunch, a start over SSH) creating it fails with kCVReturnInvalidDisplay (-6661),
/// and Avalonia's platform setup throws "not able to start the RenderTimer", which ended the app at start. So the app
/// first waits for a display, checked the way Avalonia will need it, backing off up to <see cref="MaxDelay"/> between
/// checks, and says so in client.log.
/// </summary>
internal static class DisplayWait
{
    /// <summary>The longest pause between two checks: a display that wakes is used within this.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>The first pause; each next one is twice as long, up to <see cref="MaxDelay"/>.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Returns once <paramref name="hasDisplay"/> is true, at once when it is already, or once <paramref name="limit"/>
    /// has passed without one (null: no limit); meanwhile notes the wait and its end (<paramref name="note"/>). True when
    /// a display is active.
    /// </summary>
    public static bool UntilActive(Func<bool> hasDisplay, Action<TimeSpan> sleep, Action<string> note, TimeSpan? limit = null)
    {
        if (hasDisplay()) return true;
        note("No active display (the screen is locked or asleep): the window opens once there is one.");
        var waited = TimeSpan.Zero;
        var delay = FirstDelay;
        while (true)
        {
            if (limit is { } max && waited >= max)
            {
                note(string.Create(CultureInfo.InvariantCulture, $"Still no active display after {waited.TotalSeconds:0} s: not waiting any longer."));
                return false;
            }
            if (limit is { } cap && waited + delay > cap) delay = cap - waited;
            sleep(delay);
            waited += delay;
            delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
            if (hasDisplay()) break;
        }
        note(string.Create(CultureInfo.InvariantCulture, $"A display is active after {waited.TotalSeconds:0} s: opening the window."));
        return true;
    }

    /// <summary>How long a run nobody watches (a package smoke test, a benchmark) waits for a display before it goes on
    /// and fails with Avalonia's own error in client.log, instead of hanging a script.</summary>
    public static readonly TimeSpan UnattendedLimit = TimeSpan.FromMinutes(2);

    /// <summary>Avalonia.Native's failure to start its render timer (the display link), as it words it.</summary>
    public static bool IsRenderTimerFailure(Exception e) =>
        e is InvalidOperationException && e.Message.Contains("RenderTimer", StringComparison.Ordinal);

    /// <summary>Whether a display link can be created now: exactly what Avalonia.Native's render timer does at setup.</summary>
    [SupportedOSPlatform("macos")]
    public static bool MacHasActiveDisplay()
    {
        if (CVDisplayLinkCreateWithActiveCGDisplays(out var link) != 0 || link == IntPtr.Zero) return false;
        CVDisplayLinkRelease(link);
        return true;
    }

    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    [DllImport(CoreVideo)]
    private static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr displayLinkOut);

    [DllImport(CoreVideo)]
    private static extern void CVDisplayLinkRelease(IntPtr displayLink);
}
