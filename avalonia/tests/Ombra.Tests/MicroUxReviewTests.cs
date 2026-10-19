using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class MicroUxReviewTests
{
    private static async Task Settle(int ms = 120)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("OMBRA_REVIEW_DIR") is not { } dir) return;
        Directory.CreateDirectory(dir);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(dir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Mouse_chooses_the_clicked_slash_suggestion_not_the_keyboard_selection()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1180);
        try
        {
            var composer = w.FindControl<TextBox>("Composer")!;
            composer.Focus();
            w.KeyTextInput("/");
            await FileTestRepo.Until(() => vm.CommandSuggestions.Count > 1, "slash choices");
            await Settle();
            var menu = w.FindControl<Border>("CommandMenu")!;
            var button = menu.GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.DataContext is CommandSuggestionViewModel { IsSelected: false });
            var choice = (CommandSuggestionViewModel)button.DataContext!;
            var p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), w)!.Value;
            w.MouseMove(p);
            Shot(w, "slash-mouse-before");
            w.MouseDown(p, MouseButton.Left);
            w.MouseUp(p, MouseButton.Left);
            await Settle();
            Assert.Equal(choice.Insert, vm.ComposerText);
            Assert.True(composer.IsFocused);
            Shot(w, "slash-mouse-after");
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Shift_click_extends_selection_in_a_rendered_reply()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1180);
        try
        {
            await PanesUiTests.SendAsync(vm, "hello", () => vm.Rows.OfType<AssistantRowViewModel>().Any());
            await Settle();
            var text = w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<SelectableTextBlock>()
                .First(t => t.IsEffectivelyVisible && t.Bounds.Width > 100 &&
                    t.GetVisualAncestors().OfType<Ombra.App.Controls.MarkdownView>().Any());
            var left = text.TranslatePoint(new Point(2, Math.Min(10, text.Bounds.Height / 2)), w)!.Value;
            var right = left + new Vector(Math.Min(65, text.Bounds.Width - 4), 0);
            w.MouseDown(left, MouseButton.Left);
            w.MouseUp(left, MouseButton.Left);
            var anchor = text.SelectionStart;
            w.MouseDown(right, MouseButton.Left, RawInputModifiers.Shift);
            w.MouseUp(right, MouseButton.Left, RawInputModifiers.Shift);
            await Settle();
            Assert.Equal(anchor, text.SelectionStart);
            Assert.True(text.SelectionEnd > text.SelectionStart);
            Assert.NotEmpty(text.SelectedText ?? "");
            Shot(w, "shift-selection");
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Files_refresh_keeps_loaded_tree_visible_without_loading_flash()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1400);
        try
        {
            File.WriteAllText(Path.Combine(vm.ProjectFolder!, "visible.txt"), "hello");
            vm.ShowPaneCommand.Execute(SidePane.Files);
            await FileTestRepo.Until(() => vm.Files.Root is not null && !vm.Files.IsLoading, "loaded files");
            await Settle();
            var flashes = 0;
            vm.Files.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Files.IsLoading) && vm.Files.IsLoading) flashes++; };
            Shot(w, "files-before-refresh");
            for (var i = 0; i < 3; i++)
            {
                var refresh = vm.Files.RefreshAsync();
                while (!refresh.IsCompleted) { Assert.False(vm.Files.IsLoading); await Settle(15); }
                await refresh;
                Shot(w, "files-refresh-" + i);
            }
            Assert.Equal(0, flashes);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Real_ls_color_output_is_inert_and_visible_in_the_tool_card()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS ls verification; other OS has its own native gate");
        var root = TestProcesses.TempDir("micro-ansi");
        Directory.CreateDirectory(Path.Combine(root, "colored-directory"));
        var start = new ProcessStartInfo("/bin/ls") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--color=always");
        start.ArgumentList.Add(root);
        start.Environment["CLICOLOR_FORCE"] = "1";
        start.Environment["TERM"] = "xterm-256color";
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        Assert.Contains('\u001b', output);
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        try
        {
            vm.Apply(new SessionSnapshot(1, SessionPhase.Ready,
                [new ToolItem(1, "color-ls", "bash", "{\"command\":\"ls --color=always\"}", ToolStatus.Succeeded, output)],
                null, 2, null, 0, 0, null, null, null, []));
            var row = Assert.Single(vm.Rows.OfType<ToolRowViewModel>());
            row.IsExpanded = true;
            w.Show();
            await Settle();
            Assert.Contains("colored-directory", row.ConsoleOutput);
            Assert.DoesNotContain('\u001b', row.ConsoleOutput);
            Assert.DoesNotContain('␛', row.ConsoleOutput);
            Assert.Contains(w.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == row.ConsoleOutput);
            Shot(w, "ansi-ls-tool-card");
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }
}
