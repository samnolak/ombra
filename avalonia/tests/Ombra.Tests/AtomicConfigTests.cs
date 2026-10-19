using System.Text;
using Ombra.ClientCore;
using Ombra.ClientCore.Browser;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class AtomicConfigTests
{
    [Fact]
    public void Stale_site_settings_apply_only_the_requested_change()
    {
        var dir = TestProcesses.TempDir("site-settings-stale");
        var path = Path.Combine(dir, "sites.json");
        var first = new BrowserSiteSettings(path);
        var stale = new BrowserSiteSettings(path);
        first.AlwaysAllow("https://one.example", PermissionKind.Camera);
        stale.AlwaysAllow("https://two.example", PermissionKind.Microphone);
        Assert.Equal(2, new BrowserSiteSettings(path).Sites.Count);

        var beforeRevocation = new BrowserSiteSettings(path);
        first.Forget("https://two.example"); // not present in first's old snapshot
        beforeRevocation.DownloadFolder = dir;
        var saved = new BrowserSiteSettings(path);
        Assert.False(saved.IsAlwaysAllowed("https://two.example", PermissionKind.Microphone));
        Assert.True(saved.IsAlwaysAllowed("https://one.example", PermissionKind.Camera));
        Assert.Equal(dir, saved.DownloadFolder);

        // An explicit new grant is still a grant, even if this instance remembers it already.
        first.Forget("https://one.example");
        beforeRevocation.AlwaysAllow("https://one.example", PermissionKind.Camera);
        Assert.True(new BrowserSiteSettings(path).IsAlwaysAllowed("https://one.example", PermissionKind.Camera));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_site_permission_consumers_observe_grants_and_revocations(bool readSitesFirst)
    {
        var dir = TestProcesses.TempDir("site-settings-read-refresh");
        var path = Path.Combine(dir, "sites.json");
        var writer = new BrowserSiteSettings(path);
        var consumer = new BrowserSiteSettings(path);
        writer.AlwaysAllow("https://meet.example", PermissionKind.CameraAndMicrophone);
        writer.AlwaysAllow("https://keep.example", PermissionKind.Camera);
        Assert.True(consumer.IsAlwaysAllowed("https://MEET.example:443", PermissionKind.Camera));
        Assert.True(consumer.IsAlwaysAllowed("https://meet.example", PermissionKind.Microphone));

        writer.Forget("https://meet.example");
        if (readSitesFirst)
            Assert.Equal(new[] { "https://keep.example" }, consumer.Sites);
        else
            Assert.False(consumer.IsAlwaysAllowed("https://meet.example", PermissionKind.Microphone));
        Assert.False(consumer.IsAlwaysAllowed("https://meet.example", PermissionKind.CameraAndMicrophone));
        Assert.False(consumer.IsAlwaysAllowed("https://meet.example", PermissionKind.Camera));
        Assert.Equal(new[] { "https://keep.example" }, consumer.Sites);
        Assert.True(consumer.IsAlwaysAllowed("https://keep.example", PermissionKind.Camera));

        // A later unrelated mutation must not restore the revoked permission.
        consumer.DownloadFolder = dir;
        Assert.False(writer.IsAlwaysAllowed("https://meet.example", PermissionKind.Microphone));
        Assert.True(writer.IsAlwaysAllowed("https://keep.example", PermissionKind.Camera));
    }

    [Fact]
    public void Site_permission_reads_observe_same_size_replacements_with_unchanged_timestamps()
    {
        var dir = TestProcesses.TempDir("site-settings-replaced");
        var path = Path.Combine(dir, "sites.json");
        var consumer = new BrowserSiteSettings(path);
        consumer.AlwaysAllow("https://old.example", PermissionKind.Camera);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var contents = File.ReadAllText(path);
        var length = new FileInfo(path).Length;

        AtomicFile.WriteAllText(path, contents.Replace("old.example", "new.example", StringComparison.Ordinal), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.False(consumer.IsAlwaysAllowed("https://old.example", PermissionKind.Camera));
        Assert.True(consumer.IsAlwaysAllowed("https://new.example", PermissionKind.Camera));
        Assert.Equal(new[] { "https://new.example" }, consumer.Sites);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("malformed")]
    [InlineData("unreadable")]
    public void Missing_or_unreadable_site_snapshots_do_not_authorize_or_resurrect_cached_grants(string failure)
    {
        var dir = TestProcesses.TempDir("site-settings-invalid");
        var path = Path.Combine(dir, "sites.json");
        var consumer = new BrowserSiteSettings(path);
        consumer.AlwaysAllow("https://old.example", PermissionKind.Camera);
        var sitesConsumer = new BrowserSiteSettings(path);
        var staleWriter = new BrowserSiteSettings(path);
        Assert.True(consumer.IsAlwaysAllowed("https://old.example", PermissionKind.Camera));
        File.Delete(path);
        if (failure == "malformed")
            File.WriteAllText(path, """{"alwaysAllow":{"https://old.example":["Camera"]},"broken":""");
        else if (failure == "unreadable")
            Directory.CreateDirectory(path); // opening a directory as a file fails on every supported OS

        Assert.False(consumer.IsAlwaysAllowed("https://old.example", PermissionKind.Camera));
        Assert.Empty(sitesConsumer.Sites);
        staleWriter.DownloadFolder = dir;
        Assert.False(consumer.IsAlwaysAllowed("https://old.example", PermissionKind.Camera));
        Assert.Empty(sitesConsumer.Sites);

        if (failure == "unreadable")
        {
            Assert.True(Directory.Exists(path)); // the failed read must not be replaced by a write
            staleWriter.AlwaysAllow("https://unsaved.example", PermissionKind.Microphone);
            Assert.False(staleWriter.IsAlwaysAllowed("https://unsaved.example", PermissionKind.Microphone));
            Directory.Delete(path);
        }
        staleWriter.AlwaysAllow("https://new.example", PermissionKind.Microphone);
        Assert.True(consumer.IsAlwaysAllowed("https://new.example", PermissionKind.Microphone));
        Assert.False(consumer.IsAlwaysAllowed("https://old.example", PermissionKind.Camera));
        Assert.False(consumer.IsAlwaysAllowed("https://unsaved.example", PermissionKind.Microphone));
        Assert.Equal(new[] { "https://new.example" }, sitesConsumer.Sites);
    }

    [Fact]
    public void In_memory_site_permissions_remain_available_until_explicitly_forgotten()
    {
        var settings = new BrowserSiteSettings();
        settings.AlwaysAllow("https://meet.example", PermissionKind.Camera);
        settings.AlwaysAllow("https://meet.example", PermissionKind.Microphone);
        settings.DownloadFolder = "chosen-download-folder";
        Assert.True(settings.IsAlwaysAllowed("https://meet.example", PermissionKind.CameraAndMicrophone));
        Assert.Equal(new[] { "https://meet.example" }, settings.Sites);
        Assert.Equal("chosen-download-folder", settings.DownloadFolder);
        Assert.Empty(new BrowserSiteSettings().Sites);
        settings.Forget("https://meet.example");
        Assert.False(settings.IsAlwaysAllowed("https://meet.example", PermissionKind.Camera));
        Assert.Empty(settings.Sites);
    }

    [Fact]
    public async Task Concurrent_site_instances_preserve_additions_and_revocations()
    {
        var dir = TestProcesses.TempDir("site-settings-concurrent");
        var path = Path.Combine(dir, "sites.json");
        var seed = new BrowserSiteSettings(path);
        for (var i = 0; i < 12; i++) seed.AlwaysAllow($"https://old{i}.example", PermissionKind.Camera);
        var instances = Enumerable.Range(0, 12).Select(_ => new BrowserSiteSettings(path)).ToArray();
        var consumer = new BrowserSiteSettings(path);
        await Task.WhenAll(instances.Select((settings, i) => Task.Run(() =>
        {
            settings.AlwaysAllow($"https://new{i}.example", PermissionKind.Microphone);
            settings.Forget($"https://old{i}.example");
        })));
        var saved = new BrowserSiteSettings(path);
        Assert.Equal(12, saved.Sites.Count);
        for (var i = 0; i < 12; i++)
        {
            Assert.False(saved.IsAlwaysAllowed($"https://old{i}.example", PermissionKind.Camera));
            Assert.True(saved.IsAlwaysAllowed($"https://new{i}.example", PermissionKind.Microphone));
            Assert.False(consumer.IsAlwaysAllowed($"https://old{i}.example", PermissionKind.Camera));
            Assert.True(consumer.IsAlwaysAllowed($"https://new{i}.example", PermissionKind.Microphone));
        }
        Assert.Equal(saved.Sites, consumer.Sites);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Failed_site_read_does_not_remember_unsaved_permissions()
    {
        var dir = TestProcesses.TempDir("site-settings-failure");
        var path = Path.Combine(dir, "sites.json");
        Directory.CreateDirectory(path); // the backing store cannot be opened as a file
        var settings = new BrowserSiteSettings(path);
        settings.AlwaysAllow("https://one.example", PermissionKind.Camera);
        Assert.False(settings.IsAlwaysAllowed("https://one.example", PermissionKind.Camera));
        Assert.Empty(settings.Sites);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Directory.Delete(path);
        settings.AlwaysAllow("https://one.example", PermissionKind.Camera);
        Assert.True(new BrowserSiteSettings(path).IsAlwaysAllowed("https://one.example", PermissionKind.Camera));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_site_revocations_deny_locally_and_persist_before_the_next_successful_change(bool unrelatedMutationFirst)
    {
        var dir = TestProcesses.TempDir("site-settings-unsaved");
        var path = Path.Combine(dir, "sites.json");
        var settings = new BrowserSiteSettings(path);
        settings.AlwaysAllow("https://saved.example", PermissionKind.CameraAndMicrophone);
        File.Delete(path + ".lock");
        Directory.CreateDirectory(path + ".lock"); // fail lock acquisition without changing the readable store

        settings.AlwaysAllow("https://unsaved.example", PermissionKind.Microphone);
        settings.Forget("https://saved.example");
        settings.AlwaysAllow("https://saved.example", PermissionKind.Microphone); // another failed write must not clear the veto
        settings.DownloadFolder = dir;
        Assert.Equal(dir, settings.DownloadFolder); // non-permission fallback remains available until refresh
        Assert.False(settings.IsAlwaysAllowed("https://unsaved.example", PermissionKind.Microphone));
        Assert.False(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.Camera));
        Assert.False(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.Microphone));
        Assert.Empty(settings.Sites);

        Directory.Delete(path + ".lock");
        if (unrelatedMutationFirst)
        {
            settings.DownloadFolder = dir;
            Assert.False(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.Camera));
            Assert.False(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.Microphone));
            Assert.Empty(settings.Sites);
            var afterMutation = new BrowserSiteSettings(path);
            Assert.Empty(afterMutation.Sites);
            Assert.Equal(dir, afterMutation.DownloadFolder);
        }

        settings.AlwaysAllow("https://saved.example", PermissionKind.Microphone);
        Assert.True(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.Microphone));
        Assert.False(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.Camera));
        Assert.False(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.CameraAndMicrophone));
        Assert.Equal(new[] { "https://saved.example" }, settings.Sites);
        var saved = new BrowserSiteSettings(path);
        Assert.False(saved.IsAlwaysAllowed("https://unsaved.example", PermissionKind.Microphone));
        Assert.False(saved.IsAlwaysAllowed("https://saved.example", PermissionKind.Camera));
        Assert.True(saved.IsAlwaysAllowed("https://saved.example", PermissionKind.Microphone));

        saved.Forget("https://saved.example");
        Assert.False(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.Microphone));
        Assert.Empty(settings.Sites);
        saved.AlwaysAllow("https://saved.example", PermissionKind.CameraAndMicrophone);
        Assert.True(settings.IsAlwaysAllowed("https://saved.example", PermissionKind.CameraAndMicrophone));
    }

    [Fact]
    public void Failed_temp_write_preserves_previous_file_and_cleans_temp()
    {
        var dir = TestProcesses.TempDir("atomic-config-write-failure");
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "previous contents");
        Assert.Throws<EncoderFallbackException>(() => AtomicFile.WriteAllText(path, "\ud800", new UTF8Encoding(false, true)));
        Assert.Equal("previous contents", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_generated_files_are_complete_and_do_not_use_legacy_temp_paths()
    {
        var dir = TestProcesses.TempDir("generated-config-concurrent");
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var defaults = new AgentBrowserDefaults(bridge, dir, inherited: _ => null);
        var titles = new SessionTitles(dir);
        var spec = new OmpLaunchSpec { FileName = "bun", Arguments = [Path.Combine(dir, "engine", "host.ts")] };
        var options = new OmpRuntimeOptions { Command = "omp" };
        var paths = new[] { defaults.OverlayPath, defaults.ExtensionPath, titles.ExtensionPath };
        foreach (var path in paths) File.WriteAllText(path + ".tmp", "not our temporary file");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            var browser = new AgentBrowserDefaults(bridge, dir, inherited: _ => null);
            Assert.Contains(AgentBrowserDefaults.ExtensionFlag, browser.ForSession(options, new LaunchRequest()).SessionArguments);
            Assert.Contains("--extension", new SessionTitles(dir).Apply(spec).SessionArguments);
        })));
        Assert.Equal(defaults.OverlayYaml(), File.ReadAllText(defaults.OverlayPath));
        Assert.Equal(defaults.ExtensionSource(), File.ReadAllText(defaults.ExtensionPath));
        Assert.Contains("export default function ombraTitles(pi)", File.ReadAllText(titles.ExtensionPath));
        foreach (var path in paths) Assert.Equal("not our temporary file", File.ReadAllText(path + ".tmp"));
        Assert.Equal(3, Directory.GetFiles(dir, "*.tmp").Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_browser_file_replace_cleans_temp_and_omits_generated_arguments(bool blockExtension)
    {
        var dir = TestProcesses.TempDir("browser-config-replace-failure");
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var defaults = new AgentBrowserDefaults(bridge, dir, inherited: _ => null);
        Directory.CreateDirectory(blockExtension ? defaults.ExtensionPath : defaults.OverlayPath);
        var result = defaults.ForSession(new OmpRuntimeOptions { Command = "omp" }, new LaunchRequest());
        Assert.False(result.Environment.ContainsKey(AgentBrowserDefaults.ConfigFilesVariable));
        Assert.DoesNotContain(AgentBrowserDefaults.ExtensionFlag, result.SessionArguments);
        Assert.Equal(bridge.Endpoint, result.Environment[AgentBrowserBridge.SocketVariable]);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Failed_title_replace_cleans_temp_and_preserves_launch_spec()
    {
        var dir = TestProcesses.TempDir("title-config-replace-failure");
        var titles = new SessionTitles(dir);
        Directory.CreateDirectory(titles.ExtensionPath);
        var spec = new OmpLaunchSpec { FileName = "bun", Arguments = [Path.Combine(dir, "engine", "host.ts")] };
        Assert.Same(spec, titles.Apply(spec));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }
}
