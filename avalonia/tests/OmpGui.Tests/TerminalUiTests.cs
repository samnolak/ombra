using Avalonia.Headless;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Terminal panel: a real shell on a PTY (ConPTY on Windows, Unix PTY elsewhere), input/output, no orphans.</summary>
public sealed class TerminalUiTests
{
    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static bool Alive(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [AvaloniaFact]
    public async Task A_shell_runs_in_the_project_folder_and_ends_with_the_window()
    {
        var project = TestProcesses.TempDir("term-project");
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("term-sessions")), new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 800 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");

        vm.ToggleTerminalCommand.Execute(null);
        var tab = Assert.Single(vm.Terminals);
        Assert.Equal(TerminalKind.Shell, tab.Kind);
        Assert.True(SessionCatalog.SamePath(project, tab.WorkingDirectory));
        var term = w.TerminalOf(tab)!;
        await Until(() => term.IsLive || tab.HasExited, "shell started");
        Assert.False(tab.HasExited, tab.Status);
        var pid = term.Pid;

        // cmd.exe and POSIX shells both print the result of this line.
        await term.SendInputAsync("echo ompgui-terminal-ok\r", CancellationToken.None);
        await Until(() => term.FindInBuffer("ompgui-terminal-ok", default) >= 2, "echoed output"); // the typed line and its output

        // A second tab, then closing the first ends its process.
        vm.NewShellCommand.Execute(null);
        Assert.Equal(2, vm.Terminals.Count);
        vm.CloseTerminalCommand.Execute(tab);
        await Until(() => !Alive(pid), "closed tab's shell ended", 10);

        var second = vm.Terminals.Single();
        var term2 = w.TerminalOf(second)!;
        await Until(() => term2.IsLive, "second shell started");
        var pid2 = term2.Pid;
        w.Close();
        await Until(() => !w.IsVisible, "window closed", 15);
        await Until(() => !Alive(pid2), "no shell left after the window closed", 10);
    }

    /// <summary>
    /// The terminal opens beside the conversation (not above it), full height, and takes the keyboard: in a new
    /// session the composer sits in the middle of the page, and a panel under the transcript ended up at the top of
    /// the window while typing still went to the composer (reported on macOS).
    /// </summary>
    [AvaloniaFact]
    public async Task The_terminal_opens_beside_the_conversation_and_takes_the_keyboard()
    {
        var project = TestProcesses.TempDir("term-side");
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("term-side-sessions")), new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1400, Height = 820 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        Assert.Empty(vm.Rows); // a new session: the composer is in the middle of the page

        vm.ToggleTerminalCommand.Execute(null);
        var tab = Assert.Single(vm.Terminals);
        var term = w.TerminalOf(tab)!;
        await Until(() => term.IsLive || tab.HasExited, "shell started");
        await Until(() => w.FindControl<Avalonia.Controls.Border>("TerminalPanel")!.Bounds.Width > 0, "panel laid out");

        var panel = w.FindControl<Avalonia.Controls.Border>("TerminalPanel")!;
        var header = w.FindControl<Avalonia.Controls.Border>("HeaderBar")!;
        var composer = w.FindControl<Avalonia.Controls.Border>("ComposerBox")!;
        var panelBox = panel.Bounds;
        var composerRight = composer.TranslatePoint(new Avalonia.Point(composer.Bounds.Width, 0), w)!.Value.X;
        var panelLeft = panel.TranslatePoint(default, w)!.Value.X;
        Assert.True(panelLeft >= composerRight, $"terminal at x={panelLeft}, composer ends at x={composerRight}");
        Assert.True(panelBox.Height >= w.Bounds.Height - 1, $"terminal {panelBox.Height} high in a {w.Bounds.Height} window");
        Assert.True(header.IsEffectivelyVisible);
        Assert.InRange(panelBox.Width, 360, 700);

        // The keyboard is in the terminal, not the composer
        var focused = w.FocusManager?.GetFocusedElement() as Avalonia.Visual;
        Assert.NotNull(focused);
        Assert.True(focused == term || focused.GetVisualAncestors().Contains(term), $"focus is on {focused}");

