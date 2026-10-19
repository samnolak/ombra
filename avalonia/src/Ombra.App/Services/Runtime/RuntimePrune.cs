namespace Ombra.App.Services.Runtime;

/// <summary>
/// Removes from a freshly installed npm tree what the engine on this platform never loads, before the host's
/// <c>--self-check</c> runs on the result (the installer's last gate). Every rule names why the files are unreachable;
/// a rule whose evidence no longer holds after a pin change must be re-checked (avalonia/docs/PARITY.md, F3).
/// Measured on darwin-aarch64, 18.8.0: node_modules 951 → 441 MB, the whole pack 1017 → 515 MiB (26,719 → 13,228 files).
/// </summary>
internal static class RuntimePrune
{
    /// <summary>
    /// Whole packages nothing on the engine's Bun path imports:
    /// onnxruntime-web — @huggingface/transformers' "node" export (dist/transformers.node.mjs) inlines the ORT web
    ///   bundle it needs and loads onnxruntime-node for inference; only its browser export imports this package.
    /// lucide-react, react, react-dom, scheduler — dependencies of @oh-my-pi/omp-stats' dashboard sources
    ///   (src/client/*.tsx), which only its build.ts bundles; the server serves the prebuilt dist/client
    ///   (kept current below, so it is never rebuilt).
    /// </summary>
    internal static readonly string[] Packages = ["onnxruntime-web", "lucide-react", "react", "react-dom", "scheduler"];

    /// <summary>Bun platform key → Node's process.platform / process.arch, which pick onnxruntime-node's binding
    /// (dist/binding.js: <c>../bin/napi-v6/${process.platform}/${process.arch}/onnxruntime_binding.node</c>).</summary>
    internal static (string Os, string Arch)? NodeTarget(string platform) => platform.Split('-') switch
    {
        ["darwin", var cpu, ..] => ("darwin", Arch(cpu)),
        ["linux", var cpu, ..] => ("linux", Arch(cpu)),
        ["windows", var cpu, ..] => ("win32", Arch(cpu)),
        _ => null,
    };

    private static string Arch(string cpu) => cpu == "aarch64" ? "arm64" : cpu;

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // node_modules/.bin links are never followed
        IgnoreInaccessible = false,
    };

    /// <summary>Prunes <paramref name="ompDir"/>/node_modules for <paramref name="platform"/>; returns the bytes removed.</summary>
    internal static long Prune(string ompDir, string platform, CancellationToken ct)
    {
        var modules = Path.Combine(ompDir, "node_modules");
        if (!Directory.Exists(modules)) return 0; // nothing installed: the self-check that follows fails
        long removed = 0;
        foreach (var name in Packages) removed += Delete(Path.Combine(modules, name));

        // onnxruntime-node ships prebuilt bindings for every OS/CPU; only process.platform/process.arch is loaded.
        if (NodeTarget(platform) is (string os, string arch))
        {
            foreach (var napi in Dirs(Path.Combine(modules, "onnxruntime-node", "bin")))
            foreach (var osDir in Dirs(napi))
            {
                foreach (var archDir in Dirs(osDir))
                {
                    if (Path.GetFileName(osDir) == os && Path.GetFileName(archDir) == arch)
                    {
                        // macOS: the binding links @rpath/libonnxruntime.1.dylib; the package also carries a byte-identical
                        // copy under its full version name that nothing links.
                        var linked = Path.Combine(archDir, "libonnxruntime.1.dylib");
                        if (File.Exists(linked))
                            foreach (var copy in Directory.EnumerateFiles(archDir, "libonnxruntime.1.*.dylib"))
                                if (SameBytes(copy, linked)) removed += Delete(copy);
                        continue;
                    }
                    removed += Delete(archDir);
                }
                if (!Directory.EnumerateFileSystemEntries(osDir).Any()) Directory.Delete(osDir);
            }
        }

        // Source maps and type declarations are never executed. @oh-my-pi packages are kept whole: their sources
        // import *.d.ts files as text (prelude definitions for the agent's tools).
        foreach (var file in Directory.EnumerateFiles(modules, "*", Walk))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var types = name.EndsWith(".d.ts", StringComparison.Ordinal) || name.EndsWith(".d.mts", StringComparison.Ordinal)
                || name.EndsWith(".d.cts", StringComparison.Ordinal);
            if (!name.EndsWith(".map", StringComparison.Ordinal) && !types) continue;
            if (types && Path.GetRelativePath(modules, file).Replace('\\', '/').StartsWith("@oh-my-pi/", StringComparison.Ordinal)) continue;
            removed += Delete(file);
        }

        // omp-stats rebuilds its dashboard into its own folder when a source is newer than dist/client (server.ts
        // ensureClientBuild); the install writes both at once in no fixed order. The prebuilt dashboard is made the
        // newer, so it is served as shipped and nothing is ever written into the (read-only, bundled) pack.
        var stats = Path.Combine(modules, "@oh-my-pi", "omp-stats");
        var client = Path.Combine(stats, "src", "client");
        var dist = Path.Combine(stats, "dist", "client");
        if (Directory.Exists(client) && Directory.Exists(dist))
        {
            var newest = Directory.EnumerateFiles(client, "*", Walk).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            var stamp = (newest > DateTime.UtcNow ? newest : DateTime.UtcNow).AddMinutes(1);
            foreach (var f in Directory.EnumerateFiles(dist, "*", Walk)) File.SetLastWriteTimeUtc(f, stamp);
        }
        return removed;
    }

    private static IEnumerable<string> Dirs(string dir) => Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : [];

    private static long Delete(string path)
    {
        if (File.Exists(path))
        {
            var size = new FileInfo(path).Length;
            File.Delete(path);
            return size;
        }
        if (!Directory.Exists(path)) return 0;
        var bytes = Directory.EnumerateFiles(path, "*", Walk).Sum(f => new FileInfo(f).Length);
        Directory.Delete(path, recursive: true);
        return bytes;
    }

    private static bool SameBytes(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var x = File.OpenRead(a);
        using var y = File.OpenRead(b);
        var bx = new byte[1 << 16];
        var by = new byte[1 << 16];
        int n;
        while ((n = x.ReadAtLeast(bx, bx.Length, throwOnEndOfStream: false)) > 0)
        {
            if (y.ReadAtLeast(by, n, throwOnEndOfStream: false) != n || !bx.AsSpan(0, n).SequenceEqual(by.AsSpan(0, n))) return false;
        }
        return true;
    }
}
