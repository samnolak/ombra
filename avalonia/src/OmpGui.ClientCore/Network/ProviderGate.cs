using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmpGui.Rpc;

namespace OmpGui.ClientCore.Network;

/// <summary>The addresses one provider uses: its API (model base URLs) and its sign-in (token, device and account endpoints).</summary>
public sealed record ProviderHosts(IReadOnlyList<string> Api, IReadOnlyList<string> SignIn);

/// <summary>
/// What <see cref="ProviderGate"/> found for one omp launch: the providers the user added (a stored sign-in, an API key in
/// the environment or omp's settings, a <c>models.yml</c> entry), the user's own <c>disabledProviders</c>, the list omp
/// gets, and the addresses of every provider omp knows (strict network privacy allows those of the added ones).
/// </summary>
public sealed record ProviderGateResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> UserDisabled,
    IReadOnlyList<string> Disabled,
    IReadOnlyDictionary<string, ProviderHosts> Hosts);

/// <summary>Why omp was not started: the gate could not tell which providers the user added.</summary>
public sealed class ProviderGateException(string message) : Exception(message);

/// <summary>
/// Keeps omp from contacting model providers the user has not added. omp 18.8.0 fetches public model lists from every
/// provider it knows, signed in or not, and probes local model servers (UPSTREAM OMP ISSUE: docs/upstream/
/// omp-discovery-opt-in.md); its <c>disabledProviders</c> setting stops that per provider. Before each start the app runs
/// a small script on omp's own Bun runtime that asks omp's modules which providers exist and which ones the user added,
/// then passes the rest to omp as <c>disabledProviders</c> in a <c>--config</c> overlay (omp's documented per-run
/// settings layer): the user's config.yml is never written, and their own <c>disabledProviders</c> are kept.
/// UNDOCUMENTED / VERSION-PINNED: the script imports omp 18.8.0 package modules and reads its catalog rules
/// (<c>pi-catalog/src/compat/rules</c>); RealProviderGateTests checks it against the pinned runtime.
/// </summary>
public static class ProviderGate
{
    /// <summary>Set for the script: the project folder whose <c>.omp/config.yml</c> also counts (never on its command line).</summary>
    public const string CwdVariable = "OMPGUI_GATE_CWD";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string CliEntry = "node_modules/@oh-my-pi/pi-coding-agent/src/cli.ts";

