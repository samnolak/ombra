using System.Text.Json;

namespace Ombra.App.Services.Runtime;

/// <summary>
/// <c>Ombra --install-engine &lt;root&gt; [--engine-platform key]</c>, run by tools/package/package.sh on the published app:
/// installs the pinned pack into <c>root/&lt;pack&gt;</c> with the client's own installer (pinned Bun integrity, frozen
/// lockfile, owned source artifact, pruning, host <c>--self-check</c>), so the package carries exactly what a user's
/// install would produce. A pack already in root for this pin and platform (a local packaging cache) is checked again
/// with the host's self-check instead of being downloaded again. The platform defaults to this machine's; the osx-x64
/// package is built on an arm64 Mac, where the key must name the package's own CPU.
/// Prints the pack folder and its size as JSON; exit code 0 = ready to bundle.
/// </summary>
public static class EngineBundleCli
{
    public static async Task<int> RunAsync(AppArgs args)
    {
        var root = Path.GetFullPath(args.InstallEngine!);
        var platform = args.EnginePlatform ?? Platform.RuntimePlatform.Key(out _);
        // No BundledRoot: this builds the bundle. Bun inherits the environment (proxy, NODE_EXTRA_CA_CERTS).
        var installer = new RuntimeInstaller(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, root, platform);
        try
        {
            if (!installer.Supported) throw new NotSupportedException($"no pinned Bun build for platform {platform ?? "unknown"}");
            var rt = installer.FindInstalled() is { } found && found.Platform == platform ? found : null;
            if (rt is null)
            {
                var last = "";
                rt = await installer.InstallAsync(new Progress<RuntimeInstallProgress>(p =>
                {
                    if (p.Stage == last) return;
                    last = p.Stage;
                    Console.Error.WriteLine(p.Stage);
                }), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                Console.Error.WriteLine($"reusing {rt.Directory}");
                var check = Path.Combine(Path.GetTempPath(), "ombra-engine-check-" + Guid.NewGuid().ToString("N")[..8]);
                try { await installer.CheckHostAsync(rt, check, CancellationToken.None).ConfigureAwait(false); }
                finally { if (Directory.Exists(check)) Directory.Delete(check, recursive: true); }
            }
            // The bundled pack runs read-only with its cache in the user's runtimes folder (InstalledRuntime.TranspilerCache):
            // the build machine's cache is never read there.
            var cache = InstalledRuntime.TranspilerCacheIn(rt.Directory);
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            long bytes = 0, files = 0;
            foreach (var f in new DirectoryInfo(rt.Directory).EnumerateFiles("*", SearchOption.AllDirectories)) { bytes += f.Length; files++; }
            Console.WriteLine(JsonSerializer.Serialize(new { pack = RuntimePack.Name, platform, directory = rt.Directory, bytes, files }));
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException
                                   or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("engine install failed: " + e.Message);
            return 1;
        }
    }
}
