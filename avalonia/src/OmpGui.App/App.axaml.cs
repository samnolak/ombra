using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using OmpGui.App.Services.Runtime;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = Program.Args;
            var store = new ClientSettingsStore(args.ConfigPath ?? Environment.GetEnvironmentVariable(OmpRuntimeOptions.ConfigEnvVar) ?? OmpRuntimeOptions.DefaultConfigPath);
            OmpRuntimeOptions options;
            string? configError = null;
            try
            {
                options = store.Load();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                options = new OmpRuntimeOptions();
                configError = $"Could not read the settings ({e.Message}); using defaults.";
            }
            MainViewModel.ApplyTheme(options.Theme);
            // Last line of defence: log a UI-thread exception instead of taking omp and the session down with the app.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine("Unhandled UI exception: " + e.Exception);
                e.Handled = true;
            };
            var platform = OmpGui.App.Platform.RuntimePlatform.Key(out _);
            var installer = new RuntimeInstaller(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, RuntimePack.DefaultRoot, platform);
            // Every launch reads the settings again, so a restart applies what the user saved. An empty command means
            // the pinned runtime the client installed, else omp on PATH.
            OmpRuntimeOptions Current()
            {
                OmpRuntimeOptions o;
                try { o = store.Load(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { o = options; }
                // After an update that brought a new pack, omp runs from the earlier one until the new one is installed.
                return (installer.FindInstalled() ?? installer.FindPrevious()?.Runtime) is { } rt ? rt.ApplyTo(o) : o;
            }
            // omp's browser tool drives the preview panel (AgentBrowserBridge: the cmux protocol omp speaks)
            var agentBrowser = AgentBrowserBridge.TryStart();
            OmpLaunchSpec WithBrowser(OmpLaunchSpec spec) => agentBrowser?.AddTo(spec) ?? spec;
            OmpLaunchSpec Launch(LaunchRequest request) => WithBrowser(Current().ToLaunchSpec(request));
            configError ??= options.ApprovalModeWarning;
            var initial = new LaunchRequest(options.WorkingDirectory ?? ExistingDirectory(options.LastWorkingDirectory),
                ApprovalMode: OmpRuntimeOptions.EffectiveApprovalMode(options.ApprovalMode));
            // "Bypass permissions" was chosen explicitly before; say so at every start, not only in the chip.
            if (initial.ApprovalMode == "yolo")
                configError = (configError is null ? "" : configError + "\n") + "Bypass permissions is on: omp runs every tool, including shell commands, without asking. Change it under the message box.";
            installer.RemovePrevious(); // packs of earlier versions, once this version's is installed (before omp starts)
            var session = new SessionController(Launch, initial);
            var updater = OmpGui.App.Services.UpdateInstaller.ForThisApp(out var noInstall);
            var vm = new MainViewModel(session, args, configError, store)
            {
                OmpTuiLaunch = dir => WithBrowser(Current().ToTuiLaunchSpec(dir)),
                OmpCliLaunch = (cliArgs, dir) => Current().ToCliLaunchSpec(cliArgs, dir),
                RuntimeInstaller = installer,
                Updates = new OmpGui.App.Services.UpdateChecker(new HttpClient { Timeout = TimeSpan.FromMinutes(30) },
                    options.UpdateFeed ?? OmpGui.App.Services.UpdateChecker.DefaultFeed,
                    OmpGui.App.Services.UpdateChecker.ThisVersion(), OmpGui.App.Services.UpdateChecker.ThisRid(),
                    OmpGui.App.Services.UpdateChecker.BuiltInPublicKey()),
                UpdateInstaller = updater,
                UpdateInstallUnavailable = noInstall,
            };
            if (agentBrowser is not null)
            {
                agentBrowser.Page = new PreviewAgentPage(vm);
                desktop.Exit += (_, _) => agentBrowser.Dispose();
            }
            var window = new MainWindow { DataContext = vm, Notifier = CreateNotifier() };
            desktop.MainWindow = window;
            window.Opened += (_, _) => vm.OnWindowOpened();
            vm.NotificationsEnabled = options.Notifications ?? true;
            SetUpMenus(desktop, window, vm);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static string? ExistingDirectory(string? path) => path is not null && Directory.Exists(path) ? path : null;

    /// <summary>Platform Layer choice: the only place that asks which OS this is.</summary>
    private static OmpGui.App.Platform.INotifier CreateNotifier() =>
        OperatingSystem.IsMacOS() ? new OmpGui.App.Platform.OsaScriptNotifier()
        : OperatingSystem.IsWindows() ? new OmpGui.App.Platform.TaskbarFlashNotifier()
        : new OmpGui.App.Platform.NotifySendNotifier();

    /// <summary>
    /// macOS application menu (About, Settings ⌘,, Quit are where Mac users look for them) and a tray / menu-bar icon
    /// on every platform that has one. Everything in them is also in the window itself.
    /// </summary>
    private void SetUpMenus(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel vm)
    {
        var settings = new NativeMenuItem("Settings…") { Command = vm.OpenSettingsCommand, Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Meta) };
        var appMenu = new NativeMenu();
        appMenu.Add(settings);
        NativeMenu.SetMenu(this, appMenu);

        var file = new NativeMenu();
        file.Add(new NativeMenuItem("New Session") { Command = vm.NewSessionCommand });
        file.Add(new NativeMenuItem("Open Folder…") { Command = vm.OpenFolderCommand });
        var view = new NativeMenu();
        view.Add(new NativeMenuItem("Sessions") { Command = vm.ToggleSidebarCommand });
        view.Add(new NativeMenuItem("Terminal") { Command = vm.ToggleTerminalCommand });
        view.Add(new NativeMenuItem("Events") { Command = vm.ToggleDebugCommand });
        var bar = new NativeMenu();
        bar.Add(new NativeMenuItem("File") { Menu = file });
        bar.Add(new NativeMenuItem("View") { Menu = view });
        NativeMenu.SetMenu(window, bar);

        try
        {
            var show = new NativeMenuItem("Show OMP GUI");
            show.Click += (_, _) => ShowWindow(window);
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => window.Close();
            var trayMenu = new NativeMenu();
            trayMenu.Add(show);
            trayMenu.Add(new NativeMenuItem("New Session") { Command = vm.NewSessionCommand });
            trayMenu.Add(quit);
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OmpGui/Assets/icon-256.png"))),
                ToolTipText = "OMP GUI",
                Menu = trayMenu,
            };
            tray.Clicked += (_, _) => ShowWindow(window);
            TrayIcon.SetIcons(this, [tray]);
            desktop.Exit += (_, _) => tray.Dispose();
        }
        catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException or NotSupportedException)
        {
            // No tray on this desktop (e.g. a Linux session without a status-notifier host): the window has everything.
        }
    }

    private static void ShowWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }
}
