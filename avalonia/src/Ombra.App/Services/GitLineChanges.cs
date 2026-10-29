namespace Ombra.App.Services;

/// <summary>How a line differs from the file at HEAD (the editor's change gutter).</summary>
public enum LineChangeKind { Added, Modified, Deleted }

/// <summary>
/// One gutter mark: <see cref="Line"/> is 1-based in the current text. A deletion marks the line after which lines were
/// removed (0: before the first line).
/// </summary>
public sealed record LineChange(int Line, LineChangeKind Kind);

/// <summary>
/// The editor's git change gutter: the buffer (saved or not) against the file at HEAD. The HEAD text is read once per
/// open, save or tab activation (<see cref="ReadBaseAsync"/>, off the UI thread); every edit is then compared in-process
/// (<see cref="Compute"/>), so the marks follow unsaved typing.
/// </summary>
public static class GitLineChanges
{
    /// <summary>Above this many cells of the line table, the changed middle is marked modified as a whole.</summary>
    private const long MaxTable = 4_000_000;

    /// <summary>
    /// The file's text at HEAD: "" for a file git does not track yet or that HEAD lacks (every line is added); null outside
    /// a repository, for an ignored file, or when git is missing (no gutter).
    /// </summary>
    public static async Task<string?> ReadBaseAsync(string fullPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is null) return null;
        var name = Path.GetFileName(fullPath);
        var tracked = await Git.RunAsync(dir, ["ls-files", "--error-unmatch", "--", name], ct, TimeSpan.FromSeconds(5));
        if (tracked is null) return null;
        if (tracked.ExitCode != 0)
        {
            // Untracked inside a repository and not ignored: all new. Outside one, git exits 128.
            var ignored = await Git.RunAsync(dir, ["check-ignore", "-q", "--", name], ct, TimeSpan.FromSeconds(5));
            return ignored is { ExitCode: 1 } ? "" : null;
        }
        var head = await Git.RunAsync(dir, ["show", "HEAD:./" + name], ct, TimeSpan.FromSeconds(10), maxOutput: 8 * 1024 * 1024);
        if (head is null || head.TooLong) return null;
        return head.ExitCode == 0 ? head.Output : ""; // staged but not committed yet: all new
    }

    /// <summary>The gutter marks of <paramref name="current"/> against <paramref name="baseText"/>, line by line.</summary>
    public static IReadOnlyList<LineChange> Compute(string baseText, string current)
    {
        var a = Lines(baseText);
        var b = Lines(current);
        var marks = new List<LineChange>();
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        int n = endA - start, m = endB - start;
        if (n == 0 && m == 0) return marks;
        if ((long)(n + 1) * (m + 1) > MaxTable)
        {
            Run(marks, start, n, m);
            return marks;
        }
        // Longest common subsequence of the changed middle, then the runs between kept lines
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = a[start + i] == b[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        int x = 0, y = 0, removed = 0, added = 0, runAt = start;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[start + x] == b[start + y])
            {
                Run(marks, runAt, removed, added);
                removed = added = 0;
                x++; y++;
                runAt = start + y;
            }
            else if (y < m && (x == n || table[x, y + 1] >= table[x + 1, y])) { added++; y++; }
            else { removed++; x++; }
        }
        Run(marks, runAt, removed, added);
        return marks;
    }

    /// <summary>A run of removed and added lines at 0-based line <paramref name="at"/> of the current text.</summary>
    private static void Run(List<LineChange> marks, int at, int removed, int added)
    {
        if (added == 0)
        {
            if (removed > 0) marks.Add(new LineChange(at, LineChangeKind.Deleted));
            return;
        }
        var kind = removed == 0 ? LineChangeKind.Added : LineChangeKind.Modified;
        for (var l = 0; l < added; l++) marks.Add(new LineChange(at + l + 1, kind));
    }

    /// <summary>
    /// A unified diff (<c>@@ -a,b +c,d @@</c> hunks with <paramref name="context"/> unchanged lines around each change) of
    /// <paramref name="current"/> against <paramref name="baseText"/>, for the Editor page's diff vs HEAD (DiffView);
    /// empty when they have the same lines. Line ends do not count, as in <see cref="Compute"/>.
    /// </summary>
    public static string UnifiedDiff(string baseText, string current, int context = 3)
    {
        var a = Lines(baseText);
        var b = Lines(current);
        // The edit script: ' ' kept, '-' removed (from a), '+' added (from b)
        var ops = new List<(char Op, int A, int B)>();
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) { ops.Add((' ', start, start)); start++; }
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        int n = endA - start, m = endB - start;
        if (n == 0 && m == 0) return "";
        if ((long)(n + 1) * (m + 1) > MaxTable)
        {
            for (var i = 0; i < n; i++) ops.Add(('-', start + i, -1));
            for (var j = 0; j < m; j++) ops.Add(('+', -1, start + j));
        }
        else
        {
            var table = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    table[i, j] = a[start + i] == b[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y]) { ops.Add((' ', start + x, start + y)); x++; y++; }
                else if (x < n && (y == m || table[x + 1, y] >= table[x, y + 1])) { ops.Add(('-', start + x, -1)); x++; }
                else { ops.Add(('+', -1, start + y)); y++; }
            }
        }
        for (var k = 0; k < a.Length - endA; k++) ops.Add((' ', endA + k, endB + k));

        var text = new System.Text.StringBuilder();
        var index = 0;
        while (index < ops.Count)
        {
            while (index < ops.Count && ops[index].Op == ' ') index++;
            if (index == ops.Count) break;
            var first = Math.Max(0, index - context);
            // The hunk takes in every later change fewer than 2×context kept lines away, then context lines after the last
            var lastChange = index;
            for (var k = index + 1; k < ops.Count && k - lastChange <= context * 2 + 1; k++)
                if (ops[k].Op != ' ') lastChange = k;
            var last = Math.Min(ops.Count - 1, lastChange + context);
            int oldStart = -1, newStart = -1, oldCount = 0, newCount = 0;
            var body = new System.Text.StringBuilder();
            for (var k = first; k <= last; k++)
            {
                var (op, ia, ib) = ops[k];
                if (op != '+') { if (oldStart < 0) oldStart = ia; oldCount++; }
                if (op != '-') { if (newStart < 0) newStart = ib; newCount++; }
                body.Append(op).Append(op == '+' ? b[ib] : a[ia]).Append('\n');
            }
            // An empty side starts at the line before (git's convention)
            oldStart = oldStart < 0 ? OldBefore(ops, first) : oldStart + 1;
            newStart = newStart < 0 ? NewBefore(ops, first) : newStart + 1;
            text.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n").Append(body);
            index = last + 1;
        }
        return text.ToString();

        static int OldBefore(List<(char Op, int A, int B)> ops, int at)
        {
            for (var k = at - 1; k >= 0; k--) if (ops[k].A >= 0) return ops[k].A + 1;
            return 0;
        }
        static int NewBefore(List<(char Op, int A, int B)> ops, int at)
        {
            for (var k = at - 1; k >= 0; k--) if (ops[k].B >= 0) return ops[k].B + 1;
            return 0;
        }
    }

    /// <summary>Lines without their ends; a final line break does not start another line.</summary>
    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}
