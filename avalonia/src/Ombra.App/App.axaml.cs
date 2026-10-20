using Ombra.App.Localization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Ombra.App.Services.Runtime;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = Program.Args;
            if (Program.StartupRefused)
            {
                ShowStartupRefusal(desktop);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            // Establish Windows kernel job ownership before any engine, terminal or detached broker can start;
            // failure aborts startup rather than launching unprotected processes. On Unix retain the marked-process
            // reaper, including old brokers, before a new engine can join one left by an earlier app.
            OwnedProcesses.MarkThisProcess();
            var owned = new OwnedProcesses(RuntimePack.DefaultRoot);
            owned.Stop(OwnedProcesses.Moment.Start);
            desktop.Exit += (_, _) => owned.Stop(OwnedProcesses.Moment.Quit);
            var store = new ClientSettingsStore(Program.SettingsPath);
            OmpRuntimeOptions options;
            string? configError = null;
            try
            {
                options = store.Load();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                options = new OmpRuntimeOptions();
                configError = Loc.F("Could not read the settings ({0}); using defaults.", e.Message);
            }
            Ombra.App.Localization.Loc.Load(Ombra.App.Localization.Loc.Resolve(options.Language, store.Path), store.Path);
            MainViewModel.ApplyTheme(options.Theme);
            var platform = Ombra.App.Platform.RuntimePlatform.Key(out _);
            var installer = new RuntimeInstaller(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, RuntimePack.DefaultRoot, platform)
            {
                BundledRoot = RuntimePack.BundledRoot, // the package's own engine: no download on first run
            };
            // Every launch reads settings again. An empty command requires this client's owned runtime;
            // older unpatched packs and omp on PATH are not compatible default fallbacks.
            OmpRuntimeOptions Current()
            {
                OmpRuntimeOptions o;
                try { o = store.Load(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { o = options; }
                return installer.ApplyDefaultTo(o);
            }
            // bun install reaches the registry through the same network as the engine: same corporate trust (CorporateTrust)
            installer.NetworkEnvironment = () =>
            {
                var env = new Dictionary<string, string?>();
                Ombra.ClientCore.Network.CorporateTrust.Apply(env, Current(), Environment.GetEnvironmentVariable);
                return env;
            };
            // omp's browser tool drives the preview panel — over Tern (TernHost: tabs with trusted native input) or cmux
            // (AgentBrowserBridge, also the fallback a Tern-less omp finds) — and is made the agent's default for every omp
            // started here unless the setting is off (AgentBrowserDefaults)
            var agentBrowser = AgentBrowserBridge.TryStart();
            var tern = agentBrowser is not null && Ombra.ClientCore.Browser.AgentBrowserDefaults.ChooseBackend() == Ombra.ClientCore.Browser.AgentBrowserBackend.Tern
                ? Ombra.ClientCore.Browser.TernHost.TryStart() : null;
            var browserDefaults = agentBrowser is null ? null
                : new Ombra.ClientCore.Browser.AgentBrowserDefaults(agentBrowser, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Path))!, "agent-browser"), tern);
            // Network privacy (Settings › Advanced): every omp start first turns off the providers the user has not added,
            // and in strict mode goes through the app's filtering proxy
            var privacy = new Ombra.ClientCore.Network.NetworkPrivacy(Ombra.ClientCore.Network.NetworkPrivacy.DirectoryFor(store.Path));
            // Chats get names from omp's own title generator, as in omp's terminal (SessionTitles)
            var titles = new SessionTitles(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Path))!, "session-titles"));
            // A folder with its own tools omp would run (MCP servers, hooks, extensions…) starts restricted until trusted (WorkspaceTrust)
            var trust = new WorkspaceTrust(WorkspaceTrust.PathFor(store.Path));
            async Task<OmpLaunchSpec> Launch(LaunchRequest request, CancellationToken ct)
            {
                var o = Current();
                return await privacy.ApplyAsync(titles.Apply(trust.Apply(browserDefaults?.ForSession(o, request) ?? o.ToLaunchSpec(request))), o, ct);
            }
            if (configError is null && options.ApprovalModeWarning is { } approvalWarning)
                configError = Loc.T(approvalWarning);
            // The project used last, when it still exists; else a new chat outside any project, in a folder of its own (not
            // the home folder, which the agent would roam and the Files pane list; an app opened from Finder runs in "/").
            // A chat used last is not taken up again: its folder holds that chat, and the window starts on a new one.
            var chats = new ChatFolders(ChatFolders.DefaultRoot, ChatFolders.LegacyDefaultRoot);
            var initial = new LaunchRequest(options.WorkingDirectory ?? ProjectToResume(options.LastWorkingDirectory, chats) ?? NewChatFolder(chats),
                ApprovalMode: OmpRuntimeOptions.EffectiveApprovalMode(options.ApprovalMode));
            // "Bypass permissions" was chosen explicitly before; say so at every start, not only in the chip.
            if (initial.ApprovalMode == "yolo")
                configError = (configError is null ? "" : configError + "\n") + Loc.T("Bypass permissions is on: omp runs every tool, including shell commands, without asking. Change it under the message box.");
            // A crash restarts the chats on their sessions by themselves (backoff, attempt limit); siblings inherit it.
            var session = new SessionController(Launch, initial) { AutoRecover = AutoRecoverPolicy.Default };
            var updater = Ombra.App.Services.UpdateInstaller.ForThisApp(out var noInstall);
            var vm = new MainViewModel(session, args, configError, store, Program.Drafts)
            {
                Chats = chats,
                Privacy = privacy,
                WorkspaceTrust = trust,
                OmpTuiLaunch = async (dir, ct) =>
                {
                    var o = Current();
                    return await privacy.ApplyAsync(trust.ApplyToTerminal(browserDefaults?.ForTui(o, dir) ?? o.ToTuiLaunchSpec(dir)), o, ct);
                },
                OmpCliLaunch = (cliArgs, dir) =>
                {
                    var o = Current();
                    // Subcommands that load extensions run outside a folder that is not trusted (WorkspaceTrust.ApplyToCli)
                    return privacy.ApplyStrict(trust.ApplyToCli(o.ToCliLaunchSpec(cliArgs, dir), cliArgs.FirstOrDefault()), o);
                },
                RuntimeInstaller = installer,
                Updates = new Ombra.App.Services.UpdateChecker(new HttpClient { Timeout = TimeSpan.FromMinutes(30) },
                    options.UpdateFeed ?? Ombra.App.Services.UpdateChecker.DefaultFeed,
                    Ombra.App.Services.UpdateChecker.ThisVersion(), Ombra.App.Services.UpdateChecker.ThisRid(),
                    Ombra.App.Services.UpdateChecker.BuiltInPublicKey()),
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
            var window = new MainWindow
            {
                DockAttention = OperatingSystem.IsMacOS() ? new Ombra.App.Platform.MacDockAttention() : null,
                DataContext = vm,
                Notifier = CreateNotifier(),
                WindowLog = Ombra.App.Services.ClientLog.Shared, // where the window was put and who moved it (MainWindow.FrameLog.cs)
            };
            desktop.MainWindow = window;
            // AppKit's Dock reopen callback only raises Avalonia's activation event; it does not
            // deminiaturize our window. Window.Activated cannot help while no window is visible.
            if (OperatingSystem.IsMacOS() && this.TryGetFeature<IActivatableLifetime>() is { } activation)
                activation.Activated += (_, e) => RestoreWindowOnActivation(window, e.Kind);
            Ombra.App.Services.PerfLog.Attach(window); // OMBRA_PERF=1: frame-rate and layout graphs, slow applies on stderr
            window.Opened += (_, _) => vm.OnWindowOpened();
            vm.NotificationsEnabled = options.Notifications ?? true;
            vm.WatchGitRemotes = options.WatchGitRemotes ?? true;
            vm.SendKey = options.SendKey == "mod-enter" ? "mod-enter" : "enter";
            vm.LoadAgentBrowserEngine(options.AgentBrowserEngine);
            SetUpMenus(desktop, window, vm);
            if (OperatingSystem.IsMacOS()) Ombra.App.Nightly.NightlyAcceptance.StartIfRequested(window, vm); // OMBRA_NIGHTLY_ACCEPTANCE: development check only
            if (OperatingSystem.IsMacOS()) Ombra.App.Nightly.NightlyTeamLink.Start(new Ombra.App.Nightly.TeamViewModelNightlySource(() => vm.Team)); // B1/B2: Nightly shares through this Ombra
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowStartupRefusal(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Settings and translations may be read without acquiring writer ownership; never save from this window.
        try
        {
            var settings = new ClientSettingsStore(Program.SettingsPath).Load();
            Loc.Load(Loc.Resolve(settings.Language, Program.SettingsPath), Program.SettingsPath);
            MainViewModel.ApplyTheme(settings.Theme);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
        var title = Program.AlreadyRunning ? Loc.T("Ombra is already running") : Loc.T("Draft storage is unavailable");
        var message = Program.AlreadyRunning
            ? Loc.T("Another Ombra instance is using this configuration. Return to that window, or close it before opening another. Your drafts were not changed.")
            : Loc.F("Ombra could not get exclusive access to its draft storage, so it did not open a chat. Your drafts were not changed.\n\n{0}", Program.StartupError?.Message);
        desktop.Exit += (_, e) => e.ApplicationExitCode = 1;
        desktop.MainWindow = CreateStartupRefusalWindow(title, message, Program.SettingsPath);
    }

    internal static Window CreateStartupRefusalWindow(string title, string message, string settingsPath)
    {
        var close = new Button
        {
            Name = "StartupRefusalClose", Content = Loc.T("Close"), IsDefault = true, IsCancel = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
        };
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(new SelectableTextBlock { Text = settingsPath, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var scroll = new ScrollViewer
        {
            Name = "StartupRefusalScroll", Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        var layout = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(scroll);
        Grid.SetRow(close, 1);
        layout.Children.Add(close);
        var window = new Window
        {
            Title = title, Width = 520, Height = 360, CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = layout,
        };
        void ConstrainToScreen()
        {
            if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen) return;
            // Work area excludes the Dock/taskbar; leave room for native frame decorations as well.
            window.MaxWidth = Math.Max(1, screen.WorkingArea.Width / screen.Scaling - 64);
            window.MaxHeight = Math.Max(1, screen.WorkingArea.Height / screen.Scaling - 64);
            window.Width = Math.Min(window.Width, window.MaxWidth);
            window.Height = Math.Min(window.Height, window.MaxHeight);
        }
        ConstrainToScreen();
        window.Opened += (_, _) => { ConstrainToScreen(); close.Focus(); };
        close.Click += (_, _) => window.Close();
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape) return;
            e.Handled = true;
            window.Close();
        };
        return window;
    }

    private static string? ProjectToResume(string? path, ChatFolders chats) =>
        path is not null && Directory.Exists(path) && !chats.Contains(path) ? path : null;

    /// <summary>A new chat's folder; the home folder only when the app's data folder cannot be written (omp needs some
    /// folder to start in).</summary>
    private static string NewChatFolder(ChatFolders chats)
    {
        try { return chats.Create(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("No folder for a new chat: " + e.Message);
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>Platform Layer choice: the only place that asks which OS this is.</summary>
    private static Ombra.App.Platform.INotifier CreateNotifier() =>
        OperatingSystem.IsMacOS() ? new Ombra.App.Platform.OsaScriptNotifier()
        : OperatingSystem.IsWindows() ? new Ombra.App.Platform.TaskbarFlashNotifier()
        : new Ombra.App.Platform.NotifySendNotifier();

    /// <summary>
    /// macOS application menu (About, Settings ⌘,, Quit are where Mac users look for them), the window's menu bar
    /// (MainWindow.MenuBar) and a tray / menu-bar icon on every platform that has one. Everything in them is also in
    /// the window itself.
    /// </summary>
    private void SetUpMenus(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel vm)
    {
        var settings = new NativeMenuItem(Loc.T("Settings…")) { Command = vm.OpenSettingsCommand, Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Meta) };
        var appMenu = new NativeMenu();
        appMenu.Add(settings);
        NativeMenu.SetMenu(this, appMenu);
        NativeMenu.SetMenu(window, window.MenuBar(vm));

        try
        {
            var show = new NativeMenuItem(Loc.T("Show Ombra"));
            show.Click += (_, _) => ShowWindow(window);
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => window.Close();
            var trayMenu = new NativeMenu();
            trayMenu.Add(show);
            trayMenu.Add(new NativeMenuItem(Loc.T("New Chat")) { Command = vm.NewChatCommand });
            trayMenu.Add(quit);
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Ombra/Assets/icon-round.png"))),
                ToolTipText = "Ombra",
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

    internal static void RestoreWindowOnActivation(Window window, ActivationKind kind)
    {
        if (kind == ActivationKind.Reopen
            || (kind == ActivationKind.Background && window.WindowState == WindowState.Minimized))
            ShowWindow(window);
    }

    private static void ShowWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }
}
