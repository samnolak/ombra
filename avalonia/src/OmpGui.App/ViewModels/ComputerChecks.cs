using System.Runtime.InteropServices;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

public enum CheckState { Ok, Warning, Failed, Unknown }

/// <summary>One line of the computer-use requirements, with a way to fix it when there is one (a System Settings pane).</summary>
/// <param name="Action">A System Settings URL to open, or <see cref="TurnOnEvalAction"/>.</param>
public sealed record RequirementCheck(string Title, string Detail, CheckState State, string? ActionLabel = null, string? Action = null)
{
    public const string TurnOnEvalAction = "omp:eval.js";

    public bool IsOk => State == CheckState.Ok;
    public bool IsWarning => State == CheckState.Warning;
    public bool IsFailed => State == CheckState.Failed;
    public bool IsUnknown => State == CheckState.Unknown;
    public bool HasAction => Action is not null;
}

/// <summary>
/// What omp's desktop backend (omp 18.2.0, crates/pi-natives/src/desktop) needs on this computer, checked live:
/// Linux picks Wayland when WAYLAND_DISPLAY is set, else X11 on DISPLAY (XTEST for input, RandR for screens,
/// AT-SPI over the session bus for windows and controls; Wayland capture is not in omp's builds). macOS needs Screen
/// Recording (CGPreflightScreenCaptureAccess) and Accessibility (AXIsProcessTrusted) for the app that starts omp.
/// Windows needs no permission (GDI capture, SendInput, UI Automation).
/// </summary>
public static class ComputerChecks
{
    public const string ScreenRecordingPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture";
    public const string AccessibilityPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    public static async Task<IReadOnlyList<RequirementCheck>> RunAsync(Func<ToolCommand, CancellationToken, Task<ToolResult>> runTool,
        Func<string, string?> env, CancellationToken ct, string? os = null)
    {
        os ??= OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";
        return os switch
        {
            "macos" => MacChecks(),
            "windows" =>
            [
                new("No permission needed", "On Windows omp takes screenshots, clicks and types, and reads windows and controls (UI Automation) without asking for a permission.", CheckState.Ok),
            ],
            _ => await LinuxChecksAsync(runTool, env, ct),
        };
    }

