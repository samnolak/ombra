using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Platform;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Attention when the window is in the background: title mark, OS notification, cleared on activation.</summary>
public sealed class AttentionTests
{
    internal sealed class RecordingNotifier : INotifier
    {
        public readonly List<(string Title, string Message, Action Clicked)> Calls = [];
        public void Notify(string title, string message, nint windowHandle, Action clicked) => Calls.Add((title, message, clicked));
    }

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

    private static async Task<(MainWindow w, MainViewModel vm, RecordingNotifier n)> OpenAsync(string scenario)
    {
        var n = new RecordingNotifier();
        var vm = new MainViewModel(new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("att")), new LaunchRequest(TestProcesses.TempDir("attp"))), new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 700, Notifier = n };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm, n);
    }

    [AvaloniaFact]
    public async Task A_run_that_ends_in_the_background_marks_the_title_and_notifies()
    {
        var (w, vm, n) = await OpenAsync("normal");
        vm.ComposerText = "do it";
        vm.SendCommand.Execute(null);
        vm.SetWindowActive(false);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(), "run end");
        await Until(() => vm.NeedsAttention, "attention");
        Assert.StartsWith("● ", vm.WindowTitle);
        Assert.StartsWith("● ", w.Title);
        Assert.Equal("omp finished", Assert.Single(n.Calls).Title);
        Assert.StartsWith("tok0", n.Calls[0].Message);

        vm.SetWindowActive(true);
        Assert.False(vm.NeedsAttention);
        Assert.DoesNotContain("●", vm.WindowTitle);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_run_that_ends_while_minimized_notifies_before_the_window_is_restored()
    {
        // Regression (package check on the Linux candidate): while minimized the UI applies no snapshots, and the
        // attention check ran only inside an apply, so a run ending in a minimized window never notified.
        var (w, vm, n) = await OpenAsync("normal");
        // Minimized first: a run that ends while the window is still active rightly does not notify.
        w.WindowState = Avalonia.Controls.WindowState.Minimized;
        vm.SetWindowState("Minimized");
        vm.SetWindowActive(false);
        vm.ComposerText = "do it";
        vm.SendCommand.Execute(null);
        await Until(() => n.Calls.Count > 0, "notification while minimized");
        Assert.Equal("omp finished", Assert.Single(n.Calls).Title);
        Assert.StartsWith("tok0", n.Calls[0].Message);
        Assert.StartsWith("● ", vm.WindowTitle);
        Assert.Equal(Avalonia.Controls.WindowState.Minimized, w.WindowState);

        w.WindowState = Avalonia.Controls.WindowState.Normal;
        vm.SetWindowState("Normal");
        vm.SetWindowActive(true);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(), "restore shows the result");
        Assert.Single(n.Calls); // the restore's apply does not notify again
        Assert.False(vm.NeedsAttention);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task An_approval_asked_while_minimized_notifies()
    {
        var (w, vm, n) = await OpenAsync("approval");
        // Minimized first: an approval card shown while the window is active rightly does not notify.
        w.WindowState = Avalonia.Controls.WindowState.Minimized;
        vm.SetWindowState("Minimized");
        vm.SetWindowActive(false);
        vm.ComposerText = "run it";
        vm.SendCommand.Execute(null);
        await Until(() => n.Calls.Count > 0, "notification while minimized");
        Assert.Equal("omp needs your approval", n.Calls[0].Title);

        w.WindowState = Avalonia.Controls.WindowState.Normal;
        vm.SetWindowState("Normal");
        vm.SetWindowActive(true);
        await Until(() => vm.HasDialog, "approval shown after restore");
        Assert.Single(n.Calls);
        vm.CurrentDialog!.DenyCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "run end");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_run_that_starts_and_ends_between_two_updates_still_notifies()
    {
        // Regression (one-core candidate run): the end of a run was taken from a running → ready change between two
        // applied snapshots. A short run that started and ended between two applies was never seen running, so the
        // background window got no notification.
        var (w, vm, n) = await OpenAsync("normal");
        vm.SetWindowActive(false);
        var before = SnapshotOf(vm);
        Assert.Equal(SessionPhase.Ready, before.Phase);
        vm.Apply(before with { Version = before.Version + 1, RunsEnded = before.RunsEnded + 1 });
        Assert.Equal("omp finished", Assert.Single(n.Calls).Title);
        Assert.True(vm.NeedsAttention);
        vm.Apply(before with { Version = before.Version + 2, RunsEnded = before.RunsEnded + 1 });
        vm.Apply(before); // an older snapshot applied late
        vm.Apply(before with { Version = before.Version + 3, RunsEnded = before.RunsEnded + 1 });
        Assert.Single(n.Calls); // the same run never notifies twice
        await vm.DisposeAsync();
        w.Close();
    }

    private static SessionSnapshot SnapshotOf(MainViewModel vm) => vm.Session.Snapshot();

    [AvaloniaFact]
    public async Task An_approval_in_the_background_notifies_and_nothing_happens_while_active()
    {
        var (w, vm, n) = await OpenAsync("approval");
        vm.ComposerText = "run it";
        vm.SendCommand.Execute(null);
        await Until(() => vm.HasDialog, "approval shown");
        Assert.Empty(n.Calls); // window active: the card is enough
        vm.CurrentDialog!.AllowCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "run end");
        Assert.Empty(n.Calls);

        vm.SetWindowActive(false);
        vm.NotificationsEnabled = false;
        vm.ComposerText = "again";
        vm.SendCommand.Execute(null);
        await Until(() => vm.HasDialog, "second approval");
        Assert.True(vm.NeedsAttention); // the title still says so
        Assert.Empty(n.Calls);          // but no OS notification when turned off
        vm.CurrentDialog!.DenyCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "run end");
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public void Notification_text_that_looks_like_an_option_stays_text()
    {
        // Regression (review round 4): a reply starting with a Markdown bullet ("- Fixed…") was parsed by notify-send as
        // options, so no notification appeared (and "-t …" / "-h …" would have changed its behaviour).
        var args = OmpGui.App.Platform.NotifySendNotifier.Arguments("OMP GUI", "- Fixed the bug");
        Assert.Equal(["--app-name=OMP GUI", "--", "OMP GUI", "- Fixed the bug"], args);
        // The clickable form ends its options the same way
        Assert.Equal(["--app-name=OMP GUI", "--action=default=Open", "--wait", "--", "OMP GUI", "- Fixed the bug"],
            OmpGui.App.Platform.NotifySendNotifier.ActionArguments("OMP GUI", "- Fixed the bug"));
    }

    [Fact]
    public void Notifiers_never_throw_when_their_tool_is_missing()
    {
        new NotifySendNotifier().Notify("t", "m", 0, () => { });
        new OsaScriptNotifier().Notify("t", "\" & do shell script \"x", 0, () => { });
        new TaskbarFlashNotifier().Notify("t", "m", 0, () => { });
    }

    /// <summary>
    /// Real-app finding: the frame a reply ended in froze for 50–85 ms when "omp finished" was notified (the window in the
    /// background): .NET's Process.Start forks the app on macOS, stalling the UI thread on the runtime's locks. The helper
    /// is started without a fork there (posix_spawn): it still gets its arguments (spaces kept, nothing interpreted) and
    /// the app's environment, and Detached returns at once.
    /// </summary>
    [Fact]
    public async Task A_detached_helper_gets_its_arguments_and_the_environment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the notifiers that start helpers are macOS and Linux ones");
        var dir = TestProcesses.TempDir("detached");
        var script = Path.Combine(dir, "helper.sh");
        var output = Path.Combine(dir, "out.txt");
        await File.WriteAllTextAsync(script, $"printf '%s|%s|%s' \"$1\" \"$2\" \"$HOME\" > '{output}.tmp' && mv '{output}.tmp' '{output}'\n", TestContext.Current.CancellationToken);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Run.Detached("sh", [script, "one two", "\" & $(rm -rf x)"]);
        Assert.True(watch.ElapsedMilliseconds < 50, $"starting the helper took {watch.ElapsedMilliseconds} ms");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(output) && DateTime.UtcNow < deadline) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal($"one two|\" & $(rm -rf x)|{Environment.GetEnvironmentVariable("HOME")}", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }
}
