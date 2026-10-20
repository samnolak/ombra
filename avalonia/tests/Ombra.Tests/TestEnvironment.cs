using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ombra.ClientCore;

namespace Ombra.Tests;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Apply()
    {
        OneTempFolderPerRun();
        KeepChatsOutOfTheUsersDataFolder();
        KeepTeamsOutOfTheUsersDataFolder();
        // The mouse and the accessibility settings on the desk running the tests are not the tests'
        Ombra.App.Platform.SystemPointer.Override = () => null;
        Ombra.App.Platform.ReducedMotion.Override = false;
    }

    /// <summary>
    /// Everything the run puts in the temp folder (<see cref="TestProcesses.TempDir"/>, <see cref="Path.GetTempPath"/>,
    /// the fake omp and every other child process, which inherit the variable) lands in &lt;temp&gt;/ombra-tests/run-&lt;pid&gt;,
    /// deleted when the run exits. A run that was killed leaves its folder: the next run deletes it.
    /// </summary>
    private static void OneTempFolderPerRun()
    {
        var parent = Path.Combine(Path.GetTempPath(), "ombra-tests");
        if (Directory.Exists(parent))
            foreach (var dir in Directory.EnumerateDirectories(parent))
                if (!(Path.GetFileName(dir) is var name && name.StartsWith("run-", StringComparison.Ordinal) && int.TryParse(name[4..], out var pid) && Alive(pid)))
                    TryDelete(dir);
        var run = Directory.CreateDirectory(Path.Combine(parent, "run-" + Environment.ProcessId)).FullName;
        foreach (var variable in OperatingSystem.IsWindows() ? new[] { "TMP", "TEMP" } : ["TMPDIR"])
            Environment.SetEnvironmentVariable(variable, run);
        // TMPDIR may be a repository's .temp folder. Fixtures without their own .git must
        // never discover that enclosing repository; nested fixture repositories still work.
        var ceilings = Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES");
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES",
            string.IsNullOrEmpty(ceilings) ? run : run + Path.PathSeparator + ceilings);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(run);
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Windows refuses read-only files (git objects); a file still open stays until the next run
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Every window a test opens makes chat folders (a new chat, the spare omp's): never in the user's data folder.</summary>
    private static void KeepChatsOutOfTheUsersDataFolder() =>
        Environment.SetEnvironmentVariable(ChatFolders.RootEnvVar, TestProcesses.TempDir("chats"));

    /// <summary>A window that opens the Team page (or reads the team account at start) uses this teams root, never the
    /// user's (~/Library/Application Support/Ombra/teams); a test may still point it at a folder of its own.</summary>
    private static void KeepTeamsOutOfTheUsersDataFolder() =>
        Environment.SetEnvironmentVariable("OMBRA_TEAMS_DIR", TestProcesses.TempDir("teams"));
}
