using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Diagnostics leave the machine: no secret may be in them, whatever shape it has or wherever it shows up.</summary>
public sealed class DiagnosticsTests
{
    [Theory]
    [InlineData("key sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 end", "sk-ant-api03")]
    [InlineData("OPENAI: sk-proj-abcdefghijklmnopqrstuvwxyz123456", "sk-proj-abcdef")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdef")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnop", "github_pat_11")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE here", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("google AIzaSyA-1234567890abcdefghijklmnopqrstu", "AIzaSyA-12345")]
    [InlineData("Authorization: Bearer abc.def-ghi_jkl123456", "abc.def-ghi")]
    [InlineData("ANTHROPIC_API_KEY=abcdefgh12345678 next", "abcdefgh12345678")]
    [InlineData("export MY_SERVICE_TOKEN=zzzzyyyyxxxx1111", "zzzzyyyyxxxx1111")]
    [InlineData("{\"apiKey\": \"plain-secret-value\"}", "plain-secret-value")]
    [InlineData("{\"refresh_token\":\"r3fr3sh-t0k3n\"}", "r3fr3sh-t0k3n")]
    [InlineData("proxy https://user:hunter2hunter2@proxy.local:8080", "hunter2hunter2")]
    [InlineData("omp --api-key s3cr3t-value --model x", "s3cr3t-value")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "eyJzdWIiOiIxMjM0NTY3ODkwIn0")]
    public void Credential_shapes_are_masked(string text, string secret)
    {
        var redacted = new SecretRedactor().Redact(text);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(SecretRedactor.Mask, redacted);
    }

    [Theory]
    [InlineData("input tokens: 1200, output tokens: 300")]
    [InlineData("author: Jane Doe")]
    [InlineData("phase Ready; model anthropic/claude-sonnet; context 12%")]
    [InlineData("git checkout -b feature/token-refresh")]
    public void Ordinary_text_is_left_alone(string text) => Assert.Equal(text, new SecretRedactor().Redact(text));

    [Fact]
    public void Known_values_are_masked_anywhere_and_the_home_folder_becomes_a_tilde()
    {
        var r = new SecretRedactor(["opaque-local-value-42", "abc"], home: "/home/alice");
        var text = r.Redact("omp said: failed with opaque-local-value-42 in /home/alice/project (abc)");
        Assert.Equal("omp said: failed with [redacted] in ~/project (abc)", text); // values under 6 chars are not treated as secrets
    }

    [Fact]
    public async Task The_bundle_holds_no_secret_from_settings_errors_or_logs()
    {
        var dir = TestProcesses.TempDir("diag");
        var settings = Path.Combine(dir, "omp-gui.local.json");
        const string EnvSecret = "Zq9-custom-provider-value-7731";   // no recognizable shape: only known because it is in `environment`
        const string ArgSecret = "another-opaque-8812";
        await File.WriteAllTextAsync(settings, $$"""
            {
              // local file
              "command": "/opt/omp/bin/omp",
              "profile": "work",
              "environment": { "MY_PROVIDER_KEY": "{{EnvSecret}}", "HOME": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "home"))}}, "UNSET": null },
              "extraArgs": ["--api-key", "{{ArgSecret}}", "--no-lsp"],
              "theme": "dark"
            }
            """);
        var startup = Path.Combine(dir, "startup.log");
        await File.WriteAllTextAsync(startup, $"omp_start_failed request to https://api.example.test failed: Authorization: Bearer sk-live-abcdefghijklmnop1234\nprovider echoed {EnvSecret}\n");
        var snapshot = new SessionSnapshot(3, SessionPhase.Faulted, [], "prov/model", 2, "s1", 10, 2000, null, null,
            $"omp exited: invalid key {ArgSecret} for provider (ANTHROPIC_API_KEY=sk-ant-xyzxyzxyzxyzxyzxyz1)", [], 0, 0);

        var bundle = new DiagnosticsBundle { SettingsPath = settings, Session = snapshot, StartupLogPath = startup, Home = dir };
        using var ms = new MemoryStream();
        await bundle.WriteAsync(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var all = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            all.Append("=== ").AppendLine(entry.FullName).AppendLine(await reader.ReadToEndAsync());
        }
        var text = all.ToString();
        Assert.Contains("=== settings.json", text);
        Assert.Contains("=== session.txt", text);
        Assert.Contains("=== startup.log", text);
        foreach (var secret in new[] { EnvSecret, ArgSecret, "sk-live-abcdefghijklmnop1234", "sk-ant-xyzxyzxyzxyzxyzxyz1", dir })
            Assert.DoesNotContain(secret, text);
        Assert.Contains("\"MY_PROVIDER_KEY\": \"[redacted]\"", text);
        Assert.Contains("\"profile\": \"work\"", text); // what helps diagnosis stays
        Assert.Contains("phase Faulted", text);
    }

    [Fact]
    public async Task Secret_flags_in_any_argument_list_are_masked()
    {
        // Regression (review round 4): only extraArgs was scanned; a key in prefixArgs stayed in clear text, because the
        // indented JSON puts the flag and its value on separate lines where the pattern does not reach.
        var dir = TestProcesses.TempDir("diag-args");
        var settings = Path.Combine(dir, "omp-gui.local.json");
        await File.WriteAllTextAsync(settings, """
            { "command": "bun", "prefixArgs": ["--no-install", "cli.ts", "--api-key", "opaque-prefix-secret-1", "--auth-token=joined-secret-2", "--model", "keep/me"] }
            """);
        using var ms = new MemoryStream();
        await new DiagnosticsBundle { SettingsPath = settings, Home = dir }.WriteAsync(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
        var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain("opaque-prefix-secret-1", text);
        Assert.DoesNotContain("joined-secret-2", text);
        Assert.Contains("--auth-token=[redacted]", text);
        Assert.Contains("keep/me", text);
        Assert.Contains("--no-install", text);
    }

    /// <summary>
    /// Settings a user edited by hand into shapes the client does not expect. The planted value has no credential shape,
    /// so only the settings structure tells it is secret; it must be masked in settings.json and in a log that echoes it,
    /// and the bundle must still be written.
    /// </summary>
    [Theory]
    [InlineData("""{ "environment": ["MY_KEY=opaque-shape-8841"] }""")]                          // environment as a list
    [InlineData("""{ "environment": "MY_KEY=opaque-shape-8841" }""")]                            // environment as one string
    [InlineData("""{ "environment": { "MY_KEY": { "value": "opaque-shape-8841" } } }""")]        // nested value
    [InlineData("""{ "environment": { "MY_KEY": ["opaque-shape-8841"] } }""")]                   // list value
    [InlineData("""{ "Environment": { "MY_KEY": "opaque-shape-8841" } }""")]                     // other case
    [InlineData("""{ "environment": { "A": "x" }, "environment": { "MY_KEY": "opaque-shape-8841" } }""")] // duplicate key
    [InlineData("""[ { "environment": { "MY_KEY": "opaque-shape-8841" } } ]""")]                // not an object
    [InlineData("""{ "profiles": { "work": { "environment": { "MY_KEY": "opaque-shape-8841" } } } }""")] // deeper
    [InlineData("""{ "apiKey": "opaque-shape-8841" }""")]                                        // a key-named field
    [InlineData("""{ "extraArgs": ["--api-key", 7, "--token", "opaque-shape-8841"] }""")]        // mixed list
    [InlineData("""{ "extraArgs": [["--api-key", "opaque-shape-8841"]] }""")]                    // nested list
    [InlineData("\uFEFF{ \"environment\": { \"MY_KEY\": \"opaque-shape-8841\" } }")]            // byte order mark
    public async Task Hand_edited_settings_of_unexpected_shapes_leak_nothing(string json)
    {
        var dir = TestProcesses.TempDir("diag-shapes");
        var settings = Path.Combine(dir, "omp-gui.local.json");
        await File.WriteAllTextAsync(settings, json);
        var startup = Path.Combine(dir, "startup.log");
        await File.WriteAllTextAsync(startup, "provider said: bad credentials opaque-shape-8841\n");
        using var ms = new MemoryStream();
        await new DiagnosticsBundle { SettingsPath = settings, StartupLogPath = startup, Home = dir }.WriteAsync(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var text = string.Join("\n", zip.Entries.Select(e => e.FullName + "\n" + new StreamReader(e.Open()).ReadToEnd()));
        Assert.Contains("settings.json", text);
        Assert.Contains("startup.log", text);
        Assert.DoesNotContain("opaque-shape-8841", text);
    }

    [Fact]
    public async Task Values_of_a_settings_file_that_does_not_parse_are_still_masked_elsewhere()
    {
        // Regression (Windows CI): a settings file that did not parse (an unescaped Windows path) gave no known values,
        // so an environment value echoed into a log was not masked.
        var dir = TestProcesses.TempDir("diag-broken");
        var settings = Path.Combine(dir, "omp-gui.local.json");
        await File.WriteAllTextAsync(settings, "{ \"environment\": { \"K\": \"opaque-value-5521\", \"HOME\": \"C:\\Users\\x\" } ");
        var startup = Path.Combine(dir, "startup.log");
        await File.WriteAllTextAsync(startup, "provider said: opaque-value-5521 is not valid\n");
        using var ms = new MemoryStream();
        await new DiagnosticsBundle { SettingsPath = settings, StartupLogPath = startup, Home = dir }.WriteAsync(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var text = string.Join("\n", zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.DoesNotContain("opaque-value-5521", text);
        Assert.Contains("not included", text);
    }

    [Fact]
    public async Task A_settings_file_that_does_not_parse_is_left_out()
    {
        var dir = TestProcesses.TempDir("diag-bad");
        var settings = Path.Combine(dir, "omp-gui.local.json");
        await File.WriteAllTextAsync(settings, "{ \"environment\": { \"K\": \"half-written-secret-value");
        using var ms = new MemoryStream();
        await new DiagnosticsBundle { SettingsPath = settings, Home = dir }.WriteAsync(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
        var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain("half-written-secret-value", text);
        Assert.Contains("not included", text);
    }

    /// <summary>Keeps the bytes after the writer disposes the stream.</summary>
    private sealed class KeptStream : MemoryStream
    {
        public byte[] Saved { get; private set; } = [];
        protected override void Dispose(bool disposing) { Saved = ToArray(); base.Dispose(disposing); }
    }

    [AvaloniaTheory]
    [InlineData(1100, 760)]
    [InlineData(480, 640)]
    public async Task Settings_saves_a_redacted_bundle_where_the_user_chooses(double width, double height)
    {
        var dir = TestProcesses.TempDir("diag-ui");
        var store = new ClientSettingsStore(Path.Combine(dir, "omp-gui.local.json"));
        store.Update(o => o with { Environment = new() { ["PROVIDER_KEY"] = "ui-planted-secret-4411" } });
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), settings: store)
        {
            Updates = new UpdateChecker(new HttpClient(), UpdateChecker.DefaultFeed, "0.1.0", "linux-x64"),
        };
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        var saved = new KeptStream();
        string? suggested = null;
        vm.SaveFileRequested += name => { suggested = name; return Task.FromResult<Stream?>(saved); };
        vm.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var button = w.FindControl<Button>("SaveDiagnosticsButton")!;
        button.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Shot(w, $"ui-settings-updates-diagnostics-{width}x{height}");
        button.Command!.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (saved.Saved.Length == 0 && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        Assert.Matches(@"^omp-gui-diagnostics-\d{8}-\d{6}\.zip$", suggested);
        using var zip = new ZipArchive(new MemoryStream(saved.Saved));
        Assert.Contains(zip.Entries, e => e.FullName == "settings.json");
        using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
        Assert.DoesNotContain("ui-planted-secret-4411", await reader.ReadToEndAsync());
        Assert.StartsWith("Diagnostics saved.", vm.SettingsMessage);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Cancelling_the_save_dialog_writes_nothing()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        vm.SaveFileRequested += _ => Task.FromResult<Stream?>(null);
        await vm.SaveDiagnosticsCommand.ExecuteAsync(null);
        Assert.Equal("", vm.SettingsMessage);
        await vm.DisposeAsync();
    }

    private static void Shot(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(dir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(dir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
