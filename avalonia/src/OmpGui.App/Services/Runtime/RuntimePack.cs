using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmpGui.ClientCore;

namespace OmpGui.App.Services.Runtime;

/// <summary>One Bun build on npm: package <c>@oven/bun-&lt;key&gt;</c>, its registry integrity and the executable in the tarball.</summary>
public sealed record BunBuild(string Key, string Integrity, string Exe)
{
    public string Package => "@oven/bun-" + Key;
    public string TarballPath => $"{Package}/-/bun-{Key}-{RuntimePack.BunVersion}.tgz";
}

/// <summary>
/// The pinned runtime pack (<c>runtime/packs/omp-18.8.0-bun-1.4.2</c>): Original OMP Core 18.8.0 from its published npm
/// package, run by Bun 1.4.2. Nothing floats: Bun is checked against the npm registry integrity recorded here, and omp
/// with every dependency comes from the pack's lockfile (<c>bun install --frozen-lockfile</c>, which checks each
/// package's integrity). No lifecycle scripts run; omp is not patched.
/// </summary>
public static class RuntimePack
{
    public const string Name = "omp-18.8.0-bun-1.4.2";
    public const string OmpVersion = "18.8.0";
    public const string BunVersion = "1.4.2";
    public const string CliEntry = "node_modules/@oh-my-pi/pi-coding-agent/src/cli.ts";
    public const string DefaultRegistry = "https://registry.npmjs.org";
    public static readonly IReadOnlyList<string> PackFiles = ["package.json", "bun.lock", "bunfig.toml"];

    /// <summary>npm <c>dist.integrity</c> of <c>@oven/bun-*@1.4.2</c> (read from registry.npmjs.org when pinned).</summary>
    public static readonly IReadOnlyDictionary<string, BunBuild> Bun = new[]
    {
        new BunBuild("linux-x64", "sha512-9/E/UXOTpSo3YsV5g+FhtTd/qTpiWoKuxS12cqtuYA1ssu9fRAoPQnipFgGyck3tWO63iUdxBiygq+kELFawng==", "bin/bun"),
        new BunBuild("linux-x64-baseline", "sha512-OKKzX/SOVIUVARCzTg+2r4Rcc4fPyWnQmBsd3K08buxeu7DAof4iwW9zSb6Lt1NhTCu4Uhi8k9UeI6f7GEE4Zg==", "bin/bun"),
        new BunBuild("linux-aarch64", "sha512-3BBP9ovJ2RGHFH6Ae1CAtxNtG1+YY6GD6rmYbsUosoAk9+OEl6zeDQ/k4fBkc6dYOJCtWnx8hUxzNzQATSmvYQ==", "bin/bun"),
        new BunBuild("darwin-aarch64", "sha512-MXdZkP1featqxZ+/VTXWG1BVjM4OGBehVY2Q88EeUj/7L0UMeCGItmyPYTN+wxvlGJ6F66JEtzsw+GvQWewnag==", "bin/bun"),
        new BunBuild("darwin-x64", "sha512-gZTxZuLjkUhAWjTETu3tw0WhsEdNkJ64daj60ybhPf835a2yollV3yTkK9JozvzKPx4TRFzLSl8C+U525pxVbw==", "bin/bun"),
        new BunBuild("darwin-x64-baseline", "sha512-lVDbk/HkN+RP8QKXBe0aou7EifA2PC9+Z4cvy2VDpmR1ONdDm8e2h/HsU7ABq7mvVs/76iq5H86nMvAhwJDadQ==", "bin/bun"),
        new BunBuild("windows-x64", "sha512-+bN6OuVld/9diT/RLSXSW7JE6CvNE3gL9XsAEjULi1nUsXd6DNO6GuA9jNdNb3r8PdJFnYHr5aypNV1Oj3Rd9g==", "bin/bun.exe"),
        new BunBuild("windows-x64-baseline", "sha512-lq82LqVjSXAx9gNUcibCdfQdtrelh86FxQqlhpOdiV4SZQAP8HHUxWhnfbjx2fqz+jzglwBvjmlEs7xFyONBZw==", "bin/bun.exe"),
        new BunBuild("windows-aarch64", "sha512-8EJ1ST7339WJE3poPW5nBgVW/lWf9HBz4W27ZUNhburKmcBLOByPyE6DP9fHD8FQGm5c+ilUN2hX1mrW0jxq9Q==", "bin/bun.exe"),
    }.ToDictionary(b => b.Key);

    /// <summary>A pack file (package.json, bun.lock, bunfig.toml) as built into the client from <c>runtime/packs</c>.</summary>
    public static Stream OpenPackFile(string name) =>
        typeof(RuntimePack).Assembly.GetManifestResourceStream("runtime-pack/" + name)
        ?? throw new FileNotFoundException("runtime pack file not built into the client", name);

