using Ombra.App.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

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
    /// <summary>Average character advances, in ems, of the proportional text and of the code font.</summary>
    private const double CharWidth = 0.57;
    private const double MonoCharWidth = 0.6;
    private const double Message = 14, MessageLine = 22, Caption = 12, Code = 13, CodeLine = 19;
    /// <summary>An open Shell card's or output's bar (Button.card-header).</summary>
    private const double CardHeader = 28;
    /// <summary>A step's line (ToggleButton.tool-row), the thinking line and a message's line of actions.</summary>
    private const double Line = 28;
    /// <summary>A step's or a group's line with the 2 px it keeps above and below (the steps' rhythm).</summary>
    private const double StepLine = 2 + Line + 2;

    private static double EstimateRow(object? row, double width)
    {
        // A step of a folded group, or of a folded turn, takes no room
        if (row is RowViewModel { IsFolded: true }) return 0;
        var column = Math.Min(width, ColumnWidth);
        return row switch
        {
            AssistantRowViewModel a => Assistant(a, column),
            UserRowViewModel u => User(u, column),
            ToolRowViewModel t => Tool(t, column),
            StepGroupRowViewModel => StepLine,
            // 8 above (under the message's 28 px line of actions), the 22 px line, its hairline 10 below it, 16 under
            TurnFoldRowViewModel => 8 + 22 + 11 + 16,
            // The change card: 12 above and 6 below, its hairline, the 62 px header, 32 px rows and "Show N more files"
            TurnEndRowViewModel e => e.HasFiles ? 18 + 2 + 62 + (e.HasFileRows ? e.ShownFiles.Count * 32 + (e.HasMoreFiles ? 32 : 0) : 0) : 0,
            CommandOutputRowViewModel c => c.IsMarker ? 28 + 18 : 8 + 16 + Lines(c.Text, (column - 24) / (Code * MonoCharWidth)) * CodeLine,
            NoticeRowViewModel n => 28 + Lines(n.Text, 440 / (Caption * CharWidth)) * 18,
            // A message between agents: 8 above and 8 below, its bar, then two preview lines or the message, 8 and 10 inside
            AgentMessageRowViewModel m => 16 + CardHeader + 18 + (m.IsExpanded ? MarkdownHeight.Estimate(m.Text, column - 24) : 2 * 20),
            _ => 60,
        };
    }

    /// <summary>The thinking line, the thoughts when open, the reply, an error and the actions line, 8 apart, inside 10 above and below.</summary>
    private static double Assistant(AssistantRowViewModel a, double column)
    {
        if (!a.HasContent) return 0;
        var parts = 0;
        var height = 20.0;
        void Part(double h)
        {
            height += h + (parts > 0 ? 8 : 0);
            parts++;
        }
        // The thoughts hang 6 under their line inside one part
        if (a.HasThinking) Part(Line + (a.IsThinkingOpen ? 6 + 6 + Lines(a.Thinking, (column - 24) / (14 * CharWidth)) * 22 : 0));
        if (a.HasText) Part(MarkdownHeight.Estimate(a.Text, column));
        if (a.HasError) Part(16 + Lines(a.ErrorText ?? "", (column - 120) / (Message * CharWidth)) * MessageLine);
        if (a.ShowActions) Part(Line);
        return height;
    }

    /// <summary>
    /// 26 above and 2 below: the day and time over it (22 under them), the images (78 px squares, 10 apart) and the
    /// comments pill over the bubble (at most 515 wide, 15×10 inside), 8 apart, then the 28 px line of its actions.
    /// </summary>
    private static double User(UserRowViewModel u, double column)
    {
        var text = Math.Min(column - 48, 515) - 30;
        var parts = 0;
        var height = 26.0 + 2;
        void Part(double h)
        {
            height += h + (parts > 0 ? 8 : 0);
            parts++;
        }
        if (u.HasDateLabel) height += 4 + 16 + 22;
        if (u.HasImages) Part(Math.Ceiling(u.Previews.Count / Math.Max(1, Math.Floor((column - 48 + 10) / 88))) * 88 - 10);
        if (u.HasComments) Part(28);
        if (u.HasBubble) Part(20 + (u.HasOwnText ? Lines(u.DisplayText, text / (Message * CharWidth)) * MessageLine : 0) + (u.IsLong ? 4 + 20 : 0));
        Part(Line - 2);
        return height;
    }

    /// <summary>
    /// The step's 28 px line with 2 above and below; under it, 30 px in: a command's Shell card (2 above, 8 below, its
    /// bar when open, 8 + 10 inside, the command's lines, its output 4 below them, its exit code), what anything else
    /// gave (its lines, "… +N lines"), or opened, the change or the output under its 28 px bar.
    /// </summary>
    private static double Tool(ToolRowViewModel t, double column)
    {
        var height = StepLine;
        var mono = Code * MonoCharWidth;
        if (t.ShowConsole)
        {
            // The card spans the column's 16 px of pull-out less 30 + 8 around it, a 1 px hairline and 12 + 12 inside
            var text = column - 46;
            var command = Lines(t.Command, (text - mono - 8) / mono);
            height += 2 + 8 + 2 + 18 + (t.IsExpanded ? CardHeader + command * CodeLine : Math.Min(command, ToolRowViewModel.CommandLines) * CodeLine);
            if (t.ShowConsoleOutput) height += 4 + (t.IsExpanded ? Lines(t.ConsoleOutput, text / mono) : Count(t.ConsoleOutput, '\n')) * CodeLine;
            if (t.HasExitText) height += 6 + 16;
        }
        if (t.ShowResult) height += Count(t.Result, '\n') * 19 + 2 + (t.ShowMore ? 20 : 0);
        if (t.ShowDiff) height += t.DiffCards.Sum(DiffView.EstimatedHeight);
        if (t.ShowOutput) height += 8 + 16 + CardHeader + Lines(t.ShownOutput, (column - 46) / mono) * CodeLine;
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
