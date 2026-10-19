using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.Services.Runtime;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>First run in the real window: omp missing (install the pinned runtime) and omp without a model (omp's own setup).</summary>
public sealed class FirstRunUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
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

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static (MainWindow w, MainViewModel vm) Open(MainViewModel vm, double width, double height)
    {
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        return (w, vm);
    }

    private static bool Shown(Control c) => c.IsEffectivelyVisible && c.Bounds.Width > 0;

    /// <summary>Like the app: an empty command means the installed runtime (here: the fake omp once "installed").</summary>
    private static Func<LaunchRequest, OmpLaunchSpec> LaunchVia(RuntimeInstaller installer, string missing) =>
        _ => installer.FindInstalled() is null ? new OmpLaunchSpec { FileName = missing } : TestProcesses.Fake("normal");

    [AvaloniaTheory]
    [InlineData(1100, 720)]
    [InlineData(480, 400)]
    public async Task Missing_omp_offers_the_pinned_runtime_and_starts_it_once_installed(double width, double height)
    {
        var root = TestProcesses.TempDir("fr-root");
        var tgz = RuntimeInstallerTests.Tarball();
        var installer = RuntimeInstallerTests.Installer(root, tgz, new RuntimeInstallerTests.FakeBun());
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("fr-settings"), "ombra.local.json"));
        var launch = LaunchVia(installer, Path.Combine(root, "omp-not-installed"));
        var launches = 0;
        var s = new SessionController(request =>
        {
            Interlocked.Increment(ref launches);
            return launch(request);
        }, new LaunchRequest());
        var (w, vm) = Open(new MainViewModel(s, new AppArgs(), settings: store) { RuntimeInstaller = installer }, width, height);
        await Until(() => vm.CanRecover, "start failed");
        Assert.Equal(StartProblem.NotFound, s.Snapshot().StartProblem);
        Assert.True(vm.IsOnboarding);
        Assert.True(vm.ShowSetupScreen);
        Assert.Equal(1, Volatile.Read(ref launches));
        var install = w.FindControl<Button>("InstallRuntimeButton")!;
        await Until(() => Shown(install), "install offered");
        Assert.Same(vm.InstallRuntimeCommand, install.Command);
        Assert.True(install.Command!.CanExecute(null));
        Assert.False(Shown(w.FindControl<Button>("SetUpProviderButton")!));
        var right = install.TranslatePoint(new Avalonia.Point(install.Bounds.Width, 0), w)!.Value.X;
        Assert.True(right <= w.Bounds.Width + 0.5, $"install button clipped at {right} > {w.Bounds.Width}");
        Shot(w, $"ui-first-run-install-{width}x{height}");
        vm.ComposerText = "keep this unsent draft after installation";

        install.Command!.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "omp started from the installed runtime");
        Assert.False(vm.CanRecover);
        Assert.NotNull(installer.FindInstalled());
        Assert.Equal("", vm.RuntimeInstallText);
        Assert.NotNull(s.ProcessId);
        Assert.Equal(2, Volatile.Read(ref launches));
        Assert.Equal("keep this unsent draft after installation", vm.ComposerText);
        Assert.Empty(s.Snapshot().Items.OfType<UserItem>());
        Assert.Empty(s.Snapshot().Queued);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_install_says_why_and_can_be_retried()
    {
        var root = TestProcesses.TempDir("fr-bad");
        var installer = RuntimeInstallerTests.Installer(root, RuntimeInstallerTests.Tarball(), new RuntimeInstallerTests.FakeBun(),
            integrity: RuntimeInstallerTests.Integrity(RuntimeInstallerTests.Tarball("bin/x")));
        var s = new SessionController(LaunchVia(installer, Path.Combine(root, "omp-not-installed")), new LaunchRequest());
        var (w, vm) = Open(new MainViewModel(s, new AppArgs()) { RuntimeInstaller = installer }, 1100, 720);
        await Until(() => vm.ShowInstallRuntime, "install offered");
        vm.InstallRuntimeCommand.Execute(null);
        await Until(() => !vm.IsInstallingRuntime && vm.RuntimeInstallText.Length > 0, "install failed");
        var failed = Loc.F("Engine setup failed: {0} Nothing was changed. Try repairing the engine here.", "\0").Split('\0');
        Assert.StartsWith(failed[0], vm.RuntimeInstallText);
        Assert.Contains(Loc.F("{0}@{1} does not match its pinned integrity.", "@oven/bun-" + RuntimeInstallerTests.Key, RuntimePack.BunVersion), vm.RuntimeInstallText);
        Assert.Contains(failed[1], vm.RuntimeInstallText);
        Assert.True(vm.ShowInstallRuntime); // offered again
        Assert.True(vm.CanRecover);
        Shot(w, "ui-first-run-install-failed");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_command_the_user_set_is_not_overridden_by_an_install_offer()
    {
        var root = TestProcesses.TempDir("fr-own");
        var installer = RuntimeInstallerTests.Installer(root, RuntimeInstallerTests.Tarball(), new RuntimeInstallerTests.FakeBun());
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("fr-own-settings"), "ombra.local.json"));
        var missing = Path.Combine(root, "my-omp");
        store.Update(o => o with { Command = missing });
        var s = new SessionController(new OmpLaunchSpec { FileName = missing });
        var (w, vm) = Open(new MainViewModel(s, new AppArgs(), settings: store) { RuntimeInstaller = installer }, 1100, 720);
        await Until(() => vm.CanRecover, "start failed");
        Assert.False(vm.ShowInstallRuntime);
        Assert.Contains(Loc.T("Settings") + " → " + Loc.T("Advanced"), vm.RecoverHint); // the page's names as the user sees them
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(1100, 720)]
    [InlineData(480, 400)]
    public async Task Omp_without_a_model_opens_omp_login_in_the_terminal(double width, double height)
    {
        var s = new SessionController(TestProcesses.Fake("no-models"));
        var (shell, args) = MainViewModel.DefaultShell();
        IReadOnlyList<string>? launched = null;
        var vm = new MainViewModel(s, new AppArgs())
        {
            // Stands in for `omp login`: the test checks the tab is opened with the CLI launch the settings give.
            OmpCliLaunch = (cliArgs, dir) =>
            {
                launched = cliArgs;
                return new OmpLaunchSpec { FileName = shell, Arguments = args, WorkingDirectory = dir };
            },
        };
        (var w, vm) = Open(vm, width, height);
        await Until(() => vm.CanRecover, "start failed");
        Assert.Equal(Loc.T("Connect a model provider"), vm.RecoverTitle);
        Assert.Contains("Sign in", vm.RecoverHint);
        var setUp = w.FindControl<Button>("SetUpProviderButton")!;
        await Until(() => Shown(setUp), "setup offered");
        Assert.False(Shown(w.FindControl<Button>("InstallRuntimeButton")!));
        Shot(w, $"ui-first-run-provider-{width}x{height}");

        setUp.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var tab = Assert.Single(vm.Terminals);
        Assert.Equal(["login"], launched); // lists every provider, even those turned off for omp (NetworkPrivacy)
        Assert.Equal(TerminalKind.OmpTui, tab.Kind);
        Assert.True(vm.IsTerminalOpen);
        Assert.Contains(Loc.T("Sign in to a model provider in the terminal beside the conversation: choose one with the arrow keys and Enter, finish in the browser (or paste an API key), then press Try again."), vm.ComposerMessage);
        Assert.DoesNotContain("/login", vm.ComposerMessage);
        vm.CloseTerminalCommand.Execute(tab);
        await vm.DisposeAsync();
        w.Close();
    }
}
