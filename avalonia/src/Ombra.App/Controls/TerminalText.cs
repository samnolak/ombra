using System.Text;

namespace Ombra.App.Controls;

/// <summary>The text a terminal shows: its scrollback and screen, as lines (wrapped rows joined back into one).</summary>
public static class TerminalText
{
    /// <summary>
    /// The last <paramref name="maxLines"/> lines with something on them, oldest first, trailing blank lines left out.
    /// Reads the active buffer: a full-screen program (omp's own UI, an editor) has no scrollback, only its screen.
    /// Call on the UI thread: the terminal's output is written there.
    /// </summary>
    public static IReadOnlyList<string> Tail(XTerm.Terminal terminal, int maxLines)
    {
        var lines = terminal.Buffer.Lines;
        var cols = terminal.Cols;
        var result = new List<string>();
        var row = new List<string>(); // one logical line's rows, last first
        for (var i = lines.Length - 1; i >= 0 && result.Count < maxLines; i--)
        {
            if (lines[i] is not { } line) continue;
            // A row continued by the next one keeps its trailing spaces: they are part of the text
            var continued = i + 1 < lines.Length && lines[i + 1] is { IsWrapped: true };
            row.Add(line.TranslateToString(trimRight: !continued, 0, Math.Min(cols, line.Length)));
            if (line.IsWrapped && i > 0) continue;
            var text = new StringBuilder();
            for (var r = row.Count - 1; r >= 0; r--) text.Append(row[r]);
            row.Clear();
            var logical = text.ToString().TrimEnd();
            if (result.Count == 0 && logical.Length == 0) continue; // the screen's empty rows under the last output
            result.Add(logical);
        }
        result.Reverse();
        return result;
    }
}
