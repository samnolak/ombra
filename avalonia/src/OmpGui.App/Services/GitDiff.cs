using System.Globalization;
using System.Text.RegularExpressions;
using OmpGui.ClientCore;

namespace OmpGui.App.Services;

/// <summary>One "@@ … @@" part of a file's diff, its lines exactly as git printed them (a '\r' of a CRLF file kept).</summary>
public sealed record DiffHunk(string Header, int OldStart, int NewStart, IReadOnlyList<string> Lines);

/// <summary>
/// A file's changes since the last commit, as <c>git diff</c> prints them: the file header (<c>diff --git</c>, mode
/// lines, <c>---</c>/<c>+++</c>) and the hunks. A hunk with that header is a patch git can apply on its own, which is how
/// one hunk is reverted (<c>git apply -R</c>).
/// </summary>
public sealed partial record FileDiff(IReadOnlyList<string> Header, IReadOnlyList<DiffHunk> Hunks, bool IsBinary)
{
    /// <summary>What git printed (a reload that reads the same keeps what is shown, a comment being written too).</summary>
    public string Text { get; init; } = "";

    /// <summary>git printed more than <see cref="GitReview.MaxDiffChars"/>: nothing of it is kept.</summary>
    public bool TooLarge { get; init; }

    /// <summary>
    /// The file is new since the last commit (untracked, added, or the new name of a rename): its one hunk is the whole
    /// file, and taking it out deletes the file.
    /// </summary>
    public bool IsNewFile => Header.Any(l => l.StartsWith("new file mode ", StringComparison.Ordinal));

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    public static FileDiff Parse(string diff)
    {
        var header = new List<string>();
        var hunks = new List<DiffHunk>();
        var binary = false;
        string? hunkHeader = null;
        int oldStart = 0, newStart = 0;
        var lines = new List<string>();
        var all = diff.Split('\n');
        // The output ends with a newline: the empty piece after it is no line
        var count = all.Length > 0 && all[^1].Length == 0 ? all.Length - 1 : all.Length;
        for (var i = 0; i < count; i++)
        {
            var line = all[i];
            var m = HunkHeader().Match(line);
            if (m.Success)
            {
                if (hunkHeader is not null) hunks.Add(new DiffHunk(hunkHeader, oldStart, newStart, lines));
                hunkHeader = line;
                oldStart = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                newStart = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                lines = [];
            }
            else if (hunkHeader is not null) lines.Add(line);
            else
            {
                header.Add(line);
                if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch") binary = true;
            }
        }
        if (hunkHeader is not null) hunks.Add(new DiffHunk(hunkHeader, oldStart, newStart, lines));
        return new FileDiff(header, hunks, binary) { Text = diff };
    }

    /// <summary>The file header and <paramref name="hunk"/> alone: a patch for <c>git apply</c>.</summary>
    public string PatchFor(DiffHunk hunk) =>
        string.Join('\n', Header) + "\n" + hunk.Header + "\n" + string.Join('\n', hunk.Lines) + "\n";
}

/// <summary>
/// The git commands of the Files pane's review: reading a file's diff and putting a file or a hunk back as the last
/// commit has it. Paths are relative to the repository's top folder, where they run. External diff programs and text
/// conversions stay off: the opened folder is not trusted, and its config could name either.
/// </summary>
public static class GitReview
{
    private static readonly string[] DiffOptions =
        ["-c", "core.quotepath=off", "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/", "-U3"];

    /// <summary>
    /// The most of a diff that is read (about 8 MB in memory). The view shows only its first rows anyway; a huge
    /// untracked log or data file would otherwise be read whole, and again on every refresh while omp runs.
    /// </summary>
    public const int MaxDiffChars = 4_000_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The changes of <paramref name="relativePath"/> since the last commit (staged and not); for a file git does not
    /// track yet, the whole file as added. Null when git could not say.
    /// </summary>
    public static async Task<FileDiff?> ReadAsync(string top, string relativePath, bool untracked, CancellationToken ct)
    {
        string[] args = untracked
            ? [.. DiffOptions, "--no-index", "--", "/dev/null", relativePath]
            : [.. DiffOptions, "HEAD", "--", relativePath];
        var r = await Git.RunAsync(top, args, ct, Timeout, MaxDiffChars);
        if (r is { TooLong: true }) return new FileDiff([], [], false) { TooLarge = true };
        // "--no-index" says 1 when the files differ, which they always do here
        return r is { ExitCode: 0 } || untracked && r is { ExitCode: 1 } ? FileDiff.Parse(r.Output) : null;
    }

    /// <summary>
    /// Takes <paramref name="hunk"/> back out of the file, and out of git's staging area when it was staged; null when
    /// done, else the reason.
    /// </summary>
    public static async Task<string?> RevertHunkAsync(Func<ToolCommand, CancellationToken, Task<ToolResult>> run, string top, FileDiff diff, DiffHunk hunk,
        CancellationToken ct)
    {
        var patch = Path.Combine(Path.GetTempPath(), $"ompgui-hunk-{Guid.NewGuid():N}.patch");
        await File.WriteAllTextAsync(patch, diff.PatchFor(hunk), ct);
        try
        {
            Task<ToolResult> Apply(params string[] how) =>
                run(new ToolCommand("git", ["apply", .. how, "--whitespace=nowarn", patch], top, Timeout), ct);

            // The diff is against the last commit, so the index holds either side of the hunk: the change (staged; it has
            // to come out of the index too, or the next commit would still take it) or the last commit's lines (not
            // staged). Neither means part of it was staged and then edited again: taking it out of the file alone would
            // hide the change from this view while the next commit still takes the staged part, so nothing is touched.
            var staged = await Apply("-R", "--cached", "--check");
            if (staged.NotFound) return "git was not found on this computer.";
            if (!staged.Ok && !(await Apply("--cached", "--check")).Ok)
                return "part of it is staged and was changed again since. Stage the file or unstage it in git first.";
            var r = await Apply("-R");
            if (!r.Ok) return r.NotFound ? "git was not found on this computer." : r.Message;
            if (!staged.Ok) return null;
            var cached = await Apply("-R", "--cached");
            return cached.Ok ? null : "the file is back, but git's staging area still has it: " + cached.Message;
        }
        finally
        {
            try { File.Delete(patch); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Puts the file back as the last commit has it, in the index and on disk (a file added to git since goes; a renamed
    /// file goes back to <paramref name="renamedFrom"/>, its name in the last commit); a file git does not track is
    /// deleted. Null when done, else the reason.
    /// </summary>
    public static async Task<string?> RevertFileAsync(Func<ToolCommand, CancellationToken, Task<ToolResult>> run, string top, string relativePath, bool untracked,
        string? renamedFrom, CancellationToken ct)
    {
        if (untracked)
        {
            try
            {
                File.Delete(Path.Combine(top, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
        }
        // The new name is not in the last commit, so restoring it takes it away; the old name comes back with it
        string[] paths = renamedFrom is null ? [relativePath] : [renamedFrom, relativePath];
        var r = await run(new ToolCommand("git", ["restore", "--source=HEAD", "--staged", "--worktree", "--", .. paths], top, Timeout), ct);
        return r.Ok ? null : r.NotFound ? "git was not found on this computer." : r.Message;
    }
}
