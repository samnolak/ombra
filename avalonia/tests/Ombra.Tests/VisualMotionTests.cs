using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.Platform;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class VisualMotionTests
{
    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Point point, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseMove(point, modifiers);
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Settle()
    {
        var until = DateTime.UtcNow.AddMilliseconds(220);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Motion_tokens_drive_hover_chevrons_and_send_and_reduced_motion_removes_scale(bool reduced)
    {
        ReducedMotion.Override = reduced;
        var action = new Button { Classes = { "session-action" }, Content = "Action", Width = 80 };
        var row = new Panel { Classes = { "session-row" }, Background = Brushes.Transparent, Height = 40, Children = { action } };
        var send = new Button { Classes = { "send" }, Content = "↑" };
        var chevron = new Icon { Classes = { "chevron" }, Data = Geometry.Parse("M8,4 L16,12 L8,20") };
        var toggle = new ToggleButton { Classes = { "thinking-toggle" }, Content = chevron };
        var window = new Window { Content = new StackPanel { Children = { row, send, toggle } }, Width = 300, Height = 200 };
        window.Show();
        try
        {
            Assert.Equal(reduced ? TimeSpan.Zero : Motion.Fast, window.Resources["GuiMotionFast"]);
            Assert.Equal(reduced ? TimeSpan.Zero : Motion.Base, window.Resources["GuiMotionBase"]);
            var fade = Assert.IsType<DoubleTransition>(Assert.Single(action.Transitions!));
            Assert.Equal(reduced ? TimeSpan.Zero : Motion.Fast, fade.Duration);
            Assert.IsType<CubicEaseOut>(fade.Easing);
            Assert.Contains(chevron.Transitions!, transition => transition is TransformOperationsTransition);
            window.MouseMove(new Point(290, 180));
            await Settle();
            Assert.Equal(0, action.Opacity);
            window.MouseMove(Center(row, window));
            await Settle();
            Assert.Equal(1, action.Opacity);
            toggle.IsChecked = true;
            await Settle();
            Assert.Equal(0, chevron.RenderTransform!.Value.M11, 5);
            var point = Center(send, window);
            window.MouseDown(point, MouseButton.Left);
            await Settle();
            Assert.Equal(reduced ? 1 : 0.94, send.RenderTransform!.Value.M11, 5);
            MicroUxTabTests.Shot(window, $"motion-send-reduced-{reduced}");
            window.MouseUp(point, MouseButton.Left);
            ReducedMotion.Override = !reduced;
            Motion.Refresh(window);
            Assert.Equal(!reduced ? TimeSpan.Zero : Motion.Fast, fade.Duration);
        }
        finally { window.Close(); ReducedMotion.Override = null; }
    }

    [AvaloniaFact]
    public void Diff_header_background_collapses_but_path_right_click_and_disabled_command_do_not()
    {
        var collapsed = 0;
        var opened = 0;
        var allowed = true;
        var view = new DiffView
        {
            Change = new FileChange("src/example.cs", FileChangeKind.Modified, "+1|hello"),
            CollapseCommand = new RelayCommand(() => collapsed++, () => allowed),
            OpenFileCommand = new RelayCommand<FileLink>(_ => opened++),
        };
        var window = new Window { Content = view, Width = 650, Height = 200 };
        window.Show();
        try
        {
            var bar = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("diff-head"));
            var path = view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("diff-path-link"));
            Click(window, Center(path, window));
            Assert.Equal(1, opened);
            Assert.Equal(0, collapsed);
            var background = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), window)!.Value;
            Click(window, background);
            Assert.Equal(1, collapsed);
            window.MouseDown(background, MouseButton.Right);
            window.MouseUp(background, MouseButton.Right);
            Assert.Equal(1, collapsed);
            allowed = false;
            Click(window, background);
            Assert.Equal(1, collapsed);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Markdown_hover_highlights_the_whole_body_row_without_intercepting_text()
    {
        var markdown = new MarkdownView { Markdown = "| Name | Value |\n| --- | --- |\n| Alpha | First |\n| Beta | Second |" };
        var window = new Window { Content = markdown, Width = 500, Height = 300 };
        window.Show();
        try
        {
            var grid = markdown.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("md-table"));
            var highlights = grid.Children.OfType<Border>().Where(b => b.Classes.Contains("md-row-hover")).ToArray();
            Assert.Equal(2, highlights.Length);
            var cell = grid.Children.OfType<Border>().Single(b => b.Classes.Contains("md-cell") && Grid.GetRow(b) == 1 && Grid.GetColumn(b) == 1);
            window.MouseMove(Center(cell, window));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("active", highlights[0].Classes);
            Assert.DoesNotContain("active", highlights[1].Classes);
            Assert.NotNull(highlights[0].Background);
            Assert.Equal(2, Grid.GetColumnSpan(highlights[0]));
            Assert.False(highlights[0].IsHitTestVisible);
            Assert.IsAssignableFrom<SelectableTextBlock>(cell.Child);
            MicroUxTabTests.Shot(window, "markdown-row-hover");
            window.MouseMove(new Point(490, 290));
            Assert.DoesNotContain("active", highlights[0].Classes);
        }
        finally { window.Close(); }
    }

    private static AssistantItem Reply(long key, string text) => new(key, text, "thinking", false, "stop", null);
    private static ToolItem Tool(long key) => new(key, $"c{key}", "read", "{}", ToolStatus.Succeeded, "one\ntwo\nthree\nfour", "src/file.cs");
    private static SessionSnapshot Snapshot(long version, params TranscriptItem[] items) =>
        new(version, SessionPhase.Ready, items, null, 2, null, 0, 0, null, null, null, []);

    [AvaloniaFact]
    public async Task Alt_chevron_updates_every_card_in_its_turn_even_when_the_cards_are_not_realized()
    {
        ReducedMotion.Override = true;
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var first = new UserItem(1, "earlier", true);
        var second = new UserItem(10, "this turn", true);
        var items = new List<TranscriptItem> { first, Tool(2), Tool(3), Reply(4, "earlier answer"), second };
        items.AddRange(Enumerable.Range(11, 45).Select(i => Tool(i)));
        items.Add(Reply(60, "answer"));
        vm.Apply(Snapshot(1, items.ToArray()));
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var folds = vm.Rows.OfType<TurnFoldRowViewModel>().ToArray();
            var transcript = window.FindControl<ListBox>("Transcript")!;
            transcript.ScrollIntoView(folds[1]);
            window.UpdateLayout();
            var button = transcript.GetVisualDescendants().OfType<ToggleButton>()
                .Single(b => b.Classes.Contains("turn-fold-head") && ReferenceEquals(b.DataContext, folds[1]));
            var icon = button.GetVisualDescendants().OfType<Icon>().Single();
            Click(window, Center(icon, window), RawInputModifiers.Alt);
            Assert.True(folds[1].IsExpanded);
            Assert.False(folds[0].IsExpanded);
            Assert.All(vm.Rows.OfType<ToolRowViewModel>().Where(t => t.Key >= 11), t => Assert.True(t.IsExpanded));
            Assert.All(vm.Rows.OfType<ToolRowViewModel>().Where(t => t.Key < 10), t => Assert.False(t.IsExpanded));
            Assert.True(vm.Rows.OfType<AssistantRowViewModel>().Last().IsThinkingOpen);
            Assert.All(vm.Rows.OfType<StepGroupRowViewModel>().Where(g => g.Key >= 11), g => Assert.True(g.IsExpanded));
            MicroUxTabTests.Shot(window, "alt-expand-turn");
            window.MouseWheel(Center(transcript, window), new Vector(0, 10));
            transcript.ScrollIntoView(folds[1]);
            window.UpdateLayout();
            button = transcript.GetVisualDescendants().OfType<ToggleButton>()
                .Single(b => b.Classes.Contains("turn-fold-head") && ReferenceEquals(b.DataContext, folds[1]));
            icon = button.GetVisualDescendants().OfType<Icon>().Single();
            Click(window, Center(icon, window), RawInputModifiers.Alt);
            Assert.False(folds[1].IsExpanded);
            Assert.All(vm.Rows.OfType<ToolRowViewModel>(), t => Assert.False(t.IsExpanded));
        }
        finally { window.Close(); await vm.DisposeAsync(); ReducedMotion.Override = null; }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jump_marks_streamed_content_not_history_or_layout_and_clears_when_following(bool reduced)
    {
        ReducedMotion.Override = reduced;
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var user = new UserItem(10, "question", true);
        var reply = Reply(11, "first words");
        vm.Apply(Snapshot(1, user, reply));
        var button = new Button { Name = "JumpToLatest", Content = new Icon(), IsVisible = false };
        var viewer = new ScrollViewer { Height = 100, Content = new Border { Height = 1000 } };
        var window = new Window { Content = new StackPanel { Children = { viewer, button } }, Width = 400, Height = 300 };
        window.Show();
        var scroll = new ChatScrollController(viewer, viewer, null);
        using var indicator = new JumpToLatestController(button, scroll, vm);
        try
        {
            scroll.ReadHistory();
            Assert.True(button.IsVisible);
            Assert.False(indicator.HasNewActivity);
            var earlier = new UserItem(20, "older page", true);
            vm.Apply(Snapshot(2, earlier, user, reply));
            Assert.False(indicator.HasNewActivity);
            viewer.Height = 120;
            window.UpdateLayout();
            Assert.False(indicator.HasNewActivity);
            vm.Apply(Snapshot(3, earlier, user, reply with { Text = "first words and more" }));
            Assert.True(indicator.HasNewActivity);
            Assert.Equal(Loc.T("New activity below — jump to latest"), ToolTip.GetTip(button));
            await Settle();
            MicroUxTabTests.Shot(window, $"jump-unread-reduced-{reduced}");
            scroll.FollowLatest();
            Assert.False(indicator.HasNewActivity);
            Assert.False(button.IsHitTestVisible);
            Assert.False(button.Focusable);
            if (reduced) Assert.False(button.IsVisible);
            await Settle();
            Assert.False(button.IsVisible);
            Assert.Equal(Loc.T("Jump to latest"), ToolTip.GetTip(button));
            scroll.ReadHistory();
            vm.Apply(Snapshot(4, earlier, user, reply with { Text = "even more" }));
            Assert.True(indicator.HasNewActivity);
            vm.Rows.Clear();
            Assert.False(indicator.HasNewActivity);
        }
        finally { window.Close(); await vm.DisposeAsync(); ReducedMotion.Override = null; }
    }
}
