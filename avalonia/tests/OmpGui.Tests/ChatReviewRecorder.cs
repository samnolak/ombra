using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Design review tool (runs only with OMPGUI_REVIEW_DIR): records a paced session frame by frame.</summary>
public sealed class ChatReviewRecorder
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    private static double Y(Window w, Control c) => c.IsEffectivelyVisible ? c.TranslatePoint(new Point(0, 0), w)?.Y ?? -1 : -1;

    [AvaloniaFact]
    public async Task Record_a_session()
    {
        if (Dir is null) return;
        Directory.CreateDirectory(Path.Combine(Dir, "frames"));
        var s = new SessionController(TestProcesses.FakeFactory("session", TestProcesses.TempDir("rev")), new LaunchRequest(TestProcesses.TempDir("rev-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var composer = w.FindControl<Border>("ComposerBox")!;
        var activity = w.FindControl<Grid>("ActivityLine")!;
        var todo = w.FindControl<Border>("TodoCard")!;
        var list = w.FindControl<ListBox>("Transcript")!;
        var log = new StringBuilder("ms,phase,status,activityY,composerY,todoY,rows,offset,extent,viewport,atBottom\n");
        var start = DateTime.UtcNow;
        vm.ComposerText = "change the greeting to Hello";
        vm.SendCommand.Execute(null);
        var i = 0;
        var endAt = DateTime.MaxValue;
        while (DateTime.UtcNow < start.AddSeconds(40))
        {
            Dispatcher.UIThread.RunJobs();
            var sv = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var ms = (DateTime.UtcNow - start).TotalMilliseconds;
            log.Append(CultureInfo.InvariantCulture,
                $"{ms:0},{vm.Phase},\"{vm.StatusText}\",{Y(w, activity):0},{Y(w, composer):0},{Y(w, todo):0},{vm.Rows.Count},{sv?.Offset.Y:0},{sv?.Extent.Height:0},{sv?.Viewport.Height:0},{(sv is null ? "" : sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 2 ? "1" : "0")}\n");
            using (var frame = w.CaptureRenderedFrame())
            using (var f = File.Create(Path.Combine(Dir, "frames", $"f{i++:0000}.png")))
                frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            if (vm.Phase == SessionPhase.Ready && endAt == DateTime.MaxValue && ms > 500) endAt = DateTime.UtcNow.AddSeconds(1.5);
            if (DateTime.UtcNow > endAt) break;
            await Task.Delay(40);
        }
        File.WriteAllText(Path.Combine(Dir, "frames.csv"), log.ToString());
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>Clipped or out-of-bounds text and controls in the main column, for the review log.</summary>
    private static IEnumerable<string> Problems(Window w)
    {
        var main = w.FindControl<Grid>("MainColumn")!;
        var right = main.TranslatePoint(new Point(main.Bounds.Width, 0), w)!.Value.X;
        foreach (var c in main.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0))
        {
            if (c.FindAncestorOfType<ListBox>() is { Name: "DebugList" }) continue;
            var p = c.TranslatePoint(new Point(0, 0), w);
            if (p is null) continue;
            var name = c.Name ?? (c is TextBlock t0 ? "\"" + (t0.Text ?? "")[..Math.Min(24, (t0.Text ?? "").Length)] + "\"" : c.GetType().Name + "." + string.Join(".", c.Classes));
            if (c is TextBlock t && t.TextTrimming == Avalonia.Media.TextTrimming.None && t.TextWrapping == Avalonia.Media.TextWrapping.NoWrap
                && !string.IsNullOrEmpty(t.Text))
            {
                var need = new TextBlock { Text = t.Text, FontSize = t.FontSize, FontFamily = t.FontFamily, FontWeight = t.FontWeight };
                need.Measure(Size.Infinity);
                if (need.DesiredSize.Width > t.Bounds.Width + 1) yield return $"clipped text {name}: needs {need.DesiredSize.Width:0}, has {t.Bounds.Width:0}";
            }
            if (c is Button or TextBlock or Avalonia.Controls.Shapes.Ellipse && p.Value.X + c.Bounds.Width > right + 1 && c.FindAncestorOfType<ScrollViewer>() is null)
                yield return $"past the column {name}: right {p.Value.X + c.Bounds.Width:0} > {right:0}";
        }
    }

    [AvaloniaFact]
    public async Task Resize_sweep()
    {
        if (Dir is null) return;
        var outDir = Path.Combine(Dir, "sizes");
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "1");
        var s = new SessionController(TestProcesses.FakeFactory("session", TestProcesses.TempDir("rev")), new LaunchRequest(TestProcesses.TempDir("rev-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        async Task Settle(int ms = 700) { var d = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < d) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(15); } }
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) await Settle(30);
        vm.ComposerText = "change the greeting to Hello";
        vm.SendCommand.Execute(null);
        await Settle(400);
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) await Settle(30);
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null);
        await Settle(400);
        var list = w.FindControl<ListBox>("Transcript")!;
        var report = new StringBuilder();
        void Shot(string name)
        {
            Dispatcher.UIThread.RunJobs();
            // The frame is what the user sees: measure after it was laid out and drawn
            using var frame = w.CaptureRenderedFrame();
            var sv = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var atBottom = sv is null || sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 2;
            report.AppendLine($"## {name}  ({w.Width}x{w.Height}) atBottom={atBottom}");
            foreach (var p in Problems(w).Distinct()) report.AppendLine("  " + p);
            using var f = File.Create(Path.Combine(outDir, name + ".png"));
            frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        foreach (var (wd, ht) in new[] { (1440, 900), (1180, 760), (1000, 700), (860, 640), (760, 600), (640, 560), (480, 360) })
        {
            w.Width = wd; w.Height = ht; await Settle();
            Shot($"w{wd}x{ht}");
        }
        w.Width = 1180; w.Height = 760; await Settle();
        vm.ToggleTerminalCommand.Execute(null); await Settle(800); Shot("terminal-open");
        vm.ToggleTerminalCommand.Execute(null); await Settle(); Shot("terminal-closed");
        vm.IsPreviewOpen = true; await Settle(); Shot("preview-open-1180");
        w.Width = 900; await Settle(); Shot("preview-open-900");
        vm.IsPreviewOpen = false; w.Width = 1180; await Settle();
        vm.ToggleSidebarCommand.Execute(null); await Settle(); Shot("sidebar-hidden");
        w.Width = 700; await Settle(); Shot("sidebar-hidden-700");
        vm.ToggleSidebarCommand.Execute(null); w.Width = 1180; await Settle();
        foreach (var r in vm.Rows.OfType<ToolRowViewModel>()) r.IsExpanded = true;
        await Settle(); Shot("all-tools-open");
        File.WriteAllText(Path.Combine(Dir, "sizes.md"), report.ToString());
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Shoot_a_running_command(string theme)
    {
        if (Dir is null) return;
        MainViewModel.ApplyTheme(theme);
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "120");
        try
        {
            var s = new SessionController(TestProcesses.FakeFactory("session", TestProcesses.TempDir("rev")), new LaunchRequest(TestProcesses.TempDir("rev-project")));
            var vm = new MainViewModel(s, new AppArgs());
            var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
            w.Show();
            vm.OnWindowOpened();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            vm.ComposerText = "change the greeting to Hello";
            vm.SendCommand.Execute(null);
            while (!vm.Rows.OfType<ToolRowViewModel>().Any(t => t.ShowTail && t.Tail.Split('\n').Length >= 3) && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Dispatcher.UIThread.RunJobs();
            using (var frame = w.CaptureRenderedFrame())
            using (var f = File.Create(Path.Combine(Dir, $"running-{theme}.png")))
                frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            await vm.DisposeAsync();
            w.Close();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null);
            MainViewModel.ApplyTheme("light");
        }
    }

    [AvaloniaFact]
    public async Task Shoot_the_composer_menus()
    {
        if (Dir is null) return;
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("rev")), new LaunchRequest(TestProcesses.TempDir("rev-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        await Task.Delay(300);
        foreach (var name in new[] { "AttachButton", "ProjectButton", "ApprovalButton" })
        {
            var b = w.FindControl<Button>(name)!;
            b.Flyout!.ShowAt(b);
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
            using (var frame = w.CaptureRenderedFrame())
            using (var f = File.Create(Path.Combine(Dir, $"menu-{name}.png")))
                frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            b.Flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        }
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Shoot_file_changes(string theme)
    {
        if (Dir is null) return;
        MainViewModel.ApplyTheme(theme);
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "1");
        try
        {
            var s = new SessionController(TestProcesses.FakeFactory("session", TestProcesses.TempDir("rev")), new LaunchRequest(TestProcesses.TempDir("rev-project")));
            var vm = new MainViewModel(s, new AppArgs());
            var w = new MainWindow { DataContext = vm, Width = 1180, Height = 1100 };
            w.Show();
            vm.OnWindowOpened();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            vm.ComposerText = "change the greeting to Hello";
            vm.SendCommand.Execute(null);
            while (vm.Phase != SessionPhase.Running && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            var list = w.FindControl<ListBox>("Transcript")!;
            var edit = vm.Rows.OfType<ToolRowViewModel>().First(t => t.Name == "edit");
            for (var k = 0; k < 20; k++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            list.ScrollIntoView(edit);
            for (var k = 0; k < 10; k++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            var sv = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            if (list.ContainerFromItem(edit) is Control c && c.TranslatePoint(new Point(0, 0), sv) is { } p)
                sv.Offset = new Vector(0, sv.Offset.Y + p.Y - 20);
            for (var k = 0; k < 10; k++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            using (var frame = w.CaptureRenderedFrame())
            using (var f = File.Create(Path.Combine(Dir, $"changes-{theme}.png")))
                frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            await vm.DisposeAsync();
            w.Close();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null);
            MainViewModel.ApplyTheme("light");
        }
    }
}