    /// <summary>Machine-level install root (never the omp profile or the client settings); OMPGUI_RUNTIME_DIR overrides it.</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("OMPGUI_RUNTIME_DIR") is { Length: > 0 } root ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "OmpGui", "runtimes");
}

/// <summary>An installed pack: omp is started as <c>bun --no-install cli.ts …</c>.</summary>
public sealed record InstalledRuntime(string Directory, string Bun, string Cli, string Platform)
{
    /// <summary>
    /// Where Bun keeps its runtime transpiler cache. Bun's default is the user's global <c>~/.bun/install/cache</c>;
    /// the pack keeps it in its own folder so the client writes nothing outside its folders.
    /// </summary>
    public const string TranspilerCacheVariable = "BUN_RUNTIME_TRANSPILER_CACHE_PATH";
    public static string TranspilerCacheIn(string packDirectory) => Path.Combine(packDirectory, "transpiler-cache");

    /// <summary>The pack's own <c>bunfig.toml</c>, next to its <c>package.json</c>.</summary>
    public string BunConfig => Path.Combine(Directory, "omp", "bunfig.toml");

    /// <summary>The settings with this runtime filled in where the user left the command empty.</summary>
    public OmpRuntimeOptions ApplyTo(OmpRuntimeOptions o)
    {
        if (o.Command is not null || o.PrefixArgs.Length > 0) return o;
        var env = new Dictionary<string, string?>(o.Environment, o.Environment.Comparer);
        env.TryAdd(TranspilerCacheVariable, TranspilerCacheIn(Directory)); // a value the user set wins
        // Bun reads bunfig.toml (preload scripts) and .env from its working directory, which is the opened project:
        // a folder could run code in omp or redirect its provider traffic. Bun gets the pack's config and no env file;
        // omp still loads its own .env from the profile.
        return o with { Command = Bun, PrefixArgs = ["--config=" + BunConfig, "--no-env-file", "--no-install", Cli], Environment = env };
    }
}

/// <summary>A pack an earlier client version installed, with the omp version it runs.</summary>
public sealed record PreviousRuntime(InstalledRuntime Runtime, string Pack, string Omp, DateTimeOffset InstalledAt);

public sealed record RuntimeInstallProgress(string Stage, long Done = 0, long Total = 0);

/// <summary>Result of a child process the installer runs (bun install, omp --version).</summary>
public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>
/// Installs <see cref="RuntimePack"/> under <c>root/&lt;pack name&gt;</c>. Everything happens in a staging folder that
/// replaces the target only once omp answered with the pinned version; a failure or cancel leaves the previous
/// state as it was and no partial files. Only folders this installer created are ever removed.
/// </summary>
public sealed class RuntimeInstaller
{
    private const long MaxTarballBytes = 300L << 20;
    private const string Marker = "installed.json";
    /// <summary>Written into every folder this installer creates: only such folders are ever removed as leftovers.</summary>
    private const string WorkMarker = ".ompgui-runtime-work";
    private readonly HttpClient _http;
    private readonly string _registry;
    private readonly IReadOnlyDictionary<string, BunBuild> _builds;

    public RuntimeInstaller(HttpClient http, string root, string? platform, string registry = RuntimePack.DefaultRegistry,
        IReadOnlyDictionary<string, BunBuild>? builds = null)
    {
        _http = http;
        Root = root;
        Platform = platform;
        _registry = registry.TrimEnd('/');
        _builds = builds ?? RuntimePack.Bun;
    }

    public string Root { get; }
    public string? Platform { get; }
    public string TargetDirectory => Path.Combine(Root, RuntimePack.Name);
    public bool Supported => Platform is not null && _builds.ContainsKey(Platform);

    /// <summary>Runs a child process to completion; replaceable in tests.</summary>
    public Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessResult>> Run { get; init; } = RunProcessAsync;

