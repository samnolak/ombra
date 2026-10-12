using OmpGui.App.Controls;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views;

/// <summary>
/// How tall a conversation row will be before it is laid out (<see cref="TranscriptPanel.EstimateHeight"/>): its text
/// and its parts in the sizes of the row templates (MainWindow.axaml, Styles.Session.axaml). The panel places the rows
/// it has not measured by these, so the scroll bar has the conversation's length from the start, and an estimate is the
/// row's own: measuring other rows never changes it. Off by a few percent, a row moves the rows below it by that much
/// once it is measured; the rows above stay where they are.
/// </summary>
public static class TranscriptRowHeights
{
    /// <summary>For the panel (MainWindow.axaml): a row's estimated height at the list's width.</summary>
    public static Func<object?, double, double> Estimate { get; } = EstimateRow;

    /// <summary>The rows' column (ListBoxItem MaxWidth in Styles.Session.axaml).</summary>
    private const double ColumnWidth = 768;
    /// <summary>How far a step open under its exploration group is indented (Border.row-step.in-group: 7 + 1 + 15).</summary>
    private const double GroupIndent = 23;
    /// <summary>Average character advances, in ems, of the proportional text and of the code font.</summary>
    private const double CharWidth = 0.57;
    private const double MonoCharWidth = 0.6;
    private const double Message = 16, MessageLine = 24, Ui = 14, UiLine = 22, Code = 13, CodeLine = 19;

    private static double EstimateRow(object? row, double width)
    {
        // A step of a folded exploration group takes no room
        if (row is RowViewModel { IsFolded: true }) return 0;
        var column = Math.Min(width, ColumnWidth) - (row is RowViewModel { InGroup: true } ? GroupIndent : 0);
        return row switch
        {
            AssistantRowViewModel a => Assistant(a, column),
            UserRowViewModel u => User(u, column),
            ToolRowViewModel t => Tool(t, column),
            ExplorationGroupRowViewModel => 32, // one tool line
            // 28 high, 4 above, 8 below; the list of files 2 below, its hairline and 4 inside around 28 px rows
            TurnEndRowViewModel e => 40 + (e.ShowFiles ? 2 + 2 + 8 + e.Files.Count * 28 : 0),
            CommandOutputRowViewModel c => c.IsMarker ? 37 : 8 + 16 + Lines(c.Text, (column - 24) / (Code * MonoCharWidth)) * CodeLine,
            NoticeRowViewModel n => 8 + (n.IsError || n.IsWarning ? 16 : 4) + Lines(n.Text, (column - 48) / (Ui * CharWidth)) * UiLine,
            _ => 60,
        };
    }

    /// <summary>The thinking toggle, the thoughts when open, the reply, an error and the actions line, 6 apart, inside 6 above and below.</summary>
    private static double Assistant(AssistantRowViewModel a, double column)
    {
        if (!a.HasContent) return 0;
        var parts = 0;
        var height = 12.0;
        void Part(double h)
        {
            height += h + (parts > 0 ? 6 : 0);
            parts++;
        }
        if (a.HasThinking) Part(24);
        if (a.IsThinkingOpen) Part(8 + Lines(a.Thinking, (column - 14) / (Ui * CharWidth)) * UiLine);
        if (a.HasText) Part(MarkdownHeight.Estimate(a.Text, column));
        if (a.HasError) Part(16 + Lines(a.ErrorText ?? "", (column - 120) / (Message * CharWidth)) * MessageLine);
        if (a.ShowActions) Part(26);
        return height;
    }

    /// <summary>A bubble (at most 600 wide, 14×9 inside) 18 below the row above: its images, text, comments chip and fold link, 4 apart.</summary>
    private static double User(UserRowViewModel u, double column)
    {
        var text = Math.Min(column, 600) - 28;
        var parts = 0;
        var height = 26.0 + 18;
        void Part(double h)
        {
            height += h + (parts > 0 ? 4 : 0);
            parts++;
        }
        if (u.HasImages) Part(6 + Math.Ceiling(u.Previews.Count / Math.Max(1, Math.Floor((text + 6) / 78))) * 78 - 6);
        if (u.HasOwnText) Part(Lines(u.DisplayText, text / (Message * CharWidth)) * MessageLine);
        if (u.HasComments) Part(28);
        if (u.IsLong) Part(20);
        return height;
    }

    /// <summary>
    /// The call's line (28, 2 above and below); under it, 30 px in: a command's console (2 above, 6 below, 7 inside,
    /// the command's lines, its output 4 below them, "… +N lines"), what anything else gave (its lines, "… +N lines"),
    /// or opened, the change or the output.
    /// </summary>
    private static double Tool(ToolRowViewModel t, double column)
    {
        var height = 32.0;
        var mono = Code * MonoCharWidth;
        if (t.ShowConsole)
        {
            // The console spans the column's 16 px of pull-out less 30 + 8 around it, a 2 px rule and 10 + 12 inside
            var text = column - 46;
            var command = Lines(t.Command, (text - mono - 8) / mono);
            height += 2 + 6 + 14 + (t.IsExpanded ? command : Math.Min(command, ToolRowViewModel.CommandLines)) * CodeLine;
            if (t.ShowConsoleOutput) height += 4 + (t.IsExpanded ? Lines(t.ConsoleOutput, text / mono) : Count(t.ConsoleOutput, '\n')) * CodeLine;
            if (t.ShowConsoleMore) height += 2 + 20;
        }
        if (t.ShowResult) height += Count(t.Result, '\n') * 19 + 2 + (t.ShowMore ? 20 : 0);
        if (t.ShowDiff) height += t.DiffCards.Sum(DiffView.EstimatedHeight);
        if (t.ShowOutput) height += 8 + 16 + Lines(t.ShownOutput, (column - 46) / mono) * CodeLine;
        return height;
    }

    /// <summary>The lines <paramref name="text"/> takes wrapped at <paramref name="perLine"/> characters.</summary>
    private static int Lines(string text, double perLine)
    {
        perLine = Math.Max(1, perLine);
        var lines = 0;
        foreach (var line in text.AsSpan().EnumerateLines()) lines += Math.Max(1, (int)Math.Ceiling(line.Length / perLine));
        return Math.Max(1, lines);
    }

    /// <summary>The lines of <paramref name="text"/>, unwrapped.</summary>
    private static int Count(string text, char c) => text.AsSpan().Count(c) + 1;
}
