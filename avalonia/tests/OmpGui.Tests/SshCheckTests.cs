using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// What the SSH test concludes from ssh's debug log (the lines OpenSSH 10 prints; addresses, names and keys made up):
/// each stage counts only when the log proves it, and a proxied route never passes off the jump host's lines as the
/// server's.
/// </summary>
public class SshCheckTests
{
    private static ToolResult Run(int exit, params string[] stderr) => new(exit, "", string.Join('\n', stderr) + "\n");

    private const string Connecting = "debug1: Connecting to box.example.invalid [192.0.2.10] port 2222.";
    private const string Established = "debug1: Connection established.";
    private const string LocalVersion = "debug1: Local version string SSH-2.0-OpenSSH_10.3";
    private const string RemoteVersion = "debug1: Remote protocol version 2.0, remote software version OpenSSH_9.6";
    private const string HostKey = "debug1: Server host key: ssh-ed25519 SHA256:AAAAexampleexampleexampleexampleexampleexample";
    private const string Matches = "debug1: Host '[192.0.2.10]:2222' is known and matches the ED25519 host key.";
    private const string ViaJump = "debug1: Setting implicit ProxyCommand from ProxyJump: ssh -W '[%h]:%p' jump.example.invalid";
    private const string ProxyExec = "debug1: Executing proxy command: exec ssh -W '[192.0.2.10]:22' jump.example.invalid";

    [Fact]
    public void Each_failure_is_placed_at_the_stage_the_log_proves()
    {
        var cases = new (ToolResult Result, SshStage? At, SshFailure Failure, string Code)[]
        {
            (Run(255, "ssh: Could not resolve hostname box.example.invalid: nodename nor servname provided, or not known"),
                SshStage.Resolve, SshFailure.ResolveFailed, "resolve-failed"),
            (Run(255, Connecting, "debug1: connect to address 192.0.2.10 port 2222: Connection refused",
                "ssh: connect to host box.example.invalid port 2222: Connection refused"), SshStage.Tcp, SshFailure.TcpRefused, "tcp-refused"),
            (Run(255, Connecting, "ssh: connect to host box.example.invalid port 2222: Operation timed out"), SshStage.Tcp, SshFailure.TcpTimeout, "tcp-timeout"),
            (Run(255, Connecting, "ssh: connect to host box.example.invalid port 2222: No route to host"), SshStage.Tcp, SshFailure.TcpUnreachable, "tcp-unreachable"),
            // TCP up, our greeting sent, none back: the case this check exists for
            (Run(255, Connecting, Established, LocalVersion, "Connection timed out during banner exchange", "Connection to 192.0.2.10 port 2222 timed out"),
                SshStage.Banner, SshFailure.BannerTimeout, "banner-timeout"),
            (Run(255, Connecting, Established, LocalVersion, "kex_exchange_identification: read: Connection reset by peer", "Connection reset by 192.0.2.10 port 2222"),
                SshStage.Banner, SshFailure.ClosedBeforeBanner, "banner-closed"),
            (Run(255, Connecting, Established, LocalVersion, "kex_exchange_identification: Connection closed by remote host"),
                SshStage.Banner, SshFailure.ClosedBeforeBanner, "banner-closed"),
            // An HTTP server on the port: lines before any SSH- line, then it closes
            (Run(255, Connecting, Established, LocalVersion, "debug1: kex_exchange_identification: banner line 0: HTTP/1.1 400 Bad Request",
                "kex_exchange_identification: Connection closed by remote host"), SshStage.Banner, SshFailure.NotSsh, "banner-not-ssh"),
            (Run(255, Connecting, Established, LocalVersion, "debug1: Remote protocol version 1.5, remote software version Old",
                "Protocol major versions differ: 2 vs. 1"), SshStage.Banner, SshFailure.NotSsh, "banner-not-ssh"),
            (Run(255, Connecting, Established, LocalVersion, RemoteVersion, "debug1: SSH2_MSG_KEXINIT received", "debug1: kex: algorithm: (no match)",
                "Unable to negotiate with 192.0.2.10 port 2222: no matching key exchange method found. Their offer: legacy-kex@example.invalid"),
                SshStage.Kex, SshFailure.KexNoMatch, "kex-no-match"),
            (Run(255, Connecting, Established, LocalVersion, RemoteVersion, "debug1: SSH2_MSG_KEXINIT sent", "Connection closed by 192.0.2.10 port 2222"),
                SshStage.Kex, SshFailure.ConnectionClosed, "kex-closed"),
            (Run(255, Connecting, Established, LocalVersion, RemoteVersion, HostKey, "@    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @",
                "Host key verification failed."), SshStage.HostKey, SshFailure.HostKeyChanged, "hostkey-changed"),
            (Run(255, Connecting, Established, LocalVersion, RemoteVersion, HostKey, "Host key verification failed."),
                SshStage.HostKey, SshFailure.HostKeyRejected, "hostkey-rejected"),
            (Run(255, Connecting, Established, LocalVersion, RemoteVersion, HostKey, Matches, "debug1: Authentications that can continue: publickey",
                "Load key \"/home/alice/.ssh/id_example\": bad permissions", "alice@box.example.invalid: Permission denied (publickey)."),
                SshStage.Auth, SshFailure.KeyUnusable, "auth-key-unusable"),
            (Run(255, Connecting, Established, LocalVersion, RemoteVersion, HostKey, "Warning: Permanently added '[192.0.2.10]:2222' (ED25519) to the list of known hosts.",
                "debug1: Authentications that can continue: publickey", "alice@box.example.invalid: Permission denied (publickey)."),
                SshStage.Auth, SshFailure.AuthDenied, "auth-denied"),
            // Signed in, the command itself failed: the connection is not the problem
            (Run(1, Connecting, Established, LocalVersion, RemoteVersion, HostKey, Matches, "Authenticated to box.example.invalid ([192.0.2.10]:2222) using \"publickey\"."),
                null, SshFailure.RemoteCommandFailed, "command-failed"),
            (Run(0, Connecting, Established, LocalVersion, RemoteVersion, HostKey, Matches, "Authenticated to box.example.invalid ([192.0.2.10]:2222) using \"publickey\"."),
                null, SshFailure.None, "ok"),
            // ssh failed before connecting at all (a broken config line): no stage is claimed
            (Run(255, "/home/alice/.ssh/config: line 3: Bad configuration option: frobnicate"), null, SshFailure.Unknown, "unknown"),
        };
        foreach (var (result, at, failure, code) in cases)
        {
            var d = SshCheck.Diagnose(result);
            Assert.True((at, failure, code) == (d.FailedAt, d.Failure, d.Code), $"{result.Stderr}\n→ {d}");
        }
    }