    /// <summary>
    /// Prints one JSON line: <c>{"added":[…],"userDisabled":[…],"disabled":[…],"hosts":{"id":{"api":[…],"signIn":[…]}}}</c>.
    /// Offline: it reads omp's credential store, settings and bundled catalog; nothing is fetched.
    /// </summary>
    public const string Script = """
        import * as fs from "node:fs";
        import * as path from "node:path";
        import { discoverAuthStorage } from "@oh-my-pi/pi-coding-agent/sdk";
        import { Settings } from "@oh-my-pi/pi-coding-agent/config/settings";
        import { cfgDisabledProviders } from "@oh-my-pi/pi-coding-agent/config/model-settings";
        import { getAgentDir } from "@oh-my-pi/pi-utils";
        import { PROVIDER_DESCRIPTORS } from "@oh-my-pi/pi-catalog/provider-models/descriptors";
        import { MODELS_DEV_CATALOG_PROVIDER_IDS } from "@oh-my-pi/pi-catalog/provider-models/openai-compat";
        import { getBundledModels, getBundledProviders } from "@oh-my-pi/pi-catalog/models";

        const cwd = process.env.OMPGUI_GATE_CWD || process.cwd();
        const agentDir = getAgentDir();
        // Every provider omp may contact on its own: discovery descriptors, models.dev catalog slices, bundled
        // catalogs, and the local servers it probes (ollama, llama.cpp, LM Studio). Not the built-ins that fetch no
        // model list: on-device inference (local), web search engines (web), Apple's on-device models (apple).
        const universe = new Set([
          ...PROVIDER_DESCRIPTORS.map(d => d.providerId),
          ...MODELS_DEV_CATALOG_PROVIDER_IDS,
          ...getBundledProviders(),
          "ollama", "llama.cpp", "lm-studio",
        ]);
        for (const p of ["local", "web", "apple"]) universe.delete(p);

        const added = new Set();
        const auth = await discoverAuthStorage(agentDir, { cwd });
        for (const p of universe) if (auth.keys.source(p) !== undefined) added.add(p);

        const hosts = new Map();
        const entry = p => {
          let e = hosts.get(p);
          if (!e) hosts.set(p, (e = { api: new Set(), signIn: new Set() }));
          return e;
        };
        // Loopback addresses (OAuth callbacks, local servers) never go through a proxy, so they are left out.
        const hostOf = url => {
          try {
            const u = new URL(url);
            const h = u.hostname.toLowerCase();
            if (u.protocol !== "https:" && u.protocol !== "http:") return undefined;
            return h === "localhost" || h.startsWith("127.") || h === "[::1]" ? undefined : h;
          } catch {
            return undefined;
          }
        };
        for (const p of universe) {
          for (const m of getBundledModels(p)) {
            const h = hostOf(m.baseUrl);
            if (h) entry(p).api.add(h);
          }
        }

        const rules = path.join(path.dirname(Bun.resolveSync("@oh-my-pi/pi-catalog/models", import.meta.dir)), "compat", "rules");
        for (const kind of ["providers", "auth"]) {
          for (const f of fs.readdirSync(path.join(rules, kind)).filter(f => f.endsWith(".kdl"))) {
            const text = fs.readFileSync(path.join(rules, kind, f), "utf8").replace(/^\s*\/\/.*$/gm, "");
            const id = text.match(kind === "auth" ? /^auth "([^"]+)"/m : /^provider "([^"]+)"/m)?.[1];
            if (!id) continue;
            // A sign-in variant (openai-codex-device) stores its credential as another provider (store-as).
            const ids = kind === "auth" ? [id, text.match(/\bstore-as "([^"]+)"/)?.[1] ?? id] : [id];
            for (const m of text.matchAll(/https?:\/\/[A-Za-z0-9.-]+/g)) {
              const h = hostOf(m[0]);
              if (h) for (const owner of ids) (kind === "auth" ? entry(owner).signIn : entry(owner).api).add(h);
            }
          }
        }

        const modelsYml = path.join(agentDir, "models.yml");
        if (fs.existsSync(modelsYml)) {
          const doc = Bun.YAML.parse(fs.readFileSync(modelsYml, "utf8"));
          for (const [p, cfg] of Object.entries(doc?.providers ?? {})) {
            added.add(p);
            const h = hostOf(cfg?.baseUrl);
            if (h) entry(p).api.add(h);
          }
        }

        const settings = await Settings.loadReadOnly({ cwd });
        const userDisabled = [...new Set(cfgDisabledProviders.get(settings).filter(p => typeof p === "string"))];
        const disabled = new Set(userDisabled);
        for (const p of universe) if (!added.has(p)) disabled.add(p);

        const out = {};
        for (const [p, e] of hosts) out[p] = { api: [...e.api].sort(), signIn: [...e.signIn].sort() };
        console.log(JSON.stringify({ added: [...added].sort(), userDisabled: userDisabled.sort(), disabled: [...disabled].sort(), hosts: out }));
        """;

    /// <summary>
    /// The script's launch for <paramref name="engine"/> (an omp launch): omp's Bun runtime, its bunfig, the launch's
    /// environment (profile, keys) and project folder. Null when omp is not run as <c>bun … cli.ts</c> from a package
    /// install (a compiled omp, a wrapper): then there are no omp modules to ask.
    /// </summary>
    public static OmpLaunchSpec? HelperSpec(OmpLaunchSpec engine)
    {
        if (!Path.GetFileNameWithoutExtension(engine.FileName).Equals("bun", StringComparison.OrdinalIgnoreCase)) return null;
        var cli = engine.Arguments.ToList().FindIndex(a => a.Replace('\\', '/').EndsWith(CliEntry, StringComparison.Ordinal));
        if (cli < 0) return null;
        var entry = engine.Arguments[cli];
        var ompDirectory = entry[..^CliEntry.Length].TrimEnd('/', '\\');
        // Bun's own options come before cli.ts; omp's (a --config overlay among them) after it
        var args = engine.Arguments.Take(cli).Where(a => a.StartsWith("--config=", StringComparison.Ordinal)).ToList();
        args.AddRange(["--no-env-file", "-e", Script]);
        var env = new Dictionary<string, string?>(engine.Environment)
        {
            [CwdVariable] = engine.WorkingDirectory ?? System.Environment.CurrentDirectory,
        };
        return new OmpLaunchSpec { FileName = engine.FileName, Arguments = args, WorkingDirectory = ompDirectory, Environment = env };
    }