        if (Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR") is { Length: > 0 } dir)
        {
            await Task.Delay(600);
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = w.CaptureRenderedFrame();
            Directory.CreateDirectory(dir);
            using var file = File.Create(Path.Combine(dir, "terminal-beside.png"));
            frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        // A narrow window: the terminal covers the conversation instead of squeezing it
        w.Width = 720;
        await Until(() => Avalonia.Controls.Grid.GetColumn(panel) == 0, "terminal over the conversation");
        Assert.True(panel.ZIndex > 0);
        w.Width = 1400;
        await Until(() => Avalonia.Controls.Grid.GetColumn(panel) > 0, "docked again");

        vm.ToggleTerminalCommand.Execute(null);
        await Until(() => !panel.IsVisible, "hidden");
        w.Close();
    }

    [Fact]
    public void Default_shell_comes_from_the_environment_not_an_os_check()
    {
        var (file, _) = MainViewModel.DefaultShell();
        Assert.False(string.IsNullOrEmpty(file));
    }

    [AvaloniaFact]
    public void The_app_brings_the_terminal_theme_itself()
    {
        // Regression (one-core run): the library adds its theme on first use behind a process-wide flag, so an
        // Application created later in the process had terminals without a template, and no tab ever started.
        // Compiled into the app's own styles (a Styles entry), not a StyleInclude the library added at run time.
        var compiled = Avalonia.Application.Current!.Styles.OfType<Avalonia.Styling.Styles>()
            .SelectMany(st => st).OfType<Avalonia.Styling.Style>().Select(st => st.Selector?.ToString() ?? "");
        Assert.Contains(compiled, sel => sel.Contains("TerminalControl", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_tab_closed_before_its_shell_started_never_starts_one()
    {
        // Regression (review round 4): the start poll kept going for a closed tab and could launch a shell ~10 s later
        // on a control nothing tracked any more (so nothing would ever end it).
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("term-closed")), new LaunchRequest(TestProcesses.TempDir("term-closed-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.NewShellCommand.Execute(null);
        var tab = vm.Terminals.Last();
        var control = w.TerminalOf(tab)!;
        vm.CloseTerminalCommand.Execute(tab);  // closed before the first layout pass: its shell has not started
        w.StartWhenReady(control, tab);        // a pending start poll firing afterwards
        Assert.False(tab.Started);
        Assert.False(control.IsLive);
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public void The_omp_tui_gets_the_users_environment_not_the_rpc_hints()
    {
        // Regression (review round 4): the TUI inherited NO_COLOR and a forced C.UTF-8 locale meant for the RPC process,
        // and a variable removed for omp (null) was dropped, so the TUI inherited it from the app after all.
        var o = new OmpRuntimeOptions
        {
            Command = "omp",
            Profile = "work",
            Environment = new() { ["MY_SETTING"] = "1", ["ANTHROPIC_API_KEY"] = null },
        };
        var tui = o.ToTuiLaunchSpec("/p");
        Assert.Equal(["--approval-mode", "write"], tui.Arguments.Take(2)); // never omp's own default, yolo
        Assert.False(tui.Environment!.ContainsKey("NO_COLOR"));
        Assert.False(tui.Environment.ContainsKey("LC_ALL"));
        Assert.Equal("1", tui.Environment["MY_SETTING"]);
        Assert.Equal("work", tui.Environment["OMP_PROFILE"]);
        Assert.True(tui.Environment.ContainsKey("ANTHROPIC_API_KEY") && tui.Environment["ANTHROPIC_API_KEY"] is null);
        Assert.Equal("1", o.ToLaunchSpec().Environment!["NO_COLOR"]); // the RPC process keeps its hints

        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs()) { OmpTuiLaunch = _ => tui };
        vm.OpenOmpTuiCommand.Execute(null);
        Assert.Equal("", Assert.Single(vm.Terminals).Environment["ANTHROPIC_API_KEY"]); // passed empty = unset for omp
    }

    [Fact]
    public void A_shell_variable_that_names_no_file_is_not_used()
    {
        // Regression (Windows CI): $SHELL was taken as is; from Git Bash on Windows it is an MSYS path (/usr/bin/bash)
        // that cannot be started, so the terminal (and the self-test's terminal check) failed.
        var saved = Environment.GetEnvironmentVariable("SHELL");
        try
        {
            Environment.SetEnvironmentVariable("SHELL", Path.Combine(TestProcesses.TempDir("no-shell"), "bash-not-here"));
            var (file, _) = MainViewModel.DefaultShell();
            Assert.True(File.Exists(file) || file == "/bin/sh", file);
            Assert.DoesNotContain("bash-not-here", file);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHELL", saved);
        }
    }
}
