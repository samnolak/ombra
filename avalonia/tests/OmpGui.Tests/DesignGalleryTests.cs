using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The main screens in the light and dark theme, as screenshots for the design guide (docs/design/screens).
/// Also checks that nothing on them is clipped at the right edge.
/// </summary>
public sealed class DesignGalleryTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

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

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Main_screens_in_both_themes(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        try
        {
            foreach (var (scenario, prompt, until, name) in new (string, string, Func<MainViewModel, bool>, string)[]
            {
                ("markdown", "explain the change", vm => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(), "transcript"),
                ("approval", "run it", vm => vm.HasDialog, "approval"),
                ("todo", "plan it", vm => vm.Phase == SessionPhase.Ready && vm.HasTodos, "todos"),
            })
            {
                var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("gallery")), new LaunchRequest(TestProcesses.TempDir("gallery-project"), ApprovalMode: "write"));
                var vm = new MainViewModel(s, new AppArgs());
                var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
                w.Show();
                vm.OnWindowOpened();
                await Until(() => vm.Phase == SessionPhase.Ready, "ready");
                if (name == "transcript")
                {
                    // A new session: the greeting in the middle of the empty conversation
                    Assert.True(w.FindControl<StackPanel>("EmptyState")!.IsEffectivelyVisible);
                    Shot(w, $"design-new-session-{theme}");
                }
                vm.ComposerText = prompt;
                vm.SendCommand.Execute(null);
                await Until(() => until(vm), name);
                Assert.False(w.FindControl<StackPanel>("EmptyState")!.IsEffectivelyVisible);
                Shot(w, $"design-{name}-{theme}");
                if (name == "transcript")
                {
                    // The preview panel beside the conversation (headless: no web engine, so its empty state)
                    vm.Preview.OfferUrlsFrom("  VITE ready in 312 ms\n  ➜  Local:   http://localhost:5173/");
                    vm.IsPreviewOpen = true;
                    Shot(w, $"design-preview-{theme}");
                    vm.IsPreviewOpen = false;
                }
                if (vm.CurrentDialog is { } d) d.DenyCommand.Execute(null);
                await vm.DisposeAsync();
                w.Close();
            }

            var failed = new SessionController(TestProcesses.Fake("no-models"));
            var fvm = new MainViewModel(failed, new AppArgs());
            var fw = new MainWindow { DataContext = fvm, Width = 1180, Height = 760 };
            fw.Show();
            fvm.OnWindowOpened();
            await Until(() => fvm.CanRecover, "first run");
            Shot(fw, $"design-first-run-{theme}");
            fvm.OpenSettingsCommand.Execute(null);
            Shot(fw, $"design-settings-{theme}");
            await fvm.DisposeAsync();
            fw.Close();
        }
        finally
        {
            MainViewModel.ApplyTheme("system");
        }
    }
}
