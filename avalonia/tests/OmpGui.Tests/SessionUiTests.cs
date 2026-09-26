using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Sessions sidebar and header in the real window over the fake omp (sessions kept in a temp folder).</summary>
public sealed class SessionUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm, SessionController s)> OpenAsync(double width = 1200, double height = 760)
    {
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("ui-sessions")), new LaunchRequest(TestProcesses.TempDir("ui-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm, s);
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

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static async Task SendAsync(MainWindow w, MainViewModel vm, string text)
    {
        w.FindControl<TextBox>("Composer")!.Focus();
        w.KeyTextInput(text);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == text) && vm.Rows.OfType<ToolRowViewModel>().Any(), "run " + text);
    }

    [AvaloniaFact]
    public async Task Sidebar_lists_sessions_new_session_and_switching_back()
    {
        var (w, vm, _) = await OpenAsync();
        Assert.True(w.FindControl<ListBox>("SessionList")!.IsEffectivelyVisible);
        await SendAsync(w, vm, "first topic");
        await Until(() => vm.Sessions.Count == 1 && vm.Sessions[0].Title == "first topic", "listed");
        Assert.True(vm.Sessions[0].IsCurrent);
        Assert.Equal("first topic", vm.SessionTitle);

        var mod = w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
        w.KeyPress(Key.N, (RawInputModifiers)mod, PhysicalKey.N, null);
        await Until(() => vm.Rows.Count == 0 && vm.SessionTitle == "New session" && vm.Phase == SessionPhase.Ready, "new session");
        await SendAsync(w, vm, "second topic");
        await Until(() => vm.Sessions.Count == 2 && vm.Sessions[0].Title == "second topic", "both listed");
        Shot(w, "ui-sessions-sidebar");

        vm.OpenSessionCommand.Execute(vm.Sessions.Single(x => x.Title == "first topic"));
        await Until(() => vm.SessionTitle == "first topic" && vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text).SequenceEqual(["first topic"]), "switched back");
        Assert.True(vm.Sessions.Single(x => x.Title == "first topic").IsCurrent);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Rename_in_the_header_with_keyboard()
    {
        var (w, vm, _) = await OpenAsync();
        await SendAsync(w, vm, "something");
        vm.StartRenameCommand.Execute(null);
        await Until(() => w.FindControl<TextBox>("RenameBox")!.IsFocused, "rename box focused");
        w.KeyPress(Key.A, (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers, PhysicalKey.A, null);
        w.KeyTextInput("Parser work");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.SessionTitle == "Parser work" && vm.Sessions.Any(x => x.Title == "Parser work"), "renamed");
        Assert.False(vm.IsRenaming);
        Assert.True(w.FindControl<TextBox>("Composer")!.IsFocused);

        vm.StartRenameCommand.Execute(null);
        await Until(() => w.FindControl<TextBox>("RenameBox")!.IsFocused, "rename box focused again");
        w.KeyTextInput("discarded");
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsRenaming);
        Assert.Equal("Parser work", vm.SessionTitle);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Sidebar_hides_in_narrow_windows_and_opens_on_demand()
    {
        var (w, vm, _) = await OpenAsync(640, 480);
        Assert.True(vm.IsNarrow);
        Assert.False(vm.ShowSidebar);
        var mod = (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
        w.KeyPress(Key.B, mod, PhysicalKey.B, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ShowSidebar);
        Shot(w, "ui-sessions-narrow-open");
        w.Width = 1200;
        Dispatcher.UIThread.RunJobs();
        await Until(() => !vm.IsNarrow, "wide");
        Assert.True(vm.ShowSidebar);
        w.KeyPress(Key.B, mod, PhysicalKey.B, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ShowSidebar);
        await vm.DisposeAsync();
        w.Close();
    }
}
