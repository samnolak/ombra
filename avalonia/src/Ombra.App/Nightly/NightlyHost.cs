using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Ombra.App.Nightly;

/// <summary>
/// The app's one <see cref="NightlyRuntime"/>, made when the first Nightly pane needs it and ended when the app exits
/// (its process tree with it).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class NightlyHost
{
    private static NightlyRuntime? _runtime;
    private static bool _exitHooked;
    private static bool _themeHooked;
    private static bool _windowControlsShown = true;

    /// <summary>Whether the main window's traffic lights show (MainWindow.FitChrome; not in full screen): Nightly's top
    /// row keeps their place free while they do.</summary>
    public static bool WindowControlsShown
    {
        get => _windowControlsShown;
        set
        {
            _windowControlsShown = value;
            if (_runtime is { } runtime) runtime.WindowControlsShown = value;
        }
    }

    /// <summary>The runtime, or why it cannot run (looked up again on every call until it can).</summary>
    public static (NightlyRuntime? Runtime, string? Problem) Get()
    {
        if (_runtime is not null) return (_runtime, null);
        var settings = Program.SettingsPath.Length > 0 ? Program.SettingsPath : Ombra.ClientCore.ClientInstanceLease.SettingsPath();
        var (options, problem) = NightlyRuntimeOptions.Resolve(settings);
        if (options is null) return (null, problem);
        try
        {
            _runtime = new NightlyRuntime(options, a => Dispatcher.UIThread.Post(a));
        }
        catch (InvalidOperationException e)
        {
            return (null, e.Message);
        }
        if (Application.Current is { } app)
        {
            // Nightly follows Ombra's appearance (the effective one: Settings' light/dark, or macOS's under "system").
            _runtime.DarkTheme = app.ActualThemeVariant == ThemeVariant.Dark;
            _runtime.WindowControlsShown = _windowControlsShown;
            if (!_themeHooked)
            {
                _themeHooked = true;
                app.ActualThemeVariantChanged += (_, _) =>
                {
                    if (_runtime is { } runtime) runtime.DarkTheme = app.ActualThemeVariant == ThemeVariant.Dark;
                };
            }
        }
        if (!_exitHooked)
        {
            _exitHooked = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Exit += (_, _) => Shutdown();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }
        return (_runtime, null);
    }

    /// <summary>Whether a live runtime runs the app at <paramref name="app"/> (an installed version not to remove).</summary>
    public static bool Uses(string app)
    {
        var binary = _runtime?.Options.Binary;
        if (binary is null) return false;
        var full = Path.GetFullPath(app).TrimEnd('/') + "/";
        return binary.StartsWith(full, StringComparison.Ordinal) || binary.StartsWith(NightlyMach.RealPath(full.TrimEnd('/')) + "/", StringComparison.Ordinal);
    }

    /// <summary>A runtime that gave up (<see cref="NightlyState.Failed"/>) starts again.</summary>
    public static void RetryFailed()
    {
        if (_runtime is { State: NightlyState.Failed } runtime) runtime.Retry();
    }

    public static void Shutdown()
    {
        var runtime = Interlocked.Exchange(ref _runtime, null);
        runtime?.Dispose();
    }
}
