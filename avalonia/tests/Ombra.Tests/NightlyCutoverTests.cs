using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Ombra.App.Localization;
using Ombra.App.Nightly;
using Ombra.App.ViewModels;
using Ombra.ClientCore.Browser;

namespace Ombra.Tests;

/// <summary>
/// The K1 cutover: Nightly quits by itself before it is ended (graceful profile shutdown), protocol 4's messages, the
/// page's questions answered through the preview's own cards (<see cref="NightlyDialogs"/>), the pinned runtime package
/// (<see cref="NightlyRuntimePackage"/>), and the pane falling back to the built-in preview with the reason.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class NightlyCutoverTests
{
    // ── graceful shutdown (stand-in processes) ──

    private sealed class Serial
    {
        private readonly object _gate = new();
        public void Post(Action a) => ThreadPool.QueueUserWorkItem(_ => { lock (_gate) a(); });
        public T Run<T>(Func<T> f) { lock (_gate) return f(); }
        public void Run(Action a) { lock (_gate) a(); }
    }

    /// <summary>A shell with a child that quits cleanly on SIGTERM (as Nightly on Quit) or, <paramref name="stubborn"/>, ignores it
    /// (its children live 1 s each: nothing outlives a failed test long enough to hold the test host's output open).</summary>
    private static Process StandIn(bool stubborn) =>
        Process.Start(new ProcessStartInfo("/bin/sh", ["-c", stubborn ? "trap '' TERM; while :; do sleep 1; done" : "trap 'exit 0' TERM; sleep 600 & wait"])
        { UseShellExecute = false })!;

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task A_stop_on_purpose_lets_the_process_quit_by_itself_then_ends_what_it_left()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "libproc child lookup is macOS");
        var serial = new Serial();
        Process? started = null;
        using var supervisor = new NightlySupervisor(() => started = StandIn(stubborn: false), serial.Post, TimeSpan.FromSeconds(30));
        serial.Run(supervisor.Start);
        await Until(() => NightlyMach.Children(started!.Id).Count == 1, "child started");
        var child = NightlyMach.Children(started!.Id)[0];
        await Task.Delay(1300); // the supervisor records descendants every second
        serial.Run(supervisor.MarkReady);

        try
        {
            var asked = 0;
            var ending = serial.Run(() => supervisor.Stop(() =>
            {
                asked++;
                NightlyMach.Signal(started.Id, 15);
                return true;
            }, TimeSpan.FromSeconds(10)));
            Assert.Equal(NightlyState.Stopped, serial.Run(() => supervisor.State));
            await ending.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, asked);
            Assert.True(started.HasExited);
            Assert.Equal(0, started.ExitCode); // quit by itself, not killed
            await Until(() => !Alive(child), "the child it left was ended");
        }
        finally
        {
            // A leftover `sleep 600` holds the test host's output open.
            if (Alive(child)) NightlyMach.Signal(child, 9);
        }
    }

    [Fact]
    public async Task A_process_that_does_not_quit_within_the_grace_is_ended_and_the_next_start_waits_for_it()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "libproc child lookup is macOS");
        var serial = new Serial();
        var starts = new List<Process>();
        using var supervisor = new NightlySupervisor(() =>
        {
            var p = StandIn(stubborn: true);
            starts.Add(p);
            return p;
        }, serial.Post, TimeSpan.FromSeconds(30));
        try
        {
            serial.Run(supervisor.Start);
            var first = starts[0];
            serial.Run(supervisor.MarkReady);
            // Asked, but it never quits (a hung Nightly): only the grace ends it.
            var ending = serial.Run(() => supervisor.Stop(() => true, TimeSpan.FromSeconds(1.5)));
            Assert.False(ending.IsCompleted);
            // A start meanwhile waits: one profile, one process.
            serial.Run(supervisor.Start);
            Assert.Equal(NightlyState.Starting, serial.Run(() => supervisor.State));
            Assert.Equal(1, serial.Run(() => starts.Count));
            await ending.WaitAsync(TimeSpan.FromSeconds(10));
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); // SIGKILL reaped
            Assert.NotEqual(0, first.ExitCode); // killed after the grace
            await Until(() => serial.Run(() => starts.Count == 2), "started after the previous one ended");
            serial.Run(supervisor.Stop);
        }
        finally
        {
            // A leftover `sleep` holds the test host's output open: end every stand-in tree whatever failed.
            foreach (var p in serial.Run(() => starts.ToArray())) NightlySupervisor.KillTree(p.Id);
        }
    }

    // ── protocol 4 ──

    [Fact]
    public void Protocol_four_messages_have_the_layout_of_the_native_structs()
    {
        var quit = NightlyProtocol.QuitMessage();
        Assert.Equal(32, quit.Length);
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(quit.AsSpan(20)));

        var cancel = NightlyProtocol.DownloadCancelMessage(3, 9);
        Assert.Equal(32, cancel.Length);
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(cancel.AsSpan(20)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(cancel.AsSpan(24)));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(cancel.AsSpan(28)));

        var reply = NightlyProtocol.DialogReplyMessage(3, 9, 0x1000, 12);
        Assert.Equal(64, reply.Length); // ScriptResultMsg
        Assert.Equal(41, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(20)));
        Assert.Equal(0x80000000u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(0)) & 0x80000000u);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(24)));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(40)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(44)));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(48)));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(56)));

        var dialog = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(dialog.AsSpan(44), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(dialog.AsSpan(48), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(dialog.AsSpan(56), 2);
        Assert.Equal((5u, 7u, "{}"), NightlyProtocol.ReadDialog(dialog, "{}"u8.ToArray()));
        BinaryPrimitives.WriteUInt32LittleEndian(dialog.AsSpan(56), 40); // longer than the payload: refused
        Assert.Null(NightlyProtocol.ReadDialog(dialog, "{}"u8.ToArray()));
        var closed = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(closed.AsSpan(24), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(closed.AsSpan(28), 7);
        Assert.Equal((5u, 7u), NightlyProtocol.ReadDialogClosed(closed));
    }

    // ── the page's questions ──

    private static (BrowserPageHooks Hooks, DialogBroker Broker) Page(BrowserSiteSettings? site = null)
    {
        var broker = new DialogBroker();
        return (new BrowserPageHooks { Broker = () => broker, SiteSettings = site }, broker);
    }

    private static async Task<BrowserDialog> Asked(DialogBroker broker)
    {
        await Until(() => broker.Current is not null, "card shown", 5);
        return broker.Current!;
    }

    [Fact]
    public async Task Javascript_dialogs_are_answered_on_the_preview_cards()
    {
        var (hooks, broker) = Page();
        var confirm = NightlyDialogs.AnswerAsync(hooks, 1, """{"kind":"confirm","origin":"https://a.test","message":"Delete?"}""", null, null, CancellationToken.None);
        var card = await Asked(broker);
        Assert.Equal(BrowserDialogKind.Confirm, card.Kind);
        Assert.Equal("Delete?", card.Message);
        card.Complete(true);
        Assert.Equal("""{"ok":true}""", await confirm);

        var prompt = NightlyDialogs.AnswerAsync(hooks, 2, """{"kind":"prompt","message":"Name?","default":"x"}""", new Uri("https://a.test/p"), null, CancellationToken.None);
        card = await Asked(broker);
        Assert.Equal("x", ((PromptRequest)card.Request).Default);
        Assert.Equal(new Uri("https://a.test/p"), card.Request.PageUrl);
        card.Complete("Ann");
        Assert.Equal("""{"ok":true,"value":"Ann"}""", await prompt);

        var alert = NightlyDialogs.AnswerAsync(hooks, 3, """{"kind":"alert","message":"Hi"}""", null, null, CancellationToken.None);
        (await Asked(broker)).Complete(null);
        Assert.Equal("{}", await alert);

        // Withdrawn by Nightly (navigation): the card goes with the nobody-answered answer.
        using var cts = new CancellationTokenSource();
        var leave = NightlyDialogs.AnswerAsync(hooks, 4, """{"kind":"beforeunload"}""", null, null, cts.Token);
        await Asked(broker);
        cts.Cancel();
        Assert.Equal("""{"ok":true}""", await leave);
        Assert.Null(broker.Current);
    }

    [Fact]
    public async Task Omp_credentials_answer_only_its_origin_never_a_foreign_frame_or_proxy()
    {
        var block = new TernBlock(1, 1);
        var page = new TernHostTests.FakePage(block, new TernOpenRequest(1, 800, 600));
        block.Attach(page);
        var hooks = new BrowserPageHooks { Broker = () => page.Broker, AgentActive = () => true };
        await block.DispatchAsync("credentials", new JsonObject { ["username"] = "agent", ["password"] = "pw", ["origin"] = "https://intranet.test" }, CancellationToken.None);
        await block.DispatchAsync("goto", new JsonObject { ["url"] = "https://intranet.test/" }, CancellationToken.None);
        var top = new Uri("https://intranet.test/");

        async Task Refused(string json)
        {
            var asked = NightlyDialogs.AnswerAsync(hooks, 1, json, top, null, CancellationToken.None);
            (await Asked(page.Broker)).Cancel();
            Assert.Equal("""{"ok":false}""", await asked);
        }

        // An iframe of another host answering 401, and a proxy: the user's card, not omp's password
        await Refused("""{"kind":"auth","host":"attacker.test","port":443,"realm":"x","proxy":false,"failed":false}""");
        await Refused("""{"kind":"auth","host":"intranet.test","port":443,"realm":"x","proxy":true,"failed":false}""");
        Assert.Equal("""{"ok":true,"user":"agent","password":"pw"}""",
            await NightlyDialogs.AnswerAsync(hooks, 2, """{"kind":"auth","host":"intranet.test","port":443,"realm":"x","proxy":false,"failed":false}""", top, null, CancellationToken.None));
    }

    [Fact]
    public async Task Sign_in_permissions_and_files_use_the_same_cards_and_remembered_sites()
    {
        var site = new BrowserSiteSettings();
        var (hooks, broker) = Page(site);
        var auth = NightlyDialogs.AnswerAsync(hooks, 1, """{"kind":"auth","host":"intranet.test","port":443,"realm":"Staff","proxy":false,"user":"ann","failed":true}""",
            null, null, CancellationToken.None);
        var card = await Asked(broker);
        var request = (CredentialRequest)card.Request;
        Assert.Equal(("intranet.test", 443, "Staff", true, "ann"), (request.Host, request.Port, request.Realm, request.FailedBefore, request.UserName));
        card.Complete(new CredentialAnswer("ann", "s3cret"));
        Assert.Equal("""{"ok":true,"user":"ann","password":"s3cret"}""", await auth);

        var permission = NightlyDialogs.AnswerAsync(hooks, 2, """{"kind":"permission","origin":"https://maps.test","permission":"geolocation"}""",
            null, null, CancellationToken.None);
        card = await Asked(broker);
        Assert.Equal(PermissionKind.Geolocation, ((PermissionRequest)card.Request).Permission);
        card.Complete(PermissionAnswer.AlwaysForSite);
        Assert.Equal("""{"answer":"always"}""", await permission);
        Assert.True(site.IsAlwaysAllowed("https://maps.test", PermissionKind.Geolocation));
        // Remembered: no card the second time.
        Assert.Equal("""{"answer":"always"}""",
            await NightlyDialogs.AnswerAsync(hooks, 3, """{"kind":"permission","origin":"https://maps.test","permission":"geolocation"}""", null, null, CancellationToken.None));

        var camera = NightlyDialogs.AnswerAsync(hooks, 4, """{"kind":"permission","origin":"https://call.test","permission":"camera-microphone"}""", null, null, CancellationToken.None);
        card = await Asked(broker);
        Assert.Equal(PermissionKind.CameraAndMicrophone, ((PermissionRequest)card.Request).Permission);
        card.Complete(PermissionAnswer.Deny);
        Assert.Equal("""{"answer":"deny"}""", await camera);

        var files = NightlyDialogs.AnswerAsync(hooks, 5, """{"kind":"file","multiple":true,"directory":false,"accept":[".png","image/*"]}""", null, null, CancellationToken.None);
        card = await Asked(broker);
        var chooser = (FileChooserRequest)card.Request;
        Assert.True(chooser.Multiple);
        Assert.Equal([".png", "image/*"], chooser.Accept);
        card.Complete((IReadOnlyList<string>)["/tmp/a.png", "/tmp/b.png"]);
        Assert.Equal("""{"paths":["/tmp/a.png","/tmp/b.png"]}""", await files);

        var none = NightlyDialogs.AnswerAsync(hooks, 6, """{"kind":"file"}""", null, null, CancellationToken.None);
        (await Asked(broker)).Complete(null);
        Assert.Equal("""{"paths":null}""", await none);
    }

    [Fact]
    public async Task A_download_asks_where_to_save_then_follows_nightlys_progress()
    {
        var (hooks, _) = Page();
        var changes = new List<BrowserDownloadState>();
        hooks.DownloadChanged += d => changes.Add(d.State);
        DownloadRequest? asked = null;
        hooks.ChooseDownloadPath = (r, _) =>
        {
            asked = r;
            return Task.FromResult<string?>("/tmp/report.pdf");
        };
        NightlyDialogs.StartedDownload? started = null;
        var answer = await NightlyDialogs.AnswerAsync(hooks, 8, """{"kind":"download","name":"../report.pdf","size":2048,"url":"https://a.test/r.pdf"}""",
            new Uri("https://a.test/"), d => started = d, CancellationToken.None);
        Assert.Equal("""{"path":"/tmp/report.pdf"}""", answer);
        Assert.Equal(("report.pdf", 2048L), (asked!.FileName, asked.Size!.Value));
        Assert.NotNull(started);
        Assert.Equal(8u, started.DialogId);
        Assert.Equal([BrowserDownloadState.Asking, BrowserDownloadState.Saving], changes);

        Assert.False(NightlyDialogs.ApplyDownload(started.Item, """{"state":"saving","received":1024,"total":2048,"path":"/tmp/report.pdf"}"""));
        Assert.Equal(0.5, started.Item.Progress);
        Assert.True(NightlyDialogs.ApplyDownload(started.Item, """{"state":"finished","received":2048,"total":2048,"path":"/tmp/report.pdf"}"""));
        Assert.Equal(BrowserDownloadState.Finished, started.Item.State);
        var failed = new BrowserDownload(null);
        Assert.True(NightlyDialogs.ApplyDownload(failed, """{"state":"failed","received":0,"total":-1,"error":"Network error"}"""));
        Assert.Equal(("Network error", BrowserDownloadState.Failed), (failed.Error, failed.State));

        // Not saved: Nightly gets no path, and the download is listed as cancelled.
        hooks.ChooseDownloadPath = (_, _) => Task.FromResult<string?>(null);
        Assert.Equal("""{"path":null}""", await NightlyDialogs.AnswerAsync(hooks, 9, """{"kind":"download","name":"x.zip"}""", null, null, CancellationToken.None));
        Assert.Equal(BrowserDownloadState.Canceled, changes[^1]);
    }

    // ── the pinned runtime package ──

    private static string Temp() => Directory.CreateTempSubdirectory("k1-pkg-").FullName;

    /// <summary>A Nightly.app stand-in zipped as pack-nightly.py does (ditto --keepParent); its pin.</summary>
    private static async Task<(string Zip, NightlyRuntimePackage.Manifest Pin)> FakePackage(string dir, string version, string build)
    {
        var app = Path.Combine(dir, "src", "Nightly.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
        await File.WriteAllTextAsync(Path.Combine(app, "Contents", "MacOS", "nightly"), "#!/bin/sh\n");
        await File.WriteAllTextAsync(Path.Combine(app, "Contents", "Resources", "application.ini"), $"[App]\nVersion={version}\nBuildID={build}\n\n[Gecko]\nMinVersion=1\n");
        var zip = Path.Combine(dir, $"nightly-runtime-{version}-{build}.zip");
        var ditto = Process.Start(new ProcessStartInfo("/usr/bin/ditto", ["-c", "-k", "--keepParent", app, zip]))!;
        await ditto.WaitForExitAsync();
        Assert.Equal(0, ditto.ExitCode);
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)));
        return (zip, new NightlyRuntimePackage.Manifest(version, build, NightlyProtocol.Version, sha, new FileInfo(zip).Length, Path.GetFileName(zip), null, new string('c', 40)));
    }

    [Fact]
    public void The_pin_is_built_into_the_client_and_speaks_this_protocol()
    {
        var pin = NightlyRuntimePackage.Pinned;
        Assert.NotNull(pin);
        Assert.Equal(NightlyProtocol.Version, pin.Protocol);
        Assert.Equal(64, pin.Sha256.Length);
        Assert.True(NightlyRuntimePackage.IsCdhash(pin.Cdhash), pin.Cdhash);
        Assert.Null(NightlyRuntimePackage.Parse("""{"version":"1","buildId":"2","protocol":4,"sha256":"nothex","size":1,"archive":"a.zip","cdhash":"0123456789abcdef0123456789abcdef01234567"}"""));
    }

    [Theory]
    [InlineData(""",""")]                                                          // no cdhash: no pin
    [InlineData(""","cdhash":"0123456789ABCDEF0123456789ABCDEF01234567",""")]      // not as codesign prints it
    [InlineData(""","cdhash":"0123456789abcdef0123456789abcdef0123456",""")]       // 39 digits
    [InlineData(""","cdhash":"0123456789abcdef0123456789abcdef01234567\" or true",""")] // requirement injection
    [InlineData(""","cdhash":null,""")]
    [SupportedOSPlatform("macos")]
    public void The_pin_needs_the_runtime_cdhash(string cdhash)
    {
        const string sha = "233e581c01a8c04719d10498239faa0539e59a2ae2c09d16c9e97f89e0c92f80";
        Assert.Null(NightlyRuntimePackage.Parse($$"""{"version":"1","buildId":"2","protocol":4,"sha256":"{{sha}}"{{cdhash}}"size":1,"archive":"a.zip"}"""));
        var ok = NightlyRuntimePackage.Parse($$"""{"version":"1","buildId":"2","protocol":4,"sha256":"{{sha}}","cdhash":"0123456789abcdef0123456789abcdef01234567","size":1,"archive":"a.zip"}""");
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", ok?.Cdhash);
        Assert.Equal("cdhash H\"0123456789abcdef0123456789abcdef01234567\"", NightlyMach.CdhashRequirement(ok!.Cdhash));
    }

    [Fact]
    public async Task Only_the_pinned_package_is_installed_and_an_update_replaces_versions_not_in_use()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ditto is macOS");
        var dir = Temp();
        try
        {
            var root = Path.Combine(dir, "runtimes");
            var (zip, pin) = await FakePackage(dir, "1.0", "100");
            Directory.CreateDirectory(Path.Combine(root, "packages"));
            Assert.Equal(NightlyRuntimePackage.Problem.PackageMissing, NightlyRuntimePackage.Find(pin, root).Problem);
            File.Copy(zip, Path.Combine(root, "packages", pin.Archive));
            Assert.Equal(NightlyRuntimePackage.Problem.NotInstalled, NightlyRuntimePackage.Find(pin, root).Problem);

            // A package whose hash is not the pin's is refused, nothing installed.
            var tampered = pin with { Sha256 = new string('0', 64) };
            await Assert.ThrowsAsync<InvalidDataException>(() => NightlyRuntimePackage.InstallAsync(null, _ => false, CancellationToken.None, tampered, root));
            // So is one holding another version than pinned.
            var wrongVersion = pin with { Version = "9.9" };
            await Assert.ThrowsAsync<InvalidDataException>(() => NightlyRuntimePackage.InstallAsync(null, _ => false, CancellationToken.None, wrongVersion, root));
            // So is a pin for another protocol.
            await Assert.ThrowsAsync<InvalidDataException>(() => NightlyRuntimePackage.InstallAsync(null, _ => false, CancellationToken.None, pin with { Protocol = 3 }, root));
            Assert.Null(NightlyRuntimePackage.Find(pin, root).App);

            var steps = new List<string>();
            var app = await NightlyRuntimePackage.InstallAsync(new SyncProgress(steps), _ => false, CancellationToken.None, pin, root);
            Assert.Equal(Path.Combine(root, "1.0-100", "Nightly.app"), app);
            Assert.Equal((app, NightlyRuntimePackage.Problem.None), NightlyRuntimePackage.Find(pin, root));
            Assert.Contains(steps, s => s == Loc.T("Checking the Nightly package"));

            // A new pin: the old version is an update to replace; while a runtime runs it, it stays.
            var (zip2, pin2) = await FakePackage(Path.Combine(dir, "v2"), "1.1", "200");
            Assert.Equal(NightlyRuntimePackage.Problem.PackageMissing, NightlyRuntimePackage.Find(pin2, root).Problem);
            File.Copy(zip2, Path.Combine(root, "packages", pin2.Archive));
            Assert.Equal(NightlyRuntimePackage.Problem.UpdateRequired, NightlyRuntimePackage.Find(pin2, root).Problem);
            await NightlyRuntimePackage.InstallAsync(null, path => path == app, CancellationToken.None, pin2, root);
            Assert.True(Directory.Exists(Path.Combine(root, "1.0-100")));
            Assert.Equal(1, NightlyRuntimePackage.RemoveOlder(root, pin2, _ => false));
            Assert.False(Directory.Exists(Path.Combine(root, "1.0-100")));
            Assert.NotNull(NightlyRuntimePackage.Find(pin2, root).App);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class SyncProgress(List<string> steps) : IProgress<string>
    {
        public void Report(string value) => steps.Add(value);
    }

    // ── which engine shows pages ──

    [AvaloniaFact]
    public async Task Nightly_that_cannot_run_falls_back_to_the_preview_with_the_reason_and_an_install()
    {
        var ready = false;
        var vm = new PreviewViewModel
        {
            CheckNightly = () => ready ? (true, null, false) : (false, "Nightly 1.0 is not installed yet.", true),
            InstallNightlyRuntime = (progress, _) =>
            {
                progress.Report("step");
                ready = true;
                return Task.CompletedTask;
            },
        };
        vm.WantsNightly = true;
        Assert.False(vm.UseNightly);
        Assert.True(vm.ShowNightlyFallback);
        Assert.Equal("Nightly 1.0 is not installed yet.", vm.NightlyFallbackReason);
        Assert.True(vm.InstallNightlyCommand.CanExecute(null));

        await vm.InstallNightlyCommand.ExecuteAsync(null);
        Assert.True(vm.UseNightly);
        Assert.False(vm.ShowNightlyFallback);

        // It stops working: the preview takes over again and says so; how it stopped is only under "Show details".
        vm.ReportNightlyUnusable(Loc.F("Nightly exited unexpectedly (code {0}).", 137));
        Assert.False(vm.UseNightly);
        Assert.Equal(Loc.T("Nightly is not responding."), vm.NightlyFallbackReason);
        Assert.DoesNotContain("137", vm.NightlyFallbackReason);
        Assert.True(vm.HasNightlyFallbackDetail);
        Assert.Contains("137", vm.NightlyFallbackDetail);
        Assert.False(vm.CanInstallNightly);
        vm.RetryNightlyCommand.Execute(null);
        Assert.True(vm.UseNightly);

        vm.WantsNightly = false;
        Assert.False(vm.UseNightly);
        Assert.Null(vm.NightlyFallbackReason);
        Assert.Null(vm.NightlyFallbackDetail);
    }

    [AvaloniaFact]
    public void A_failed_install_says_why_and_keeps_the_preview()
    {
        var vm = new PreviewViewModel
        {
            CheckNightly = () => (false, "missing", true),
            InstallNightlyRuntime = (_, _) => throw new InvalidDataException("not the pinned one"),
        };
        vm.WantsNightly = true;
        vm.InstallNightlyCommand.Execute(null);
        Assert.Equal(Loc.F("Nightly could not be installed: {0}", "not the pinned one"), vm.NightlyInstallStatus);
        Assert.False(vm.UseNightly);
        Assert.False(vm.IsInstallingNightly);
    }

    private static MainViewModel NewMain() =>
        new(new Ombra.ClientCore.SessionController(TestProcesses.Fake("normal")), new Ombra.App.AppArgs(),
            settings: new Ombra.ClientCore.ClientSettingsStore(Path.Combine(TestProcesses.TempDir("k1-consent"), "ombra.local.json")));

    [AvaloniaFact]
    public void The_first_run_consent_chooses_the_engine_and_is_asked_once()
    {
        var main = NewMain();
        main.Preview.CheckNightly = () => (true, null, false);
        main.LoadAgentBrowserEngine(null);
        Assert.Equal(OperatingSystem.IsMacOS(), main.Preview.NeedsNightlyConsent);
        Assert.False(main.Preview.UseNightly); // the preview until answered
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Nightly is macOS");
        main.Preview.AcceptNightlyCommand.Execute(null);
        Assert.False(main.Preview.NeedsNightlyConsent);
        Assert.Equal("nightly", main.AgentBrowserEngine);
        Assert.True(main.Preview.UseNightly);

        var other = NewMain();
        other.Preview.CheckNightly = () => (true, null, false);
        other.LoadAgentBrowserEngine(null);
        other.Preview.DeclineNightlyCommand.Execute(null);
        Assert.Equal("preview", other.AgentBrowserEngine);
        Assert.False(other.Preview.UseNightly);

        var saved = NewMain();
        saved.Preview.CheckNightly = () => (true, null, false);
        saved.LoadAgentBrowserEngine("preview");
        Assert.False(saved.Preview.NeedsNightlyConsent);
    }

    [AvaloniaFact]
    public void Switching_engines_keeps_the_tabs_their_pages_and_the_marks()
    {
        var vm = new PreviewViewModel { CheckNightly = () => (true, null, false) };
        vm.NavigateCommand.Execute("localhost:5173/a");
        var first = vm.ActiveTab;
        var agent = vm.OpenAgentTab("surface-1");
        vm.NavigateTabForAgent(agent, "http://localhost:5173/b");
        var tabs = vm.Tabs.ToList();
        var urls = tabs.Select(t => t == vm.ActiveTab ? vm.CurrentUrl : t.Url).ToList();
        var marks = vm.Marks.Items.Count;

        vm.WantsNightly = true;
        Assert.True(vm.UseNightly);
        Assert.Equal(tabs, vm.Tabs.ToList());
        Assert.Equal(urls, vm.Tabs.Select(t => t == vm.ActiveTab ? vm.CurrentUrl : t.Url).ToList());
        Assert.Equal(marks, vm.Marks.Items.Count);
        vm.WantsNightly = false;
        Assert.Equal(tabs, vm.Tabs.ToList());
        Assert.Same(first, vm.Tabs[0]);
    }

    [AvaloniaFact]
    public async Task A_card_keeps_the_nightly_page_shown_and_hides_the_native_web_view()
    {
        var vm = new PreviewViewModel { CheckNightly = () => (true, null, false) };
        vm.NavigateCommand.Execute("localhost:5173/a");
        var asked = vm.ActiveTab!.Dialogs.AlertAsync(new AlertRequest("hi"), CancellationToken.None);
        await Until(() => vm.Dialogs.CoversPage, "the card covers the page");
        Assert.False(vm.ShowWebView); // the native web view would draw over the card
        vm.WantsNightly = true;
        Assert.True(vm.ShowWebView); // the Nightly pane is Avalonia's: its page stays live under the card
        vm.ActiveTab.Dialogs.Current!.Complete(null);
        await asked;
    }

    // ── the real runtime (OMBRA_TEST_NIGHTLY_APP: a protocol-4 Nightly.app; OMBRA_TEST_NIGHTLY_PROFILE_ROOT) ──

    /// <summary>A pane as NightlyPane answers: the page's questions through <see cref="NightlyDialogs"/> and a broker.</summary>
    private sealed class AnsweringView(NightlyRuntime runtime, BrowserPageHooks hooks) : INightlyView
    {
        public uint WindowId;
        public int Frames;
        public NightlyProtocol.LocationInfo? Location;
        public List<string> Questions { get; } = [];
        public List<uint> Withdrawn { get; } = [];
        public void OnRuntimeState(NightlyState state, string? detail) { }
        public void OnAttached(uint windowId) => WindowId = windowId;
        public void OnDetached() => WindowId = 0;
        public void OnWindow(NightlyProtocol.WindowInfo window) { }
        public void OnSurface(NightlyProtocol.SurfaceInfo surface, nint ioSurface) => NightlyMach.CFRelease(ioSurface);
        public void OnFrame(NightlyProtocol.FrameInfo frame) { Frames++; runtime.ReleaseFrame(frame.WindowId, frame.Seq); }
        public void OnLocation(NightlyProtocol.LocationInfo location) => Location = location;
        public void OnPopup(uint popupId, NightlyPopup popup) => runtime.Send(NightlyProtocol.PopupChoiceMessage(WindowId, popupId, -1));
        public void OnPopupClosed(uint popupId) { }
        public void OnCursor(int cursor) { }
        public void OnTextRect(NightlyProtocol.TextRectInfo rect) { }
        public void OnPopupWindow(uint windowId) => runtime.CloseWindow(windowId);
        public void OnWindowClosed() { }
        public void OnDialogClosed(uint dialogId) => Withdrawn.Add(dialogId);
        public void OnDialog(uint dialogId, string json)
        {
            Questions.Add(json);
            var window = WindowId;
            _ = Task.Run(async () =>
            {
                var answer = await NightlyDialogs.AnswerAsync(hooks, dialogId, json, null, d => runtime.TrackDownload(window, d), CancellationToken.None);
                await runtime.ReplyDialogAsync(window, dialogId, answer);
            });
        }
    }

    [Fact]
    public async Task The_real_runtime_asks_ombra_its_pages_questions_downloads_there_and_quits_cleanly()
    {
        var app = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_APP");
        var root = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.SkipUnless(OperatingSystem.IsMacOS() && app is { Length: > 0 } && root is { Length: > 0 },
            "set OMBRA_TEST_NIGHTLY_APP (a protocol-4 Nightly.app) and OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        var profile = Path.Combine(root!, "cutover-test-" + Guid.NewGuid().ToString("N")[..8]);
        var downloads = Path.Combine(root!, "cutover-downloads-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(downloads);
        var serial = new Serial();
        var options = new NightlyRuntimeOptions(NightlyMach.RealPath(Path.Combine(app!, "Contents", "MacOS", "nightly")), profile, null, null, null, AllowUnsignedPeer: true);
        var runtime = new NightlyRuntime(options, serial.Post);
        var broker = new DialogBroker();
        var hooks = new BrowserPageHooks { Broker = () => broker, ChooseDownloadPath = (r, _) => Task.FromResult<string?>(Path.Combine(downloads, r.FileName)) };
        var finished = new TaskCompletionSource<BrowserDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.DownloadChanged += d =>
        {
            if (d.State is BrowserDownloadState.Finished or BrowserDownloadState.Failed) finished.TrySetResult(d);
        };
        // The user's answers: confirm OK, prompt "Ombra", permission once.
        broker.Changed += (_, _) =>
        {
            switch (broker.Current)
            {
                case { Kind: BrowserDialogKind.Confirm } c: c.Complete(true); break;
                case { Kind: BrowserDialogKind.Prompt } p: p.Complete("Ombra"); break;
                case { Kind: BrowserDialogKind.Alert } a: a.Complete(null); break;
                case { Kind: BrowserDialogKind.Permission } g: g.Complete(PermissionAnswer.AllowOnce); break;
            }
        };
        var view = new AnsweringView(runtime, hooks);
        try
        {
            var token = serial.Run(() => runtime.Register(view));
            await Until(() => serial.Run(() => runtime.State == NightlyState.Running && view.WindowId != 0 && view.Frames > 0), "running", 90);
            serial.Run(() => runtime.Send(NightlyProtocol.CommandMessage(view.WindowId, NightlyProtocol.CommandOp.Navigate,
                "data:text/html,<title>K1 cutover</title><a id=d download=\"hello.txt\" href=\"data:text/plain,hello from nightly\">d</a>")!));
            await Until(() => serial.Run(() => view.Location is { Title: "K1 cutover" } l && !l.Flags.HasFlag(NightlyProtocol.LocationFlags.Loading)), "page", 30);

            async Task<string> Page(string script)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                return (await runtime.RunScriptAsync(view.WindowId, NightlyProtocol.ScriptKind.Page, script, "", timeout.Token)).Text;
            }

            // window.confirm / prompt block the page until Ombra answers.
            await Page("setTimeout(() => { window.__c = confirm('Sure?'); window.__p = prompt('Name?', 'x'); }, 0); 1");
            await Until(() => view.Questions.Count >= 2, "confirm and prompt asked", 30);
            await Until(() => Page("String(window.__c) + '|' + window.__p").Result == "true|Ombra", "answers reached the page", 30);
            Assert.Contains(view.Questions, q => q.Contains("\"kind\":\"confirm\"") && q.Contains("Sure?"));
            Assert.Contains(view.Questions, q => q.Contains("\"kind\":\"prompt\""));

            // A download asks where to save, saves there and reports the end.
            await Page("document.getElementById('d').click(); 1");
            var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(BrowserDownloadState.Finished, done.State);
            Assert.Equal("hello from nightly", await File.ReadAllTextAsync(Path.Combine(downloads, "hello.txt")));
            Assert.Contains(view.Questions, q => q.Contains("\"kind\":\"download\"") && q.Contains("hello.txt"));

            // The last pane closes: Nightly is asked to quit and does, writing its session, before any kill.
            var pid = serial.Run(() => runtime.Pid);
            var nightly = Process.GetProcessById(pid);
            var clock = Stopwatch.StartNew();
            serial.Run(() => runtime.Unregister(token));
            await Until(() => nightly.HasExited, "Nightly quit", (int)NightlyRuntime.QuitGrace.TotalSeconds + 5);
            // Quit by itself well inside the grace (a kill comes only after it).
            Assert.True(clock.Elapsed < NightlyRuntime.QuitGrace - TimeSpan.FromSeconds(1), $"quit after {clock.Elapsed}");
            await runtime.Ending.WaitAsync(NightlyRuntime.QuitGrace + TimeSpan.FromSeconds(5));
            // Gecko's shutdown checkpoints: a clean quit reached profile-before-change (the profile was closed).
            var checkpoints = Path.Combine(profile, "sessionCheckpoints.json");
            Assert.True(File.Exists(checkpoints), "no sessionCheckpoints.json");
            Assert.Contains("\"profile-before-change\":true", (await File.ReadAllTextAsync(checkpoints)).Replace(" ", ""));
        }
        finally
        {
            serial.Run(runtime.Dispose);
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
            try { Directory.Delete(downloads, recursive: true); } catch (IOException) { }
        }
    }
}
