// Shared by the native feature smoke (feature-smoke/feature-smoke.targets compiles it into that build of Ombra.App) and
// by Ombra.Tests (its project links it): a harness that starts a real omp gives it a home and agent folder of its own,
// and keeps the app's chats and teams out of the user's data folder.
namespace Ombra.Verify;

/// <summary>
/// Refuses a test or smoke run whose omp would write the user's own sessions (<c>~/.omp/agent/sessions</c>, listed by
/// the user's Ombra as chats) or whose chats and teams would land in the user's Ombra data folder. A settings file the
/// app could not read once ran a smoke on the defaults: the user's omp profile and folders, real model included.
/// </summary>
internal static class HarnessIsolation
{
    private static readonly StringComparison Paths = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The user's home: this process's, and on macOS the account's own (a harness may run with HOME moved).</summary>
    private static IEnumerable<string> UserHomes()
    {
        var homes = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify) };
        if (OperatingSystem.IsMacOS()) homes.Add(Path.Combine("/Users", Environment.UserName));
        return homes.Where(h => h.Length > 0).Select(Full).Distinct(StringComparer.FromComparison(Paths));
    }

    /// <summary>The user's Ombra data and settings folders (chats, teams, runtimes, settings).</summary>
    private static IEnumerable<string> UserDataFolders() =>
        new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }
            .Select(f => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify))
            .Where(d => d.Length > 0)
            .Select(d => Full(Path.Combine(d, "Ombra")))
            .Distinct(StringComparer.FromComparison(Paths));

    /// <summary>
    /// What is not isolated; empty when the run may start. <paramref name="launchEnvironment"/> is what the app sets for
    /// omp (a key with a null value is removed), over <paramref name="inherited"/>; <paramref name="chatsRoot"/> and
    /// <paramref name="teamsRoot"/> are the app's folders for this run (null: not used by the harness).
    /// </summary>
    public static List<string> Problems(IReadOnlyDictionary<string, string?> launchEnvironment, Func<string, string?> inherited,
        string? chatsRoot, string? teamsRoot)
    {
        string? Effective(string key) => launchEnvironment.TryGetValue(key, out var v) ? v : inherited(key);
        var problems = new List<string>();
        var homes = UserHomes().ToList();
        var userOmp = homes.Select(h => Path.Combine(h, ".omp")).ToList();
        var data = UserDataFolders().ToList();
        var homeKey = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        var home = Effective(homeKey);
        if (string.IsNullOrEmpty(home))
            problems.Add($"omp gets no {homeKey} of its own (it would use the user's {homes.FirstOrDefault()})");
        else if (homes.Any(h => Same(home, h)) || userOmp.Any(o => Within(home, o)))
            problems.Add($"omp's {homeKey} is the user's own ({home}): set \"{homeKey}\" in the settings' \"environment\" to a folder of the run");
        else
        {
            var agent = Effective("PI_CODING_AGENT_DIR") is { Length: > 0 } a ? a : Path.Combine(home, ".omp", "agent");
            if (userOmp.Any(o => Within(agent, o)) || data.Any(d => Within(agent, d)))
                problems.Add($"omp's agent folder is the user's ({agent}): its sessions would show in the user's Ombra");
        }
        foreach (var (what, root) in new[] { ("chats", chatsRoot), ("teams", teamsRoot) })
            if (root is not null && (root.Length == 0 || data.Any(d => Within(root, d)) || homes.Any(h => Same(root, h))))
                problems.Add($"the app's {what} folder is the user's ({(root.Length == 0 ? "unset" : root)})");
        return problems;
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), Paths);

    private static bool Within(string path, string root)
    {
        var p = Full(path);
        var r = Full(root);
        return string.Equals(p, r, Paths) || p.StartsWith(r + Path.DirectorySeparatorChar, Paths);
    }
}
