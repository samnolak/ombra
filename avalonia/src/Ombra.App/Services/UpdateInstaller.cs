using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ombra.App.Services.Runtime;

namespace Ombra.App.Services;

/// <summary>A verified update unpacked next to the app, waiting for the app to quit.</summary>
public sealed record StagedUpdate(string Version, string Staging, string NewRoot);

/// <summary>How the last update ended, written by the apply helper or script (<c>last-update.json</c>).</summary>
public sealed record UpdateResult(bool Ok, string? From, string? To, string? Error, string? Previous);

/// <summary>
/// Replaces the app with a verified package (<see cref="UpdateChecker"/> checked its signature, size and SHA-256).
/// The package is unpacked into a folder next to the app (same disk, so the swap is two renames) and started once
/// with <c>--version</c>; an app-owned Windows helper (a shell script elsewhere) waits for the app to quit,
/// moves the running version aside to <c>.&lt;name&gt;.previous</c> and moves the new one in and starts it again.
/// A failed move puts the old version back. Settings, sessions and the omp runtime live elsewhere and are not touched.
/// Only a packaged app in a folder this user may change is updated in place; otherwise the package is only downloaded.
/// </summary>
public sealed class UpdateInstaller
{
    private const string StagingMarker = ".ombra-update-staging";
    private const string ResultFile = "last-update.json";

    /// <param name="installRoot">The folder that is replaced: the unpacked package folder, or the <c>.app</c> bundle on macOS.</param>
    /// <param name="executable">The app's executable, relative to <paramref name="installRoot"/>.</param>
    /// <param name="stateDirectory">Where the downloaded package, the script, its log and the result go.</param>
    public UpdateInstaller(string installRoot, string executable, string stateDirectory)
    {
        InstallRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        Executable = executable;
        StateDirectory = stateDirectory;
    }

    public string InstallRoot { get; }
    public string Executable { get; }
    public string StateDirectory { get; }
    private string Parent => Path.GetDirectoryName(InstallRoot)!;
    private string Name => Path.GetFileName(InstallRoot);
    public string PreviousDirectory => Path.Combine(Parent, $".{Name}.previous");
    private bool IsBundle => InstallRoot.EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the staged app once to check it starts here; replaceable in tests.</summary>
    public Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessResult>> Run { get; init; } = RuntimeInstaller.RunProcessAsync;

    /// <summary>Starts the independent apply helper or script; replaceable in tests.</summary>
    public Action<ProcessStartInfo> Launch { get; init; } = psi => Ombra.ClientCore.OwnedProcesses.StartIndependent(psi)?.Dispose();

    /// <summary>Where updates keep their files: <c>%LOCALAPPDATA%/Ombra/updates</c> (OMBRA_UPDATES_DIR overrides it).</summary>
    public static string DefaultStateDirectory =>
        Environment.GetEnvironmentVariable("OMBRA_UPDATES_DIR") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Ombra", "updates");

    /// <summary>
    /// The installer for this running app, or null with the reason when it cannot replace itself (a development
    /// build started through <c>dotnet</c>, or a folder this user may not change, such as Program Files).
    /// </summary>
    public static UpdateInstaller? ForThisApp(out string? whyNot)
    {
        whyNot = null;
        var exe = Environment.ProcessPath;
        var dir = exe is null ? null : Path.GetDirectoryName(Path.GetFullPath(exe));
        // A package is a self-contained publish of the Ombra executable: the .NET runtime sits next to it. A build
        // started with dotnet, or the framework-dependent build output, is not replaced.
        if (exe is null || dir is null || (Path.GetFileName(exe) is not ("Ombra" or "Ombra.exe") && !LegacyNames.Executables.Contains(Path.GetFileName(exe)))
            || !new[] { "libcoreclr.so", "libcoreclr.dylib", "coreclr.dll" }.Any(f => File.Exists(Path.Combine(dir, f))))
        {
            whyNot = "this copy runs from a development build, not from a package";
            return null;
        }
        var installer = OperatingSystem.IsMacOS() && dir.EndsWith(Path.Combine(".app", "Contents", "MacOS"), StringComparison.OrdinalIgnoreCase)
            ? new UpdateInstaller(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, Path.Combine("Contents", "MacOS", Path.GetFileName(exe)), DefaultStateDirectory)
            : new UpdateInstaller(dir, Path.GetFileName(exe), DefaultStateDirectory);
        if (!installer.CanWrite())
        {
            whyNot = $"this user cannot change the folder the app is in ({installer.Parent})";
            return null;
        }
        return installer;
    }

