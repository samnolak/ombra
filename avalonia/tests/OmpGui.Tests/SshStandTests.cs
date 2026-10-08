using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The SSH test against a stand on 127.0.0.1 with this computer's real ssh: servers that stay silent, close, speak
/// HTTP or offer no common algorithm, a closed port, and (where sshd can run as this user) a real sshd for host keys,
/// sign-in, ProxyJump and ProxyCommand. Every run uses its own ssh_config (<c>-F</c>), known_hosts and made-up keys:
/// nothing from the user's ~/.ssh is read or changed.
/// </summary>
public sealed class SshStandTests
{
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(2);
    private static readonly string User = Environment.UserName;

    private static void SkipUnlessSsh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand uses POSIX ssh_config paths and sshd");
        Assert.SkipWhen(WorkspaceTools.Which("ssh") is null, "no ssh client");
    }

    /// <summary>An ssh_config for one run: its own known_hosts, no agent, only the keys named.</summary>
    private static string Config(string dir, string extra = "", string knownHosts = "known_hosts")
    {
        var path = Path.Combine(dir, "ssh_config-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(path, $"""
            UserKnownHostsFile {Path.Combine(dir, knownHosts)}
            GlobalKnownHostsFile /dev/null
            IdentityAgent none
            IdentitiesOnly yes
            {extra}
            """);
        return path;
    }

    private static async Task<SshDiagnosis> Check(string config, string host, int port, string? key = null, string? user = null)
    {
        var entry = new SshHostEntry("stand", host, user, port, "user");
        var r = await WorkspaceTools.RunAsync(new ToolCommand("ssh", ["-F", config, .. SshCheck.Arguments(entry, key, Connect)], Timeout: SshCheck.ToolTimeout(Connect)));
        return SshCheck.Diagnose(r);
    }

    [Fact]
    public async Task Real_ssh_against_servers_that_never_finish_the_greeting()
    {
        SkipUnlessSsh();
        var dir = TestProcesses.TempDir("ssh-stand");
        var config = Config(dir);
        using var stop = new CancellationTokenSource();
        int Serve(Func<Socket, Task> talk)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = Task.Run(async () =>
            {
                using var reg = stop.Token.Register(listener.Stop);
                while (!stop.IsCancellationRequested)
                {
                    Socket s;
                    try { s = await listener.AcceptSocketAsync(stop.Token); }
                    catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                    _ = Task.Run(async () => { using (s) { try { await talk(s); } catch (Exception e) when (e is SocketException or OperationCanceledException) { } } });
                }
            });
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var silent = Serve(async _ => await Task.Delay(Timeout.Infinite, stop.Token));
        var closes = Serve(_ => Task.CompletedTask);
        var http = Serve(async s =>
        {
            await s.ReceiveAsync(new byte[256], SocketFlags.None, stop.Token);
            await s.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"), SocketFlags.None, stop.Token);
            await Task.Delay(200, stop.Token);
        });
        var oddKex = Serve(async s =>
        {
            await s.SendAsync(Encoding.ASCII.GetBytes("SSH-2.0-Stand_1.0\r\n"), SocketFlags.None, stop.Token);
            await s.SendAsync(KexInit("legacy-kex@example.invalid"), SocketFlags.None, stop.Token);
            await Task.Delay(2000, stop.Token);
        });
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var watch = Stopwatch.StartNew();
        var d = await Check(config, "127.0.0.1", silent);
        Assert.Equal((SshRoute.Direct, (int?)silent, SshStage.Banner, SshFailure.BannerTimeout), (d.Route, d.Port, d.FailedAt!.Value, d.Failure));
        Assert.True(watch.Elapsed >= Connect - TimeSpan.FromMilliseconds(200), $"gave up after {watch.Elapsed}");

        Assert.Equal((SshStage.Banner, SshFailure.ClosedBeforeBanner), Stage(await Check(config, "127.0.0.1", closes)));
        Assert.Equal((SshStage.Banner, SshFailure.NotSsh), Stage(await Check(config, "127.0.0.1", http)));
        Assert.Equal((SshStage.Kex, SshFailure.KexNoMatch), Stage(await Check(config, "127.0.0.1", oddKex)));
        Assert.Equal((SshStage.Tcp, SshFailure.TcpRefused), Stage(await Check(config, "127.0.0.1", closed)));
        Assert.Equal((SshStage.Resolve, SshFailure.ResolveFailed), Stage(await Check(config, "no-such-host.example.invalid", 22)));
        await stop.CancelAsync();
    }

    [Fact]
    public async Task Real_ssh_against_a_real_sshd_directly_and_through_jump_hosts()
    {
        SkipUnlessSsh();
        var sshd = new[] { "/usr/sbin/sshd", "/usr/local/sbin/sshd", "/opt/homebrew/sbin/sshd" }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(sshd is null || WorkspaceTools.Which("ssh-keygen") is null, "no sshd / ssh-keygen here");
        var dir = TestProcesses.TempDir("ssh-stand-sshd");
        foreach (var name in new[] { "host", "client", "stranger" })
        {
            var gen = await WorkspaceTools.RunAsync(new ToolCommand("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", name + "@example.invalid", "-f", Path.Combine(dir, name)]));
            Assert.True(gen.Ok, gen.Message);
        }
        File.Copy(Path.Combine(dir, "client.pub"), Path.Combine(dir, "authorized_keys"));
        await using var server = await Sshd.StartAsync(sshd!, dir);
        Assert.SkipWhen(server is null, "sshd does not run as this user here");
        var port = server!.Port;
        var client = Path.Combine(dir, "client");

        var direct = Config(dir);
        var signedIn = await Check(direct, "127.0.0.1", port, client, User);
        Assert.True(signedIn.Connected, signedIn.ToString());
        Assert.Equal((SshStage.Auth, SshFailure.AuthDenied), Stage(await Check(direct, "127.0.0.1", port, Path.Combine(dir, "stranger"), User)));

        // known_hosts holds another key for this address: the check stops there and leaves the file as it was
        var pinned = Path.Combine(dir, "known_hosts-pinned");
        var wrong = $"[127.0.0.1]:{port} {File.ReadAllText(Path.Combine(dir, "stranger.pub")).Trim()}\n";
        File.WriteAllText(pinned, wrong);
        Assert.Equal((SshStage.HostKey, SshFailure.HostKeyChanged), Stage(await Check(Config(dir, knownHosts: "known_hosts-pinned"), "127.0.0.1", port, client, User)));
        Assert.Equal(wrong, File.ReadAllText(pinned));

        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
        using var holder = new CancellationTokenSource();
        var held = new List<Socket>();
        _ = Task.Run(async () =>
        {
            try { while (true) held.Add(await silent.AcceptSocketAsync(holder.Token)); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        });
        var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var refusedPort = ((IPEndPoint)refused.LocalEndpoint).Port;
        refused.Stop();

        var jumps = Config(dir, $"""
            Host jump
              HostName 127.0.0.1
              Port {port}
              User {User}
              IdentityFile {client}
            Host deadjump
              HostName 127.0.0.1
              Port {refusedPort}
            Host via-jump
              HostName 127.0.0.1
              ProxyJump jump
            Host via-deadjump
              HostName 127.0.0.1
              ProxyJump deadjump
            """);
        var viaJump = await Check(jumps, "via-jump", port, client, User);
        Assert.True(viaJump.Connected && viaJump.Route == SshRoute.ProxyJump, viaJump.ToString());
        var silentBehindJump = await Check(jumps, "via-jump", silentPort, client, User);
        Assert.Equal((SshRoute.ProxyJump, SshStage.Banner, SshFailure.BannerTimeout), (silentBehindJump.Route, silentBehindJump.FailedAt!.Value, silentBehindJump.Failure));
        var deadJump = await Check(jumps, "via-deadjump", port, client, User);
        Assert.Equal((SshStage.Banner, SshFailure.ProxyFailed), Stage(deadJump));
        Assert.Contains("Connection refused", deadJump.ProxyMessage);

        var command = Config(dir, $"""
            Host via-command
              HostName 127.0.0.1
              ProxyCommand ssh -F {jumps} -W %h:%p jump
            """);
        var viaCommand = await Check(command, "via-command", silentPort, client, User);
        Assert.Equal((SshRoute.ProxyCommand, SshStage.Banner, SshFailure.BannerTimeout), (viaCommand.Route, viaCommand.FailedAt!.Value, viaCommand.Failure));
        await holder.CancelAsync();
        silent.Stop();
        foreach (var s in held) s.Dispose();
    }

    private static (SshStage, SshFailure) Stage(SshDiagnosis d)
    {
        Assert.True(d.FailedAt is not null, d.ToString());
        return (d.FailedAt!.Value, d.Failure);
    }

    /// <summary>An SSH_MSG_KEXINIT offering only <paramref name="kex"/>, which no client supports.</summary>
    private static byte[] KexInit(string kex)
    {
        var payload = new List<byte> { 20 };
        payload.AddRange(RandomNumberGenerator.GetBytes(16));
        foreach (var list in new[] { kex, "ssh-ed25519", "aes128-ctr", "aes128-ctr", "hmac-sha2-256", "hmac-sha2-256", "none", "none", "", "" })
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, list.Length);
            payload.AddRange(len);
            payload.AddRange(Encoding.ASCII.GetBytes(list));
        }
        payload.AddRange(new byte[5]); // first_kex_packet_follows, reserved
        var padding = 8 - (payload.Count + 5) % 8;
        if (padding < 4) padding += 8;
        var packet = new byte[4 + 1 + payload.Count + padding];
        BinaryPrimitives.WriteInt32BigEndian(packet, 1 + payload.Count + padding);
        packet[4] = (byte)padding;
        payload.CopyTo(packet, 5);
        return packet;
    }

    /// <summary>sshd as this user on a free loopback port with the stand's host key and authorized_keys.</summary>
    private sealed class Sshd : IAsyncDisposable
    {
        private readonly Process _process;
        public int Port { get; }

        private Sshd(Process process, int port) => (_process, Port) = (process, port);

        public static async Task<Sshd?> StartAsync(string sshd, string dir)
        {
            // Without PerSourcePenalties a newer sshd would shut out 127.0.0.1 after the refused sign-ins; an older one
            // doesn't know the option and gets a second try without it
            foreach (var penalties in new[] { "PerSourcePenalties no\n", "" })
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                var config = Path.Combine(dir, "sshd_config");
                File.WriteAllText(config, $"""
                    ListenAddress 127.0.0.1
                    Port {port}
                    HostKey {Path.Combine(dir, "host")}
                    AuthorizedKeysFile {Path.Combine(dir, "authorized_keys")}
                    PasswordAuthentication no
                    KbdInteractiveAuthentication no
                    UsePAM no
                    StrictModes no
                    PidFile {Path.Combine(dir, "sshd.pid")}
                    {penalties}
                    """);
                var psi = new ProcessStartInfo(sshd) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
                foreach (var a in new[] { "-D", "-e", "-f", config }) psi.ArgumentList.Add(a);
                var process = Process.Start(psi)!;
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                for (var i = 0; i < 50 && !process.HasExited; i++)
                {
                    try
                    {
                        using var c = new TcpClient();
                        await c.ConnectAsync(IPAddress.Loopback, port);
                        return new Sshd(process, port);
                    }
                    catch (SocketException) { await Task.Delay(100); }
                }
                if (!process.HasExited) process.Kill();
                process.Dispose();
            }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            _process.Dispose();
        }
    }
}
