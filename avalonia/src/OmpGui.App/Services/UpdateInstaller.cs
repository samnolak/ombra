using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OmpGui.App.Services.Runtime;

namespace OmpGui.App.Services;

/// <summary>A verified update unpacked next to the app, waiting for the app to quit.</summary>
public sealed record StagedUpdate(string Version, string Staging, string NewRoot);

/// <summary>How the last update ended, written by the apply script (<c>last-update.json</c>).</summary>
public sealed record UpdateResult(bool Ok, string? From, string? To, string? Error, string? Previous);

/// <summary>
/// Replaces the app with a verified package (<see cref="UpdateChecker"/> checked its signature, size and SHA-256).
/// The package is unpacked into a folder next to the app (same disk, so the swap is two renames) and started once
/// with <c>--version</c>; a small script then waits for the app to quit, moves the running version aside to
/// <c>.&lt;name&gt;.previous</c> (kept for one update, to go back by hand), moves the new one in and starts it again.
/// A failed move puts the old version back. Settings, sessions and the omp runtime live elsewhere and are not touched.
/// Only a packaged app in a folder this user may change is updated in place; otherwise the package is only downloaded.
/// </summary>
public sealed class UpdateInstaller
{
    private const string StagingMarker = ".ompgui-update-staging";
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

    /// <summary>Starts the apply script; replaceable in tests (which run it themselves).</summary>
    public Action<ProcessStartInfo> Launch { get; init; } = psi => Process.Start(psi)?.Dispose();

