using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Update check: a signed manifest over HTTPS, the package verified by size and SHA-256.</summary>
public sealed class UpdateTests
{
    private const string Feed = "https://updates.test/update.json";
    private static readonly byte[] Package = Encoding.ASCII.GetBytes(new string('p', 5000));

    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(files.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Manifest(string version, string rid = "linux-x64", string? url = null, string? file = null, string? sha = null, long? size = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            version,
            notes = "Faster start.",
            assets = new Dictionary<string, object>
            {
                [rid] = new
                {
                    file = file ?? $"omp-gui-{version}-{rid}.tar.gz",
                    url = url ?? $"https://updates.test/omp-gui-{version}-{rid}.tar.gz",
                    sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(Package)),
                    size = size ?? Package.Length,
                },
            },
        });

    private static (UpdateChecker Checker, Server Server) Checker(byte[] manifest, string current = "0.1.0", string rid = "linux-x64", byte[]? package = null)
    {
        var server = new Server(new()
        {
            [Feed] = manifest,
            [Feed + ".sig"] = TestSigning.Sign(manifest),
            ["https://updates.test/omp-gui-0.2.0-linux-x64.tar.gz"] = package ?? Package,
        });
        return (new UpdateChecker(new HttpClient(server), Feed, current, rid, TestSigning.PublicKey), server);
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("0.1.0", "0.1.0-ci.57.abc1234", 1)]
    [InlineData("0.1.0-ci.9.a", "0.1.0-ci.57.b", -1)]
    [InlineData("0.1.0-rc.1", "0.1.0-beta.2", 1)]
    [InlineData("v0.1.0", "0.1.0+build.5", 0)]
    [InlineData("0.1", "0.1.0", 0)]
    public void Versions_compare_by_semver(string a, string b, int expected) => Assert.Equal(expected, UpdateChecker.CompareVersions(a, b));

    /// <summary>
    /// Regression (candidate check against the real update.json): a framework-dependent run on a distro-built .NET
    /// reported ubuntu.24.04-x64, which no package carries, so the update check would never find one. The platform is
    /// named the way the packages are.
    /// </summary>
    [Fact]
    public void This_platform_is_named_as_the_packages_are()
    {
        Assert.Matches("^(win|osx|linux|linux-musl)-(x64|arm64|x86|arm)$", UpdateChecker.ThisRid());
        Assert.DoesNotContain(".", UpdateChecker.ThisRid());
    }

    [Fact]
    public async Task A_newer_version_is_offered_and_its_package_is_verified_before_it_is_kept()
    {
        var (checker, server) = Checker(Manifest("0.2.0"));
        var check = await checker.CheckAsync(CancellationToken.None);
        Assert.True(check.Available);
        Assert.Equal("0.2.0", check.Latest);
        Assert.Equal([Feed, Feed + ".sig"], server.Requests); // checking downloads nothing but the manifest and its signature
        var folder = TestProcesses.TempDir("upd");
        var file = await checker.DownloadAsync(check.Asset!, folder, null, CancellationToken.None);
        Assert.Equal(Path.Combine(folder, "omp-gui-0.2.0-linux-x64.tar.gz"), file);
        Assert.Equal(Package, File.ReadAllBytes(file));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task The_same_or_an_older_version_is_up_to_date()
    {
        Assert.False((await Checker(Manifest("0.1.0")).Checker.CheckAsync(CancellationToken.None)).Available);
        var older = await Checker(Manifest("0.0.9")).Checker.CheckAsync(CancellationToken.None);
        Assert.False(older.Available);
        Assert.Contains("up to date", older.Message);
    }

    [Fact]
    public async Task A_version_without_a_package_for_this_platform_is_reported()
    {
        var check = await Checker(Manifest("0.2.0", rid: "osx-arm64")).Checker.CheckAsync(CancellationToken.None);
        Assert.False(check.Available);
        Assert.Contains("not for this platform (linux-x64)", check.Message);
    }

    [Fact]
    public async Task A_tampered_package_is_not_kept()
    {
        var tampered = (byte[])Package.Clone();
        tampered[10] = (byte)'x';
        var (checker, _) = Checker(Manifest("0.2.0"), package: tampered);
        var check = await checker.CheckAsync(CancellationToken.None);
        var folder = TestProcesses.TempDir("upd-bad");
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadAsync(check.Asset!, folder, null, CancellationToken.None));
        Assert.Contains("SHA-256", e.Message);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task A_package_of_another_size_is_not_kept()
    {
        var (checker, _) = Checker(Manifest("0.2.0", size: Package.Length - 1));
        var check = await checker.CheckAsync(CancellationToken.None);
        var folder = TestProcesses.TempDir("upd-size");
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadAsync(check.Asset!, folder, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Theory]
    [InlineData("http://updates.test/omp.tar.gz", null, null)]
    [InlineData(null, "../../.bashrc", null)]
    [InlineData(null, "sub/dir.tar.gz", null)]
    [InlineData(null, null, "not-a-hash")]
    public async Task Unsafe_manifests_are_refused(string? url, string? file, string? sha)
    {
        var (checker, _) = Checker(Manifest("0.2.0", url: url, file: file, sha: sha));
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"schema\":1,\"version\":\"0.2.0\",\"assets\":{\"linux-x64\":{\"file\":\"a.tar.gz\",\"url\":\"https://x/a\",\"size\":5}}}")]
    [InlineData("{\"schema\":1,\"version\":\"0.2.0\",\"assets\":{\"linux-x64\":null}}")]
    public async Task A_manifest_with_missing_fields_is_refused_not_crashed(string json)
    {
        // Regression (review round 4): a null sha256 or asset threw a NullReferenceException the view did not catch.
        var bytes = Encoding.UTF8.GetBytes(json);
        var server = new Server(new() { [Feed] = bytes, [Feed + ".sig"] = TestSigning.Sign(bytes) });
        var checker = new UpdateChecker(new HttpClient(server), Feed, "0.1.0", "linux-x64", TestSigning.PublicKey);
        try
        {
            var check = await checker.CheckAsync(CancellationToken.None);
            Assert.False(check.Available); // a null asset is "not for this platform"
        }
        catch (InvalidDataException) { }
    }

    [Fact]
    public async Task A_file_of_the_same_name_in_the_folder_is_never_replaced()
    {
        var (checker, _) = Checker(Manifest("0.2.0"));
        var check = await checker.CheckAsync(CancellationToken.None);
        var folder = TestProcesses.TempDir("upd-exists");
        var mine = Path.Combine(folder, "omp-gui-0.2.0-linux-x64.tar.gz");
        await File.WriteAllTextAsync(mine, "the user's own file");
        var file = await checker.DownloadAsync(check.Asset!, folder, null, CancellationToken.None);
        Assert.Equal(Path.Combine(folder, "omp-gui-0.2.0-linux-x64 (2).tar.gz"), file);
        Assert.Equal("the user's own file", await File.ReadAllTextAsync(mine));
    }

    [Fact]
    public async Task A_plain_http_feed_is_refused_and_a_missing_feed_is_reported()
    {
        var http = new UpdateChecker(new HttpClient(new Server([])), "http://updates.test/update.json", "0.1.0", "linux-x64", TestSigning.PublicKey);
        await Assert.ThrowsAsync<InvalidDataException>(() => http.CheckAsync(CancellationToken.None));
        var missing = await new UpdateChecker(new HttpClient(new Server([])), Feed, "0.1.0", "linux-x64", TestSigning.PublicKey).CheckAsync(CancellationToken.None);
        Assert.False(missing.Available);
        Assert.Equal("No release is published yet.", missing.Message);
    }

    [AvaloniaFact]
    public async Task Settings_check_download_and_verify_on_request()
    {
        var (checker, server) = Checker(Manifest("0.2.0"));
        var folder = TestProcesses.TempDir("upd-ui");
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs()) { Updates = checker, UpdateDownloadFolder = folder };
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        Assert.Empty(server.Requests); // the automatic check waits UpdateCheckDelay (20 s) after start
        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal("OMP GUI 0.1.0 (linux-x64)", vm.AppVersionText);
        vm.CheckForUpdatesCommand.Execute(null);
        await Until(() => vm.UpdateAvailable, "update offered");
        Assert.StartsWith("Version 0.2.0 is available", vm.UpdateText);
        Assert.Equal("Faster start.", vm.UpdateNotes);
        Assert.Equal("Update available: 0.2.0", vm.UpdatePill);
        Assert.Equal("Download and verify", vm.UpdateActionLabel); // a test run is no package: download only
        vm.DownloadUpdateCommand.Execute(null);
        await Until(() => vm.DownloadedUpdate is not null, "downloaded");
        Assert.StartsWith("Downloaded and verified", vm.UpdateText);
        Assert.True(File.Exists(vm.DownloadedUpdate));
        await vm.DisposeAsync();
        w.Close();
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }
}
