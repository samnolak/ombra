using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Ombra.App.Localization;
using Ombra.App.Controls;
using Xunit;

namespace Ombra.Tests;

/// <summary>Markdown views drawn in states the transcript puts them in (hidden rows, finished streams).</summary>
public sealed class MarkdownViewRenderTests
{
    [AvaloniaFact]
    public void Oversized_document_plain_fallback_bounds_layout_without_changing_markdown()
    {
        var original = "| a | b |\n|---|---|\n" + new string('x', 220000);
        var view = new MarkdownView { Markdown = original };
        var preview = Assert.IsType<SelectableTextBlock>(view.Content);
        Assert.True(preview.Text!.Length < 17000);
        Assert.StartsWith("| a | b |", preview.Text);
        Assert.NotNull(preview.ContextFlyout);
        Assert.Same(original, view.Markdown);
    }

    [AvaloniaFact]
    public void Large_table_preview_is_bounded_and_copy_keeps_the_original()
    {
        var markdown = "| A | B | C | D |\n|---|---|---|---|\n" +
            string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"| row{i} | b | c | d |"));
        var root = MarkdownView.Build(markdown);
        var grid = root.GetLogicalDescendants().OfType<Grid>().Single(g => g.Classes.Contains("md-table"));
        Assert.InRange(grid.RowDefinitions.Count, 2, 100);
        Assert.InRange(grid.Children.Count(c => c.Classes.Contains("md-cell")), 4, 400);
        Assert.InRange(grid.Children.Count(c => c.Classes.Contains("md-row-hover")), 1, 99);
        var copy = Assert.Single(root.GetLogicalDescendants().OfType<Button>());
        string? copied = null;
        void OnCopy(string value) => copied = value;
        MarkdownView.CopyRequested += OnCopy;
        try { copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { MarkdownView.CopyRequested -= OnCopy; }
        Assert.Equal(markdown, copied);
        Assert.True(MarkdownHeight.Estimate(markdown, 600) < 5000);
    }

    [AvaloniaFact]
    public void Wide_tables_and_long_cells_keep_whole_unicode_clusters()
    {
        var cell = new string('a', 1023) + "👩‍💻" + new string('z', 6000);
        var markdown = "| " + string.Join(" | ", Enumerable.Repeat("h", 80)) + " |\n|" +
            string.Join("|", Enumerable.Repeat("---", 80)) + "|\n| " +
            string.Join(" | ", new[] { cell }.Concat(Enumerable.Repeat("value", 79))) + " |";
        var root = MarkdownView.Build(markdown);
        var grid = root.GetLogicalDescendants().OfType<Grid>().Single(g => g.Classes.Contains("md-table"));
        Assert.InRange(grid.ColumnDefinitions.Count, 2, 16);
        var preview = root.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .Single(t => t.Text?.StartsWith(new string('a', 1000), StringComparison.Ordinal) == true).Text!;
        Assert.DoesNotContain("👩", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("\u200d", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("zzz", preview, StringComparison.Ordinal);
        Assert.Contains(new string('a', 1000), preview, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData("", 24, 24)]
    [InlineData("", 28, 28)]
    [InlineData("", 29, 24)]
    [InlineData("python", 28, 28)]
    [InlineData("python", 29, 24)]
    [InlineData("python", 4000, 24)]
    [InlineData("indented", 28, 28)]
    [InlineData("indented", 29, 24)]
    public void Code_fold_boundary_builds_only_visible_lines_and_copies_every_line(string language, int lines, int visible)
    {
        var code = CodeLines(lines);
        var markdown = language == "indented" ? "    " + code.Replace("\n", "\n    ") : $"```{language}\n{code}\n```";
        var root = MarkdownView.Build(markdown);
        root.Measure(new Size(600, double.PositiveInfinity));
        var text = CodeText(root);
        Assert.Equal(CodeLines(visible), SelectedCode(text));
        Assert.Equal(56 + visible * 20 + (lines > 28 ? 36 : 0), MarkdownHeight.Estimate(markdown, 600));
        var fold = FoldButton(root);
        Assert.Equal(lines > 28, fold.IsVisible);
        Assert.Equal(Loc.T("Show more"), fold.Content);
        Assert.Equal("MarkdownCodeFold", Avalonia.Automation.AutomationProperties.GetAutomationId(fold));
        Assert.True(fold.Focusable);
        AssertCodeCopy(root, code);
        if (lines <= 28) return;

        fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(code, SelectedCode(text));
        Assert.Equal(Loc.T("Show less"), fold.Content);
        fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(CodeLines(24), SelectedCode(text));
        AssertCodeCopy(root, code);
    }

    [AvaloniaFact]
    public void Code_height_estimate_folds_only_after_28_lines_including_unclosed_streams()
    {
        var shortCode = "```python\n" + CodeLines(28);
        var longCode = "```python\n" + CodeLines(29);
        Assert.Equal(56 + 28 * 20, MarkdownHeight.Estimate(shortCode, 600));
        Assert.Equal(MarkdownHeight.Estimate(shortCode, 600), MarkdownHeight.Estimate(shortCode + "\n", 600));
        Assert.Equal(MarkdownHeight.Estimate(shortCode, 600), MarkdownHeight.Estimate(shortCode + "\n```", 600));
        Assert.Equal(56 + 24 * 20 + 36, MarkdownHeight.Estimate(longCode, 600));
        Assert.Equal(MarkdownHeight.Estimate(longCode, 600), MarkdownHeight.Estimate("```python\n" + CodeLines(4000) + "\n```", 600));
        Assert.Equal(56 + 29 * 20 + 36, MarkdownHeight.Estimate(longCode, 600, expandedCode: true));
    }

    [AvaloniaTheory]
    [InlineData("quote", false)]
    [InlineData("list", false)]
    [InlineData("quote-list", false)]
    [InlineData("list-quote", false)]
    [InlineData("quote", true)]
    [InlineData("list", true)]
    public void Nested_code_raw_and_parsed_estimates_bound_hidden_tails(string nesting, bool indented)
    {
        var code = CodeLines(4000);
        var block = indented ? "    " + code.Replace("\n", "\n    ") : "```python\n" + code + "\n```";
        var markdown = NestCode(block, nesting);
        var doc = Markdig.Markdown.Parse(markdown);
        const string prefix = "A preceding block.\n\n";
        var source = prefix + markdown;
        var collapsed = MarkdownHeight.Estimate(doc[0], source, prefix.Length, 600, true, _ => false);
        Assert.Equal(56 + 24 * 20 + 36, collapsed);
        Assert.Equal(collapsed, MarkdownHeight.Estimate(markdown, 600));
        var expanded = MarkdownHeight.Estimate(doc[0], source, prefix.Length, 600, true, _ => true);
        Assert.Equal(56 + 4000 * 20 + 36, expanded);
    }

    [AvaloniaFact]
    public void Nested_code_estimates_respect_each_descendants_independent_expansion()
    {
        var block = "```python\n" + CodeLines(100) + "\n```";
        var markdown = NestCode(block + "\n\n" + block, "quote");
        var quote = Assert.IsType<Markdig.Syntax.QuoteBlock>(Markdig.Markdown.Parse(markdown)[0]);
        var collapsed = MarkdownHeight.Estimate(quote, markdown, 0, 600, true, _ => false);
        var firstExpanded = MarkdownHeight.Estimate(quote, markdown, 0, 600, true, code => ReferenceEquals(code, quote[0]));
        Assert.Equal(2 * (56 + 24 * 20 + 36) + 8, collapsed);
        Assert.Equal((100 - 24) * 20, firstExpanded - collapsed);
    }

    [AvaloniaTheory]
    [InlineData("quote")]
    [InlineData("list")]
    public void Nested_multiline_paragraph_estimates_as_one_block_not_one_per_marker(string nesting)
    {
        var paragraph = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"continued line {i}"));
        var markdown = NestCode(paragraph, nesting);
        var container = Markdig.Markdown.Parse(markdown)[0];
        var nested = MarkdownHeight.Estimate(container, markdown, 0, 600, true, _ => false);
        Assert.Equal(MarkdownHeight.Estimate(paragraph, 600 - (nesting == "quote" ? 15 : 28)), nested);
    }

    [AvaloniaTheory]
    [InlineData("quote")]
    [InlineData("list")]
    public void Nested_code_lazy_height_and_expansion_survive_scrolling_and_theme(string nesting)
    {
        var code = CodeLines(100);
        var lead = string.Join("\n\n", Enumerable.Repeat("A preceding paragraph.", 40));
        var markdown = lead + "\n\n" + NestCode("```python\n" + code + "\n```", nesting) + "\n\nAfter the code.";
        var view = new MarkdownView { Markdown = markdown };
        var scroll = new ScrollViewer { Content = view };
        var w = new Window { Content = scroll, Width = 600, Height = 300 };
        var app = Application.Current!;
        var theme = app.RequestedThemeVariant;
        try
        {
            w.Show();
            Frame(w);
            Frame(w);
            var root = Assert.IsType<StackPanel>(view.Content);
            var placeholder = root.Children[40];
            Assert.Empty(placeholder.GetLogicalDescendants().OfType<SelectableTextBlock>());
            Assert.Equal(56 + 24 * 20 + 36, placeholder.DesiredSize.Height);
            scroll.Offset = new Vector(0, placeholder.Bounds.Top);
            Frame(w);
            Frame(w);
            Assert.Equal(CodeLines(24), SelectedCode(CodeText(view)));
            Assert.InRange(root.Children[40].DesiredSize.Height, placeholder.DesiredSize.Height - 100, placeholder.DesiredSize.Height + 100);
            FoldButton(view).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Frame(w);
            Frame(w);
            var expandedHeight = root.Children[40].DesiredSize.Height;
            Assert.Equal(code, SelectedCode(CodeText(view)));
            scroll.Offset = default;
            Frame(w);
            Frame(w);
            app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
            Frame(w);
            Frame(w);
            root = Assert.IsType<StackPanel>(view.Content);
            placeholder = root.Children[40];
            Assert.Empty(placeholder.GetLogicalDescendants().OfType<SelectableTextBlock>());
            Assert.Equal(56 + 100 * 20 + 36, placeholder.DesiredSize.Height);
            Assert.InRange(placeholder.DesiredSize.Height, expandedHeight - 100, expandedHeight + 100);
            scroll.Offset = new Vector(0, root.Children[41].Bounds.Top);
            Frame(w);
            Frame(w);
            Assert.Contains(view.GetLogicalDescendants().OfType<SelectableTextBlock>(), text => SelectedCode(text) == "After the code.");
            scroll.Offset = new Vector(0, root.Children[40].Bounds.Top);
            Frame(w);
            Frame(w);
            Assert.Equal(Loc.T("Show less"), FoldButton(view).Content);
            Assert.Equal(code, SelectedCode(CodeText(view)));
        }
        finally
        {
            w.Close();
            app.RequestedThemeVariant = theme;
        }
    }

    private static string NestCode(string block, string nesting) => nesting switch
    {
        "quote" => "> " + block.Replace("\n", "\n> "),
        "list" => "- " + block.Replace("\n", "\n  "),
        "quote-list" => "> - " + block.Replace("\n", "\n>   "),
        "list-quote" => "- > " + block.Replace("\n", "\n  > "),
        _ => throw new ArgumentOutOfRangeException(nameof(nesting)),
    };

    [AvaloniaFact]
    public void Streaming_code_keeps_full_copy_and_skips_hidden_syntax_until_expanded()
    {
        var code = CodeLines(28);
        var view = new MarkdownView { IsStreaming = true, Markdown = "```python\n" + code };
        var w = new Window { Content = new ScrollViewer { Content = view }, Width = 600, Height = 900 };
        try
        {
            w.Show();
            Frame(w);
            Frame(w);
            var text = CodeText(view);
            Assert.False(FoldButton(view).IsVisible);
            Assert.Equal(code, SelectedCode(text));
            code += "\nprint('line 29 👩‍💻')";
            view.Markdown = "```python\n" + code;
            Frame(w);
            Frame(w);
            Assert.Same(text, CodeText(view));
            Assert.Equal(CodeLines(24), SelectedCode(text));
            var inlines = text.Inlines!.ToArray();
            var layout = text.TextLayout;

            code += "\nprint('hidden appended tail')";
            view.Markdown = "```python\n" + code;
            Frame(w);
            Frame(w);
            Assert.Equal(inlines, text.Inlines!.ToArray());
            Assert.Same(layout, text.TextLayout);
            AssertCodeCopy(view, code);

            var fold = FoldButton(view);
            Assert.True(fold.Focus());
            w.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            w.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.Equal(Loc.T("Show less"), fold.Content);
            Assert.Equal(code, SelectedCode(text));
            code += "\nprint('expanded appended tail')";
            view.Markdown = "```python\n" + code;
            Frame(w);
            Frame(w);
            Assert.Same(fold, FoldButton(view));
            Assert.Equal(code, SelectedCode(text));
            AssertCodeCopy(view, code);

            fold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            code += "\nprint('collapsed again 👩‍💻')";
            view.Markdown = "```python\n" + code + "\n```";
            view.IsStreaming = false;
            Frame(w);
            Frame(w);
            Assert.Equal(CodeLines(24), SelectedCode(text));
            AssertCodeCopy(view, code);
        }
        finally { w.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expanded_code_survives_append_layout_and_theme_but_not_source_replacement(bool quoted)
    {
        var code = CodeLines(30);
        string Markdown(string value, bool close = false)
        {
            var source = "```python\n" + value + (close ? "\n```" : "");
            return quoted ? "> " + source.Replace("\n", "\n> ") : source;
        }
        var view = new MarkdownView { Markdown = Markdown(code) };
        var w = new Window { Content = new ScrollViewer { Content = view }, Width = 600, Height = 900 };
        var app = Application.Current!;
        var theme = app.RequestedThemeVariant;
        try
        {
            w.Show();
            Frame(w);
            Frame(w);
            FoldButton(view).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            code += "\nprint('appended')";
            view.Markdown = Markdown(code);
            Frame(w);
            Frame(w);
            Assert.Equal(Loc.T("Show less"), FoldButton(view).Content);
            Assert.Equal(code, SelectedCode(CodeText(view)));

            view.Markdown = Markdown(code, close: true);
            w.Width = 720;
            Frame(w);
            Frame(w);
            Assert.Equal(code, SelectedCode(CodeText(view)));
            var beforeTheme = CodeText(view);
            app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
            Frame(w);
            Frame(w);
            Assert.NotSame(beforeTheme, CodeText(view));
            Assert.Equal(Loc.T("Show less"), FoldButton(view).Content);
            Assert.Equal(code, SelectedCode(CodeText(view)));
            AssertCodeCopy(view, code);

            // Same position and language, but a different source must not inherit the previous block's expansion.
            code = code.Replace("line", "replacement", StringComparison.Ordinal);
            view.Markdown = Markdown(code, close: true);
            Frame(w);
            Frame(w);
            Assert.Equal(Loc.T("Show more"), FoldButton(view).Content);
            Assert.Equal(string.Join("\n", code.Split('\n').Take(24)), SelectedCode(CodeText(view)));
            AssertCodeCopy(view, code);
        }
        finally
        {
            w.Close();
            app.RequestedThemeVariant = theme;
        }
    }

    [AvaloniaFact]
    public void Appending_an_identical_code_block_does_not_share_its_expansion()
    {
        var code = CodeLines(29);
        var markdown = "```python\n" + code + "\n```";
        var view = new MarkdownView { Markdown = markdown };
        var w = new Window { Content = new ScrollViewer { Content = view }, Width = 600, Height = 900 };
        try
        {
            w.Show();
            Frame(w);
            Frame(w);
            var first = CodeText(view);
            FoldButton(view).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.Markdown += "\n\n" + markdown;
            Frame(w);
            Frame(w);
            var texts = view.GetLogicalDescendants().OfType<SelectableTextBlock>().Where(t => t.Classes.Contains("md-code")).ToArray();
            Assert.Equal(2, texts.Length);
            Assert.Same(first, texts[0]);
            Assert.Equal(code, SelectedCode(texts[0]));
            Assert.Equal(CodeLines(24), SelectedCode(texts[1]));
        }
        finally { w.Close(); }
    }

    private static string CodeLines(int count) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"print('line {i}')"));

    private static SelectableTextBlock CodeText(Control root) =>
        root.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(t => t.Classes.Contains("md-code"));

    private static Button FoldButton(Control root) =>
        root.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("md-code-fold"));

    private static string SelectedCode(SelectableTextBlock text)
    {
        text.SelectAll();
        return text.SelectedText.ReplaceLineEndings("\n");
    }

    private static void AssertCodeCopy(Control root, string expected)
    {
        var copy = root.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("md-copy") && !b.Classes.Contains("md-wrap"));
        string? copied = null;
        void OnCopy(string value) => copied = value;
        MarkdownView.CopyRequested += OnCopy;
        try { copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { MarkdownView.CopyRequested -= OnCopy; }
        Assert.Equal(expected, copied);
    }

    private static void Frame(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using (w.CaptureRenderedFrame()) { }
    }

    /// <summary>A streamed reply with links that ends as its row is hidden (its chat left) is drawn without Avalonia
    /// throwing "Visual was invalidated during the render pass" (the links were templated in the render pass).</summary>
    [AvaloniaFact]
    public void A_reply_with_links_finished_while_hidden_draws_without_throwing()
    {
        var view = new MarkdownView { IsStreaming = true, Markdown = "See [a](https://a.example/) and" };
        var host = new Border { Child = view };
        var w = new Window { Content = new Panel { Children = { host } }, Width = 600, Height = 400 };
        w.Show();
        Frame(w);
        Frame(w);
        var link = view.GetVisualDescendants().OfType<HyperlinkButton>().Single();
        Assert.Equal("https://a.example/", ToolTip.GetTip(link));
        view.Markdown += " [b](https://b.example/) too";
        view.IsStreaming = false;
        host.IsVisible = false;
        Frame(w);
        Frame(w);
        w.Close();
    }
}
