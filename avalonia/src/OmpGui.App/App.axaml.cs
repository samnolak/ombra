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
            // omp's browser tool drives the preview panel — over Tern (TernHost: tabs with trusted native input) or cmux
            // (AgentBrowserBridge, also the fallback a Tern-less omp finds) — and is made the agent's default for every omp
            // started here unless the setting is off (AgentBrowserDefaults)
            var agentBrowser = AgentBrowserBridge.TryStart();
            var tern = agentBrowser is not null && OmpGui.ClientCore.Browser.AgentBrowserDefaults.ChooseBackend() == OmpGui.ClientCore.Browser.AgentBrowserBackend.Tern
                ? OmpGui.ClientCore.Browser.TernHost.TryStart() : null;
            var browserDefaults = agentBrowser is null ? null
                : new OmpGui.ClientCore.Browser.AgentBrowserDefaults(agentBrowser, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Path))!, "agent-browser"), tern);
            // Network privacy (Settings › Advanced): every omp start first turns off the providers the user has not added,
            // and in strict mode goes through the app's filtering proxy
            var privacy = new OmpGui.ClientCore.Network.NetworkPrivacy(OmpGui.ClientCore.Network.NetworkPrivacy.DirectoryFor(store.Path));
            async Task<OmpLaunchSpec> Launch(LaunchRequest request, CancellationToken ct)
            {
                var o = Current();
                return await privacy.ApplyAsync(browserDefaults?.ForSession(o, request) ?? o.ToLaunchSpec(request), o, ct);
            }
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
                Privacy = privacy,
                OmpTuiLaunch = async (dir, ct) =>
                {
                    var o = Current();
                    return await privacy.ApplyAsync(browserDefaults?.ForTui(o, dir) ?? o.ToTuiLaunchSpec(dir), o, ct);
                },
                OmpCliLaunch = (cliArgs, dir) =>
                {
                    var o = Current();
                    return privacy.ApplyStrict(o.ToCliLaunchSpec(cliArgs, dir), o);
                },
                RuntimeInstaller = installer,
                Updates = new OmpGui.App.Services.UpdateChecker(new HttpClient { Timeout = TimeSpan.FromMinutes(30) },
                    options.UpdateFeed ?? OmpGui.App.Services.UpdateChecker.DefaultFeed,
                    OmpGui.App.Services.UpdateChecker.ThisVersion(), OmpGui.App.Services.UpdateChecker.ThisRid(),
                    OmpGui.App.Services.UpdateChecker.BuiltInPublicKey()),
                UpdateInstaller = updater,
                UpdateInstallUnavailable = noInstall,
            };
            // Last line of defence: an exception that escapes a UI handler (an async command, a drop, a web view event)
            // is logged to client.log and said once under the message box, instead of taking omp and the session down
            // with the app. Before this point a failure ends the start, and ClientLog's process hook records it.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine("Unhandled UI exception: " + e.Exception);
                vm.ReportClientError(e.Exception);
                e.Handled = true;
            };
            if (agentBrowser is not null)
            {
                agentBrowser.Host = new PreviewAgentHost(vm);
                desktop.Exit += (_, _) => agentBrowser.Dispose();
            }
            desktop.Exit += (_, _) => privacy.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (tern is not null)
            {
                tern.Browser = new TernPreviewHost(vm);
                desktop.Exit += (_, _) => tern.Dispose();
            }
            var window = new MainWindow { DataContext = vm, Notifier = CreateNotifier() };
            desktop.MainWindow = window;
            OmpGui.App.Services.PerfLog.Attach(window); // OMPGUI_PERF=1: frame-rate and layout graphs, slow applies on stderr
            window.Opened += (_, _) => vm.OnWindowOpened();
            vm.NotificationsEnabled = options.Notifications ?? true;
            vm.SendKey = options.SendKey == "mod-enter" ? "mod-enter" : "enter";
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
    /// macOS application menu (About, Settings ⌘,, Quit are where Mac users look for them), the window's menu bar
    /// (MainWindow.MenuBar) and a tray / menu-bar icon on every platform that has one. Everything in them is also in
    /// the window itself.
    /// </summary>
    private void SetUpMenus(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel vm)
    {
        var settings = new NativeMenuItem("Settings…") { Command = vm.OpenSettingsCommand, Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Meta) };
        var appMenu = new NativeMenu();
        appMenu.Add(settings);
        NativeMenu.SetMenu(this, appMenu);
        NativeMenu.SetMenu(window, window.MenuBar(vm));

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
