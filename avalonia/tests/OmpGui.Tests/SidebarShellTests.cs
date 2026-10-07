using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>The app shell: sidebar (ages, pins, status, width, Ctrl+Tab) and the settings page in the sidebar's place.</summary>
public sealed class SidebarShellTests
{
    private static async Task<(MainWindow w, MainViewModel vm)> OpenAsync(string scenario = "normal", ClientSettingsStore? store = null, double width = 1200)
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("shell-sessions")), new LaunchRequest(TestProcesses.TempDir("shell-project")));
        var vm = new MainViewModel(s, new AppArgs(), settings: store);
        var w = new MainWindow { DataContext = vm, Width = width, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm);
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

    private static async Task SendAsync(MainWindow w, MainViewModel vm, string text)
    {
        w.FindControl<TextBox>("Composer")!.Focus();
        w.KeyTextInput(text);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == text), "run " + text);
    }

    private static async Task TwoSessionsAsync(MainWindow w, MainViewModel vm)
    {
        await SendAsync(w, vm, "first topic");
        await Until(() => vm.Sessions.Any(x => x.Title == "first topic"), "first listed");
        await vm.NewSessionCommand.ExecuteAsync(null);
        await Until(() => vm.Rows.Count == 0 && vm.Phase == SessionPhase.Ready, "new session");
        await SendAsync(w, vm, "second topic");
        await Until(() => vm.Sessions.Count == 2 && vm.Sessions.Single(x => x.Title == "second topic").IsCurrent, "both listed");
    }

    [Fact]
    public void Ages_read_like_claude_code()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("now", SessionItemViewModel.Relative(now.AddSeconds(-59), now));
        Assert.Equal("now", SessionItemViewModel.Relative(now.AddMinutes(3), now)); // a clock running behind
        Assert.Equal("1m", SessionItemViewModel.Relative(now.AddSeconds(-61), now));
        Assert.Equal("5m", SessionItemViewModel.Relative(now.AddMinutes(-5), now));
        Assert.Equal("3h", SessionItemViewModel.Relative(now.AddHours(-3.5), now));
        Assert.Equal("2d", SessionItemViewModel.Relative(now.AddDays(-2), now));
        // From a week on: the date, without the year in this year
        var old = SessionItemViewModel.Relative(now.AddDays(-30), now);
        Assert.DoesNotContain("2026", old);
        Assert.Contains(now.AddDays(-30).ToLocalTime().Day.ToString(CultureInfo.CurrentCulture), old);
        Assert.Contains("2024", SessionItemViewModel.Relative(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero), now));
    }

    [Fact]
    public void Pins_are_written_where_omp_reads_them()
    {
        var root = TestProcesses.TempDir("pins");
        var file = Path.Combine(root, "sessions", "--project--", "a.jsonl");
        var pins = SessionPins.PathFor(file)!;
        Assert.Equal(Path.Combine(root, "session-pins.json"), pins);
        Assert.Empty(SessionPins.Read(pins));
        File.WriteAllText(pins, "[\"keep\"]");
        Assert.Equal(["abc", "keep"], SessionPins.Set(pins, "abc", true).Order());
        Assert.Equal("[\"keep\",\"abc\"]", File.ReadAllText(pins)); // omp's order: as pinned
        SessionPins.Set(pins, "abc", false);
        Assert.Equal(["keep"], SessionPins.Read(pins));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [AvaloniaFact]
    public async Task A_pinned_session_is_listed_under_Pinned_and_its_project_keeps_its_header()
    {
        var (w, vm) = await OpenAsync();
        await TwoSessionsAsync(w, vm);
        var first = vm.Sessions.Single(x => x.Title == "first topic");
        await vm.TogglePinItemCommand.ExecuteAsync(first);
        await Until(() => vm.SessionGroups.Count == 2 && vm.SessionGroups[0].IsPinnedGroup, "pinned group");
        Assert.Equal(["first topic"], vm.SessionGroups[0].Items.Select(i => i.Title));
        Assert.Equal(["second topic"], vm.SessionGroups[1].Items.Select(i => i.Title));
        Assert.True(vm.SessionGroups[1].IsProject);
        Assert.Contains(first.Model.Id, SessionPins.Read(SessionPins.PathFor(first.Model.Path)));
        Assert.Equal("Unpin", vm.Sessions.Single(x => x.Title == "first topic").PinLabel);

        await vm.TogglePinItemCommand.ExecuteAsync(vm.Sessions.Single(x => x.Title == "first topic"));
        await Until(() => vm.SessionGroups.Count == 1 && !vm.SessionGroups[0].IsPinnedGroup, "unpinned");
        Assert.DoesNotContain(first.Model.Id, SessionPins.Read(SessionPins.PathFor(first.Model.Path)));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_Tab_moves_through_the_sessions_in_sidebar_order()
    {
        var (w, vm) = await OpenAsync();
        await TwoSessionsAsync(w, vm);
        w.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        await Until(() => vm.SessionTitle == "first topic" && vm.Sessions.Single(x => x.Title == "first topic").IsCurrent, "next session");
        w.KeyPress(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab, null);
        await Until(() => vm.SessionTitle == "second topic", "previous session");
        // It wraps: past the last row comes the first
        w.KeyPress(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab, null);
        await Until(() => vm.SessionTitle == "first topic", "wrapped");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Settings_take_the_sidebars_place_and_Esc_goes_back()
    {
        var (w, vm) = await OpenAsync();
        var sidebar = w.FindControl<Border>("Sidebar")!;
        Assert.True(sidebar.IsEffectivelyVisible);
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        await Until(() => vm.IsSettingsOpen, "settings");
        Assert.False(sidebar.IsEffectivelyVisible);
        Assert.True(w.FindControl<Button>("BackToAppButton")!.IsEffectivelyVisible);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Until(() => !vm.IsSettingsOpen, "closed by Esc");
        Assert.True(sidebar.IsEffectivelyVisible);

        // A narrow window: the pages as a strip under "Back to app" and the title
        w.Width = 560;
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        await Until(() => vm.IsSettingsNarrow, "narrow settings");
        Assert.True(w.FindControl<ScrollViewer>("SettingsNavStrip")!.IsEffectivelyVisible);
        Assert.True(w.FindControl<Button>("BackToAppNarrowButton")!.IsEffectivelyVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_sidebar_width_is_clamped_and_kept()
    {
        var path = Path.Combine(TestProcesses.TempDir("shell-settings"), "settings.json");
        var (w, vm) = await OpenAsync(store: new ClientSettingsStore(path));
        Assert.Equal(MainViewModel.SidebarDefaultWidth, vm.SidebarWidth);
        vm.SidebarWidth = 1000;
        Assert.Equal(MainViewModel.SidebarMaxWidth, vm.SidebarWidth);
        vm.SidebarWidth = 10;
        Assert.Equal(MainViewModel.SidebarMinWidth, vm.SidebarWidth);
        vm.SidebarWidth = 333;
        vm.SaveSidebarWidth();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(333, w.FindControl<Border>("Sidebar")!.Bounds.Width);
        await vm.DisposeAsync();
        w.Close();

        (w, vm) = await OpenAsync(store: new ClientSettingsStore(path));
        Assert.Equal(333, vm.SidebarWidth);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_open_sessions_dot_says_when_omp_waits_for_an_answer()
    {
        var (w, vm) = await OpenAsync("approval");
        w.FindControl<TextBox>("Composer")!.Focus();
        w.KeyTextInput("needs approval");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.HasDialog, "approval asked");
        vm.RequestCatalogRefresh();
        await Until(() => vm.Sessions.FirstOrDefault(x => x.IsCurrent) is { Status: SessionStatus.Waiting }, "waiting dot");
        Assert.Equal("Needs your input", vm.Sessions.Single(x => x.IsCurrent).StatusText);
        await vm.DisposeAsync();
        w.Close();
    }
}
