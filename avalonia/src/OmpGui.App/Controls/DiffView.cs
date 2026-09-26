using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace OmpGui.App.Controls;

/// <summary>
/// A file change as Claude Code shows it: a card with the file's path and "+added −removed", then the changed lines
/// with their line numbers, a full-width green / red fill for added / removed lines, the code coloured by its language
/// (SyntaxHighlighter) and "⋯" between separate parts. Long changes show their first lines and "Show N more lines".
/// Reads omp's own format (<c>+ 12|code</c>, <c>-  7|code</c>, <c>  10|code</c>) and unified diffs (<c>@@ -1,2 +1,2 @@</c>).
/// Brushes are resolved when built; a theme change rebuilds it. With <see cref="OpenFileCommand"/> set, the path in the
/// header opens the file (at its first change) and a double click on a line opens it there; the command gets a
/// <see cref="FileLink"/>.
/// </summary>
public sealed partial class DiffView : ContentControl
{
    public static readonly StyledProperty<string?> DiffProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(Diff));
    public static readonly StyledProperty<string?> FilePathProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(FilePath));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> OpenFileCommandProperty =
        AvaloniaProperty.Register<DiffView, System.Windows.Input.ICommand?>(nameof(OpenFileCommand));

    /// <summary>Rows shown before "Show N more lines".</summary>
    public const int FoldAt = 16;
    private const int MaxRows = 1500;
    private const double RowHeight = 20;

    public string? Diff { get => GetValue(DiffProperty); set => SetValue(DiffProperty, value); }
    public string? FilePath { get => GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    /// <summary>Opens the changed file (the Files pane); its parameter is a <see cref="FileLink"/>.</summary>
    public System.Windows.Input.ICommand? OpenFileCommand { get => GetValue(OpenFileCommandProperty); set => SetValue(OpenFileCommandProperty, value); }

    private bool _unfolded;
    private Avalonia.Styling.ThemeVariant? _builtTheme;

    public enum Kind { Context, Added, Removed, Gap }

    public sealed record Line(Kind Kind, int? Number, string Text);

    [GeneratedRegex(@"^([+\- ])(\s*\d+)\|(.*)$")]
    private static partial Regex OmpLine();

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex Hunk();

    /// <summary>omp's code-frame lines or a unified diff, as numbered rows ("⋯" rows between separate parts).</summary>
    public static List<Line> Parse(string diff)
    {
        var rows = new List<Line>();
        var text = diff.Replace("\r\n", "\n").TrimEnd('\n');
        if (text.Length == 0) return rows;
        var lines = text.Split('\n');
        var omp = lines.Count(l => OmpLine().IsMatch(l)) * 2 >= lines.Length;
        int oldNo = 0, newNo = 0;
        int? lastNumber = null;
        foreach (var line in lines)
        {
            if (omp)
            {
                var m = OmpLine().Match(line);
                if (!m.Success)
                {
                    if (rows.Count > 0 && rows[^1].Kind != Kind.Gap) rows.Add(new(Kind.Gap, null, ""));
                    continue;
                }
                var number = int.Parse(m.Groups[2].Value.Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var kind = m.Groups[1].Value switch { "+" => Kind.Added, "-" => Kind.Removed, _ => Kind.Context };
                // A jump in the numbering is a separate part of the file
                if (lastNumber is { } prev && kind == Kind.Context && number > prev + 1 && rows.Count > 0 && rows[^1].Kind != Kind.Gap)
                    rows.Add(new(Kind.Gap, null, ""));
                rows.Add(new(kind, number, m.Groups[3].Value));
                lastNumber = number;
                continue;
            }
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)) continue;
            var h = Hunk().Match(line);
            if (h.Success)
            {
                if (rows.Count > 0) rows.Add(new(Kind.Gap, null, ""));
                oldNo = int.Parse(h.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                newNo = int.Parse(h.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }
            if (line.StartsWith('+')) rows.Add(new(Kind.Added, newNo++, line[1..]));
            else if (line.StartsWith('-')) rows.Add(new(Kind.Removed, oldNo++, line[1..]));
            else if (line.StartsWith('\\')) continue; // "\ No newline at end of file"
            else
            {
                rows.Add(new(Kind.Context, newNo > 0 ? newNo : null, line.Length > 0 ? line[1..] : ""));
                newNo++;
                oldNo++;
            }
        }
        return rows;
    }

    /// <summary>The first added (else removed) line's number: where a change starts in the file.</summary>
    public static int? FirstChangedLine(string? diff) => string.IsNullOrEmpty(diff) ? null : FirstChange(Parse(diff));

    private static int? FirstChange(List<Line> rows) =>
        rows.FirstOrDefault(r => r.Kind == Kind.Added && r.Number is not null)?.Number
        ?? rows.FirstOrDefault(r => r.Kind == Kind.Removed && r.Number is not null)?.Number;

    public static (int Added, int Removed) Stats(string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return (0, 0);
        var rows = Parse(diff);
        return (rows.Count(r => r.Kind == Kind.Added), rows.Count(r => r.Kind == Kind.Removed));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DiffProperty || change.Property == FilePathProperty
            || (change.Property == ThemeVariantScope.ActualThemeVariantProperty && Content is not null && !Equals(_builtTheme, Application.Current?.ActualThemeVariant)))
            Rebuild();
        // The command arrives with the row's other bindings: the path turns into a link without building it all again
        else if (change.Property == OpenFileCommandProperty && _link is not null) _link.IsHitTestVisible = OpenFileCommand is not null;
    }

    private Button? _link;

    private void Rebuild()
    {
        // The command is read when the link is used, so it may come before or after the diff
        Action<int?>? open = FilePath is { Length: > 0 } path
            ? line =>
            {
                var link = new FileLink(path, line);
                if (OpenFileCommand is { } command && command.CanExecute(link)) command.Execute(link);
            }
            : null;
        Content = Diff is { Length: > 0 } d ? Build(d, FilePath, _unfolded, () => { _unfolded = !_unfolded; Rebuild(); }, open, out _link) : null;
        if (_link is not null) _link.IsHitTestVisible = OpenFileCommand is not null;
        _builtTheme = Application.Current?.ActualThemeVariant;
    }

    /// <param name="openFile">Opens the file at a line (the first change for the header's path); the path becomes a link.</param>
    public static Control Build(string diff, string? path, bool unfolded = true, Action? toggleFold = null, Action<int?>? openFile = null) =>
        Build(diff, path, unfolded, toggleFold, openFile, out _);

    private static Control Build(string diff, string? path, bool unfolded, Action? toggleFold, Action<int?>? openFile, out Button? pathLink)
    {
        pathLink = null;
        var rows = Parse(diff);
        var added = rows.Count(r => r.Kind == Kind.Added);
        var removed = rows.Count(r => r.Kind == Kind.Removed);
        var lang = SyntaxHighlighter.ForName(path);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Classes = { "diff-header" } };
        var pathText = new TextBlock { Text = path ?? "", Classes = { "mono", "diff-path" }, TextTrimming = TextTrimming.PrefixCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (openFile is not null && !string.IsNullOrEmpty(path))
        {
            var first = FirstChange(rows);
            var link = new Button { Classes = { "diff-path-link" }, Content = pathText, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(link, "Open in the Files pane");
            Avalonia.Automation.AutomationProperties.SetName(link, "Open " + path);
            link.Click += (_, _) => openFile(first);
            header.Children.Add(link);
            pathLink = link;
        }
        else header.Children.Add(pathText);
        var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (added > 0) stats.Children.Add(new TextBlock { Text = $"+{added}", Classes = { "mono", "diff-added" } });
        if (removed > 0) stats.Children.Add(new TextBlock { Text = $"−{removed}", Classes = { "mono", "diff-removed" } });
        Grid.SetColumn(stats, 1);
        header.Children.Add(stats);

        var shown = unfolded ? Math.Min(rows.Count, MaxRows) : Math.Min(rows.Count, FoldAt);
        var numberWidth = Math.Max(3, rows.Where(r => r.Number is not null).Select(r => r.Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture).Length).DefaultIfEmpty(3).Max()) * 7.6 + 16;
        var body = new StackPanel { Classes = { "diff-body" } };
        var inBlock = false;
        foreach (var r in rows.Take(shown))
        {
            var row = Row(r, lang, numberWidth, ref inBlock);
            // A double click on a line opens the file there (a removed line: its number in the old file)
            if (openFile is not null && r.Number is { } n) row.DoubleTapped += (_, _) => openFile(n);
            body.Children.Add(row);
        }
        var scroller = new SideScroller
        {
            Content = body,
        };
        // Fills run the full width even when every line is shorter than the card
        scroller.SizeChanged += (_, e) => body.MinWidth = e.NewSize.Width;

        var card = new StackPanel { Children = { header, scroller } };
        if (rows.Count > FoldAt && toggleFold is not null)
        {
            var more = new Button
            {
                Classes = { "link", "diff-more" },
                Content = unfolded ? "Show less" : $"Show {rows.Count - FoldAt} more lines",
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            more.Click += (_, _) => toggleFold();
            card.Children.Add(more);
        }
        else if (rows.Count > MaxRows)
            card.Children.Add(new TextBlock { Text = $"… {rows.Count - MaxRows} more lines", Classes = { "tertiary" }, Margin = new Thickness(12, 4) });
        return new Border { Classes = { "diff-card" }, Child = card };
    }

    private static Control Row(Line r, SyntaxHighlighter.Language? lang, double numberWidth, ref bool inBlock)
    {
        if (r.Kind == Kind.Gap)
            return new TextBlock { Text = "⋯", Classes = { "diff-gap" }, Height = RowHeight, Padding = new Thickness(numberWidth - 12, 0, 0, 0) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{numberWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)},18,*"), Height = RowHeight };
        grid.Classes.Add("diff-row");
        if (r.Kind == Kind.Added) grid.Classes.Add("added");
        if (r.Kind == Kind.Removed) grid.Classes.Add("removed");
        grid.Children.Add(new TextBlock
        {
            Text = r.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Classes = { "mono", "diff-number" },
            TextAlignment = TextAlignment.Right,
            Padding = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var sign = new TextBlock
        {
            Text = r.Kind switch { Kind.Added => "+", Kind.Removed => "−", _ => "" },
            Classes = { "mono", "diff-sign" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sign, 1);
        grid.Children.Add(sign);
        var code = new SelectableTextBlock { Classes = { "mono", "diff-code" }, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 12, 0) };
        SyntaxHighlighter.AddRuns(code.Inlines!, r.Text.Replace("\t", "    "), lang, ref inBlock);
        Grid.SetColumn(code, 2);
        grid.Children.Add(code);
        return grid;
    }
}