    /// <summary>Whether this user may create and rename folders next to the app (what the swap does).</summary>
    public bool CanWrite()
    {
        var probe = Path.Combine(Parent, $".{Name}.write-check-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return File.Exists(Path.Combine(InstallRoot, Executable));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks <paramref name="package"/> next to the app and checks it: one package folder, the app's executable in
    /// it, and that executable reports <paramref name="version"/>. The package file is deleted afterwards.
    /// </summary>
    /// <exception cref="InvalidDataException">The package does not hold an app of this version.</exception>
    public async Task<StagedUpdate> StageAsync(string package, string version, CancellationToken ct)
    {
        RemoveLeftovers();
        var staging = Path.Combine(Parent, $".{Name}.update-{SafeName(version)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, StagingMarker), "Ombra update being installed; safe to delete", ct).ConfigureAwait(false);
        var ok = false;
        try
        {
            var unpacked = Path.Combine(staging, "package");
            Directory.CreateDirectory(unpacked);
            await UnpackAsync(package, unpacked, ct).ConfigureAwait(false);

            var tops = Directory.GetDirectories(unpacked).Where(d => Path.GetFileName(d) != "__MACOSX").ToList();
            if (tops.Count != 1) throw new InvalidDataException("the package does not hold exactly one app folder");
            var newRoot = tops[0];
            if (IsBundle)
                newRoot = Directory.GetDirectories(newRoot, "*.app").SingleOrDefault()
                          ?? throw new InvalidDataException("the package holds no .app bundle");
            var exe = Path.Combine(newRoot, Executable);
            if (!File.Exists(exe)) throw new InvalidDataException($"the package has no {Executable}");
            if (IsBundle && OperatingSystem.IsMacOS())
                await VerifyCodeSignatureAsync(newRoot, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            // The new version must start on this computer before anything is replaced.
            var check = new ProcessStartInfo(exe) { WorkingDirectory = newRoot };
            check.ArgumentList.Add("--version");
            var result = await Run(check, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !result.Output.Contains(version, StringComparison.Ordinal))
                throw new InvalidDataException($"the new version did not start here (exit {result.ExitCode}): {result.Output.Trim()}");
            var staged = new StagedUpdate(version, staging, newRoot);
            await File.WriteAllTextAsync(Path.Combine(staging, UpdateApplyHelper.ReadyMarker), JsonSerializer.Serialize(staged), ct).ConfigureAwait(false);
            ok = true;
            return staged;
        }
        finally
        {
            if (!ok) TryDelete(staging);
            TryDeleteFile(package);
        }
    }

    /// <summary>
    /// Unpacks a <c>.tar.gz</c> or <c>.zip</c> package into <paramref name="destination"/>. On macOS a zip is unpacked
    /// with <c>ditto</c>: the release zip carries the code signature of the bundle's non-Mach-O files (the .dll files)
    /// in extended attributes stored as AppleDouble entries, which <see cref="ZipFile"/> drops, breaking the signature.
    /// </summary>
    internal static async Task UnpackAsync(string package, string destination, CancellationToken ct)
    {
        if (package.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || package.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await using var file = File.OpenRead(package);
            await using var gz = new GZipStream(file, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gz, destination, overwriteFiles: false, ct).ConfigureAwait(false);
        }
        else if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("/usr/bin/ditto");
                foreach (var a in new[] { "-x", "-k", package, destination }) psi.ArgumentList.Add(a);
                var r = await RuntimeInstaller.RunProcessAsync(psi, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                if (r.ExitCode != 0) throw new InvalidDataException($"could not unpack the package (ditto exit {r.ExitCode}): {r.Output.Trim()}");
            }
            else await Task.Run(() => ZipFile.ExtractToDirectory(package, destination, overwriteFiles: false), ct).ConfigureAwait(false);
        }
        else throw new InvalidDataException($"unknown package type: {Path.GetFileName(package)}");
    }

    /// <summary>Refuses an unpacked macOS bundle whose code signature does not verify (macOS would deny its permissions).</summary>
    internal static async Task VerifyCodeSignatureAsync(string bundle, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/usr/bin/codesign");
        foreach (var a in new[] { "--verify", "--deep", "--strict", bundle }) psi.ArgumentList.Add(a);
        var r = await RuntimeInstaller.RunProcessAsync(psi, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new InvalidDataException($"the new version's code signature is not valid, so it was not installed: {r.Output.Trim()}");
    }

    /// <summary>
    /// Starts the helper (Windows) or writes the script that swaps <paramref name="update"/> in once
    /// process <paramref name="pid"/> has quit. With <paramref name="relaunch"/> the new version is started afterwards.
    /// </summary>
    public string Apply(StagedUpdate update, string fromVersion, int pid, bool relaunch, IReadOnlyList<string>? relaunchArgs = null)
    {
        if (!Directory.Exists(update.NewRoot)) throw new IOException("the prepared update is gone; download it again");
        if (OperatingSystem.IsWindows())
            return UpdateApplyHelper.Prepare(this, update, fromVersion, pid, relaunch, relaunchArgs ?? [], Launch);
        Directory.CreateDirectory(StateDirectory);
        // The version from before the last update, kept until this one: only a folder that holds the app is removed.
        if (Directory.Exists(PreviousDirectory))
        {
            if (!File.Exists(Path.Combine(PreviousDirectory, Executable)))
                throw new IOException($"{PreviousDirectory} is in the way and is not an earlier Ombra; move it elsewhere");
            Directory.Delete(PreviousDirectory, recursive: true);
        }
        TryDeleteFile(Path.Combine(StateDirectory, ResultFile));
        var log = Path.Combine(StateDirectory, "apply.log");
        var script = Path.Combine(StateDirectory, "apply-update.sh");
        var values = new ScriptValues(pid, InstallRoot, update.NewRoot, update.Staging, PreviousDirectory, Path.Combine(StateDirectory, ResultFile),
            log, Executable, SafeName(fromVersion), SafeName(update.Version), relaunch, relaunchArgs ?? [], OperatingSystem.IsMacOS());
        File.WriteAllText(script, ShellScript(values), new UTF8Encoding(false));
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = StateDirectory };
        psi.ArgumentList.Add(script);
        // The script outlives the app on purpose (it waits for it to quit), as does the replacement it starts.
        // Launch uses explicit Windows job breakaway; removing the Unix mark alone would not escape a Job Object.
        psi.Environment.Remove(Ombra.ClientCore.OwnedProcesses.OwnerVariable);
        Launch(psi);
        return script;
    }

    /// <summary>The result the last apply script left, removed once read; null when there is none.</summary>
    public static UpdateResult? TakeLastResult(string stateDirectory)
    {
        var file = Path.Combine(stateDirectory, ResultFile);
        UpdateApplyHelper.CleanupCompleted(stateDirectory);
        try
        {
            if (!File.Exists(file)) return null;
            var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            File.Delete(file);
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Unpacked updates never applied (the app was not closed, or the check failed): only folders with our marker.</summary>
    public void RemoveLeftovers()
    {
        UpdateApplyHelper.CleanupCompleted(StateDirectory);
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Parent, $".{Name}.update-*").ToList())
                if (File.Exists(Path.Combine(d, StagingMarker))) TryDelete(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal sealed record ScriptValues(int Pid, string Target, string NewRoot, string Staging, string Previous, string Result, string Log,
        string Executable, string From, string To, bool Relaunch, IReadOnlyList<string> Args, bool MacOs);

    internal static string ShellScript(ScriptValues v)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var args = string.Join(' ', v.Args.Select(Q));
        var start = v.MacOs
            ? $"open {Q(v.Target)}" + (args.Length > 0 ? " --args " + args : "")
            : $"nohup {Q(Path.Combine(v.Target, v.Executable))} {args} >/dev/null 2>&1 &";
        return $$"""
            #!/bin/sh
            # Written by Ombra: replaces the app with a verified update once the app has quit.
            exec >>{{Q(v.Log)}} 2>&1 </dev/null
            RESULT={{Q(v.Result)}}
            result() { printf '{"ok":%s,"from":"%s","to":"%s","error":%s,"previous":"%s"}\n' "$1" {{Q(v.From)}} {{Q(v.To)}} "$2" {{Q(v.Previous)}} > "$RESULT.tmp" && mv "$RESULT.tmp" "$RESULT"; }
            fail() { echo "$(date) failed: $1"; result false "\"$1\""; exit 1; }
            echo "$(date) updating {{v.From}} -> {{v.To}}: waiting for Ombra (pid {{v.Pid}}) to quit"
            i=0
            while kill -0 {{v.Pid}} 2>/dev/null; do
              i=$((i+1)); [ "$i" -gt 600 ] && fail "Ombra did not quit within 2 minutes; nothing was changed"
              sleep 0.2 2>/dev/null || sleep 1
            done
            mv {{Q(v.Target)}} {{Q(v.Previous)}} || fail "could not move the current version aside; nothing was changed"
            if ! mv {{Q(v.NewRoot)}} {{Q(v.Target)}}; then
              mv {{Q(v.Previous)}} {{Q(v.Target)}}
              fail "could not move the new version in; the current version was put back"
            fi
            rm -rf {{Q(v.Staging)}}
            result true null
            echo "$(date) updated to {{v.To}}; the previous version is in {{v.Previous}}"
            {{(v.Relaunch ? $"cd {Q(Path.GetDirectoryName(v.Target)!)} && {start}" : "")}}
            exit 0

            """.Replace("\r\n", "\n", StringComparison.Ordinal);
    }


    private static string SafeName(string version) =>
        new(version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string file)
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
