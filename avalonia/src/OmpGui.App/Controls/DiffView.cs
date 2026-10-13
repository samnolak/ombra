using OmpGui.App.Localization;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using OmpGui.ClientCore;

namespace OmpGui.App.Controls;

/// <summary>
/// One file a tool changed, as GitHub, Codex and Claude Code show it: a card whose header names the file (its folder
/// quiet, its name strong), says what happened to it ("New file", "Deleted", "Renamed" / "Moved" with the old path → the
/// new) and counts "+added −removed"; then the changed lines. A changed line has a solid bar at its left edge and a
/// tinted fill (green added, red removed), its sign, and the words that changed inside it on a stronger tint
/// (<see cref="DiffWords"/>); the code is coloured by its language (SyntaxHighlighter); "⋯" separates parts of the file.
/// <para>
/// Line numbers sit in two gutters, the old file's and the new one's (GitHub's layout): a removed line has only its old
/// number, an added line only its new one, an unchanged line both. One gutter had shown a removed and an added line as
/// "2" and "2", and context lines after an insertion carry numbers that no longer match the file. A new file shows only
/// the new gutter (and a lighter fill: every line is added, the bar says it), a deleted one only the old gutter and is
/// folded to its header, since its lines are gone. Long changes show their first lines and "Show N more lines". With
/// <see cref="CollapseCommand"/> set (the tool row it opened under), the header ends in a chevron that folds it away; the
/// header stays at the top of the conversation while the card is on screen (<see cref="StickyHeader"/>).
/// </para>
/// Reads omp's own format (<c>+12|code</c>, <c>-7|code</c>, <c> 10|code</c>: removed and unchanged lines numbered in
/// the old file, added lines in the new one) and unified diffs (<c>@@ -1,2 +1,2 @@</c>). Brushes are resolved when built;
/// a theme change rebuilds it. With <see cref="OpenFileCommand"/> set, the path in the header opens the file (at its
/// first change) and a double click on a line opens it there; the command gets a <see cref="FileLink"/>.
/// </summary>
public sealed partial class DiffView : ContentControl
{
    public static readonly StyledProperty<FileChange?> ChangeProperty = AvaloniaProperty.Register<DiffView, FileChange?>(nameof(Change));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> OpenFileCommandProperty =
        AvaloniaProperty.Register<DiffView, System.Windows.Input.ICommand?>(nameof(OpenFileCommand));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CollapseCommandProperty =
        AvaloniaProperty.Register<DiffView, System.Windows.Input.ICommand?>(nameof(CollapseCommand));

    /// <summary>Rows shown before "Show N more lines".</summary>
    public const int FoldAt = 16;
    private const int MaxRows = 1500;
    private const double RowHeight = 20;
    private const double HeaderHeight = 32;
    private const double BarWidth = 3;
    private const double SignWidth = 18;
    /// <summary>A gutter digit's width in the 12 px mono font, and the room around a gutter's number.</summary>
    private const double DigitWidth = 7.6, GutterPadding = 14;

    /// <summary>The file changed: its path, what happened to it, its diff.</summary>
    public FileChange? Change { get => GetValue(ChangeProperty); set => SetValue(ChangeProperty, value); }
    /// <summary>Opens the changed file (the Files pane); its parameter is a <see cref="FileLink"/>.</summary>
    public System.Windows.Input.ICommand? OpenFileCommand { get => GetValue(OpenFileCommandProperty); set => SetValue(OpenFileCommandProperty, value); }
    /// <summary>Folds the card away (its tool row's line); a chevron at the header's end while set.</summary>
    public System.Windows.Input.ICommand? CollapseCommand { get => GetValue(CollapseCommandProperty); set => SetValue(CollapseCommandProperty, value); }

    private bool _unfolded;
    private Avalonia.Styling.ThemeVariant? _builtTheme;

    public enum Kind { Context, Added, Removed, Gap }

