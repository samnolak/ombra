using Ombra.ClientCore.Teams;
using Xunit;

namespace Ombra.Tests;

public sealed class TeamServerInstallerTests : IDisposable
{
    private const string Pin = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";
    private const string Code = "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY-Z";
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
        await Assert.ThrowsAsync<ServerInstallException>(() =>
            Fake($"serverId: s\nbootstrap code: {Code}\n").InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
    }

    [Fact]
    public async Task AlreadyInstalled_ThrowsSpecificException()
    {
        await Assert.ThrowsAsync<ServerAlreadyInstalledException>(() =>
            Fake("##OMBRA-ALREADY-INSTALLED\ninstall FAILED: an Ombra server is already installed\n", exit: 1)
                .InstallAsync(new("203.0.113.7", "root", 22, _key), null, default));
    }

    [Theory]
    [InlineData("203.0.113.7; rm -rf /", "root", 22, true, null)]
    [InlineData("$(reboot).example.com", "root", 22, true, null)]
    [InlineData("203.0.113.7", "Root;x", 22, true, null)]
    [InlineData("203.0.113.7", "root", 0, true, null)]
    [InlineData("203.0.113.7", "root", 70000, true, null)]
    [InlineData("203.0.113.7", "root", 22, false, null)]
    [InlineData("team.example.com", "root", 22, true, "a'$(reboot)'@example.com")]
    public async Task InvalidInputs_RejectedBeforeSsh(string address, string user, int port, bool keyExists, string? email)
    {
        var installer = Fake(IpOutput);
        var key = keyExists ? _key : Path.Combine(_dir, "missing");
        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(new(address, user, port, key, email), null, default));
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
