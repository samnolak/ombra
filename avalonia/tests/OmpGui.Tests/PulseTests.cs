using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The "omp is working" pulses (QA D1): stepped by one shared timer instead of style animations that redrew the whole
/// window every vsync; only while shown, and nothing ticks once none is.
/// </summary>
public sealed class PulseTests
{
    private static async Task Settle(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
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

    /// <summary>Every opacity the dot takes over <paramref name="ms"/> (all pulses share one phase, so a watch shorter than
    /// the ~0.8 s a 1.1 s fade spends above 0.8 may see only its bright part).</summary>
    private static async Task<List<double>> Watch(Visual v, int ms)
    {
        var seen = new List<double>();
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            seen.Add(v.Opacity);
            await Task.Delay(10);
        }
        return seen;
    }

    [Fact]
    public void The_fade_is_sine_eased_there_and_back_in_visible_steps()
    {
        var d = TimeSpan.FromSeconds(1.1);
        Assert.Equal(1, Pulse.OpacityAt(TimeSpan.Zero, 0.3, d), 6);
        Assert.Equal(0.65, Pulse.OpacityAt(d / 2, 0.3, d), 6);
        Assert.Equal(0.3, Pulse.OpacityAt(d, 0.3, d), 6);
        Assert.Equal(1, Pulse.OpacityAt(d * 2, 0.3, d), 6);
        var previous = 1.0;
        for (var t = 0; t <= 1100; t += 1000 / Pulse.MaxFps)
        {
            var o = Pulse.OpacityAt(TimeSpan.FromMilliseconds(t), 0.3, d);
            Assert.InRange(o, 0.3, 1);
            Assert.True(o <= previous, $"fading out: {o} after {previous} at {t} ms");
            Assert.Equal(Math.Round(o * 100), o * 100, 6); // a change under 1 % is not a new frame
            previous = o;
        }
    }

    [AvaloniaFact]
    public async Task A_dot_pulses_only_while_styled_running_shown_and_in_the_window()
    {
        var dot = new Ellipse { Classes = { "state-dot", "running" } };
        var box = new Border { Child = dot };
        var panel = new StackPanel { Children = { box } };
        var w = new Window { Content = panel, Width = 200, Height = 100 };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        // (Its fade over time is watched in the window test below: here only what holds at once, whatever runs beside it)
        Assert.True(Pulse.IsPulsing(dot));
        Assert.True(Pulse.IsTicking);
        Assert.InRange(dot.Opacity, 0.3, 1);

        // Hidden (itself or an ancestor): no pulse, its own opacity back
        dot.IsVisible = false;
        Assert.False(Pulse.IsPulsing(dot));
        Assert.Equal(1, dot.Opacity);
        dot.IsVisible = true;
        Assert.True(Pulse.IsPulsing(dot));
        box.IsVisible = false;
        Assert.False(Pulse.IsPulsing(dot));
        Assert.Equal(1, dot.Opacity);
        box.IsVisible = true;
        Assert.True(Pulse.IsPulsing(dot));

        // Done (the style no longer applies): still, at full opacity
        dot.Classes.Remove("running");
        Assert.False(Pulse.IsPulsing(dot));
        Assert.Equal(1, dot.Opacity);
        Assert.Equal(Pulse.Running > 0, Pulse.IsTicking);
        Assert.Equal(1, (await Watch(dot, 150)).Min());

        // Out of the window
        dot.Classes.Add("running");
        Assert.True(Pulse.IsPulsing(dot));
        panel.Children.Remove(box);
        Assert.False(Pulse.IsPulsing(dot));
        panel.Children.Add(box);
        Assert.True(Pulse.IsPulsing(dot));
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Pulse.IsPulsing(dot));
        Assert.Equal(Pulse.Running > 0, Pulse.IsTicking);
    }

    [AvaloniaFact]
    public async Task The_activity_dot_pulses_during_a_run_and_nothing_pulses_after_it()
    {
        var s = new SessionController(TestProcesses.FakeFactory("session", TestProcesses.TempDir("pulse")), new LaunchRequest(TestProcesses.TempDir("pulse-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        var dot = w.FindControl<Grid>("ActivityLine")!.GetVisualDescendants().OfType<Ellipse>().Single();
        Assert.False(Pulse.IsPulsing(dot));

        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "80");
        try
        {
            vm.ComposerText = "change the greeting to Hello";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Running && dot.IsEffectivelyVisible, "running");
            Assert.True(Pulse.IsPulsing(dot));
            Assert.Contains(await Watch(dot, 1200), o => o < 0.8);
        }
        finally { Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null); }
        await Until(() => vm.Phase == SessionPhase.Ready, "run end");
        await Settle(100);
        Assert.False(Pulse.IsPulsing(dot));
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<Visual>(), Pulse.IsPulsing);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>Inter's contextual alternates drew "w4x0" as "w4×0" and "0x1F" as "0×1F" (QA polish): off everywhere.</summary>
    [AvaloniaFact]
    public async Task Text_is_drawn_without_contextual_alternates()
    {
        var s = new SessionController(TestProcesses.FakeFactory("session", TestProcesses.TempDir("calt")), new LaunchRequest(TestProcesses.TempDir("calt-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        var texts = w.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(texts);
        foreach (var text in texts)
        {
            var features = TextElement.GetFontFeatures(text);
            Assert.NotNull(features);
            Assert.Contains(features, f => f.Tag == "calt" && f.Value == 0);
        }
        await vm.DisposeAsync();
        w.Close();
    }
}
