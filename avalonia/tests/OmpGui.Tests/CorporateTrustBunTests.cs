using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Network;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// The engine's real runtime (Bun from an installed OMP GUI runtime pack) against synthetic local servers: with the
/// app's copy of a CA file Bun trusts a server that CA signed, an untrusted self-signed server is still refused with the
/// setting on, and the connection check classifies TLS, DNS, refused and timeout. Skipped without a runtime pack.
/// </summary>
public sealed class CorporateTrustBunTests
{
    /// <summary>Bun of an installed runtime pack (the engine's own); null skips.</summary>
    private static string? PackBun()
    {
        var exe = OperatingSystem.IsWindows() ? "bun.exe" : "bun";
        var root = OmpGui.App.Services.Runtime.RuntimePack.DefaultRoot;
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root).Select(p => Path.Combine(p, "bun", exe)).FirstOrDefault(File.Exists);
    }

    /// <summary>The servers, run by Bun itself: one signed by the synthetic CA, one self-signed, one that never answers.</summary>
    private sealed class Servers : IAsyncDisposable
    {
        private readonly Process _p;
        public int CaSigned { get; }
        public int SelfSigned { get; }
        public int Silent { get; }

        private Servers(Process p, JsonNode ports)
        {
            _p = p;
            CaSigned = ports["ca"]!.GetValue<int>();
            SelfSigned = ports["self"]!.GetValue<int>();
            Silent = ports["silent"]!.GetValue<int>();
        }

        public static async Task<Servers> StartAsync(string bun, string dir, System.Security.Cryptography.X509Certificates.X509Certificate2 ca)
        {
            var signed = CorporateTrustCerts.Server(ca);
            var self = CorporateTrustCerts.Server();
            CorporateTrustCerts.Write(dir, "signed.pem", signed.Cert.ExportCertificatePem());
            CorporateTrustCerts.Write(dir, "signed.key", signed.KeyPem);
            CorporateTrustCerts.Write(dir, "self.pem", self.Cert.ExportCertificatePem());
            CorporateTrustCerts.Write(dir, "self.key", self.KeyPem);
            var script = CorporateTrustCerts.Write(dir, "servers.mjs", """
                import net from "node:net";
                const tls = n => ({ cert: Bun.file(n + ".pem"), key: Bun.file(n + ".key") });
                const ok = () => new Response("ok");
                const ca = Bun.serve({ port: 0, hostname: "127.0.0.1", tls: tls("signed"), fetch: ok });
                const self = Bun.serve({ port: 0, hostname: "127.0.0.1", tls: tls("self"), fetch: ok });
                const silent = net.createServer(() => {}).listen(0, "127.0.0.1", () =>
                  console.log(JSON.stringify({ ca: ca.port, self: self.port, silent: silent.address().port })));
                """);
            var start = new ProcessStartInfo(bun) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
            foreach (var a in new[] { "--no-env-file", script }) start.ArgumentList.Add(a);
            var p = Process.Start(start)!;
            var line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (string.IsNullOrEmpty(line)) Assert.Fail("servers did not start: " + await p.StandardError.ReadToEndAsync());
            return new Servers(p, JsonNode.Parse(line)!);
        }

        public async ValueTask DisposeAsync()
        {
            try { _p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await _p.WaitForExitAsync();
            _p.Dispose();
        }
    }

    /// <summary>The engine's launch with Bun as its command, as the settings build it (the app's Current()).</summary>
    private static OmpLaunchSpec Engine(string bun, string? mode, string? bundle) => new OmpRuntimeOptions
    {
        Command = bun,
        CorporateTrust = mode,
        CorporateTrustBundle = bundle,
        InheritedEnvironment = _ => null,
        // No proxy from the test machine between Bun and the local servers
        Environment = new() { ["HTTPS_PROXY"] = null, ["https_proxy"] = null, ["HTTP_PROXY"] = null, ["http_proxy"] = null },
    }.ToCliLaunchSpec([]);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Bun_trusts_the_apps_copy_of_a_CA_and_still_refuses_what_nobody_trusts()
    {
        if (PackBun() is not { } bun) { Assert.Skip("no OMP GUI runtime pack installed (Bun of the engine)"); return; }
        var dir = TestProcesses.TempDir("corp-trust-bun");
        var ca = CorporateTrustCerts.Ca();
        var file = CorporateTrustCerts.Write(dir, "team.pem", ca.ExportCertificatePem());
        var check = CorporateCaFile.Validate(file, DateTimeOffset.UtcNow);
        Assert.True(check.Ok, check.Message);
        var bundle = CorporateTrust.BundlePathFor(Path.Combine(dir, "omp-gui.local.json"));
        CorporateCaFile.Install(check.Pem!, bundle);
        await using var servers = await Servers.StartAsync(bun, dir, ca);
        var signed = $"https://127.0.0.1:{servers.CaSigned}/";
        var self = $"https://127.0.0.1:{servers.SelfSigned}/";

        // Without the setting the synthetic CA is unknown; with the app's copy the server it signed answers
        var off = await ConnectionCheck.RunAsync(Engine(bun, null, null), signed);
        Assert.Equal(ConnectionCheckKind.Tls, off.Kind);
        var on = await ConnectionCheck.RunAsync(Engine(bun, CorporateTrust.PemMode, bundle), signed);
        Assert.Equal((ConnectionCheckKind.Works, "200"), (on.Kind, on.Code));
        Assert.Contains("no key was sent", on.Message);
        Assert.Equal("check: works 200", on.LogLine);

        // Verification stays on: a self-signed server nobody trusts is refused with the file and with macOS's store
        var selfOn = await ConnectionCheck.RunAsync(Engine(bun, CorporateTrust.PemMode, bundle), self);
        Assert.Equal(ConnectionCheckKind.Tls, selfOn.Kind); // DEPTH_ZERO_SELF_SIGNED_CERT or UNABLE_TO_VERIFY_LEAF_SIGNATURE (a CA:false leaf)
        Assert.StartsWith("check: tls ", selfOn.LogLine);
        if (CorporateTrust.SystemModeSupported)
        {
            var selfSystem = await ConnectionCheck.RunAsync(Engine(bun, CorporateTrust.SystemMode, null), self);
            Assert.Equal(ConnectionCheckKind.Tls, selfSystem.Kind);
            // Both ways at once (the user's own NODE_EXTRA_CA_CERTS with the system store): the CA still applies
            var system = Engine(bun, CorporateTrust.SystemMode, null);
            var both = system with { Environment = new Dictionary<string, string?>(system.Environment) { [CorporateTrust.ExtraCaVariable] = bundle } };
            Assert.Equal(ConnectionCheckKind.Works, (await ConnectionCheck.RunAsync(both, signed)).Kind);
        }
    }

    [Fact]
    public async Task The_check_tells_dns_refused_and_timeout_apart()
    {
        if (PackBun() is not { } bun) { Assert.Skip("no OMP GUI runtime pack installed (Bun of the engine)"); return; }
        var dir = TestProcesses.TempDir("corp-trust-check");
        await using var servers = await Servers.StartAsync(bun, dir, CorporateTrustCerts.Ca());
        var engine = Engine(bun, null, null);

        var dns = await ConnectionCheck.RunAsync(engine, "https://nonexistent.invalid/");
        Assert.Equal((ConnectionCheckKind.Dns, "ENOTFOUND"), (dns.Kind, dns.Code));
        var refused = await ConnectionCheck.RunAsync(engine, $"https://127.0.0.1:{FreePort()}/");
        Assert.Equal(ConnectionCheckKind.Blocked, refused.Kind);
        var started = DateTime.UtcNow;
        var silent = await ConnectionCheck.RunAsync(engine, $"https://127.0.0.1:{servers.Silent}/");
        Assert.Equal((ConnectionCheckKind.Timeout, "Timeout"), (silent.Kind, silent.Code));
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void Answers_are_classified_without_names()
    {
        Assert.Equal(ConnectionCheckKind.Works, ConnectionCheck.Classify(401, null, null).Kind);
        Assert.Equal(ConnectionCheckKind.Works, ConnectionCheck.Classify(403, null, null).Kind);
        Assert.Equal(ConnectionCheckKind.Works, ConnectionCheck.Classify(200, null, null).Kind);
        Assert.Equal(ConnectionCheckKind.ProxyAuth, ConnectionCheck.Classify(407, null, null).Kind);
        Assert.Equal((ConnectionCheckKind.Http, "500"), (ConnectionCheck.Classify(500, null, null).Kind, ConnectionCheck.Classify(500, null, null).Code));
        foreach (var tls in new[] { "SELF_SIGNED_CERT_IN_CHAIN", "UNABLE_TO_GET_ISSUER_CERT_LOCALLY", "CERT_HAS_EXPIRED", "DEPTH_ZERO_SELF_SIGNED_CERT", "ERR_TLS_CERT_ALTNAME_INVALID" })
            Assert.Equal("check: tls " + tls, ConnectionCheck.Classify(null, tls, "TypeError").LogLine);
        Assert.Equal(ConnectionCheckKind.Dns, ConnectionCheck.Classify(null, "EAI_AGAIN", "TypeError").Kind);
        foreach (var blocked in new[] { "ECONNREFUSED", "ConnectionRefused", "ECONNRESET", "EPIPE" })
            Assert.Equal(ConnectionCheckKind.Blocked, ConnectionCheck.Classify(null, blocked, "TypeError").Kind);
        Assert.Equal(ConnectionCheckKind.Timeout, ConnectionCheck.Classify(null, "23", "TimeoutError").Kind);
        // A code is reduced to letters, digits and underscores before a log sees it
        Assert.Equal("check: failed weirdhostexamplecom", ConnectionCheck.Classify(null, "weird host.example.com", null).LogLine);
        Assert.Equal(ConnectionCheckKind.Failed, ConnectionCheck.Parse("garbage").Kind);
        Assert.Equal(ConnectionCheckKind.Works, ConnectionCheck.Parse("{\"status\":401}\n").Kind);
        // Not Bun (a compiled omp, a wrapper): no inline script, the page says why
        Assert.Null(ConnectionCheck.CheckSpec(new OmpGui.Rpc.OmpLaunchSpec { FileName = "/usr/local/bin/omp" }, "https://example.invalid/", "/tmp"));
    }
}
