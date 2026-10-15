using System.Runtime.InteropServices;
using Ombra.App.Platform.Mac;

namespace Ombra.App.Platform;

/// <summary>
/// Whether the system asks apps to reduce motion: macOS "Reduce motion" (Accessibility → Display), Windows "Show
/// animations in Windows" switched off. Unknown elsewhere: no.
/// </summary>
internal static class ReducedMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>Tests set what the system says.</summary>
    internal static bool? Override { get; set; }

    public static bool System
    {
        get
        {
            if (Override is { } set) return set;
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    var workspace = MacObjC.Send(MacObjC.Cls("NSWorkspace"), "sharedWorkspace");
                    return workspace != 0 && MacObjC.SendBool(workspace, "accessibilityDisplayShouldReduceMotion");
                }
                if (OperatingSystem.IsWindows())
                {
                    var animate = 1;
                    return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref animate, 0) && animate == 0;
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                // The probe is not there: motion as usual
            }
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
