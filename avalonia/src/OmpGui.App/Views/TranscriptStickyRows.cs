using OmpGui.App.Controls;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views;

/// <summary>
/// Which conversation rows stay on screen as the head of the rows under them (<see cref="TranscriptPanel.StickyBlock"/>):
/// an open exploration group's line, over its steps. The steps follow their line (ExplorationGroups), so the block is
/// found from any of its rows by walking to the line and to the last step.
/// </summary>
public static class TranscriptStickyRows
{
    /// <summary>For the panel (MainWindow.axaml): the group line's index and its last step's, for a row of an open group.</summary>
    public static Func<IReadOnlyList<object?>, int, (int Header, int Last)?> Block { get; } = BlockOf;

    private static (int Header, int Last)? BlockOf(IReadOnlyList<object?> rows, int index)
    {
        if (index < 0 || index >= rows.Count) return null;
        var group = rows[index] switch
        {
            ExplorationGroupRowViewModel g => g,
            RowViewModel r => r.Group,
            _ => null,
        };
        if (group is not { IsExpanded: true }) return null;
        var header = index;
        while (!ReferenceEquals(rows[header], group))
        {
            if (rows[header] is not RowViewModel step || !ReferenceEquals(step.Group, group) || header == 0) return null;
            header--;
        }
        var last = header;
        while (last + 1 < rows.Count && rows[last + 1] is RowViewModel next && ReferenceEquals(next.Group, group)) last++;
        return last > header ? (header, last) : null;
    }
}
