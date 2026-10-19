using System.Diagnostics;
using Ombra.ClientCore;

namespace Ombra.App.Services;

/// <summary>
/// The move from the app's former name, OMP GUI (data folders <c>OmpGui</c>, settings file <c>omp-gui.local.json</c>,
/// macOS bundle <c>OMP GUI.app</c>), done once at start by the first Ombra run on a computer that had it.
/// </summary>
internal static class LegacyNames
{
    public const string DataFolder = "OmpGui";
    public const string ConfigFile = "omp-gui.local.json";
    public const string MacBundle = "OMP GUI.app";

    /// <summary>The executable name OMP GUI packages had: what an OMP GUI updater looks for in a package (see
    /// tools/package/package.sh) and what a shortcut made for OMP GUI still starts on Windows and Linux.</summary>
    public static readonly string[] Executables = ["OmpGui", "OmpGui.exe"];

    /// <summary>
    /// Renames each <c>OmpGui</c> data folder (machine-local data, settings) to <c>Ombra</c> and leaves a link with
    /// the old name in its place: omp's sessions recorded the chats' folders, and its config may name the certificate
    /// bundle, by their old paths. Nothing happens when an Ombra folder exists already, or when <c>OMBRA_CONFIG</c> places
    /// the settings elsewhere (the test app and the E2E runs, which must leave the installed app's folders alone).
    /// </summary>
    public static bool MigrateData(Action<string> log)
    {
        if (Environment.GetEnvironmentVariable(OmpRuntimeOptions.ConfigEnvVar) is { Length: > 0 }) return true;
        var roots = new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }
            .Select(f => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify))
            .Where(r => r.Length > 0);
        return MigrateRoots(roots, log, () =>
        {
            // Even the log migration waits for every source/destination admission and source journal lease.
            if (!OperatingSystem.IsMacOS()) return;
            var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs");
            try
            {
                if (Directory.Exists(Path.Combine(logs, "OMP GUI")) && !Path.Exists(Path.Combine(logs, "Ombra")))
                    Directory.Move(Path.Combine(logs, "OMP GUI"), Path.Combine(logs, "Ombra"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
    }

    /// <summary><paramref name="root"/>/OmpGui becomes <paramref name="root"/>/Ombra with a link left at the old path,
    /// and its settings file takes the new name.</summary>
    internal static bool MoveDataFolder(string root, Action<string> log) => MigrateRoots([root], log);

    private static bool NeedsMove(string root)
    {
        var from = Path.Combine(root, DataFolder);
        return Directory.Exists(from) && new DirectoryInfo(from).LinkTarget is null && !Path.Exists(Path.Combine(root, "Ombra"));
    }

    internal static bool MigrateRoots(IEnumerable<string> roots, Action<string> log, Action? beforeMove = null)
    {
        var candidates = roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Where(NeedsMove).Order(StringComparer.Ordinal).ToArray();
        var held = new List<IDisposable>();
        try
        {
            // Nonblocking and ordered: a failed secondary acquisition releases everything, never waits while
            // retaining another identity. Program may already own the destination on this same startup thread.
            var configs = candidates.SelectMany(root => new[]
            {
                Path.Combine(root, DataFolder, ConfigFile),
                Path.Combine(root, "Ombra", "ombra.local.json"),
            }).Order(StringComparer.Ordinal);
            foreach (var config in configs)
            {
                var lease = ClientInstanceLease.TryAcquire(config);
                if (lease is null) return false;
                held.Add(lease);
            }
            foreach (var root in candidates)
                held.Add(DraftStore.AcquireMigrationLease(DraftStore.PathFor(Path.Combine(root, DataFolder, ConfigFile))));
            beforeMove?.Invoke();
            foreach (var root in candidates) MoveDataFolderOwned(root, log);
            return true;
        }
        catch (DraftStoreInUseException) { return false; }
        finally
        {
            for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose();
        }
    }

    private static void MoveDataFolderOwned(string root, Action<string> log)
    {
        var from = Path.Combine(root, DataFolder);
        var to = Path.Combine(root, "Ombra");
        try
        {
            if (!NeedsMove(root)) return;
            var oldDraftName = Path.GetFileName(DraftStore.PathFor(Path.Combine(from, ConfigFile)));
            var newDraftName = Path.GetFileName(DraftStore.PathFor(Path.Combine(to, "ombra.local.json")));
            foreach (var name in new[] { "ombra.local.json", newDraftName, newDraftName + ".lock" })
                if (Path.Exists(Path.Combine(from, name)))
                    throw new IOException($"Cannot migrate {from}: destination filename already exists: {name}");
            Directory.Move(from, to);
            if (!Link(from, to))
            {
                Directory.Move(to, from);
                log($"could not leave a link at {from}; it stays there under its old name");
                throw new IOException($"Could not leave a link at {from}; the data stays under its old name.");
            }
            var config = Path.Combine(to, ConfigFile);
            if (File.Exists(config)) File.Move(config, Path.Combine(to, "ombra.local.json"));
            var draft = Path.Combine(to, oldDraftName);
            if (File.Exists(draft)) File.Move(draft, Path.Combine(to, newDraftName));
            // Move the held inode, never replace/delete it. The destination DraftStore will use the same lease.
            File.Move(draft + ".lock", Path.Combine(to, newDraftName + ".lock"));
            log($"moved {from} to {to}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"could not move {from} to {to}: {e.Message}");
            throw;
        }
    }

    /// <summary>A link at <paramref name="at"/> to the folder <paramref name="target"/>: a junction on Windows (a symbolic
    /// link there needs a privilege most users lack), a symbolic link elsewhere.</summary>
    private static bool Link(string at, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(at, target);
                return true;
            }
            var mklink = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "/c", "mklink", "/J", at, target }) mklink.ArgumentList.Add(a);
            using var p = Process.Start(mklink);
            if (p is null || !p.WaitForExit(10_000)) return false;
            return p.ExitCode == 0 && Directory.Exists(at);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// macOS: an OMP GUI updater installs Ombra under the old bundle's name. This run moves the bundle to
    /// <c>Ombra.app</c> beside it and starts it from there (a running app must not lose its files under it); false when
    /// it stays (another name, Ombra.app taken, a folder this user may not change).
    /// </summary>
    public static bool RenameMacBundleAndRelaunch(IReadOnlyList<string> args, Action<string> log, Action? beforeRelaunch = null)
    {
        if (!OperatingSystem.IsMacOS() || Environment.ProcessPath is not { } exe) return false;
        var bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe)));
        if (bundle is null || Path.GetFileName(bundle) != MacBundle) return false;
        var target = Path.Combine(Path.GetDirectoryName(bundle)!, "Ombra.app");
        if (Path.Exists(target)) return false;
        try
        {
            Directory.Move(bundle, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"could not rename {bundle} to {target}: {e.Message}");
            return false;
        }
        log($"renamed {bundle} to {target}; starting it from there");
        var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        open.ArgumentList.Add("-n");
        open.ArgumentList.Add(target);
        if (args.Count > 0)
        {
            open.ArgumentList.Add("--args");
            foreach (var a in args) open.ArgumentList.Add(a);
        }
        open.Environment.Remove(Ombra.ClientCore.OwnedProcesses.OwnerVariable);
        beforeRelaunch?.Invoke();
        Process.Start(open)?.Dispose();
        return true;
    }
}
