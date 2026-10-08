namespace OmpGui.App.Services;

/// <summary>
/// The app's own errors, for bug reports: a packaged app has no visible stderr, so every exception that escapes (a UI
/// handler, a background thread, a task nobody awaited) is appended here with a timestamp and its full text. Two files
/// of at most <see cref="MaxBytes"/> each: <c>client.log</c>, and <c>client.1.log</c> before it. Settings → Diagnostics
/// puts both (redacted) into the zip.
/// </summary>
public sealed class ClientLog(string path)
{
    public const long MaxBytes = 1024 * 1024;

    /// <summary>The log of this run (<c>OMPGUI_LOG_DIR</c> overrides the folder).</summary>
    public static ClientLog Shared { get; } = new(System.IO.Path.Combine(DefaultDirectory, "client.log"));

    /// <summary>macOS: <c>~/Library/Logs/OMP GUI</c>, where Console.app lists app logs; elsewhere next to the runtimes
    /// (<c>%LOCALAPPDATA%\OmpGui\logs</c>, <c>~/.local/share/OmpGui/logs</c>).</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("OMPGUI_LOG_DIR") is { Length: > 0 } dir ? dir
        : OperatingSystem.IsMacOS()
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), "Library", "Logs", "OMP GUI")
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "OmpGui", "logs");

    public string Path { get; } = path;
    public string PreviousPath => System.IO.Path.ChangeExtension(Path, ".1.log");

    private readonly Lock _gate = new();

    /// <summary>Appends <paramref name="error"/>; never throws (a log that cannot be written must not add a second failure).</summary>
    public void Write(string source, Exception error) => Append(source, error.ToString());

    /// <summary>Appends a note that is not an exception (what the app did about a condition, such as no display at start).</summary>
    public void Write(string source, string message) => Append(source, message);

    private void Append(string source, string text)
    {
        var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fffzzz} [{source}] {text}\n\n";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length + entry.Length > MaxBytes) File.Move(Path, PreviousPath, overwrite: true);
                File.AppendAllText(Path, entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Both files, oldest first, cut to the last <paramref name="maxChars"/> characters; null when there is none.</summary>
    public string? ReadTail(int maxChars)
    {
        lock (_gate)
        {
            var text = string.Concat(Read(PreviousPath), Read(Path));
            if (text.Length == 0) return null;
            return text.Length <= maxChars ? text : "…" + text[^maxChars..];
        }

        static string Read(string file)
        {
            try { return File.Exists(file) ? File.ReadAllText(file) : ""; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"({System.IO.Path.GetFileName(file)}: {e.Message})\n"; }
        }
    }

    /// <summary>
    /// Errors outside the UI thread: one that ends the process (logged before it does) and a faulted task nobody awaited.
    /// Hooked first thing in Main, so a crash while the app starts is recorded too.
    /// </summary>
    public void HookProcess()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write(e.IsTerminating ? "crash" : "background", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Write("unobserved task", e.Exception);
    }
}
