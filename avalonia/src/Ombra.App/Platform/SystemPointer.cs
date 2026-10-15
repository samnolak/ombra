using System.Runtime.InteropServices;
using Avalonia;

namespace Ombra.App.Platform;

/// <summary>
/// Where the mouse is on the desktop now, in desktop pixels (the units of a window's position: points on macOS,
/// physical pixels on Windows), straight from the system. A pointer event's position is relative to its window as the
/// window was when the event was made: for a window that moves every frame (the pet carried on the desktop) that is
/// up to a frame stale, and the pet would run ahead of the pointer. Null where the system is not asked (Linux, tests).
/// </summary>
internal static class SystemPointer
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>Tests say where their simulated mouse is (null: nowhere, use the event's position).</summary>
    internal static Func<PixelPoint?>? Override { get; set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X, Y;
    }

    public static PixelPoint? Position
    {
        get
        {
            if (Override is { } simulated) return simulated();
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    // A null event's location is the mouse now, in global display points from the primary screen's top-left
                    var e = CGEventCreate(0);
                    if (e == 0) return null;
                    try
                    {
                        var p = CGEventGetLocation(e);
                        return new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
                    }
                    finally { CFRelease(e); }
                }
                if (OperatingSystem.IsWindows()) return GetCursorPos(out var w) ? new PixelPoint(w.X, w.Y) : null;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                // Not there: the event's own position
            }
            return null;
        }
    }

    [DllImport(CoreGraphics)]
    private static extern nint CGEventCreate(nint source);

    [DllImport(CoreGraphics)]
    private static extern CGPoint CGEventGetLocation(nint e);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint obj);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point point);
}