    private static async Task<IReadOnlyList<RequirementCheck>> LinuxChecksAsync(Func<ToolCommand, CancellationToken, Task<ToolResult>> runTool,
        Func<string, string?> env, CancellationToken ct)
    {
        var list = new List<RequirementCheck>();
        var wayland = env("WAYLAND_DISPLAY") is { Length: > 0 } w ? w : null;
        var x11 = env("DISPLAY") is { Length: > 0 } d ? d : null;
        var session = env("XDG_SESSION_TYPE") is { Length: > 0 } t ? $" (session type: {t})" : "";
        if (wayland is not null)
        {
            list.Add(new("Wayland: no screenshots", $"omp uses its Wayland backend here (WAYLAND_DISPLAY={wayland}){session}. It cannot take screenshots on Wayland: that part is not in omp's builds. Mouse and keyboard go through the desktop's Remote Desktop permission, which asks you the first time. For full computer use, sign in to an X11 session.",
                CheckState.Warning));
        }
        else if (x11 is not null)
        {
            list.Add(new("X11 display", $"omp uses the X11 display {x11}{session}.", CheckState.Ok));
            list.Add(await X11ExtensionsAsync(runTool, x11, ct));
        }
        else
        {
            list.Add(new("No display", "Neither DISPLAY nor WAYLAND_DISPLAY is set where omp runs, so omp finds no screen to use.", CheckState.Failed));
        }
        if (wayland is not null || x11 is not null)
        {
            var bus = env("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 }
                      || (env("XDG_RUNTIME_DIR") is { Length: > 0 } run && File.Exists(Path.Combine(run, "bus")));
            list.Add(bus
                ? new("Accessibility (AT-SPI)", wayland is not null
                    ? "Session bus found: omp lists windows and uses controls through the desktop's accessibility service (on Wayland the window list needs it)."
                    : "Session bus found: omp reads windows and controls through the desktop's accessibility service. Optional: screenshots, mouse and keyboard work without it.", CheckState.Ok)
                : new("Accessibility (AT-SPI)", "No session bus found: omp can't read windows and controls. On X11 screenshots, mouse and keyboard still work.", CheckState.Unknown));
        }
        return list;
    }

    /// <summary>XTEST (omp's mouse and keyboard) and RANDR (its screen list), from <c>xdpyinfo</c>'s extension list.</summary>
    private static async Task<RequirementCheck> X11ExtensionsAsync(Func<ToolCommand, CancellationToken, Task<ToolResult>> runTool, string display, CancellationToken ct)
    {
        const string title = "X extensions XTEST and RandR";
        var r = await runTool(new ToolCommand("xdpyinfo", ["-display", display], Timeout: TimeSpan.FromSeconds(5)), ct);
        if (r.NotFound) return new(title, "Couldn't check: xdpyinfo is not installed (package x11-utils or xorg-xdpyinfo). omp needs XTEST to click and type and RandR to find the screens.", CheckState.Unknown);
        if (!r.Ok) return new(title, "Couldn't check: xdpyinfo says " + r.Message, CheckState.Unknown);
        var extensions = ParseXdpyinfoExtensions(r.Stdout);
        var missing = new[] { "XTEST", "RANDR" }.Where(x => !extensions.Contains(x)).ToList();
        return missing.Count == 0
            ? new(title, "Both are there: omp can click and type (XTEST) and find the screens (RandR).", CheckState.Ok)
            : new(title, $"The X server lacks {string.Join(" and ", missing)}: omp needs XTEST to click and type and RandR to find the screens.", CheckState.Failed);
    }

    /// <summary>The names under "number of extensions:" in <c>xdpyinfo</c>'s output.</summary>
    public static HashSet<string> ParseXdpyinfoExtensions(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inList = false;
        foreach (var raw in text.Split('\n'))
        {
            if (raw.TrimStart().StartsWith("number of extensions:", StringComparison.Ordinal)) { inList = true; continue; }
            if (!inList) continue;
            if (raw.Length == 0 || !char.IsWhiteSpace(raw[0])) break;
            set.Add(raw.Trim());
        }
        return set;
    }

    private static IReadOnlyList<RequirementCheck> MacChecks()
    {
        var capture = MacPermission(CGPreflightScreenCaptureAccess);
        var trusted = MacPermission(AXIsProcessTrusted);
        return
        [
            new("Screen Recording", capture switch
            {
                true => "Allowed: omp can take screenshots.",
                false => "Needed for screenshots. In System Settings → Privacy & Security → Screen Recording, turn on OMP (the app that starts omp), then quit and reopen OMP.",
                null => "Couldn't check. omp needs it for screenshots: System Settings → Privacy & Security → Screen Recording.",
            }, capture switch { true => CheckState.Ok, false => CheckState.Failed, null => CheckState.Unknown },
                capture == true ? null : "Open Screen Recording settings", capture == true ? null : ScreenRecordingPane),
            new("Accessibility", trusted switch
            {
                true => "Allowed: omp can click, type and use controls.",
                false => "Needed to click, type and use controls. In System Settings → Privacy & Security → Accessibility, turn on OMP.",
                null => "Couldn't check. omp needs it to click, type and use controls: System Settings → Privacy & Security → Accessibility.",
            }, trusted switch { true => CheckState.Ok, false => CheckState.Failed, null => CheckState.Unknown },
                trusted == true ? null : "Open Accessibility settings", trusted == true ? null : AccessibilityPane),
        ];
    }

    private static bool? MacPermission(Func<bool> probe)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try { return probe(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    // The same non-prompting TCC probes omp's macOS backend uses (macos/capture.rs, macos/ax.rs). omp runs as this app's
    // child, so macOS answers for this app: the permission omp gets is the one OMP has.
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGPreflightScreenCaptureAccess();

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AXIsProcessTrusted();
}
