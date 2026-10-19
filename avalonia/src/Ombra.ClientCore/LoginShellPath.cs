using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Ombra.ClientCore;

/// <summary>Repairs desktop launch PATH once, before any shared host or tool starts. Only PATH is imported.</summary>
public static class LoginShellPath
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    public const int OutputLimit = 64 * 1024;
    private const string Begin = "\0OMBRA_PATH\0";
    private const string End = "\0OMBRA_END\0";
    private const string Command = "/usr/bin/printf '\\000OMBRA_PATH\\000'; /usr/bin/printenv PATH; /usr/bin/printf '\\000OMBRA_END\\000'";
    public sealed record Result(string? Path, string Diagnostic);
    private static readonly Lazy<Result> Startup = new(() =>
    {
        var known = Environment.GetEnvironmentVariable("PATH");
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return new(known, "PATH recovery not needed on this platform.");
        var result = RecoverAsync(Environment.GetEnvironmentVariable("SHELL"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), known).GetAwaiter().GetResult();
        if (result.Path != known) Environment.SetEnvironmentVariable("PATH", result.Path);
        return result;
    });

    /// <summary>All child processes inherit this base; explicit launch environment overlays still win.</summary>
    public static Result Initialize() => Startup.Value;

    /// <summary>Isolated recovery, also used by regression tests. Never imports shell output other than PATH.</summary>
    public static async Task<Result> RecoverAsync(string? shell, string home, string? knownPath, TimeSpan? timeout = null)
    {
        Result Keep(string reason) => new(knownPath, "Login shell PATH recovery " + reason + "; keeping inherited PATH.");
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return Keep("skipped on this platform");
        if (string.IsNullOrEmpty(shell) || !Path.IsPathFullyQualified(shell) || !Path.IsPathFullyQualified(home) || !Directory.Exists(home))
            return Keep("unavailable");
        using var deadline = new CancellationTokenSource(timeout ?? Timeout);
        using var process = new Process();
        var started = false;
        var outputExceeded = false;
        void LimitExceeded() { outputExceeded = true; deadline.Cancel(); }
        try
        {
            process.StartInfo = CreateStartInfo(shell, home);
            started = process.Start();
            if (!started) return Keep("could not start");
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token, LimitExceeded);
            var error = ReadBoundedAsync(process.StandardError, deadline.Token, LimitExceeded);
            var completion = Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            // Cancellation may win before pipe readers settle; observe their eventual failure without logging output.
            _ = completion.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) return Keep("failed");
            var text = await output.ConfigureAwait(false);
            var begin = text.LastIndexOf(Begin, StringComparison.Ordinal);
            var end = text.LastIndexOf(End, StringComparison.Ordinal);
            if (begin < 0 || end <= begin + Begin.Length) return Keep("returned no PATH");
            var recovered = text[(begin + Begin.Length)..end];
            // printenv adds exactly one newline; embedded newlines and NULs are not PATH records.
            if (recovered.EndsWith('\n')) recovered = recovered[..^1];
            if (recovered.Length == 0 || recovered.IndexOfAny(['\0', '\r', '\n']) >= 0) return Keep("returned an invalid PATH");
            // Shell order first; retain inherited-only entries. Empty components would search a project cwd.
            var paths = recovered.Split(':').Concat((knownPath ?? "").Split(':'))
                .Where(p => p.Length > 0).Distinct(StringComparer.Ordinal);
            return new(string.Join(':', paths), "Recovered login shell PATH.");
        }
        catch (OperationCanceledException) { return Keep(outputExceeded ? "exceeded the output limit" : "timed out after the startup deadline"); }
        catch (InvalidDataException) { return Keep("exceeded the output limit"); }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { return Keep("failed"); }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
    }

    public static ProcessStartInfo CreateStartInfo(string shell, string home)
    {
        var start = new ProcessStartInfo(shell)
        {
            WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-ilc");
        start.ArgumentList.Add(Command);
        // Never follow inherited pointers to project startup files. User login files still run from HOME.
        start.Environment["HOME"] = home;
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
        foreach (var key in new[] { "ENV", "BASH_ENV", "ZDOTDIR" }) start.Environment.Remove(key);
        start.Environment["PWD"] = home;
        start.Environment.Remove("OLDPWD");
        return start;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct, Action limitExceeded)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return text.ToString();
            if (text.Length + count > OutputLimit) { limitExceeded(); throw new InvalidDataException(); }
            text.Append(buffer, 0, count);
        }
    }
}
