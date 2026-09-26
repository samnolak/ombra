using System.Diagnostics;

namespace OmpGui.App.Services;

/// <summary>A code editor found on this computer, which the Files pane and the project menu can open files in.</summary>
public sealed record EditorApp(string Name, string Executable, EditorApp.Style Syntax)
{
    /// <summary>How the editor takes a line: VS Code and its forks <c>-g file:line</c>, others <c>file:line</c>.</summary>
    public enum Style { Goto, Suffix }

    public string Label => "Open in " + Name;

    /// <summary>The command that opens <paramref name="path"/> (a file at <paramref name="line"/>, or a folder).</summary>
    public ProcessStartInfo Open(string path, int? line = null)
    {
        var psi = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
        if (line is > 0 && Syntax == Style.Goto) { psi.ArgumentList.Add("-g"); psi.ArgumentList.Add($"{path}:{line}"); }
        else if (line is > 0) psi.ArgumentList.Add($"{path}:{line}");
        else psi.ArgumentList.Add(path);
        return psi;
    }
}

/// <summary>
/// Opening things outside the app: code editors found on PATH, the platform's default app for a file, and the file
/// manager (showing the file selected where the platform can).
/// </summary>
public static class FileOpeners
{
    /// <summary>Editors looked for, in this order; only those on PATH are offered.</summary>
    public static readonly IReadOnlyList<(string Name, string Command, EditorApp.Style Syntax)> KnownEditors =
    [
        ("VS Code", "code", EditorApp.Style.Goto),
        ("Cursor", "cursor", EditorApp.Style.Goto),
        ("Windsurf", "windsurf", EditorApp.Style.Goto),
        ("Zed", "zed", EditorApp.Style.Suffix),
        ("Sublime Text", "subl", EditorApp.Style.Suffix),
    ];

    /// <summary>The known editors whose command is on <paramref name="pathVariable"/> (PATH by default).</summary>
    public static List<EditorApp> FindEditors(string? pathVariable = null)
    {
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // A GUI app on macOS starts with a short PATH: the usual places for the editors' shell commands too
        if (OperatingSystem.IsMacOS() && pathVariable is null) dirs.AddRange(["/usr/local/bin", "/opt/homebrew/bin"]);
        var found = new List<EditorApp>();
        foreach (var (name, command, syntax) in KnownEditors)
            if (FindOnPath(command, dirs) is { } exe) found.Add(new EditorApp(name, exe, syntax));
        return found;
    }

    private static string? FindOnPath(string command, IEnumerable<string> dirs)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var dir in dirs)
            foreach (var ext in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(dir, command + ext.ToLowerInvariant()); }
                catch (ArgumentException) { continue; }
                if (IsExecutable(candidate)) return candidate;
            }
        return null;
    }

    private static bool IsExecutable(string file)
    {
        if (!File.Exists(file)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            return (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>What the file manager is called here ("Finder", "Explorer", "file manager").</summary>
    public static string FileManagerName =>
        OperatingSystem.IsMacOS() ? "Finder" : OperatingSystem.IsWindows() ? "Explorer" : "file manager";

    /// <summary>Opens <paramref name="path"/> (file or folder) with the app the system uses for it.</summary>
    public static ProcessStartInfo OpenWithSystem(string path)
    {
        if (OperatingSystem.IsWindows()) return new ProcessStartInfo(path) { UseShellExecute = true };
        var psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(path);
        return psi;
    }

    /// <summary>
    /// Shows <paramref name="path"/> selected in the file manager: Explorer <c>/select</c>, Finder <c>open -R</c>, and on
    /// Linux the freedesktop FileManager1 service (Nautilus, Dolphin, Nemo…); <see cref="RevealFallback"/> opens the
    /// folder when that service is missing.
    /// </summary>
    public static ProcessStartInfo Reveal(string path)
    {
        if (OperatingSystem.IsWindows()) return new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false };
        ProcessStartInfo psi;
        if (OperatingSystem.IsMacOS())
        {
            psi = new ProcessStartInfo("open") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            return psi;
        }
        psi = new ProcessStartInfo("dbus-send") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in (string[])["--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                     "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", "array:string:" + new Uri(path).AbsoluteUri, "string:"])
            psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Where the file manager cannot select a file: its folder, opened.</summary>
    public static ProcessStartInfo RevealFallback(string path) =>
        OpenWithSystem(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);

    /// <summary>
    /// Starts <paramref name="psi"/>; with <paramref name="waitForSuccess"/> it waits (up to 5 s) and reports whether it
    /// exited with 0. False when the program cannot start.
    /// </summary>
    public static async Task<bool> StartAsync(ProcessStartInfo psi, bool waitForSuccess)
    {
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return psi.UseShellExecute; // the shell opened it in an app already running
            if (!waitForSuccess) return true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return false;
        }
    }
}
