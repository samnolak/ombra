using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using OmpGui.Rpc;

namespace OmpGui.ClientCore.Network;

public enum ConnectionCheckKind { Works, Tls, Dns, Timeout, Blocked, ProxyAuth, Http, Failed, Unavailable }

/// <summary>An outcome of <see cref="ConnectionCheck"/>: the class, the code (a status number or an error code, never a
/// name or address: safe for logs) and the sentence the page shows.</summary>
public sealed record ConnectionCheckResult(ConnectionCheckKind Kind, string Code, string Message)
{
    /// <summary>"check: tls SELF_SIGNED_CERT_IN_CHAIN": what a log may say about a check.</summary>
    public string LogLine => "check: " + Kind.ToString().ToLowerInvariant() + " " + Code;
}

/// <summary>
/// One HTTPS request without a key, made by the engine's own runtime: the Bun binary omp runs on and the environment
/// of an omp launch (so the corporate-trust variables are exactly the engine's). A tiny inline script does a plain
/// <c>fetch</c> (no headers, no body, redirects not followed, 10 s); the answer is a status or an error code, classified
/// here. Any HTTP status means HTTPS got through; it says nothing about keys or models.
/// </summary>
public static class ConnectionCheck
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Set by the script: the address to request (never on its command line).</summary>
    public const string UrlVariable = "OMPGUI_CHECK_URL";

    public const string Script =
        "const u = process.env." + UrlVariable + ";\n"
        + "try {\n"
        + "  const r = await fetch(u, { redirect: \"manual\", signal: AbortSignal.timeout(10000) });\n"
        + "  console.log(JSON.stringify({ status: r.status }));\n"
        + "} catch (e) {\n"
        + "  console.log(JSON.stringify({ code: String(e?.code ?? \"\"), name: String(e?.name ?? \"\") }));\n"
        + "}\n";

    private static readonly string[] TlsCodes =
    [
        "SELF_SIGNED_CERT_IN_CHAIN", "UNABLE_TO_GET_ISSUER_CERT_LOCALLY", "UNABLE_TO_GET_ISSUER_CERT", "UNABLE_TO_VERIFY_LEAF_SIGNATURE",
        "CERT_HAS_EXPIRED", "CERT_NOT_YET_VALID", "DEPTH_ZERO_SELF_SIGNED_CERT", "ERR_TLS_CERT_ALTNAME_INVALID", "HOSTNAME_MISMATCH",
        "CERT_UNTRUSTED", "CERT_REVOKED", "CERT_REJECTED", "CERT_SIGNATURE_FAILURE",
    ];

    private static readonly string[] DnsCodes = ["ENOTFOUND", "EAI_AGAIN", "EAI_FAIL", "EAI_NONAME", "DNSException"];

    private static readonly string[] BlockedCodes =
    [
        "ECONNREFUSED", "ConnectionRefused", "ECONNRESET", "ConnectionReset", "ConnectionClosed", "EPIPE", "FailedToOpenSocket",
        "EHOSTUNREACH", "ENETUNREACH", "HostUnreachable", "NetworkUnreachable",
    ];

    /// <summary>
    /// The runtime of <paramref name="engine"/> (an omp launch, e.g. <see cref="OmpRuntimeOptions.ToCliLaunchSpec"/>)
    /// when it is Bun; null when omp is started by another command (a compiled omp, a wrapper), where the check cannot
    /// run an inline script.
    /// </summary>
    public static OmpLaunchSpec? CheckSpec(OmpLaunchSpec engine, string url, string workingDirectory)
    {
        if (!Path.GetFileNameWithoutExtension(engine.FileName).Equals("bun", StringComparison.OrdinalIgnoreCase)) return null;
        // The engine's Bun config (the runtime pack's bunfig.toml), no .env file, the script; a temporary folder so no
        // project's bunfig.toml or .env is read
        var args = engine.Arguments.Where(a => a.StartsWith("--config=", StringComparison.Ordinal)).ToList();
        args.AddRange(["--no-env-file", "-e", Script]);
        var env = new Dictionary<string, string?>(engine.Environment) { [UrlVariable] = url };
        return new OmpLaunchSpec { FileName = engine.FileName, Arguments = args, WorkingDirectory = workingDirectory, Environment = env };
    }

    public static async Task<ConnectionCheckResult> RunAsync(OmpLaunchSpec engine, string url, CancellationToken ct = default)
    {
        var folder = Directory.CreateTempSubdirectory("ompgui-check-").FullName;
        try
        {
            if (CheckSpec(engine, url, folder) is not { } spec)
                return new(ConnectionCheckKind.Unavailable, "no-bun",
                    "The check runs on the Bun runtime omp uses, and omp is started with your own command here (Settings › Advanced › omp runtime), so the app can't check from here.");
            var start = new ProcessStartInfo(spec.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
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
                return new(ConnectionCheckKind.Unavailable, "no-start", "omp's runtime could not be started for the check. Check Settings › Advanced › omp runtime.");
            }
            var output = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct); // drained, never shown (it could name hosts)
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(RequestTimeout + TimeSpan.FromSeconds(10)); // the script's own timeout, plus Bun's start
            try { await p.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return Classify(null, "Timeout", "TimeoutError");
            }
            return Parse(await output.ConfigureAwait(false));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The script's line: <c>{"status":401}</c> or <c>{"code":"…","name":"…"}</c>.</summary>
    public static ConnectionCheckResult Parse(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return new(ConnectionCheckKind.Failed, "no-answer", "The check did not finish (omp's runtime gave no answer).");
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var s) && s.TryGetInt32(out var status)) return Classify(status, null, null);
            return Classify(null, root.TryGetProperty("code", out var c) ? c.GetString() : null, root.TryGetProperty("name", out var n) ? n.GetString() : null);
        }
        catch (JsonException)
        {
            return new(ConnectionCheckKind.Failed, "no-answer", "The check did not finish (omp's runtime gave no answer).");
        }
    }

    public static ConnectionCheckResult Classify(int? status, string? code, string? name)
    {
        if (status is { } n)
        {
            var c = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return n switch
            {
                >= 200 and < 300 or 401 or 403 => new(ConnectionCheckKind.Works, c,
                    $"HTTPS works (the server answered {n}; no key was sent, so this does not confirm the model works)."),
                407 => new(ConnectionCheckKind.ProxyAuth, c, "A proxy asked for sign-in (407). omp needs the proxy's address and credentials in its environment (HTTPS_PROXY)."),
                _ => new(ConnectionCheckKind.Http, c, $"HTTPS got through and the server answered {n}."),
            };
        }
        var safe = Safe(code) is { Length: > 0 } k ? k : Safe(name) ?? "unknown";
        if (name is "TimeoutError" or "AbortError" || code is "Timeout" or "ETIMEDOUT" or "UND_ERR_CONNECT_TIMEOUT")
            return new(ConnectionCheckKind.Timeout, "Timeout", "No answer within 10 seconds: the network, a VPN or a proxy may hold the connection.");
        if (TlsCodes.Contains(safe) || safe.Contains("CERT", StringComparison.Ordinal) || safe.Contains("TLS", StringComparison.Ordinal) || safe.Contains("SSL", StringComparison.Ordinal))
            return new(ConnectionCheckKind.Tls, safe, $"The connection's certificate is not trusted ({safe}). On a network that inspects HTTPS, set up corporate network certificates above.");
        if (DnsCodes.Contains(safe))
            return new(ConnectionCheckKind.Dns, safe, $"The server's name could not be found ({safe}): check the network connection or VPN.");
        if (BlockedCodes.Contains(safe))
            return new(ConnectionCheckKind.Blocked, safe, $"The connection was refused or cut off ({safe}): a firewall or proxy may block it.");
        if (safe.Contains("407", StringComparison.Ordinal) || safe.Contains("Proxy", StringComparison.OrdinalIgnoreCase))
            return new(ConnectionCheckKind.ProxyAuth, safe, $"The proxy refused the connection ({safe}). omp needs the proxy's address and credentials in its environment (HTTPS_PROXY).");
        return new(ConnectionCheckKind.Failed, safe, $"The request failed ({safe}).");
    }

    /// <summary>An error code as a log may carry it: letters, digits and underscores only, at most 48.</summary>
    private static string? Safe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var s = Regex.Replace(code, "[^A-Za-z0-9_]", "");
        return s.Length > 48 ? s[..48] : s;
    }
}