    /// <summary>Runs the script for <paramref name="engine"/>.</summary>
    /// <exception cref="ProviderGateException">The script could not run or gave no answer.</exception>
    public static async Task<ProviderGateResult> ComputeAsync(OmpLaunchSpec engine, CancellationToken ct = default)
    {
        var spec = HelperSpec(engine) ?? throw new ProviderGateException("omp is not started from the app's omp runtime.");
        var start = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        foreach (var a in spec.Arguments) start.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment)
            if (v is null) start.Environment.Remove(k);
            else start.Environment[k] = v;
        using var p = new Process { StartInfo = start };
        try { p.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProviderGateException("omp's runtime could not be started: " + e.Message);
        }
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try { await p.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new ProviderGateException($"the check took longer than {Timeout.TotalSeconds:0} s.");
        }
        var text = await output.ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var reason = (await error.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? $"exit code {p.ExitCode}";
            throw new ProviderGateException(reason);
        }
        return Parse(text);
    }

    /// <summary>The script's JSON line.</summary>
    /// <exception cref="ProviderGateException">No well-formed answer.</exception>
    public static ProviderGateResult Parse(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'))
            ?? throw new ProviderGateException("the check gave no answer.");
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var hosts = new Dictionary<string, ProviderHosts>(StringComparer.Ordinal);
            foreach (var p in root.GetProperty("hosts").EnumerateObject())
                hosts[p.Name] = new ProviderHosts(Strings(p.Value.GetProperty("api")), Strings(p.Value.GetProperty("signIn")));
            return new ProviderGateResult(Strings(root.GetProperty("added")), Strings(root.GetProperty("userDisabled")), Strings(root.GetProperty("disabled")), hosts);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ProviderGateException("the check's answer was not understood.");
        }

        static string[] Strings(JsonElement a) => [.. a.EnumerateArray().Select(x => x.GetString() ?? throw new InvalidOperationException())];
    }

    /// <summary>
    /// Writes the overlay for one (profile, project) pair into <paramref name="directory"/> and returns its path. The same
    /// pair always gets the same file, so the folder holds one small file per project, rewritten at every start.
    /// </summary>
    public static string WriteOverlay(ProviderGateResult result, string directory, string key)
    {
        Directory.CreateDirectory(directory);
        var name = "providers-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".yml";
        var path = Path.Combine(directory, name);
        var yaml = new StringBuilder("# Written by OMP GUI before omp starts (Settings › Advanced › Network privacy): the providers you have\n")
            .Append("# not added, plus your own disabledProviders. omp reads it with --config; your config.yml is unchanged.\n")
            .Append("disabledProviders:\n");
        foreach (var p in result.Disabled) yaml.Append("  - ").Append(JsonSerializer.Serialize(p)).Append('\n');
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, yaml.ToString());
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary><paramref name="spec"/> with <c>--config=&lt;overlay&gt;</c> right after <paramref name="prefixCount"/> arguments (the runtime's own, e.g. <c>bun … cli.ts</c>).</summary>
    public static OmpLaunchSpec Apply(OmpLaunchSpec spec, string overlayPath, int prefixCount)
    {
        var args = spec.Arguments.ToList();
        args.Insert(Math.Min(prefixCount, args.Count), "--config=" + overlayPath);
        return spec with { Arguments = args };
    }
}
