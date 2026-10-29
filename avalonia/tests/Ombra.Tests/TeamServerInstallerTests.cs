using Ombra.ClientCore.Teams;
using Xunit;

namespace Ombra.Tests;

public sealed class TeamServerInstallerTests : IDisposable
{
    private const string Pin = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";
    // As the server issues them (BootstrapCodes: ABCDEFGHJKLMNPQRSTUVWXYZ23456789, 26 in groups of 5): a live install
    // printed one with 8 and 9, which an RFC 4648 pattern cut short
    private const string Code = "ABCDE-FGH2K-LMN3P-QRS4T-8GFN9-G";
    private readonly string _dir = TestProcesses.TempDir("ombra-installer");
    private readonly string _key;

    public TeamServerInstallerTests()
    {
        _key = Path.Combine(_dir, "id_ed25519");
        File.WriteAllText(_key, "key");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class ListProgress : IProgress<ServerInstallProgress>
    {
        public List<ServerInstallProgress> Items { get; } = [];
        public void Report(ServerInstallProgress value) { lock (Items) Items.Add(value); }
    }

    /// <summary>A fake ssh that saves its stdin and arguments, prints <paramref name="output"/> and exits with <paramref name="exit"/>.</summary>
    private TeamServerInstaller Fake(string output, int exit = 0)
    {
        var outFile = Path.Combine(_dir, "out.txt");
        File.WriteAllText(outFile, output);
        var ssh = Path.Combine(_dir, "ssh");
        File.WriteAllText(ssh, $"#!/bin/bash\nprintf '%s\\n' \"$@\" > '{_dir}/args.txt'\ncat > '{_dir}/stdin.txt'\ncat '{outFile}'\nexit {exit}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new TeamServerInstaller(ssh);
    }

    private string Stdin => File.ReadAllText(Path.Combine(_dir, "stdin.txt"));

    private static string IpOutput => $"""
        ##OMBRA-STEP PreparingHost
        ##OMBRA-STEP Downloading
        ##OMBRA-STEP Verifying
        ##OMBRA-STEP Installing
        serverId:          srv-123
        endpoint:          https://203.0.113.7
        TLS pin (current): {Pin}
        bootstrap code: {Code}
        pin (SPKI SHA-256): {Pin}
        valid until: 2026-10-30
        """;

    [Fact]
    public async Task IpMode_ParsesResultAndReportsStepsInOrder()
    {
        var progress = new ListProgress();
        var r = await Fake(IpOutput).InstallAsync(new("203.0.113.7", "root", 22, _key), progress, default);

        Assert.Equal(new ServerInstallResult("https://203.0.113.7", Pin, Code, "srv-123"), r);
        var steps = progress.Items.Where(p => p.Line is null).Select(p => p.Step).ToList();
        Assert.Equal([ServerInstallStep.Connecting, ServerInstallStep.PreparingHost, ServerInstallStep.Downloading,
            ServerInstallStep.Verifying, ServerInstallStep.Installing, ServerInstallStep.Done], steps);
        Assert.DoesNotContain(progress.Items, p => p.Line is { } l && (l.Contains(Pin) || l.Contains(Code)));
        Assert.Contains("--ip '203.0.113.7'", Stdin);
        Assert.Contains(TeamServerRelease.Sha256, Stdin);
        Assert.Contains("trap cleanup EXIT", Stdin);
        Assert.Contains("/root/ombra-install.?*) \"${SUDO[@]}\" rm -rf -- \"$work\"", Stdin);
    }

    [Fact]
    public async Task DomainMode_HasNoPin()
    {
        var output = $"serverId: srv-9\nendpoint: https://team.example.com\nbootstrap code: {Code}\n";
        var r = await Fake(output).InstallAsync(new("team.example.com", "admin", 2222, _key, "ops@example.com"), null, default);

        Assert.Null(r.Pin);
        Assert.Equal("https://team.example.com", r.Server);
        Assert.Contains("--domain 'team.example.com' --acme-email 'ops@example.com'", Stdin);
        Assert.Contains("2222", File.ReadAllText(Path.Combine(_dir, "args.txt")));
    }

    [Fact]
    public async Task InstallFailed_ThrowsWithRedactedLog()
    {
        var output = $"bootstrap code: {Code}\nTLS pin (current): {Pin}\ninstall FAILED: compose up\n";
        var ex = await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake(output, exit: 1).InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));

        Assert.Contains("install FAILED: compose up", ex.Log);
        Assert.DoesNotContain(Code, ex.Log);
        Assert.DoesNotContain(Pin, ex.Log);
    }

    [Fact]
    public async Task NonZeroExit_Throws()
    {
        var ex = await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake(IpOutput, exit: 3).InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
        Assert.DoesNotContain(Code, ex.Log);
    }

    [Fact]
    public async Task MissingPinInIpMode_Throws()
    {
        var ex = await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake($"serverId: s\nbootstrap code: {Code}\n").InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
        Assert.Equal((ServerInstallFailure.MissingOutput, "tls-pin"), (ex.Reason, ex.Detail));
    }

