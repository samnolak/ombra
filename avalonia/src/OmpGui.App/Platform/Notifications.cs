using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmpGui.App.Platform;

/// <summary>Tells the user something happened while they were not looking (run finished, approval needed), in the window
/// in the background or in a chat not shown.</summary>
public interface INotifier
{
    /// <param name="windowHandle">The native window handle, for platforms that flash the window instead.</param>
    /// <param name="clicked">Shows the chat the notification is about; called (on any thread) when the user clicks the
    /// notification, where the platform reports that.</param>
    void Notify(string title, string message, nint windowHandle, Action clicked);

    /// <summary>The notification is the window itself (a flashing taskbar button): bringing the window to the front is
    /// the click.</summary>
    bool ClickActivatesWindow => false;
}

/// <summary>No OS notification (tests, or when the user turned them off). The window title still carries a mark.</summary>
public sealed class NoNotifier : INotifier
{
    public void Notify(string title, string message, nint windowHandle, Action clicked) { }
}

/// <summary>
/// Linux: freedesktop notifications through <c>notify-send</c> (libnotify), if installed. Text goes as argv. With an
/// "Open" action (libnotify 0.7.10 and later), notify-send waits and prints the action the user chose; an older one
/// rejects the option, and the notification goes again without it.
/// </summary>
public sealed class NotifySendNotifier : INotifier
{
    public void Notify(string title, string message, nint windowHandle, Action clicked) => _ = NotifyAsync(title, message, clicked);

    private static async Task NotifyAsync(string title, string message, Action clicked)
    {
        var (exit, output) = await Run.CaptureAsync("notify-send", ActionArguments(title, message));
        if (exit == 0 && output.Trim() == "default") clicked();
        else if (exit is not (0 or null)) Run.Detached("notify-send", Arguments(title, message));
    }

    /// <summary>"--" ends the options: a reply that starts with "- item" or "-t 0" is text, not an option.</summary>
    internal static string[] Arguments(string title, string message) => ["--app-name=OMP GUI", "--", title, message];

    /// <summary>A click on the notification body is its "default" action.</summary>
    internal static string[] ActionArguments(string title, string message) =>
        ["--app-name=OMP GUI", "--action=default=Open", "--wait", "--", title, message];
}

/// <summary>
/// macOS: Notification Center through <c>osascript</c>. The text is passed as script arguments, never spliced into the
/// script, so it cannot inject AppleScript. osascript reports no click (one opens Script Editor): the sidebar's mark
/// leads to the chat instead.
/// </summary>
public sealed class OsaScriptNotifier : INotifier
{
    public void Notify(string title, string message, nint windowHandle, Action clicked) =>
        Run.Detached("osascript", ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, message]);
}

/// <summary>Windows: flashes the taskbar button until the window is activated (FlashWindowEx); activating it is the click.</summary>
public sealed class TaskbarFlashNotifier : INotifier
{
    public bool ClickActivatesWindow => true;

    public void Notify(string title, string message, nint windowHandle, Action clicked)
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
    /// <summary>
    /// Starts a helper without a shell and without waiting; a missing tool is silently skipped. On macOS through
    /// posix_spawn: .NET's Process.Start forks this process there, and while a fork of a large process is copied (35–60 ms
    /// in a long session) it holds the Objective-C runtime's and malloc's locks, so the UI thread stalled even when the
    /// start ran on another thread: the "omp finished" notification froze the frame a reply ended in. posix_spawn starts
    /// the helper without copying this process. Elsewhere Process.Start (vfork on Linux), on the thread pool.
    /// </summary>
    public static void Detached(string file, string[] args)
    {
        if (OperatingSystem.IsMacOS())
        {
            SpawnDetached(file, args);
            return;
        }
        _ = Task.Run(() =>
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
        });
    }

    /// <summary>posix_spawnp with this process's environment, the helper's output to /dev/null; the exited helper is
    /// reaped on a background thread (no zombie left).</summary>
    private static void SpawnDetached(string file, string[] args)
    {
        var strings = new List<nint>(args.Length + 1);
        nint actions = 0;
        var hasActions = false;
        try
        {
            // posix_spawn_file_actions_t is a pointer on macOS: stdin, stdout and stderr opened on /dev/null in the helper
            if (PosixSpawnFileActionsInit(ref actions) != 0) return;
            hasActions = true;
            PosixSpawnFileActionsAddOpen(ref actions, 0, "/dev/null", 0 /* O_RDONLY */, 0);
            PosixSpawnFileActionsAddOpen(ref actions, 1, "/dev/null", 1 /* O_WRONLY */, 0);
            PosixSpawnFileActionsAddOpen(ref actions, 2, "/dev/null", 1 /* O_WRONLY */, 0);
            foreach (var s in args.Prepend(file)) strings.Add(Marshal.StringToCoTaskMemUTF8(s));
            var argv = new nint[strings.Count + 1]; // ends with a null pointer
            strings.CopyTo(argv);
            var environment = Marshal.ReadIntPtr(NsGetEnviron());
            if (PosixSpawnP(out var pid, file, ref actions, 0, argv, environment) != 0) return; // not found: skipped
            new Thread(() => WaitPid(pid, out _, 0)) { IsBackground = true, Name = "reap " + file }.Start();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        finally
        {
            if (hasActions) PosixSpawnFileActionsDestroy(ref actions);
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
        }
    }

    [DllImport("libc", EntryPoint = "posix_spawnp")]
    private static extern int PosixSpawnP(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, ref nint fileActions, nint attributes, nint[] argv, nint envp);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int PosixSpawnFileActionsInit(ref nint fileActions);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
    private static extern int PosixSpawnFileActionsAddOpen(ref nint fileActions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int PosixSpawnFileActionsDestroy(ref nint fileActions);

    [DllImport("libc", EntryPoint = "waitpid")]
    private static extern int WaitPid(int pid, out int status, int options);

    /// <summary>Where this process's environment (char**) is: macOS has no <c>environ</c> a library can link to.</summary>
    [DllImport("libc", EntryPoint = "_NSGetEnviron")]
    private static extern nint NsGetEnviron();

    /// <summary>Runs a helper without a shell until it exits: its exit code and what it printed; a null code when it
    /// could not start (a missing tool). Started on the thread pool, off the UI thread.</summary>
    public static async Task<(int? Exit, string Output)> CaptureAsync(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = await Task.Run(() => Process.Start(psi));
            if (p is null) return (null, "");
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, await output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return (null, "");
        }
    }
}