    /// <summary>Where updates keep their files: <c>%LOCALAPPDATA%/OmpGui/updates</c> (OMPGUI_UPDATES_DIR overrides it).</summary>
    public static string DefaultStateDirectory =>
        Environment.GetEnvironmentVariable("OMPGUI_UPDATES_DIR") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "OmpGui", "updates");

    /// <summary>
    /// The installer for this running app, or null with the reason when it cannot replace itself (a development
    /// build started through <c>dotnet</c>, or a folder this user may not change, such as Program Files).
    /// </summary>
    public static UpdateInstaller? ForThisApp(out string? whyNot)
    {
        whyNot = null;
        var exe = Environment.ProcessPath;
        var dir = exe is null ? null : Path.GetDirectoryName(Path.GetFullPath(exe));
        // A package is a self-contained publish of the OmpGui executable: the .NET runtime sits next to it. A build
        // started with dotnet, or the framework-dependent build output, is not replaced.
        if (exe is null || dir is null || Path.GetFileName(exe) is not ("OmpGui" or "OmpGui.exe")
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
        var staging = Path.Combine(Parent, $".{Name}.update-{SafeName(version)}");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, StagingMarker), "OMP GUI update being installed; safe to delete", ct).ConfigureAwait(false);
        var ok = false;
        try
        {
            var unpacked = Path.Combine(staging, "package");
            Directory.CreateDirectory(unpacked);
            if (package.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || package.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                await using var file = File.OpenRead(package);
                await using var gz = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, unpacked, overwriteFiles: false, ct).ConfigureAwait(false);
            }
            else if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(package, unpacked, overwriteFiles: false), ct).ConfigureAwait(false);
            }
            else throw new InvalidDataException($"unknown package type: {Path.GetFileName(package)}");

            var tops = Directory.GetDirectories(unpacked).Where(d => Path.GetFileName(d) != "__MACOSX").ToList();
            if (tops.Count != 1) throw new InvalidDataException("the package does not hold exactly one app folder");
            var newRoot = tops[0];
            if (IsBundle)
                newRoot = Directory.GetDirectories(newRoot, "*.app").SingleOrDefault()
                          ?? throw new InvalidDataException("the package holds no .app bundle");
            var exe = Path.Combine(newRoot, Executable);
            if (!File.Exists(exe)) throw new InvalidDataException($"the package has no {Executable}");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            // The new version must start on this computer before anything is replaced.
            var check = new ProcessStartInfo(exe) { WorkingDirectory = newRoot };
            check.ArgumentList.Add("--version");
            var result = await Run(check, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !result.Output.Contains(version, StringComparison.Ordinal))
                throw new InvalidDataException($"the new version did not start here (exit {result.ExitCode}): {result.Output.Trim()}");
            ok = true;
            return new StagedUpdate(version, staging, newRoot);
        }
        finally
        {
            if (!ok) TryDelete(staging);
            TryDeleteFile(package);
        }
    }

    /// <summary>
    /// Writes the script that swaps <paramref name="update"/> in once process <paramref name="pid"/> (this app) has
    /// quit, and starts it. With <paramref name="relaunch"/> the new version is started afterwards.
    /// </summary>
    public string Apply(StagedUpdate update, string fromVersion, int pid, bool relaunch, IReadOnlyList<string>? relaunchArgs = null)
    {
        if (!Directory.Exists(update.NewRoot)) throw new IOException("the prepared update is gone; download it again");
        Directory.CreateDirectory(StateDirectory);
        // The version from before the last update, kept until this one: only a folder that holds the app is removed.
        if (Directory.Exists(PreviousDirectory))
        {
            if (!File.Exists(Path.Combine(PreviousDirectory, Executable)))
                throw new IOException($"{PreviousDirectory} is in the way and is not an earlier OMP GUI; move it elsewhere");
            Directory.Delete(PreviousDirectory, recursive: true);
        }
        TryDeleteFile(Path.Combine(StateDirectory, ResultFile));
        var log = Path.Combine(StateDirectory, "apply.log");
        var windows = OperatingSystem.IsWindows();
        var script = Path.Combine(StateDirectory, windows ? "apply-update.ps1" : "apply-update.sh");
        var values = new ScriptValues(pid, InstallRoot, update.NewRoot, update.Staging, PreviousDirectory, Path.Combine(StateDirectory, ResultFile),
            log, Executable, SafeName(fromVersion), SafeName(update.Version), relaunch, relaunchArgs ?? [], OperatingSystem.IsMacOS());
        File.WriteAllText(script, windows ? PowerShellScript(values) : ShellScript(values), new UTF8Encoding(false));
        ProcessStartInfo psi;
        if (windows)
        {
            psi = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = StateDirectory };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script }) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = StateDirectory };
            psi.ArgumentList.Add(script);
        }
        Launch(psi);
        return script;
    }

    /// <summary>The result the last apply script left, removed once read; null when there is none.</summary>
    public static UpdateResult? TakeLastResult(string stateDirectory)
    {
        var file = Path.Combine(stateDirectory, ResultFile);
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
            # Written by OMP GUI: replaces the app with a verified update once the app has quit.
            exec >>{{Q(v.Log)}} 2>&1 </dev/null
            RESULT={{Q(v.Result)}}
            result() { printf '{"ok":%s,"from":"%s","to":"%s","error":%s,"previous":"%s"}\n' "$1" {{Q(v.From)}} {{Q(v.To)}} "$2" {{Q(v.Previous)}} > "$RESULT.tmp" && mv "$RESULT.tmp" "$RESULT"; }
            fail() { echo "$(date) failed: $1"; result false "\"$1\""; exit 1; }
            echo "$(date) updating {{v.From}} -> {{v.To}}: waiting for OMP GUI (pid {{v.Pid}}) to quit"
            i=0
            while kill -0 {{v.Pid}} 2>/dev/null; do
              i=$((i+1)); [ "$i" -gt 600 ] && fail "OMP GUI did not quit within 2 minutes; nothing was changed"
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

    internal static string PowerShellScript(ScriptValues v)
    {
        static string Q(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
        var args = v.Args.Count == 0 ? "" : " -ArgumentList @(" + string.Join(", ", v.Args.Select(a => Q("\"" + a + "\""))) + ")";
        return $$"""
            # Written by OMP GUI: replaces the app with a verified update once the app has quit.
            $ErrorActionPreference = 'Stop'
            $log = {{Q(v.Log)}}
            function Say($m) { Add-Content -LiteralPath $log -Value ("{0} {1}" -f (Get-Date -Format o), $m) }
            function Result($ok, $err) {
              $o = [ordered]@{ ok = $ok; from = {{Q(v.From)}}; to = {{Q(v.To)}}; error = $err; previous = {{Q(v.Previous)}} }
              ($o | ConvertTo-Json -Compress) | Set-Content -LiteralPath ({{Q(v.Result)}} + '.tmp') -Encoding UTF8
              Move-Item -LiteralPath ({{Q(v.Result)}} + '.tmp') -Destination {{Q(v.Result)}} -Force
            }
            function Fail($m) { Say "failed: $m"; Result $false $m; exit 1 }
            function MoveWithRetry($from, $to) {
              for ($i = 0; $i -lt 20; $i++) {
                try { Move-Item -LiteralPath $from -Destination $to; return $true } catch { Start-Sleep -Milliseconds 500 }
              }
              return $false
            }
            Say "updating {{v.From}} -> {{v.To}}: waiting for OMP GUI (pid {{v.Pid}}) to quit"
            try { Wait-Process -Id {{v.Pid}} -Timeout 120 -ErrorAction SilentlyContinue } catch { }
            if (Get-Process -Id {{v.Pid}} -ErrorAction SilentlyContinue) { Fail 'OMP GUI did not quit within 2 minutes; nothing was changed' }
            if (-not (MoveWithRetry {{Q(v.Target)}} {{Q(v.Previous)}})) { Fail 'could not move the current version aside (a file in it is still open); nothing was changed' }
            if (-not (MoveWithRetry {{Q(v.NewRoot)}} {{Q(v.Target)}})) {
              Move-Item -LiteralPath {{Q(v.Previous)}} -Destination {{Q(v.Target)}}
              Fail 'could not move the new version in; the current version was put back'
            }
            Remove-Item -LiteralPath {{Q(v.Staging)}} -Recurse -Force -ErrorAction SilentlyContinue
            Result $true $null
            Say "updated to {{v.To}}; the previous version is in {{v.Previous}}"
            {{(v.Relaunch ? $"Start-Process -FilePath {Q(Path.Combine(v.Target, v.Executable))} -WorkingDirectory {Q(v.Target)}{args}" : "")}}
            exit 0

            """;
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
