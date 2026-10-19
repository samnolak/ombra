using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>The "open elsewhere" banner in the real window: read-only chat, kept draft, "Open here".</summary>
public sealed class SessionLeaseUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is { } d
        ? Path.Combine(d, "SessionLease")
        : Path.Combine(AppContext.BaseDirectory, "screenshots", "SessionLease");

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

    private static void Shot(Window w, string name)
    {
        for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); w.UpdateLayout(); }
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static T Named<T>(Window w, string name) where T : Control =>
        w.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    private static (MainWindow W, MainViewModel Vm) Open(SessionController s)
    {
        var vm = new MainViewModel(s, new AppArgs(), settings: new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("lease-ui"), "settings.json")));
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        return (w, vm);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task A_chat_open_in_a_terminal_is_read_only_until_Open_here(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        var f = SessionLeaseTests.Create();
        f.Hold("omp", 4242);
        var (w, vm) = Open(new SessionController(f.Factory, new LaunchRequest(f.Cwd, ResumeSessionFile: f.File)));
        try
        {
            await Until(() => vm.IsLeaseReadOnly, "held banner");
            var banner = Named<Border>(w, "SessionLeaseBanner");
            Assert.True(banner.IsEffectivelyVisible);
            Assert.Equal(Loc.F("Open in omp in a terminal (pid {0})", 4242), Named<TextBlock>(w, "SessionLeaseTitle").Text);
            Assert.True(Named<Button>(w, "OpenHereButton").IsEffectivelyEnabled);
            Assert.False(vm.CanRecover);
            Assert.False(Named<Border>(w, "RecoverCard").IsEffectivelyVisible);
            // The conversation is shown; the message box takes no input but keeps what was typed
            await Until(() => vm.Rows.OfType<UserRowViewModel>().Any(), "saved conversation");
            vm.ComposerText = "and add a --verbose flag";
            Assert.False(Named<Border>(w, "ComposerBox").IsEffectivelyEnabled);
            Assert.False(vm.SendCommand.CanExecute(null));
            Shot(w, $"held-{theme}");

            Named<Button>(w, "OpenHereButton").Command!.Execute(null);
            await Until(() => !vm.IsLeaseReadOnly && vm.Phase == SessionPhase.Ready, "opened here");
            Assert.False(Named<Border>(w, "SessionLeaseBanner").IsEffectivelyVisible);
            Assert.True(Named<Border>(w, "ComposerBox").IsEffectivelyEnabled);
            Assert.Equal("and add a --verbose flag", vm.ComposerText); // the draft survived
            Assert.Contains(f.File, Assert.Single(f.Takes));
            Shot(w, $"opened-here-{theme}");
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
            MainViewModel.ApplyTheme("light");
        }
    }

    [AvaloniaFact]
    public async Task A_holder_that_does_not_let_go_says_so_and_stays_read_only()
    {
        var f = SessionLeaseTests.Create();
        f.Hold("ombra", 977);
        f.Stick();
        var (w, vm) = Open(new SessionController(f.Factory, new LaunchRequest(f.Cwd, ResumeSessionFile: f.File)));
        try
        {
            await Until(() => vm.IsLeaseReadOnly, "held banner");
            Assert.Equal(Loc.F("Open in another Ombra window (pid {0})", 977), vm.LeaseBannerText);
            await vm.OpenHereCommand.ExecuteAsync(null);
            await Until(() => vm.HasLeaseError, "refusal shown");
            Assert.True(Named<TextBlock>(w, "SessionLeaseError").IsEffectivelyVisible);
            Assert.True(vm.IsLeaseReadOnly);
            Assert.False(Named<Border>(w, "ComposerBox").IsEffectivelyEnabled);
            Shot(w, "held-refused");
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_chat_opened_elsewhere_turns_read_only_and_keeps_the_draft()
    {
        var f = SessionLeaseTests.Create();
        var s = new SessionController(f.Factory, new LaunchRequest(f.Cwd, ResumeSessionFile: f.File));
        var (w, vm) = Open(s);
        try
        {
            await Until(() => vm.Phase == SessionPhase.Ready, "ready");
            vm.ComposerText = "half-typed thought";
            try { await s.Connection!.RequestAsync("test_lease_lost", wr => { wr.WriteString("kind", "ombra"); wr.WriteNumber("pid", 5150); }); }
            catch (RpcConnectionClosedException) { }
            await Until(() => vm.IsLeaseReadOnly && vm.Phase == SessionPhase.Stopped, "lost banner");
            Assert.Equal(Loc.T("This chat was opened elsewhere"), Named<TextBlock>(w, "SessionLeaseTitle").Text);
            Assert.False(Named<Border>(w, "ComposerBox").IsEffectivelyEnabled);
            Assert.False(vm.CanRecover);
            Assert.Equal("half-typed thought", vm.ComposerText);
            Shot(w, "lost");

            Named<Button>(w, "OpenHereButton").Command!.Execute(null);
            await Until(() => !vm.IsLeaseReadOnly && vm.Phase == SessionPhase.Ready, "opened here again");
            Assert.True(Named<Border>(w, "ComposerBox").IsEffectivelyEnabled);
            Assert.Equal("half-typed thought", vm.ComposerText);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }
}
