using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmpGui.App.Platform;

/// <summary>Tells the user something happened while the window was in the background (run finished, approval needed).</summary>
public interface INotifier
{
    /// <param name="windowHandle">The native window handle, for platforms that flash the window instead.</param>
    void Notify(string title, string message, nint windowHandle);
}

/// <summary>No OS notification (tests, or when the user turned them off). The window title still carries a mark.</summary>
public sealed class NoNotifier : INotifier
{
    public void Notify(string title, string message, nint windowHandle) { }
}

/// <summary>Linux: freedesktop notifications through <c>notify-send</c> (libnotify), if installed. Text goes as argv.</summary>
public sealed class NotifySendNotifier : INotifier
{
    public void Notify(string title, string message, nint windowHandle) => Run.Detached("notify-send", Arguments(title, message));

    /// <summary>"--" ends the options: a reply that starts with "- item" or "-t 0" is text, not an option.</summary>
    internal static string[] Arguments(string title, string message) => ["--app-name=OMP GUI", "--", title, message];
}

/// <summary>
/// macOS: Notification Center through <c>osascript</c>. The text is passed as script arguments, never spliced into the
/// script, so it cannot inject AppleScript.
/// </summary>
public sealed class OsaScriptNotifier : INotifier
{
    public void Notify(string title, string message, nint windowHandle) =>
        Run.Detached("osascript", ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, message]);
}

/// <summary>Windows: flashes the taskbar button until the window is activated (FlashWindowEx).</summary>
public sealed class TaskbarFlashNotifier : INotifier
{
    public void Notify(string title, string message, nint windowHandle)
    {
        if (windowHandle == 0) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = windowHandle,
            Flags = FlashTray | FlashUntilForeground,
            Count = 0,
            Timeout = 0,
        };
        try { FlashWindowEx(ref info); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}

internal static class Run
{
    /// <summary>Starts a helper without a shell and without waiting; a missing tool is silently skipped.</summary>
    public static void Detached(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.StandardOutput.ReadToEndAsync();
            p?.StandardError.ReadToEndAsync();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
    }
}
