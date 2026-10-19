using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ombra.ClientCore;

/// <summary>
/// App-owned work stops with the app; explicit independent launches (updates and user-owned apps) are excluded.
/// omp does not stop with the omp process the app talks to: its daemon broker (<c>cli.ts __omp_worker_daemon_broker</c>)
/// runs detached, keeps the background jobs omp starts with mode
/// "persist" or "detached" (dev servers, watchers), and lives on as long as one of them does — across quitting the app
/// and across updates. A broker started by an earlier version then served the chats of the new one, and macOS charged
/// its file access to the earlier version's app (asking again for permissions given to the new one).
/// <para>
/// So the app marks its processes: <see cref="OwnerVariable"/> (the app's own process id) is set in its own
/// environment, which every child inherits — omp, the broker omp spawns, the jobs the broker spawns, terminal tabs.
/// On Unix at quit <see cref="Stop"/> ends every process carrying this app's mark, with all its descendants. At start it ends
/// what an app that is gone left behind: processes marked by a process id that is no longer a running Ombra, and
/// (from versions before the mark) processes running the app's own omp runtime while no other Ombra runs.
/// </para>
/// Windows uses an unnamed, non-inheritable Job Object: the app joins before launching children, and the kernel ends
/// the job when the app's last handle closes, including after a crash. No Windows process enumeration or PID adoption.
/// </summary>
public sealed partial class OwnedProcesses(string runtimeRoot, Func<IReadOnlyList<OwnedProcesses.Info>>? list = null)
{
    public const string OwnerVariable = "OMBRA_OWNER";

    /// <summary>One process of this user: its parent, its command line, the app that marked it (null: no mark).</summary>
    public sealed record Info(int Pid, int ParentPid, string Command, int? Owner);

    public enum Moment { Start, Quit }

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    /// <summary>Establishes crash ownership before any child starts, then marks this process for the Unix reaper.
    /// A Windows Job Object failure aborts startup; launching unprotected children is never a fallback.</summary>
    public static void MarkThisProcess()
    {
        if (OperatingSystem.IsWindows()) EnsureWindowsJob();
        Environment.SetEnvironmentVariable(OwnerVariable, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    private readonly Func<IReadOnlyList<Info>> _list = list ?? ListProcesses;
    private readonly string _runtimeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot)) + Path.DirectorySeparatorChar;

    /// <summary>The processes to end at <paramref name="moment"/> for app process <paramref name="self"/>, with their
    /// descendants; never <paramref name="self"/> or its ancestors.</summary>
    public IReadOnlyList<int> Select(Moment moment, int self)
    {
        var all = _list();
        var byPid = all.ToDictionary(p => p.Pid);
        var apps = all.Where(p => p.Pid != self && IsApp(p)).Select(p => p.Pid).ToHashSet();
        var keep = new HashSet<int>();
        for (var p = self; byPid.TryGetValue(p, out var i) && keep.Add(p);) p = i.ParentPid;
        var roots = all.Where(p => !keep.Contains(p.Pid) && !apps.Contains(p.Pid) && (p.Owner switch
        {
            { } owner when owner == self => moment == Moment.Quit,
            { } owner => !apps.Contains(owner),
            // Unmarked: an earlier version's omp, its broker or a job of it, while no other Ombra could own it
            null => moment == Moment.Start && apps.Count == 0 && p.Command.StartsWith(_runtimeRoot, StringComparison.Ordinal),
        })).Select(p => p.Pid);
        var chosen = new HashSet<int>(roots);
        var children = all.ToLookup(p => p.ParentPid, p => p.Pid);
        var queue = new Queue<int>(chosen);
        while (queue.TryDequeue(out var pid))
            foreach (var child in children[pid])
                if (!keep.Contains(child) && chosen.Add(child)) queue.Enqueue(child);
        return [.. chosen.Order()];
    }

    /// <summary>Ends what <see cref="Select"/> chooses: SIGTERM first, SIGKILL for what still runs after a grace period.
    /// Returns how many processes were asked to end. Never throws.</summary>
    public int Stop(Moment moment)
    {
        // The Windows job includes this app. Closing it here would terminate the remaining shutdown handlers;
        // leave its sole handle to the kernel at process exit (normal exit and forced termination alike).
        if (OperatingSystem.IsWindows()) return 0;
        try
        {
            var pids = Select(moment, Environment.ProcessId);
            if (pids.Count == 0) return 0;
            Signal("TERM", pids);
            var deadline = DateTime.UtcNow + Grace;
            var left = pids;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
                var alive = _list().Select(p => p.Pid).ToHashSet();
                left = [.. left.Where(alive.Contains)];
                if (left.Count == 0) break;
            }
            if (left.Count > 0) Signal("KILL", left);
            return pids.Count;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Stopping leftover processes failed: " + e.Message);
            return 0;
        }
    }

    /// <summary>An Ombra apphost, an older OmpGui apphost, or a development launch through dotnet. Independent
    /// configurations may run together: startup cleanup must not mistake any of their live owners for dead apps.</summary>
    private static bool IsApp(Info p) => AppCommand().IsMatch(p.Command) || DotnetAppCommand().IsMatch(p.Command);

    /// <summary>The executable (spaces allowed, up to an argument or a NAME=value) has a current or legacy app name.</summary>
    [GeneratedRegex(@"^(?:\S|\s(?!-|\w+=))*?(?:^|[/\\])(?:Ombra|OmpGui)(?:\.exe)?(?:\s|$)")]
    private static partial Regex AppCommand();

    [GeneratedRegex(@"^(?:(?:\S|\s(?!-|\w+=))*?[/\\])?dotnet(?:\.exe)?\s+(?:exec\s+)?(?:(?:\S|\s(?!-|\w+=))*?[/\\])?(?:Ombra|OmpGui)\.dll(?:\s|$)")]
    private static partial Regex DotnetAppCommand();

    private static void Signal(string signal, IReadOnlyList<int> pids)
    {
        if (OperatingSystem.IsWindows()) return;
        var psi = new ProcessStartInfo("/bin/kill") { RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-" + signal);
        foreach (var pid in pids) psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
    }

    [GeneratedRegex(@"(?:^|\s)OMBRA_OWNER=(\d+)(?=\s|$)")]
    private static partial Regex OwnerMark();

    /// <summary>This user's processes. macOS: <c>ps -E</c> appends each process's environment to its command line;
    /// Linux: /proc. Windows: none; ownership is the kernel job, never an inferred process list.</summary>
    public static IReadOnlyList<Info> ListProcesses()
    {
        if (OperatingSystem.IsMacOS()) return FromPs();
        if (OperatingSystem.IsLinux()) return FromProc();
        return [];
    }

    private static List<Info> FromPs()
    {
        // Without -a, ps lists only this user's processes; -x adds those without a terminal (omp, brokers, servers)
        var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-x", "-ww", "-E", "-o", "pid=,ppid=,command=" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ps did not start");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        var result = new List<Info>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var ppid)) continue;
            result.Add(new Info(pid, ppid, parts[2], Owner(parts[2])));
        }
        return result;
    }

    private static List<Info> FromProc()
    {
        var result = new List<Info>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                // environ is readable only for this user's processes
                var env = File.ReadAllText(Path.Combine(dir, "environ")).Replace('\0', ' ');
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                result.Add(new Info(pid, int.Parse(fields[1], CultureInfo.InvariantCulture), cmd, Owner(" " + env + " ")));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException) { }
        }
        return result;
    }

    private static int? Owner(string text) =>
        OwnerMark().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var owner) ? owner : null;
}