    /// <summary>How long the download may go without a byte before it fails as stalled; shorter in tests.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The installed pack, or null when there is none or it is incomplete.</summary>
    public InstalledRuntime? FindInstalled()
    {
        try
        {
            var marker = Path.Combine(TargetDirectory, Marker);
            if (!File.Exists(marker)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            if (doc.RootElement.GetProperty("pack").GetString() != RuntimePack.Name) return null;
            var platform = doc.RootElement.GetProperty("platform").GetString() ?? "";
            var rt = Layout(TargetDirectory, platform);
            return rt is not null && File.Exists(rt.Bun) && File.Exists(rt.Cli) ? rt : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A pack an earlier version of the client installed (another pack name): after an update that brings a new omp,
    /// omp keeps running from it until the new pack is installed. The newest one when there are several.
    /// </summary>
    public PreviousRuntime? FindPrevious() => Previous().OrderByDescending(p => p.InstalledAt).FirstOrDefault();

    /// <summary>Removes the packs earlier versions installed, once this version's pack is installed; returns how many.</summary>
    public int RemovePrevious()
    {
        if (FindInstalled() is null) return 0;
        var removed = 0;
        foreach (var p in Previous().ToList())
        {
            TryDelete(p.Runtime.Directory);
            if (!Directory.Exists(p.Runtime.Directory)) removed++;
        }
        return removed;
    }

    /// <summary>Folders in the root that this installer completed (their <c>installed.json</c> names the folder) for another pack.</summary>
    private IEnumerable<PreviousRuntime> Previous()
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.Exists(Root) ? Directory.EnumerateDirectories(Root).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name == RuntimePack.Name || name.StartsWith('.')) continue;
            PreviousRuntime? found = null;
            try
            {
                var marker = Path.Combine(dir, Marker);
                if (!File.Exists(marker)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                var root = doc.RootElement;
                if (root.GetProperty("pack").GetString() != name) continue;
                var rt = Layout(dir, root.GetProperty("platform").GetString() ?? "");
                if (rt is null || !File.Exists(rt.Bun) || !File.Exists(rt.Cli)) continue;
                var at = root.TryGetProperty("installedAt", out var t) && t.TryGetDateTimeOffset(out var d) ? d : DateTimeOffset.MinValue;
                found = new PreviousRuntime(rt, name, root.TryGetProperty("omp", out var o) ? o.GetString() ?? "?" : "?", at);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (found is not null) yield return found;
        }
    }

    private InstalledRuntime? Layout(string dir, string platform) =>
        _builds.TryGetValue(platform, out var b)
            ? new InstalledRuntime(dir, Path.Combine(dir, "bun", Path.GetFileName(b.Exe)), Path.Combine(dir, "omp", RuntimePack.CliEntry.Replace('/', Path.DirectorySeparatorChar)), platform)
            : null;

    /// <exception cref="NotSupportedException">No pinned Bun build for this platform.</exception>
    /// <exception cref="InvalidDataException">The download did not match its pinned integrity, or omp did not report the pinned version.</exception>
    /// <exception cref="IOException">bun install failed (the message carries the end of its output).</exception>
    public async Task<InstalledRuntime> InstallAsync(IProgress<RuntimeInstallProgress>? progress, CancellationToken ct)
    {
        if (Platform is null || !_builds.TryGetValue(Platform, out var build))
            throw new NotSupportedException($"No pinned Bun {RuntimePack.BunVersion} build for this platform ({Platform ?? "unknown"}).");
        Directory.CreateDirectory(Root);
        // One install at a time per root (two windows, or the app and --self-test): the other one says so.
        FileStream installLock;
        try { installLock = new FileStream(Path.Combine(Root, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("Another installation of the omp runtime is running; try again when it has finished."); }
        using var _ = installLock;
        // Leftovers of an earlier attempt that could not be removed then (a file still in use on Windows) — only
        // folders this installer made (they carry its marker; the root may be a folder the user shares with other things).
        foreach (var stale in Directory.EnumerateDirectories(Root)
                     .Where(d => Path.GetFileName(d) is var n && (n.StartsWith(".staging-", StringComparison.Ordinal) || n.StartsWith(".old-", StringComparison.Ordinal)))
                     .Where(d => File.Exists(Path.Combine(d, WorkMarker))).ToList())
            TryDelete(stale);
        var staging = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, WorkMarker), "created by OMP GUI's runtime installer; safe to delete", ct).ConfigureAwait(false);
        try
        {
            var layout = Layout(staging, Platform)!;
            await DownloadBunAsync(build, layout.Bun, progress, ct).ConfigureAwait(false);

            progress?.Report(new RuntimeInstallProgress($"Installing omp {RuntimePack.OmpVersion} from the pinned lockfile"));
            var ompDir = Path.Combine(staging, "omp");
            Directory.CreateDirectory(ompDir);
            foreach (var name in RuntimePack.PackFiles)
            {
                await using var src = RuntimePack.OpenPackFile(name);
                await using var dst = File.Create(Path.Combine(ompDir, name));
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            var cache = Path.Combine(staging, "cache");
            var install = new ProcessStartInfo(layout.Bun) { WorkingDirectory = ompDir };
            foreach (var a in new[] { "install", "--frozen-lockfile", "--ignore-scripts", "--no-progress" }) install.ArgumentList.Add(a);
            install.Environment["BUN_INSTALL_CACHE_DIR"] = cache; // no writes to the user's global Bun cache
            install.Environment[InstalledRuntime.TranspilerCacheVariable] = InstalledRuntime.TranspilerCacheIn(staging);
            install.Environment["DO_NOT_TRACK"] = "1";
            var result = await Run(install, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, "install.log"), result.Output, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new IOException($"bun install failed (exit {result.ExitCode}):\n{Tail(result.Output, 12)}");
            TryDelete(cache);

            progress?.Report(new RuntimeInstallProgress("Checking omp"));
            var version = new ProcessStartInfo(layout.Bun) { WorkingDirectory = ompDir };
            foreach (var a in new[] { "--no-install", layout.Cli, "--version" }) version.ArgumentList.Add(a);
            // Running omp fills Bun's transpiler cache: in the pack (it moves with it), never in ~/.bun.
            version.Environment[InstalledRuntime.TranspilerCacheVariable] = InstalledRuntime.TranspilerCacheIn(staging);
            var v = await Run(version, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            if (v.ExitCode != 0 || !v.Output.Contains(RuntimePack.OmpVersion, StringComparison.Ordinal))
                throw new InvalidDataException($"omp did not report version {RuntimePack.OmpVersion} (exit {v.ExitCode}):\n{Tail(v.Output, 6)}");

            var marker = JsonSerializer.Serialize(new
            {
                pack = RuntimePack.Name,
                omp = RuntimePack.OmpVersion,
                bun = RuntimePack.BunVersion,
                platform = Platform,
                bunPackage = build.Package,
                bunIntegrity = build.Integrity,
                installedAt = DateTimeOffset.UtcNow,
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(staging, Marker), marker, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Swap in: the old install (ours, by construction of TargetDirectory) moves aside first, so a failed move
            // can be undone, and is deleted only once the new one is in place.
            string? old = null;
            if (Directory.Exists(TargetDirectory))
            {
                old = Path.Combine(Root, ".old-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.Move(TargetDirectory, old);
            }
            try
            {
                Directory.Move(staging, TargetDirectory);
            }
            catch
            {
                if (old is not null) Directory.Move(old, TargetDirectory);
                throw;
            }
            if (old is not null) TryDelete(old);
            progress?.Report(new RuntimeInstallProgress("Installed"));
            return Layout(TargetDirectory, Platform)!;
        }
        finally
        {
            if (Directory.Exists(staging)) TryDelete(staging);
        }
    }

    private async Task DownloadBunAsync(BunBuild build, string exePath, IProgress<RuntimeInstallProgress>? progress, CancellationToken ct)
    {
        var stage = $"Downloading Bun {RuntimePack.BunVersion}";
        progress?.Report(new RuntimeInstallProgress(stage));
        if (!build.Integrity.StartsWith("sha512-", StringComparison.Ordinal)) throw new InvalidDataException("only sha512 integrities are accepted");
        var expected = Convert.FromBase64String(build.Integrity["sha512-".Length..]);
        var tgz = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exePath)!)!, "bun.tgz");
        // The client has no total timeout (a pack is tens of MB on any connection); a connection that stays open and
        // sends nothing would show the last progress forever, and --self-test --install-runtime has no Cancel. Each wait
        // (the headers, then every read) gets StallTimeout; a chunk that arrives restarts it.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await _http.GetAsync($"{_registry}/{build.TarballPath}", HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(tgz, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            var buffer = new byte[1 << 16];
            long written = 0;
            int n;
            stall.CancelAfter(StallTimeout);
            while ((n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                written += n;
                if (written > MaxTarballBytes) throw new InvalidDataException($"{build.Package} is larger than {MaxTarballBytes >> 20} MB");
                sha.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                progress?.Report(new RuntimeInstallProgress(stage, written, total));
            }
            if (!CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), expected))
                throw new InvalidDataException($"{build.Package}@{RuntimePack.BunVersion} does not match its pinned integrity.");
        }
        catch (Exception e) when (e is OperationCanceledException or IOException && stall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The download stalled (no data for {StallTimeout.TotalSeconds:0} s). Check the connection and try again.");
        }

        // Only the verified tarball is opened, and only the Bun executable is taken out of it.
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await using (var file = File.OpenRead(tgz))
        await using (var gz = new GZipStream(file, CompressionMode.Decompress))
        {
            var reader = new TarReader(gz);
            var found = false;
            while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
            {
                if (entry.Name != "package/" + build.Exe || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                await entry.ExtractToFileAsync(exePath, overwrite: false, ct).ConfigureAwait(false); // keeps the executable mode on Unix
                found = true;
                break;
            }
            if (!found) throw new InvalidDataException($"{build.Package} has no {build.Exe}");
        }
        File.Delete(tgz);
    }

    private static string Tail(string text, int lines) =>
        string.Join('\n', text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Runs <paramref name="psi"/> with output captured; the process tree it started is killed on cancel or timeout.</summary>
    public static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception) { }
            // Our own child only; if it cannot be killed, do not wait forever for it.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(psi.FileName)} did not finish within {timeout.TotalMinutes:0} min");
        }
        process.WaitForExit(); // drains the redirected output
        lock (output) return new ProcessResult(process.ExitCode, output.ToString());
    }
}
