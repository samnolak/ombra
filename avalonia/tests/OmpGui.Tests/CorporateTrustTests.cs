using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Browser;
using OmpGui.ClientCore.Network;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// Settings › Advanced › Corporate network certificates: every omp launch gets the trust variable of the chosen mode
/// (and only then), the user's own variables win, a certificate file is checked before the app keeps a copy, turning it
/// off removes only the app's file, and open chats restart with it (idle ones now, a working one after its run).
/// </summary>
public sealed class CorporateTrustTests
{
    private static readonly Func<string, string?> Clean = _ => null;

    private static string Bundle(string dir)
    {
        var path = Path.Combine(dir, "network-trust", CorporateTrust.BundleFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, CorporateTrustCerts.Ca().ExportCertificatePem());
        return path;
    }

    // ── Every launch path ──

    [Fact]
    public async Task Every_launch_path_gets_the_trust_variable_of_the_chosen_mode()
    {
        var dir = TestProcesses.TempDir("corp-trust-launch");
        var bundle = Bundle(dir);
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var defaults = new AgentBrowserDefaults(bridge, Path.Combine(dir, "agent-browser"), inherited: Clean);

        foreach (var (mode, variable, value) in new[]
                 {
                     (CorporateTrust.PemMode, CorporateTrust.ExtraCaVariable, bundle),
                     (CorporateTrust.SystemMode, CorporateTrust.SystemCaVariable, CorporateTrust.SystemModeSupported ? "1" : null),
                 })
        {
            var o = new OmpRuntimeOptions { Command = "omp", CorporateTrust = mode, CorporateTrustBundle = mode == CorporateTrust.PemMode ? bundle : null, InheritedEnvironment = Clean };
            var specs = new[]
            {
                o.ToLaunchSpec(new LaunchRequest(dir)), o.ToTuiLaunchSpec(dir), o.ToCliLaunchSpec(["plugin", "list"], dir),
                defaults.ForSession(o, new LaunchRequest(dir)), defaults.ForTui(o, dir),
            };
            foreach (var s in specs)
            {
                Assert.Equal(value, s.Environment.GetValueOrDefault(variable));
                Assert.False(s.Environment.ContainsKey(variable == CorporateTrust.ExtraCaVariable ? CorporateTrust.SystemCaVariable : CorporateTrust.ExtraCaVariable));
            }
            // The terminal tab with omp's own UI (the terminal can only add variables)
            var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs()) { OmpTuiLaunch = d => defaults.ForTui(o, d) };
            vm.OpenOmpTuiCommand.Execute(null);
            Assert.Equal(value, Assert.Single(vm.Terminals).Environment.GetValueOrDefault(variable));
            await vm.DisposeAsync();
        }

