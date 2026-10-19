using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// Every main screen, both themes, wide and narrow, measured by <see cref="LayoutAudit"/>: icons in the middle of their
/// text, texts on one baseline, button content centred, no overlaps, no clipped text, cards clear of the message box.
/// With OMBRA_REVIEW_DIR set it also writes the findings and screenshots there.
/// </summary>
public sealed class LayoutAuditTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMBRA_REVIEW_DIR");

    private static async Task Settle(int ms = 250)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static async Task Until(Func<bool> c, int seconds = 20)
    {
        var d = DateTime.UtcNow.AddSeconds(seconds);
        while (!c() && DateTime.UtcNow < d) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Audit
    {
        public readonly StringBuilder Report = new();
        public int Count;

        public void Take(Window w, string screen)
        {
            using (var frame = w.CaptureRenderedFrame())
            {
                if (Dir is not null)
                {
                    Directory.CreateDirectory(Path.Combine(Dir, "audit"));
                    using var f = File.Create(Path.Combine(Dir, "audit", screen + ".png"));
                    frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
            var found = LayoutAudit.Run(w);
            Count += found.Count;
            Report.Append(LayoutAudit.Report(screen, found));
        }
    }

    private static async Task<(MainWindow w, MainViewModel vm)> Open(string scenario, double width, double height, string? sessionDir = null)
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, sessionDir ?? TestProcesses.TempDir("audit")), new LaunchRequest(TestProcesses.TempDir("audit-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        // On macOS the app's top row is the title bar: audited (and shot) with the traffic lights' insets, as it shows
        w.TrafficLightsShown = OperatingSystem.IsMacOS();
        vm.OnWindowOpened();
        await Until(() => vm.Phase is SessionPhase.Ready or SessionPhase.Faulted);
        await Settle();
        return (w, vm);
    }

    private static async Task Send(MainViewModel vm, string text, Func<bool> until)
    {
        vm.ComposerText = text;
        vm.SendCommand.Execute(null);
        await Until(until);
        await Settle(400);
    }

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.MainScreens), MemberType = typeof(DesignMatrix))]
    public async Task Main_screens_are_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}";
        try
        {
            var (w, vm) = await Open("markdown", width, height);
            a.Take(w, $"{tag}-new-session");
            foreach (var name in new[] { "AttachButton", "ProjectButton", "ApprovalButton" })
            {
                var b = w.FindControl<Button>(name)!;
                if (!b.IsEffectivelyVisible) continue;
                b.Flyout!.ShowAt(b);
                await Settle();
                a.Take(w, $"{tag}-menu-{name}");
                b.Flyout.Hide();
                await Settle(100);
            }
            await Send(vm, "explain the change", () => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any());
            a.Take(w, $"{tag}-transcript");
            foreach (var t in vm.Rows.OfType<ToolRowViewModel>()) t.IsExpanded = true;
            foreach (var r in vm.Rows.OfType<AssistantRowViewModel>()) r.IsThinkingOpen = true;
            await Settle();
            a.Take(w, $"{tag}-transcript-open");
            await vm.OpenSettingsCommand.ExecuteAsync(null);
            // Every settings page, at its top and (when it scrolls) at its end
            foreach (var c in vm.SettingsCategories)
            {
                vm.SettingsCategory = c.Key;
                await Settle(400);
                a.Take(w, $"{tag}-settings-{c.Key}");
                var page = w.FindControl<ScrollViewer>("SettingsScroll")!;
                if (page.Extent.Height > page.Viewport.Height + 1)
                {
                    page.ScrollToEnd();
                    await Settle();
                    a.Take(w, $"{tag}-settings-{c.Key}-end");
                    page.ScrollToHome();
                }
            }
            // The rail's pages: in the sidebar's and the conversation's place (no chats sidebar beside them)
            vm.CloseSettingsCommand.Execute(null);
            foreach (var (open, name) in new (Action, string)[] { (() => vm.OpenTeamPageCommand.Execute(null), "team"), (() => vm.OpenBrowserPageCommand.Execute(null), "browser") })
            {
                open();
                await Settle(400);
                Assert.False(vm.ShowSidebar, name);
                Assert.False(w.FindControl<Border>("Sidebar")!.IsVisible, name);
                a.Take(w, $"{tag}-{name}-page");
            }
            vm.ShowChatsCommand.Execute(null);
            await vm.DisposeAsync();
            w.Close();

            (w, vm) = await Open("approval", width, height);
            await Send(vm, "run it", () => vm.HasDialog);
            a.Take(w, $"{tag}-approval");
            await vm.DisposeAsync();
            w.Close();

            (w, vm) = await Open("ask", width, height);
            await Send(vm, "ask me", () => vm.HasDialog);
            a.Take(w, $"{tag}-question");
            vm.CurrentDialog!.Options[0].ChooseCommand.Execute(null);
            await Until(() => vm.CurrentDialog is { IsInput: true });
            await Settle();
            a.Take(w, $"{tag}-question-input");
            await vm.DisposeAsync();
            w.Close();

            (w, vm) = await Open("todo", width, height);
            await Send(vm, "plan it", () => vm.Phase == SessionPhase.Ready && vm.HasTodos);
            a.Take(w, $"{tag}-todos");
            vm.IsTodoExpanded = true;
            await Settle();
            a.Take(w, $"{tag}-todos-open");
            vm.IsPreviewOpen = true;
            vm.Preview.OfferUrlsFrom("Local: http://localhost:5173/");
            await Settle();
            a.Take(w, $"{tag}-preview");
            vm.IsPreviewOpen = false;
            vm.ToggleTerminalCommand.Execute(null);
            await Settle(600);
            a.Take(w, $"{tag}-terminal");
            vm.ToggleTerminalCommand.Execute(null);
            await vm.DisposeAsync();
            w.Close();

            (w, vm) = await Open("no-models", width, height);
            a.Take(w, $"{tag}-setup");
            await vm.DisposeAsync();
            w.Close();
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }

    private sealed class SteadyMic : Ombra.App.Services.Speech.IMicrophone
    {
        public void Start() { }
        public TimeSpan Recorded => TimeSpan.FromSeconds(3);
        public float[] Stop() => new float[16_000];
        public double? ReadLevel() => 0.4;
        public void Dispose() { }
    }

    private static string Png(string dir, string name)
    {
        using var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(40, 30), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        var path = Path.Combine(dir, name);
        bmp.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.MainScreens), MemberType = typeof(DesignMatrix))]
    public async Task More_screens_are_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}-more";
        try
        {
            var sessions = TestProcesses.TempDir("audit-more");
            var s = new SessionController(TestProcesses.FakeFactory("queue-stream", sessions), new LaunchRequest(TestProcesses.TempDir("audit-p1")));
            var vm = new MainViewModel(s, new AppArgs()) { SpeechModelInstalled = _ => true, MicrophoneFactory = () => new SteadyMic(),
                TranscriberFactory = _ => throw new InvalidOperationException("not transcribing here") };
            var w = new MainWindow { DataContext = vm, Width = width, Height = height };
            w.Show();
            w.TrafficLightsShown = OperatingSystem.IsMacOS();
            vm.OnWindowOpened();
            await Until(() => vm.Phase == SessionPhase.Ready);
            if (width < 760) { vm.ToggleSidebarCommand.Execute(null); await Settle(); a.Take(w, $"{tag}-sidebar-over"); vm.ToggleSidebarCommand.Execute(null); }
            await vm.AddFilesAsync([Png(sessions, "shot.png"), Png(sessions, "second.png")]);
            await Settle();
            a.Take(w, $"{tag}-attachments");
            vm.ComposerText = "long task";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Running);
            vm.ComposerText = "queue me";
            vm.SendCommand.Execute(null);
            await Settle(300);
            a.Take(w, $"{tag}-running-queued");
            await vm.ToggleDictationCommand.ExecuteAsync(null);
            await Settle(300);
            a.Take(w, $"{tag}-dictation");
            vm.CancelDictationCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Ready, 30);
            await Settle(300);
            vm.StartRenameCommand.Execute(null);
            await Settle();
            a.Take(w, $"{tag}-rename");
            vm.IsRenaming = false;
            vm.OpenModelMenuCommand.Execute(null);
            await Settle(400);
            a.Take(w, $"{tag}-model-menu");
            vm.IsModelMenuOpen = false;
            var thinking = w.FindControl<Button>("ThinkingBox")!;
            if (thinking.IsEffectivelyVisible)
            {
                thinking.Flyout!.ShowAt(thinking);
                await Settle();
                a.Take(w, $"{tag}-thinking-menu");
                thinking.Flyout.Hide();
            }
            vm.Preview.OnNavigationCompleted(new Uri("http://localhost:5173/"), true, false, false);
            vm.Preview.OnPageMessage(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["type"] = "annotation", ["token"] = vm.Preview.AnnotationToken, ["id"] = "a1", ["comment"] = "Make the button bigger", ["url"] = "http://localhost:5173/",
                ["mark"] = new Dictionary<string, object> { ["kind"] = "point", ["x"] = 120, ["y"] = 160, ["viewportWidth"] = 1280, ["viewportHeight"] = 800 },
                ["element"] = new Dictionary<string, object> { ["selector"] = "#buy", ["tag"] = "button", ["html"] = "<button id=\"buy\">", ["text"] = "Buy" },
            }));
            await Settle();
            a.Take(w, $"{tag}-page-comments");
            vm.SendCommand.Execute(null);
            await Until(() => vm.Rows.OfType<UserRowViewModel>().Any(r => r.HasComments));
            await Settle(400);
            a.Take(w, $"{tag}-sent-comments");
            await Until(() => vm.Phase == SessionPhase.Ready, 30);
            System.Diagnostics.Process.GetProcessById(s.ProcessId!.Value).Kill();
            await Until(() => vm.CanRecover);
            await Settle(400);
            a.Take(w, $"{tag}-crash-card");
            await vm.DisposeAsync();
            w.Close();

            var noModel = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs()) { SpeechModelInstalled = _ => false };
            w = new MainWindow { DataContext = noModel, Width = width, Height = height };
            w.Show();
            w.TrafficLightsShown = OperatingSystem.IsMacOS();
            noModel.OnWindowOpened();
            await Until(() => noModel.Phase == SessionPhase.Ready);
            await noModel.ToggleDictationCommand.ExecuteAsync(null);
            await Settle();
            a.Take(w, $"{tag}-dictation-offer");
            await noModel.DisposeAsync();
            w.Close();
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }

    /// <summary>The audit itself: a zero means something only if it flags a broken row, a left-stuck label, an overlap.</summary>
    [AvaloniaFact]
    public async Task The_audit_catches_what_it_should()
    {
        var glyph = (Avalonia.Media.Geometry)Avalonia.Application.Current!.FindResource("IconCheck")!;
        var low = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children =
        {
            new Ombra.App.Controls.Icon { Data = glyph, Size = 16, Margin = new Thickness(0, 10, 0, 0) },
            new TextBlock { Text = "Label", FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top },
        } };
        var stuck = new Button { MinWidth = 200, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, Content = "Allow" };
        var over = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = -20, Children = { new Button { Content = "One" }, new Button { Content = "Two" } } };
        // The old checkbox: a 14 px tick, drawn low in its square, in a 16 px box with a 1.5 px border
        var oldTick = Avalonia.Media.Geometry.Parse("M5.5 12.5 L10 17 L18.5 8");
        var box = new Border { Width = 16, Height = 16, BorderThickness = new Thickness(1.5), BorderBrush = Avalonia.Media.Brushes.Black,
            Background = Avalonia.Media.Brushes.Black, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Child = new Panel { Children = { new Ombra.App.Controls.Icon { Data = oldTick, Size = 14, StrokeThickness = 3.75,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } } };
        // The old message box: "+" 22 px in from the left edge, Send 10 px from the right
        var card = new Border { Width = 400, Height = 60, CornerRadius = new CornerRadius(24), Padding = new Thickness(12, 0, 10, 0),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children =
            {
                new Button { Classes = { "round" }, Width = 32, Height = 32, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                new Button { Classes = { "send" }, Width = 32, Height = 32, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, [Grid.ColumnProperty] = 2 },
            } } };
        // The old status badge: its text stretched to the badge, drawn at its top, above the 12 px text beside it
        var badge = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children =
        {
            new Border { MinHeight = 20, Padding = new Thickness(8, 1), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = Avalonia.Media.Brushes.Gray, Child = new TextBlock { Text = "Running", FontSize = 12 } },
            new TextBlock { Text = "task", FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        } };
        // The old Nightly consent strip: an accent pill beside a default rounded rectangle
        var pair = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children =
        {
            new Button { Classes = { "accent" }, Content = "Use Nightly" },
            new Button { Content = "Keep the built-in preview" },
        } };
        // The old Team invite: a 36 px picker beside the 32 px "Create link" pill
        var picker = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children =
        {
            new ComboBox { Name = "TallPicker", MinHeight = 36 },
            new Button { Classes = { "accent" }, Content = "Create link" },
        } };
        // The old Team members: one row ends with Remove, the next 8 px further in
        Grid Row(double right) => new Grid { Classes = { "plug-row" }, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children =
            { new Button { Content = "More", Margin = new Thickness(0, 0, right, 0), [Grid.ColumnProperty] = 1 } } };
        var list = new Border { Classes = { "plug-list" }, Child = new StackPanel { Children = { Row(0), Row(8) } } };
        var w = new Window { Width = 600, Height = 520, Content = new StackPanel { Spacing = 12, Margin = new Thickness(20), Children = { low, stuck, over, box, card, badge, pair, picker, list } } };
        w.Show();
        await Settle();
        var found = LayoutAudit.Run(w);
        Assert.Contains(found, f => f.Kind == "icon-text");
        Assert.Contains(found, f => f.Kind == "button-content" && f.Detail.Contains("off centre"));
        Assert.Contains(found, f => f.Kind == "overlap");
        Assert.Contains(found, f => f.Kind == "frame-centre");
        Assert.Contains(found, f => f.Kind == "frame-overflow");
        Assert.Contains(found, f => f.Kind == "badge-baseline");
        Assert.Contains(found, f => f.Kind == "mirror");
        Assert.Contains(found, f => f.Kind == "button-pair");
        Assert.Contains(found, f => f.Kind == "button-pair" && f.Where.Contains("TallPicker"));
        Assert.Contains(found, f => f.Kind == "row-edge");
        w.Close();
    }

    /// <summary>
    /// Colours come from the tokens (Tokens.axaml, the Gui* brushes) so both themes follow: a view that writes #66000000 or
    /// White draws the same in light and dark. The pairing QR code keeps its white quiet zone on purpose.
    /// </summary>
    [Fact]
    public void Views_take_colours_from_the_tokens()
    {
        var src = Path.GetDirectoryName(RuntimeInstallerTests.RepoFile(Path.Combine("avalonia", "src", "Ombra.App", "Ombra.App.csproj")))!;
        var literal = new System.Text.RegularExpressions.Regex(
            @"\b(Background|Foreground|BorderBrush|Fill|Stroke|Color)=""(#[0-9A-Fa-f]{3,8}|White|Black|Red|Green|Blue|Gray|Grey|Yellow|Orange)""");
        var found = Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(d => d is "bin" or "obj"))
            .Where(f => !Path.GetFileName(f).StartsWith("Styles", StringComparison.Ordinal) && Path.GetFileName(f) != "Tokens.axaml")
            .SelectMany(f => { var lines = File.ReadAllLines(f); return lines.Select((line, i) => (f, line, i, next: i + 1 < lines.Length ? lines[i + 1] : "")); })
            // An OpacityMask's stops are alpha, not a colour of the theme
            .Where(x => literal.IsMatch(x.line) && !x.line.Contains("GradientStop", StringComparison.Ordinal) && !x.next.Contains("PairingQr", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(found.Count == 0, "Use a Gui* token (DynamicResource) instead of a literal colour:\n" + string.Join("\n", found));
    }
}
