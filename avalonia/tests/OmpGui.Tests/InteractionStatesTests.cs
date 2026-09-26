using System.Globalization;
using System.Text;
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

/// <summary>
/// Hover, pressed and keyboard focus on every button of a working window, in both themes: nothing may change size or
/// move when the pointer or the focus reaches it (a jumping row), and with OMPGUI_REVIEW_DIR each state is saved with
/// its button's place (states.tsv) for the design review's contact sheets.
/// </summary>
public sealed class InteractionStatesTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    private static async Task Settle(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(10); }
    }

    private static bool Shown(Control c) => c.IsEffectivelyVisible && c.Bounds.Width > 1 && c.Bounds.Height > 1;

    private static Rect Box(Window w, Control c) => c.TransformToVisual(w) is { } m ? new Rect(c.Bounds.Size).TransformToAABB(m) : default;

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Hover_pressed_and_focus_move_nothing(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        var root = TestProcesses.TempDir("states-sessions");
        var cwd = Path.Combine(TestProcesses.TempDir("states"), "weather-cli");
        Directory.CreateDirectory(cwd);
        var file = ReadmeScreenshotTests.Write(root, cwd, "Add a --units flag to forecast", ReadmeScreenshotTests.Conversation(), DateTime.UtcNow.AddMinutes(-2));
        var spec = TestProcesses.Fake("normal");
        var s = new SessionController(req => spec with
        {
            Arguments = [.. spec.Arguments, "--resume", file],
            WorkingDirectory = req.WorkingDirectory,
            Environment = new Dictionary<string, string?> { ["FAKE_SESSION_DIR"] = root, ["FAKE_MODEL"] = "local/coder" },
        }, new LaunchRequest(cwd, ResumeSessionFile: file, ApprovalMode: "write"));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        w.Show();
        vm.OnWindowOpened();
        var tsv = new StringBuilder();
        var moved = new List<string>();
        var shot = 0;
        void Save(string state, Control c)
        {
            var b = Box(w, c);
            var name = $"{theme}-{state}-{shot++:000}.png";
            using var frame = w.CaptureRenderedFrame();
            if (Dir is null) return;
            Directory.CreateDirectory(Path.Combine(Dir, "states"));
            using (var f = File.Create(Path.Combine(Dir, "states", name))) frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            tsv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{theme}\t{state}\t{name}\t{b.X:0}\t{b.Y:0}\t{b.Width:0}\t{b.Height:0}\t{c.GetType().Name}\t{c.Name}\t{string.Join('.', c.Classes.Where(k => !k.StartsWith(':')))}"));
        }
        try
        {
            var until = DateTime.UtcNow.AddSeconds(40);
            while (vm.Phase is not (SessionPhase.Ready or SessionPhase.Faulted) && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            Assert.Equal(SessionPhase.Ready, vm.Phase);
            vm.ShowPaneCommand.Execute(SidePane.Plan);
            await Settle(800);
            var client = new Rect(0, 0, w.Bounds.Width, w.Bounds.Height);
            // Only buttons wholly in view: a half-hidden one is scrolled into view by a press, as it should be
            bool InView(Button b)
            {
                var box = Box(w, b);
                return client.Contains(box) && b.GetVisualAncestors().OfType<ScrollViewer>().All(v => Box(w, v).Inflate(0.5).Contains(box));
            }
            var buttons = w.GetVisualDescendants().OfType<Button>()
                .Where(b => Shown(b) && InView(b) && !b.GetVisualAncestors().OfType<Avalonia.Controls.Primitives.ScrollBar>().Any()).ToList();
            Assert.NotEmpty(buttons);
            foreach (var b in buttons)
            {
                var before = Box(w, b);
                var centre = before.Center;
                w.MouseMove(centre, RawInputModifiers.None);
                await Settle(60);
                Save("hover", b);
                w.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
                await Settle(40);
                Save("pressed", b);
                var pressed = Box(w, b);
                // Released away from it: no click, no flyout
                w.MouseUp(new Point(w.Bounds.Width - 2, w.Bounds.Height - 2), MouseButton.Left, RawInputModifiers.None);
                w.MouseMove(new Point(w.Bounds.Width - 2, w.Bounds.Height - 2), RawInputModifiers.None);
                await Settle(30);
                var after = Box(w, b);
                // Fluent's press feedback scales a button to 98 % about its centre: that is the press, not a jump
                var scaled = pressed.Width / before.Width;
                var pressOk = Math.Abs(pressed.Center.X - before.Center.X) <= 0.5 && Math.Abs(pressed.Center.Y - before.Center.Y) <= 0.5
                              && scaled is >= 0.97 and <= 1.001 && Math.Abs(pressed.Height / before.Height - scaled) < 0.01;
                if (!pressOk || Math.Abs(after.X - before.X) > 0.5 || Math.Abs(after.Y - before.Y) > 0.5 || Math.Abs(after.Width - before.Width) > 0.5)
                    moved.Add($"{b.Name}.{string.Join('.', b.Classes)}: {before} → pressed {pressed} → after {after}");
            }
            // The keyboard: Tab from the message box through the window
            w.FindControl<TextBox>("Composer")!.Focus();
            await Settle(50);
            for (var i = 0; i < 40; i++)
            {
                w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                await Settle(40);
                if (w.FocusManager?.GetFocusedElement() is Control f && Shown(f)) Save("focus", f);
            }
        }
        finally
        {
            if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "states", "states.tsv"), tsv.ToString());
            await vm.DisposeAsync();
            w.Close();
        }
        Assert.True(moved.Count == 0, string.Join("\n", moved));
    }
}