        // Off (and a file mode whose copy is gone): nothing is added
        foreach (var o in new[]
                 {
                     new OmpRuntimeOptions { Command = "omp", InheritedEnvironment = Clean },
                     new OmpRuntimeOptions { Command = "omp", CorporateTrust = CorporateTrust.PemMode, CorporateTrustBundle = Path.Combine(dir, "gone.pem"), InheritedEnvironment = Clean },
                 })
            foreach (var s in new[] { o.ToLaunchSpec(), o.ToTuiLaunchSpec(dir), defaults.ForSession(o, new LaunchRequest(dir)) })
            {
                Assert.False(s.Environment.ContainsKey(CorporateTrust.SystemCaVariable));
                Assert.False(s.Environment.ContainsKey(CorporateTrust.ExtraCaVariable));
            }
    }

    [Fact]
    public async Task Chats_their_siblings_and_restarts_all_start_with_it()
    {
        var dir = TestProcesses.TempDir("corp-trust-chats");
        var log = Path.Combine(dir, "launches.jsonl");
        var bundle = Bundle(dir);
        var fake = TestProcesses.Fake("normal");
        var o = new OmpRuntimeOptions
        {
            Command = fake.FileName, PrefixArgs = [.. fake.Arguments], InheritedEnvironment = Clean,
            Environment = new() { ["FAKE_OMP_LAUNCH_LOG"] = log, ["FAKE_SESSION_DIR"] = dir },
            CorporateTrust = CorporateTrust.PemMode, CorporateTrustBundle = bundle,
        };
        await using var first = new SessionController(o.ToLaunchSpec, new LaunchRequest(dir, ApprovalMode: "write"));
        await first.StartAsync();
        await using var second = first.Sibling(new LaunchRequest(dir, ApprovalMode: "write"));
        await second.StartAsync();
        await first.SetApprovalModeAsync("always-ask");
        var starts = File.ReadAllLines(log).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(3, starts.Count);
        Assert.All(starts, s => Assert.Equal(bundle, s["env"]![CorporateTrust.ExtraCaVariable]?.GetValue<string>()));
    }

    // ── The user's own variables ──

    [Fact]
    public void The_users_own_variables_win_and_conflicts_are_shown_not_guessed()
    {
        var dir = TestProcesses.TempDir("corp-trust-user");
        var bundle = Bundle(dir);
        OmpRuntimeOptions Opt(string mode, Dictionary<string, string?>? env = null, Func<string, string?>? inherited = null) =>
            new() { Command = "omp", CorporateTrust = mode, CorporateTrustBundle = bundle, Environment = env ?? [], InheritedEnvironment = inherited ?? Clean };
        Func<string, string?> Inh(string k, string v) => x => x == k ? v : null;

        // System mode: a choice of their own stays theirs, the app adds nothing
        var mine = Opt(CorporateTrust.SystemMode, new() { [CorporateTrust.SystemCaVariable] = "0" });
        Assert.Equal("0", mine.ToLaunchSpec().Environment[CorporateTrust.SystemCaVariable]);
        var inheritedOff = Opt(CorporateTrust.SystemMode, inherited: Inh(CorporateTrust.SystemCaVariable, "0"));
        Assert.False(inheritedOff.ToLaunchSpec().Environment.ContainsKey(CorporateTrust.SystemCaVariable));
        var removed = Opt(CorporateTrust.SystemMode, new() { [CorporateTrust.SystemCaVariable] = null });
        Assert.Null(removed.ToLaunchSpec().Environment[CorporateTrust.SystemCaVariable]);
        var bundled = Opt(CorporateTrust.SystemMode, inherited: Inh(CorporateTrust.NodeOptionsVariable, "--max-old-space-size=4096 --use-bundled-ca"));
        Assert.False(bundled.ToLaunchSpec().Environment.ContainsKey(CorporateTrust.SystemCaVariable));

        var conflict = CorporateTrust.Inspect(new Dictionary<string, string?>(), Inh(CorporateTrust.NodeOptionsVariable, "--max-old-space-size=4096 --use-bundled-ca"));
        Assert.True(conflict.SystemCaConflict);
        Assert.Equal("NODE_OPTIONS contains --use-bundled-ca (in the environment the app was started with)", conflict.NodeOptionsFlag!.Describe());
        Assert.True(CorporateTrust.Inspect(new Dictionary<string, string?> { [CorporateTrust.SystemCaVariable] = "0" }, Clean).SystemCaConflict);
        var already = CorporateTrust.Inspect(new Dictionary<string, string?>(), Inh(CorporateTrust.NodeOptionsVariable, "--use-system-ca"));
        Assert.True(already.SystemCaAlreadyOn);
        Assert.False(already.SystemCaConflict);
        Assert.True(CorporateTrust.Inspect(new Dictionary<string, string?>(), Inh(CorporateTrust.SystemCaVariable, "1")).SystemCaAlreadyOn);

        // System mode never touches NODE_EXTRA_CA_CERTS: theirs applies too
        var both = Opt(CorporateTrust.SystemMode, inherited: Inh(CorporateTrust.ExtraCaVariable, "/home/me/team-ca.pem"));
        if (CorporateTrust.SystemModeSupported) Assert.Equal("1", both.ToLaunchSpec().Environment[CorporateTrust.SystemCaVariable]);
        Assert.False(both.ToLaunchSpec().Environment.ContainsKey(CorporateTrust.ExtraCaVariable));

        // File mode: their NODE_EXTRA_CA_CERTS (inherited or in the settings) is never replaced
        var inheritedFile = Opt(CorporateTrust.PemMode, inherited: Inh(CorporateTrust.ExtraCaVariable, "/home/me/team-ca.pem"));
        Assert.False(inheritedFile.ToLaunchSpec().Environment.ContainsKey(CorporateTrust.ExtraCaVariable));
        var settingsFile = Opt(CorporateTrust.PemMode, new() { [CorporateTrust.ExtraCaVariable] = "/home/me/team-ca.pem" });
        Assert.Equal("/home/me/team-ca.pem", settingsFile.ToLaunchSpec().Environment[CorporateTrust.ExtraCaVariable]);
        // …but ours, inherited from a process the app started, is ours
        var ours = Opt(CorporateTrust.PemMode, inherited: Inh(CorporateTrust.ExtraCaVariable, bundle));
        Assert.Equal(bundle, ours.ToLaunchSpec().Environment[CorporateTrust.ExtraCaVariable]);
    }

    // ── The certificate file ──

    [Fact]
    public void A_certificate_file_must_hold_only_valid_certificate_authorities()
    {
        var now = DateTimeOffset.UtcNow;
        var ca = CorporateTrustCerts.Ca();
        var second = CorporateTrustCerts.Ca("CN=Synthetic Test Issuing CA");
        CaFileCheck Check(string text) => CorporateCaFile.Validate(Encoding.ASCII.GetBytes(text), now);

        var one = Check(ca.ExportCertificatePem());
        Assert.True(one.Ok, one.Message);
        Assert.Equal(1, one.Count);
        Assert.StartsWith("1 certificate authority, valid until ", one.Message);
        Assert.DoesNotContain("Synthetic", one.Message); // count and validity only, never names
        var two = Check(ca.ExportCertificatePem() + "\n" + second.ExportCertificatePem() + "\n" + ca.ExportCertificatePem());
        Assert.True(two.Ok, two.Message);
        Assert.Equal(2, two.Count); // a repeated one counts once
        Assert.Equal(2, two.Pem!.Split("BEGIN CERTIFICATE").Length - 1);
        Assert.True(CorporateCaFile.Validate(ca.RawData, now).Ok); // DER (.cer)

        var server = CorporateTrustCerts.Server(ca);
        Assert.Equal("not-ca", Check(server.Cert.ExportCertificatePem()).Code);
        Assert.Equal("not-ca", Check(ca.ExportCertificatePem() + server.Cert.ExportCertificatePem()).Code); // a leaf among CAs
        Assert.Equal("not-ca", Check(CorporateTrustCerts.Ca(keyCertSign: false).ExportCertificatePem()).Code);
        Assert.Equal("private-key", Check(server.Cert.ExportCertificatePem() + server.KeyPem).Code);
        Assert.Equal("private-key", Check(ca.ExportCertificatePem() + server.KeyPem).Code);
        Assert.Equal("expired", Check(CorporateTrustCerts.Ca(from: now.AddDays(-30), to: now.AddDays(-1)).ExportCertificatePem()).Code);
        Assert.Equal("not-yet-valid", Check(CorporateTrustCerts.Ca(from: now.AddDays(2), to: now.AddDays(30)).ExportCertificatePem()).Code);
        Assert.Equal("garbage", Check("hello, this is not a certificate").Code);
        Assert.Equal("garbage", Check("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n").Code);
        Assert.Equal("no-certificate", CorporateCaFile.Validate([], now).Code);
        Assert.Equal("unsupported", Check("-----BEGIN PKCS7-----\nAAAA\n-----END PKCS7-----\n").Code);
    }

    [Fact]
    public void The_apps_copy_is_readable_only_by_the_user_and_removal_takes_only_it()
    {
        var dir = TestProcesses.TempDir("corp-trust-install");
        var bundle = CorporateTrust.BundlePathFor(Path.Combine(dir, "omp-gui.local.json"));
        CorporateCaFile.Install(CorporateTrustCerts.Ca().ExportCertificatePem(), bundle);
        Assert.True(CorporateCaFile.Validate(bundle, DateTimeOffset.UtcNow).Ok);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(bundle));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(bundle)!));
        }
        var neighbour = Path.Combine(Path.GetDirectoryName(bundle)!, "someone-elses.txt");
        File.WriteAllText(neighbour, "x");
        CorporateCaFile.Remove(bundle);
        Assert.False(File.Exists(bundle));
        Assert.True(File.Exists(neighbour)); // the folder stays while anything else is in it
    }

    [Fact]
    public async Task Diagnostics_include_the_mode_but_never_the_file()
    {
        var dir = TestProcesses.TempDir("corp-trust-diag");
        var settings = Path.Combine(dir, "omp-gui.local.json");
        var bundle = CorporateTrust.BundlePathFor(settings);
        File.WriteAllText(settings, $$"""{ "corporateTrust": "pem", "corporateTrustBundle": {{System.Text.Json.JsonSerializer.Serialize(bundle)}} }""");
        using var ms = new MemoryStream();
        await new DiagnosticsBundle { SettingsPath = settings, Home = "/nonexistent-home" }.WriteAsync(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var all = string.Join("\n", zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.Contains("\"corporateTrust\": \"pem\"", all);
        Assert.DoesNotContain(CorporateTrust.BundleFileName, all);
    }

    // ── The page and the restarts ──

    private sealed record Opened(MainWindow W, MainViewModel Vm, ClientSettingsStore Store, string Log);

    /// <summary>The window over the fake omp, each launch built from the saved settings as the app does (App.Current).</summary>
    private static async Task<Opened> OpenAsync(string scenario = "normal", double height = 900)
    {
        var dir = TestProcesses.TempDir("corp-trust-ui");
        var store = new ClientSettingsStore(Path.Combine(dir, "settings", "omp-gui.local.json"));
        var log = Path.Combine(dir, "launches.jsonl");
        var project = SessionCatalog.ResolveLinks(TestProcesses.TempDir("corp-trust-project"));
        var fake = TestProcesses.Fake(scenario);
        OmpLaunchSpec Launch(LaunchRequest r) => (store.Load() with
        {
            Command = fake.FileName, PrefixArgs = [.. fake.Arguments], InheritedEnvironment = Clean,
            Environment = new() { ["FAKE_OMP_LAUNCH_LOG"] = log, ["FAKE_SESSION_DIR"] = dir },
        }).ToLaunchSpec(r);
        var s = new SessionController(Launch, new LaunchRequest(project, ApprovalMode: "write"));
        var vm = new MainViewModel(s, new AppArgs(), settings: store);
        vm.NetworkTrust.Inherited = Clean;
        vm.NetworkTrust.SystemModeAvailable = true;
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return new(w, vm, store, log);
    }

    private static List<JsonNode> Starts(string log) => File.Exists(log) ? [.. File.ReadAllLines(log).Select(l => JsonNode.Parse(l)!)] : [];

    private static string? LastEnv(string log, string key) => Starts(log).Last()["env"]![key]?.GetValue<string>();

    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Enabling_explains_first_then_restarts_omp_and_turning_off_undoes_it()
    {
        var (w, vm, store, log) = await OpenAsync();
        var page = vm.NetworkTrust;
        await vm.OpenSettingsAtAsync("advanced");
        Assert.Equal(NetworkTrustViewModel.OffText, page.StatusText);
        var pid = vm.Session.ProcessId;

        page.BeginSetupCommand.Execute(null);
        Assert.True(page.IsExplainingSystem);
        Assert.Null(store.Load().CorporateTrust); // nothing changes before Enable
        Assert.True(w.FindControl<OmpGui.App.Views.Settings.NetworkSettings>("NetworkSettingsPage")!.FindControl<Border>("NetworkTrustExplain")!.IsEffectivelyVisible);

        await page.EnableCommand.ExecuteAsync(null);
        Assert.Equal(CorporateTrust.SystemMode, store.Load().CorporateTrust);
        Assert.Null(store.Load().CorporateTrustBundle);
        await Until(() => vm.Session.ProcessId != pid && vm.Phase == SessionPhase.Ready, "omp restarted");
        if (CorporateTrust.SystemModeSupported) Assert.Equal("1", LastEnv(log, CorporateTrust.SystemCaVariable));
        Assert.StartsWith("On", page.StatusText);
        Assert.Contains("omp restarted", page.Message);

        // Again: nothing changes, nothing restarts
        pid = vm.Session.ProcessId;
        var starts = Starts(log).Count;
        page.BeginSetupCommand.Execute(null);
        await page.EnableCommand.ExecuteAsync(null);
        Assert.Contains("nothing changed", page.Message);
        Assert.Equal(pid, vm.Session.ProcessId);
        Assert.Equal(starts, Starts(log).Count);

        await page.TurnOffCommand.ExecuteAsync(null);
        Assert.Null(store.Load().CorporateTrust);
        await Until(() => vm.Session.ProcessId != pid && vm.Phase == SessionPhase.Ready, "omp restarted without it");
        Assert.Equal(Environment.GetEnvironmentVariable(CorporateTrust.SystemCaVariable), LastEnv(log, CorporateTrust.SystemCaVariable)); // only what the test process has
        Assert.Equal(NetworkTrustViewModel.OffText, page.StatusText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_working_chat_keeps_its_run_and_restarts_with_it_after()
    {
        var (w, vm, store, log) = await OpenAsync("multi");
        vm.ComposerText = "FOREVER in A";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "A working");
        var a = vm.Session;
        var pidA = a.ProcessId;
        vm.NewSessionCommand.Execute(null);
        await Until(() => vm.Session != a && vm.Phase == SessionPhase.Ready, "B ready");
        var b = vm.Session;
        var pidB = b.ProcessId;

        var page = vm.NetworkTrust;
        page.UseFileInsteadCommand.Execute(null);
        var ca = CorporateTrustCerts.Write(TestProcesses.TempDir("corp-trust-file"), "team.pem", CorporateTrustCerts.Ca().ExportCertificatePem());
        page.ChooseFile(ca);
        await page.EnableCommand.ExecuteAsync(null);
        var bundle = CorporateTrust.BundlePathFor(store.Path);
        await Until(() => b.ProcessId != pidB && b.Snapshot().Phase == SessionPhase.Ready, "the idle chat restarted now");
        Assert.Equal(pidA, a.ProcessId); // the working one keeps its run
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        Assert.Contains("restarts omp with the change when its run ends", page.RestartNote);
        Assert.Equal(bundle, LastEnv(log, CorporateTrust.ExtraCaVariable));

        await a.AbortAsync();
        await Until(() => a.ProcessId != pidA && a.Snapshot().Phase == SessionPhase.Ready, "the working chat restarted after its run");
        Assert.All(Starts(log).TakeLast(2), s => Assert.Equal(bundle, s["env"]![CorporateTrust.ExtraCaVariable]?.GetValue<string>()));
        await Until(() => !page.HasRestartNote, "the note gone");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_file_is_checked_copied_kept_once_and_only_the_apps_copy_is_removed()
    {
        var (w, vm, store, log) = await OpenAsync();
        var page = vm.NetworkTrust;
        await vm.OpenSettingsAtAsync("advanced");
        var dir = TestProcesses.TempDir("corp-trust-files");
        var good = CorporateTrustCerts.Write(dir, "team.pem", CorporateTrustCerts.Ca().ExportCertificatePem());
        var leaf = CorporateTrustCerts.Write(dir, "server.pem", CorporateTrustCerts.Server(CorporateTrustCerts.Ca()).Cert.ExportCertificatePem());

        page.UseFileInsteadCommand.Execute(null);
        Assert.False(page.CanEnable); // nothing chosen yet
        page.ChooseFile(leaf);
        Assert.False(page.CanEnable);
        Assert.Contains("not a certificate authority", page.ChosenFileText);
        page.ChooseFile(good);
        Assert.True(page.CanEnable);
        var pid = vm.Session.ProcessId;
        await page.EnableCommand.ExecuteAsync(null);
        var bundle = CorporateTrust.BundlePathFor(store.Path);
        Assert.Equal((CorporateTrust.PemMode, bundle), (store.Load().CorporateTrust, store.Load().CorporateTrustBundle));
        Assert.True(File.Exists(bundle));
        await Until(() => vm.Session.ProcessId != pid && vm.Phase == SessionPhase.Ready, "omp restarted");
        Assert.Equal(bundle, LastEnv(log, CorporateTrust.ExtraCaVariable));
        Assert.StartsWith("On: omp also trusts 1 certificate authority", page.StatusText);

        // Set up again with the same certificates: checked again, nothing changes
        pid = vm.Session.ProcessId;
        page.BeginSetupCommand.Execute(null);
        page.UseFileInsteadCommand.Execute(null);
        Assert.True(page.ChosenFileOk); // the app's copy, checked again
        page.ChooseFile(good);
        await page.EnableCommand.ExecuteAsync(null);
        Assert.Contains("nothing changed", page.Message);
        Assert.Equal(pid, vm.Session.ProcessId);

        await page.TurnOffCommand.ExecuteAsync(null);
        Assert.False(File.Exists(bundle));
        Assert.False(Directory.Exists(Path.GetDirectoryName(bundle)));
        Assert.True(File.Exists(good)); // the user's own file is never touched
        Assert.Null(store.Load().CorporateTrustBundle);
        await Until(() => vm.Session.ProcessId != pid && vm.Phase == SessionPhase.Ready, "omp restarted without it");
        Assert.Equal(Environment.GetEnvironmentVariable(CorporateTrust.ExtraCaVariable), LastEnv(log, CorporateTrust.ExtraCaVariable)); // only what the test process has
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_users_own_variables_block_enabling_with_an_explanation()
    {
        var (w, vm, store, _) = await OpenAsync();
        var page = vm.NetworkTrust;
        page.Inherited = k => k switch
        {
            CorporateTrust.SystemCaVariable => "0",
            CorporateTrust.ExtraCaVariable => "/home/me/team-ca.pem",
            _ => null,
        };
        await vm.OpenSettingsAtAsync("advanced");
        Assert.Contains("NODE_USE_SYSTEM_CA=0", page.UserSettingsText);
        page.BeginSetupCommand.Execute(null);
        Assert.Contains("NODE_USE_SYSTEM_CA=0", page.Blocker);
        Assert.False(page.CanEnable);
        page.UseFileInsteadCommand.Execute(null);
        page.ChooseFile(CorporateTrustCerts.Write(TestProcesses.TempDir("corp-trust-block"), "team.pem", CorporateTrustCerts.Ca().ExportCertificatePem()));
        Assert.True(page.ChosenFileOk);
        Assert.Contains("NODE_EXTRA_CA_CERTS is set", page.Blocker);
        Assert.DoesNotContain("/home/me", page.Blocker); // their path is not repeated
        Assert.False(page.CanEnable);
        await page.EnableCommand.ExecuteAsync(null);
        Assert.Null(store.Load().CorporateTrust);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_card_and_the_settings_row_in_light_and_dark()
    {
        var shots = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(shots);
        var (w, vm, _, _) = await OpenAsync(height: 1500);
        var page = vm.NetworkTrust;
        await vm.OpenSettingsAtAsync("advanced");
        void Shot(string name)
        {
            Dispatcher.UIThread.RunJobs();
            w.FindControl<Control>("NetworkSettingsPage")!.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            using var frame = w.CaptureRenderedFrame();
            using var file = File.Create(Path.Combine(shots, name + ".png"));
            frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        foreach (var theme in new[] { "light", "dark" })
        {
            vm.SettingsTheme = theme;
            page.CancelCommand.Execute(null);
            Shot($"corp-trust-row-{theme}");
            page.BeginSetupCommand.Execute(null);
            Shot($"corp-trust-card-system-{theme}");
            page.UseFileInsteadCommand.Execute(null);
            page.ChooseFile(CorporateTrustCerts.Write(TestProcesses.TempDir("corp-trust-shot"), "team.pem", CorporateTrustCerts.Ca().ExportCertificatePem()));
            Shot($"corp-trust-card-file-{theme}");
        }
        // On (dark is still the theme): the status line and what happened
        await page.EnableCommand.ExecuteAsync(null);
        Shot("corp-trust-on-dark");
        vm.SettingsTheme = "system";
        await vm.DisposeAsync();
        w.Close();
    }
}