    /// <param name="Old">The line's number in the file before the change (null for an added line).</param>
    /// <param name="New">Its number after the change (null for a removed line).</param>
    public sealed record Line(Kind Kind, int? Old, int? New, string Text)
    {
        /// <summary>Where the line is in the file now; a removed line: where it was.</summary>
        public int? Number => New ?? Old;
    }

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
        // omp numbers unchanged lines in the old file: their new number is the old one moved by every line added or
        // removed above them (omp's frame lists all of a file's changes, so the count is exact)
        var shift = 0;
        int? lastOld = null, lastAdded = null;
        foreach (var line in lines)
        {
            if (omp)
            {
                var m = OmpLine().Match(line);
                if (!m.Success)
                {
                    if (rows.Count > 0 && rows[^1].Kind != Kind.Gap) rows.Add(new(Kind.Gap, null, null, ""));
                    continue;
                }
                var number = int.Parse(m.Groups[2].Value.Trim(), CultureInfo.InvariantCulture);
                var kind = m.Groups[1].Value switch { "+" => Kind.Added, "-" => Kind.Removed, _ => Kind.Context };
                // A jump in the numbering is a separate part of the file; an unchanged line right after an insertion
                // numbered in the new file is no jump
                if (kind == Kind.Context && lastOld is { } prev && number > prev + 1 && (lastAdded is not { } added || number > added + 1)
                    && rows.Count > 0 && rows[^1].Kind != Kind.Gap)
                    rows.Add(new(Kind.Gap, null, null, ""));
                switch (kind)
                {
                    case Kind.Added:
                        rows.Add(new(kind, null, number, m.Groups[3].Value));
                        shift++;
                        lastAdded = number;
                        break;
                    case Kind.Removed:
                        rows.Add(new(kind, number, null, m.Groups[3].Value));
                        shift--;
                        lastOld = number;
                        break;
                    default:
                        rows.Add(new(kind, number, number + shift, m.Groups[3].Value));
                        lastOld = number;
                        break;
                }
                continue;
            }
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)) continue;
            var h = Hunk().Match(line);
            if (h.Success)
            {
                if (rows.Count > 0) rows.Add(new(Kind.Gap, null, null, ""));
                oldNo = int.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture);
                newNo = int.Parse(h.Groups[2].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if (line.StartsWith('+')) rows.Add(new(Kind.Added, null, newNo++, line[1..]));
            else if (line.StartsWith('-')) rows.Add(new(Kind.Removed, oldNo++, null, line[1..]));
            else if (line.StartsWith('\\')) continue; // "\ No newline at end of file"
            else rows.Add(new(Kind.Context, oldNo > 0 ? oldNo++ : null, newNo > 0 ? newNo++ : null, line.Length > 0 ? line[1..] : ""));
        }
        return rows;
    }

    /// <summary>The first added (else removed) line's number: where a change starts in the file.</summary>
    public static int? FirstChangedLine(string? diff) => string.IsNullOrEmpty(diff) ? null : FirstChange(Parse(diff));

    private static int? FirstChange(List<Line> rows) =>
        rows.FirstOrDefault(r => r.Kind == Kind.Added && r.New is not null)?.New
        ?? rows.FirstOrDefault(r => r.Kind == Kind.Removed && r.Old is not null)?.Old;

    public static (int Added, int Removed) Stats(string? diff)
    {
        if (string.IsNullOrEmpty(diff)) return (0, 0);
        var rows = Parse(diff);
        return (rows.Count(r => r.Kind == Kind.Added), rows.Count(r => r.Kind == Kind.Removed));
    }

    /// <summary>
    /// The card's height as first shown (folded), with the margin the conversation's style gives it (8 + 2), for the
    /// transcript's row estimates (Views/TranscriptRowHeights).
    /// </summary>
    public static double EstimatedHeight(FileChange change)
    {
        var rows = string.IsNullOrEmpty(change.Diff) ? 0 : Math.Min(Parse(change.Diff).Count, MaxRows);
        double height = 10 + 2 + HeaderHeight;
        if (change.Kind == FileChangeKind.Deleted && rows > 0) return height + MoreHeight;
        if (rows > 0) height += Math.Min(rows, FoldAt) * RowHeight + BodyBottom;
        if (rows > FoldAt) height += MoreHeight;
        return height;
    }

    /// <summary>"Show N more lines" (24) with its bottom margin (8), and the room under the last row.</summary>
    private const double MoreHeight = 32, BodyBottom = 6;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChangeProperty
            || (change.Property == ThemeVariantScope.ActualThemeVariantProperty && Content is not null && !Equals(_builtTheme, Application.Current?.ActualThemeVariant)))
        {
            if (change.Property == ChangeProperty) _unfolded = false;
            Rebuild();
        }
        // The command arrives with the row's other bindings: the path turns into a link without building it all again
        else if (change.Property == OpenFileCommandProperty && _link is not null) _link.IsHitTestVisible = OpenFileCommand is not null;
        else if (change.Property == CollapseCommandProperty && _collapse is not null) _collapse.IsVisible = CollapseCommand is not null;
    }

    private Button? _link;
    private Button? _collapse;

    private void Rebuild()
    {
        // The command is read when the link is used, so it may come before or after the change
        Action<int?>? open = Change is { Kind: not FileChangeKind.Deleted, Path.Length: > 0 } c
            ? line =>
            {
                var link = new FileLink(c.Path, line);
                if (OpenFileCommand is { } command && command.CanExecute(link)) command.Execute(link);
            }
            : null;
        Content = Change is { } change
            ? Build(change, _unfolded, () => { _unfolded = !_unfolded; Rebuild(); }, open, () => CollapseCommand?.Execute(null), out _link, out _collapse)
            : null;
        if (_link is not null) _link.IsHitTestVisible = OpenFileCommand is not null;
        if (_collapse is not null) _collapse.IsVisible = CollapseCommand is not null;
        _builtTheme = Application.Current?.ActualThemeVariant;
    }

    /// <param name="openFile">Opens the file at a line (the first change for the header's path); the path becomes a link.</param>
    public static Control Build(FileChange change, bool unfolded = true, Action? toggleFold = null, Action<int?>? openFile = null) =>
        Build(change, unfolded, toggleFold, openFile, null, out _, out _);

    /// <param name="collapse">Folds the card away: a chevron at the header's end.</param>
    private static Control Build(FileChange change, bool unfolded, Action? toggleFold, Action<int?>? openFile, Action? collapse, out Button? pathLink, out Button? collapseButton)
    {
        var rows = string.IsNullOrEmpty(change.Diff) ? new List<Line>() : Parse(change.Diff);
        var deleted = change.Kind == FileChangeKind.Deleted;
        var card = new StackPanel { Children = { Header(change, rows, openFile, collapse, out pathLink, out collapseButton) } };
        if (rows.Count > 0)
        {
            // A deleted file's lines are gone: its card is the header until asked
            var shown = deleted ? (unfolded ? Math.Min(rows.Count, MaxRows) : 0)
                : unfolded ? Math.Min(rows.Count, MaxRows) : Math.Min(rows.Count, FoldAt);
            if (shown > 0) card.Children.Add(Body(rows, shown, change, openFile));
            var foldable = deleted || rows.Count > FoldAt;
            if (foldable && toggleFold is not null)
            {
                var more = new Button
                {
                    Classes = { "link", "diff-more" },
                    Content = deleted ? (unfolded ? Loc.T("Hide deleted lines") : Loc.Plural(rows.Count, "Show {0} deleted line", "Show {0} deleted lines"))
                        : unfolded ? Loc.T("Show less") : Loc.Plural(rows.Count - FoldAt, "Show {0} more line", "Show {0} more lines"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                more.Click += (_, _) => toggleFold();
                card.Children.Add(more);
            }
            else if (rows.Count > MaxRows && shown == MaxRows)
                card.Children.Add(new TextBlock { Text = $"… {rows.Count - MaxRows} more lines", Classes = { "tertiary" }, Margin = new Thickness(12, 4) });
        }
        var border = new Border { Classes = { "diff-card" }, Child = card };
        if (change.Kind == FileChangeKind.Created) border.Classes.Add("created");
        if (deleted) border.Classes.Add("deleted");
        return border;
    }

    /// <summary>
    /// The file icon, the path (folder quiet, name strong; a move: old → new), what happened, +/−, and with
    /// <paramref name="collapse"/> a chevron that folds the card away; on the card's own surface, as it stays at the top of
    /// the view over the card's lines (<see cref="StickyHeader"/>).
    /// </summary>
    private static Border Header(FileChange change, List<Line> rows, Action<int?>? openFile, Action? collapse, out Button? pathLink, out Button? collapseButton)
    {
        pathLink = null;
        collapseButton = null;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Classes = { "diff-header" } };
        if (Application.Current?.TryGetResource("IconFile", null, out var icon) == true && icon is Geometry g)
            header.Children.Add(new Icon { Data = g, Size = 14, Classes = { "diff-file-icon" }, VerticalAlignment = VerticalAlignment.Center });

        var pathText = new TextBlock { Classes = { "mono", "diff-path" }, TextTrimming = TextTrimming.PrefixCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        pathText.Inlines = [];
        var quiet = Res("GuiTextTertiary");
        if (change.MovedFrom is { Length: > 0 } from)
        {
            pathText.Inlines.Add(new Run(from) { Foreground = quiet });
            pathText.Inlines.Add(new Run("  →  ") { Foreground = quiet });
        }
        var (dir, name) = Split(change.Path);
        if (dir.Length > 0) pathText.Inlines.Add(new Run(dir) { Foreground = quiet });
        pathText.Inlines.Add(new Run(name) { FontWeight = FontWeight.SemiBold });
        Control path = pathText;
        if (openFile is not null && change.Path.Length > 0)
        {
            var first = FirstChange(rows);
            var link = new Button { Classes = { "diff-path-link" }, Content = pathText, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(link, Loc.T("Open in the Files pane"));
            Avalonia.Automation.AutomationProperties.SetName(link, Loc.F("Open {0}", change.Path));
            link.Click += (_, _) => openFile(first);
            path = link;
            pathLink = link;
        }
        else ToolTip.SetTip(pathText, change.MovedFrom is { } old ? $"{old} → {change.Path}" : change.Path);
        Grid.SetColumn(path, 1);
        header.Children.Add(path);

        if (Badge(change) is { } badge)
        {
            Grid.SetColumn(badge, 2);
            header.Children.Add(badge);
        }

        var added = rows.Count(r => r.Kind == Kind.Added);
        var removed = rows.Count(r => r.Kind == Kind.Removed);
        var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        if (added > 0) stats.Children.Add(new TextBlock { Text = $"+{added}", Classes = { "mono", "diff-added" } });
        if (removed > 0) stats.Children.Add(new TextBlock { Text = $"−{removed}", Classes = { "mono", "diff-removed" } });
        Grid.SetColumn(stats, 3);
        header.Children.Add(stats);
        if (collapse is not null && Application.Current?.TryGetResource("IconChevronDown", null, out var chevron) == true && chevron is Geometry down)
        {
            var fold = new Button { Classes = { "diff-collapse" }, Content = new Icon { Data = down, Size = 14 }, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(fold, Loc.T("Collapse"));
            Avalonia.Automation.AutomationProperties.SetName(fold, Loc.T("Collapse"));
            fold.Click += (_, _) => collapse();
            Grid.SetColumn(fold, 4);
            header.Children.Add(fold);
            collapseButton = fold;
        }
        // The bar draws the card's top (hairline and rounded corners) itself, so held at the top of the view it is still
        // the top of a rounded card; what it moves in shows the window around those corners (diff-head.stuck)
        var bar = new Border { Classes = { "diff-head" }, Child = new Border { Classes = { "diff-head-bar" }, Child = header } };
        StickyHeader.SetIsSticky(bar, true);
        return bar;
    }

    /// <summary>"New file", "Deleted", "Renamed" (same folder) or "Moved"; nothing for a changed file.</summary>
    private static Border? Badge(FileChange change)
    {
        var (text, kind) = change.Kind switch
        {
            FileChangeKind.Created => (Loc.T("New file"), "created"),
            FileChangeKind.Deleted => (Loc.T("Deleted"), "deleted"),
            FileChangeKind.Moved when change.IsRename => (Loc.T("Renamed"), "moved"),
            FileChangeKind.Moved => (Loc.T("Moved"), "moved"),
            _ => ("", ""),
        };
        if (text.Length == 0) return null;
        return new Border
        {
            Classes = { "diff-badge", kind },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text },
        };
    }

    /// <summary>"src/app/" and "main.py".</summary>
    private static (string Dir, string Name) Split(string path)
    {
        var cut = path.LastIndexOfAny(['/', '\\']) + 1;
        return (path[..cut], path[cut..]);
    }

    private static SideScroller Body(List<Line> rows, int shown, FileChange change, Action<int?>? openFile)
    {
        var lang = SyntaxHighlighter.ForName(change.Path);
        var texts = rows.Select(r => r.Text.Replace("\t", "    ")).ToArray();
        var marks = DiffWords.Mark(shown, i => rows[i].Kind switch { Kind.Removed => -1, Kind.Added => 1, _ => 0 }, i => texts[i]);
        var gutters = new Gutters(
            rows.Any(r => r.Old is not null) ? GutterWidth(rows.Max(r => r.Old ?? 0)) : 0,
            rows.Any(r => r.New is not null) ? GutterWidth(rows.Max(r => r.New ?? 0)) : 0,
            Res("GuiDiffAddedWordBg"), Res("GuiDiffRemovedWordBg"));
        var body = new StackPanel { Classes = { "diff-body" } };
        var inBlock = false;
        for (var i = 0; i < shown; i++)
        {
            var r = rows[i];
            var row = Row(r, texts[i], marks[i], lang, gutters, ref inBlock);
            // A double click on a line opens the file there (a removed line: where it was)
            if (openFile is not null && r.Number is { } n) row.DoubleTapped += (_, _) => openFile(n);
            body.Children.Add(row);
        }
        var scroller = new SideScroller { Content = body };
        // Fills run the full width even when every line is shorter than the card
        scroller.SizeChanged += (_, e) => body.MinWidth = e.NewSize.Width;
        return scroller;
    }

    private static double GutterWidth(int largest) =>
        Math.Max(2, largest.ToString(CultureInfo.InvariantCulture).Length) * DigitWidth + GutterPadding;

    private sealed record Gutters(double Old, double New, IBrush? AddedMark, IBrush? RemovedMark);

    private static Control Row(Line r, string text, IReadOnlyList<DiffWords.Span>? marks, SyntaxHighlighter.Language? lang, Gutters gutters, ref bool inBlock)
    {
        var columns = string.Create(CultureInfo.InvariantCulture, $"{BarWidth},{gutters.Old},{gutters.New},{SignWidth},*");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Height = RowHeight, Classes = { "diff-row" } };
        if (r.Kind == Kind.Gap)
        {
            grid.Classes.Add("gap");
            var dots = new TextBlock { Text = "⋯", Classes = { "diff-gap" } };
            Grid.SetColumn(dots, 4);
            grid.Children.Add(dots);
            return grid;
        }
        if (r.Kind == Kind.Added) grid.Classes.Add("added");
        if (r.Kind == Kind.Removed) grid.Classes.Add("removed");
        grid.Children.Add(new Border { Classes = { "diff-bar" } });
        if (gutters.Old > 0) grid.Children.Add(Number(r.Old, 1));
        if (gutters.New > 0) grid.Children.Add(Number(r.New, 2));
        var sign = new TextBlock
        {
            Text = r.Kind switch { Kind.Added => "+", Kind.Removed => "−", _ => "" },
            Classes = { "mono", "diff-sign" },
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sign, 3);
        grid.Children.Add(sign);
        var code = new SelectableTextBlock { Classes = { "mono", "diff-code" }, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 12, 0) };
        DiffWords.AddRuns(code.Inlines!, text, lang, ref inBlock, marks, r.Kind == Kind.Added ? gutters.AddedMark : gutters.RemovedMark);
        Grid.SetColumn(code, 4);
        grid.Children.Add(code);
        return grid;

        static TextBlock Number(int? n, int column)
        {
            var t = new TextBlock
            {
                Text = n?.ToString(CultureInfo.InvariantCulture) ?? "",
                Classes = { "mono", "diff-number" },
                TextAlignment = TextAlignment.Right,
                Padding = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(t, column);
            return t;
        }
    }

    /// <summary>A brush of the current theme (resolved when the card is built; a theme change builds it again).</summary>
    private static IBrush? Res(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true ? v as IBrush : null;
}