    [Fact]
    public void A_deadline_keeps_the_stage_ssh_had_reached()
    {
        // WorkspaceTools keeps what ssh printed before it was stopped and adds why
        var d = SshCheck.Diagnose(new ToolResult(-1, "", string.Join('\n', Connecting, Established, LocalVersion, RemoteVersion, HostKey, Matches,
            "debug1: Authentications that can continue: publickey", "ssh did not answer within 26 s"), TimedOut: true));
        Assert.Equal((SshStage.Auth, SshFailure.NoAnswer, "auth-no-answer"), (d.FailedAt!.Value, d.Failure, d.Code));
    }

    [Fact]
    public void A_proxied_route_never_counts_the_jump_hosts_lines_as_the_servers()
    {
        // The jump host's ssh added its own key and then the greeting from the server never came through
        var silent = SshCheck.Diagnose(Run(255, ViaJump, ProxyExec, LocalVersion,
            "Warning: Permanently added 'jump.example.invalid' (ED25519) to the list of known hosts.",
            "Connection timed out during banner exchange", "Connection to UNKNOWN port 65535 timed out"));
        Assert.Equal((SshRoute.ProxyJump, SshStage.Banner, SshFailure.BannerTimeout, (int?)null), (silent.Route, silent.FailedAt!.Value, silent.Failure, silent.Port));

        // The jump host itself failed: its words are the evidence, and the stage is still the server's greeting
        var deadJump = SshCheck.Diagnose(Run(255, ViaJump, ProxyExec, LocalVersion,
            "ssh: connect to host jump.example.invalid port 22: Connection refused",
            "kex_exchange_identification: Connection closed by remote host", "Connection closed by UNKNOWN port 65535"));
        Assert.Equal((SshStage.Banner, SshFailure.ProxyFailed, "ssh: connect to host jump.example.invalid port 22: Connection refused"),
            (deadJump.FailedAt!.Value, deadJump.Failure, deadJump.ProxyMessage));
        Assert.Equal("banner-proxy-failed", deadJump.Code);

        // The jump host's "Permission denied" is not the server refusing the sign-in
        var jumpDenied = SshCheck.Diagnose(Run(255, ViaJump, ProxyExec, LocalVersion, "alice@jump.example.invalid: Permission denied (publickey).",
            "kex_exchange_identification: Connection closed by remote host"));
        Assert.Equal(SshFailure.ProxyFailed, jumpDenied.Failure);

        var command = SshCheck.Diagnose(Run(255, "debug1: Executing proxy command: exec nc 192.0.2.10 22", LocalVersion, RemoteVersion, HostKey, Matches,
            "alice@box.example.invalid: Permission denied (publickey)."));
        Assert.Equal((SshRoute.ProxyCommand, SshStage.Auth, SshFailure.AuthDenied), (command.Route, command.FailedAt!.Value, command.Failure));
    }

    [Fact]
    public void The_answer_says_what_is_proven_and_what_is_left_to_check()
    {
        var d = SshCheck.Diagnose(Run(255, Connecting, Established, LocalVersion, "Connection timed out during banner exchange", "Connection to 192.0.2.10 port 2222 timed out"));
        var text = SshSettingsViewModel.Describe(d, "Connection to 192.0.2.10 port 2222 timed out", 255);
        var lines = text.Split('\n');
        Assert.Equal("TCP connection established, but the server's SSH greeting did not arrive within 8 s.", lines[0]);
        Assert.Equal(["✓ Address", "✓ TCP connection", "✗ Server's SSH greeting", "– Protocol negotiation", "– Host key check", "– Sign-in"],
            lines[1..7].Select(l => l.Split(':')[0]));
        Assert.Contains("Route: direct, port 2222.", lines);
        Assert.Contains("Code: banner-timeout", lines);
        // No ssh debug lines, and no guess about who is to blame
        Assert.DoesNotContain("debug1", text);
        Assert.DoesNotContain("block", text, StringComparison.OrdinalIgnoreCase);

        // Through a jump host the local DNS and TCP steps belong to the jump host
        var proxied = SshSettingsViewModel.Describe(SshCheck.Diagnose(Run(255, ViaJump, ProxyExec, LocalVersion, "Connection timed out during banner exchange")), "x", 255);
        Assert.Contains("– TCP connection: made by the jump host, not checked here", proxied);
        Assert.StartsWith("The server's SSH greeting did not come through the jump host within 8 s.", proxied);
    }
}