    [Fact]
    public async Task AlreadyInstalled_ThrowsSpecificException()
    {
        await Assert.ThrowsAsync<ServerAlreadyInstalledException>(() =>
            Fake("##OMBRA-ALREADY-INSTALLED\ninstall FAILED: an Ombra server is already installed\n", exit: 1)
                .InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
    }

    [Fact]
    public async Task UnfinishedEarlierInstall_IsRemovedThenInstalledFresh()
    {
        var r = await Fake("removing the unfinished install left by an earlier attempt\nunfinished install removed\n" + IpOutput)
            .InstallAsync(new("203.0.113.7", "root", 22, _key), null, default);
        Assert.Equal(Code, r.BootstrapCode);
        var script = Stdin;
        var cleanup = script.IndexOf("if [ -e /etc/ombra ] && \"${SUDO[@]}\" test -e \"$MARK\"", StringComparison.Ordinal);
        var already = script.IndexOf("##OMBRA-ALREADY-INSTALLED", StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && cleanup < already);
        Assert.Contains("down -v --remove-orphans", script);
        Assert.Contains("rm -rf /etc/ombra /etc/ombra-offline /opt/ombra /var/lib/ombra", script);
        Assert.True(script.IndexOf("touch \"$MARK\"", StringComparison.Ordinal) < script.IndexOf("./deploy/install.sh", StringComparison.Ordinal));
        Assert.True(script.LastIndexOf("rm -f \"$MARK\"", StringComparison.Ordinal) > script.IndexOf("./deploy/install.sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedInstall_MessageSaysWhy()
    {
        const string err = "Error response from daemon: failed to resolve reference \"docker.io/library/postgres:17-alpine\": tls: x509: certificate signed by unknown authority";
        var ex = await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake($"##OMBRA-STEP Installing\nbootstrap code: {Code}\n{err}\n", exit: 1).InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
        Assert.Equal((ServerInstallFailure.InstallFailed, err), (ex.Reason, ex.Detail));
        Assert.Contains(err, ex.Log);
        Assert.DoesNotContain(Code, ex.Log);

        var ex2 = await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake("some error first\ninstall FAILED: compose up\n", exit: 1).InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
        Assert.Equal((ServerInstallFailure.InstallFailed, "compose up"), (ex2.Reason, ex2.Detail));

        var ssh = await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake("root@203.0.113.7: Permission denied (publickey).\n", exit: 255).InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
        Assert.Equal((ServerInstallFailure.SshFailed, "root@203.0.113.7: Permission denied (publickey)."), (ssh.Reason, ssh.Detail));

        var bare = await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake("", exit: 7).InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
        Assert.Equal((ServerInstallFailure.InstallFailed, "exit 7"), (bare.Reason, bare.Detail));
    }

    [Theory]
    [InlineData("no-sudo", ServerInstallFailure.NoSudo)]
    [InlineData("unsupported-os", ServerInstallFailure.UnsupportedOs)]
    [InlineData("unsupported-arch", ServerInstallFailure.UnsupportedArch)]
    [InlineData("low-disk", ServerInstallFailure.LowDisk)]
    [InlineData("packages", ServerInstallFailure.PackagesFailed)]
    [InlineData("docker", ServerInstallFailure.DockerInstallFailed)]
    [InlineData("download", ServerInstallFailure.DownloadFailed)]
    [InlineData("checksum", ServerInstallFailure.ChecksumMismatch)]
    [InlineData("signature", ServerInstallFailure.SignatureInvalid)]
    [InlineData("release", ServerInstallFailure.ReleaseInvalid)]
    [InlineData("host", ServerInstallFailure.InstallFailed)]
    [InlineData("something-new", ServerInstallFailure.InstallFailed)]
    public async Task FailureTag_MapsToReason(string tag, ServerInstallFailure reason)
    {
        var ex = await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake($"##OMBRA-STEP PreparingHost\ninstall FAILED [{tag}]: Fedora 40 (Debian or Ubuntu required)\n", exit: 1)
                .InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
        Assert.Equal((reason, "Fedora 40 (Debian or Ubuntu required)"), (ex.Reason, ex.Detail));
        if (tag != "something-new") Assert.Contains($"fail {tag} ", Stdin); // the script really uses the tag
    }

    [Theory]
    [InlineData("203.0.113.7; rm -rf /", "root", 22, true, null, ServerInstallFailure.InvalidAddress)]
    [InlineData("$(reboot).example.com", "root", 22, true, null, ServerInstallFailure.InvalidAddress)]
    [InlineData("203.0.113.7", "Root;x", 22, true, null, ServerInstallFailure.InvalidUser)]
    [InlineData("203.0.113.7", "root", 0, true, null, ServerInstallFailure.InvalidPort)]
    [InlineData("203.0.113.7", "root", 70000, true, null, ServerInstallFailure.InvalidPort)]
    [InlineData("203.0.113.7", "root", 22, false, null, ServerInstallFailure.MissingKey)]
    [InlineData("team.example.com", "root", 22, true, "a'$(reboot)'@example.com", ServerInstallFailure.InvalidAcmeEmail)]
    [InlineData("203.0.113.7", "root", 22, true, "me@example.com", ServerInstallFailure.AcmeEmailNeedsDomain)]
    public async Task InvalidInputs_RejectedBeforeSsh(string address, string user, int port, bool keyExists, string? email, ServerInstallFailure reason)
    {
        var installer = Fake(IpOutput);
        var key = keyExists ? _key : Path.Combine(_dir, "missing");
        var ex = await Assert.ThrowsAsync<ServerInstallException>(() => installer.InstallAsync(new(address, user, port, key, email), null, default));
        Assert.Equal(reason, ex.Reason);
        Assert.False(File.Exists(Path.Combine(_dir, "stdin.txt")));
    }

    [Fact]
    public void Script_QuotesHostileValues()
    {
        var script = TeamServerInstaller.BuildScript(new("team.example.com", "root", 22, _key, "x'; reboot; '@example.com"), ipMode: false);
        Assert.Contains("--acme-email 'x'\\''; reboot; '\\''@example.com'", script);
        Assert.Equal("'a'\\''b $(c)'", TeamServerInstaller.Quote("a'b $(c)"));
    }
}
