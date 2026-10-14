using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ombra.ClientCore;

/// <summary>
/// The files a tool call changed (<see cref="ToolItem.Changes"/>), from what omp reports. Its edit tool puts them in the
/// result's details (pi-tui tools/edit.ts EditToolDetails): one file's <c>op</c> (create | delete | update), <c>move</c>
/// (the new path) and <c>sourcePath</c> (the old one), <c>path</c> and <c>diff</c>; a call that changed several files
/// lists each in <c>perFileResults</c>. Its write tool reports nothing, so whether a write created its file is known only
/// from a look at the disk before the call ran (<see cref="ExpectNewFile"/>, done by the controller).
/// </summary>
public sealed partial class ConversationState
{
    /// <summary>Write calls whose file did not exist when they started; taken by their start frame.</summary>
    private readonly HashSet<string> _newFileCalls = new(StringComparer.Ordinal);

    /// <summary>The tools that write a whole file (omp's write; the names other agents give it).</summary>
    private static bool IsWriteTool(string tool) => tool is "write" or "create" or "write_file";

    /// <summary>
    /// The write call that starts next (by its id) creates its file: it was not on disk before omp wrote it. Called with
    /// the <c>tool_execution_start</c> frame, before it is applied.
    /// </summary>
    public void ExpectNewFile(string toolCallId) => _newFileCalls.Add(toolCallId);

    /// <summary>
    /// The file a <c>tool_execution_start</c> frame's write call writes, as the call names it (maybe relative to omp's
    /// working directory); null for other tools and for targets that are not files on disk (<c>local://</c>, <c>xd://</c>).
    /// </summary>
    public static string? WriteTarget(JsonElement startFrame)
    {
        if (!IsWriteTool(Str(startFrame, "toolName") ?? "") || !startFrame.TryGetProperty("args", out var a)) return null;
        return (Str(a, "path") ?? Str(a, "file_path")) is { Length: > 0 } path && !path.Contains("://", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>
    /// A write call's file as it starts: created when the controller saw no file there, otherwise (and in a loaded
    /// history, where nobody looked) modified. Its diff is the content being written.
    /// </summary>
    private IReadOnlyList<FileChange>? WrittenChange(string id, string tool, JsonElement args, string? written, bool live)
    {
        if (!IsWriteTool(tool) || args.ValueKind != JsonValueKind.Object) return null;
        var created = live && _newFileCalls.Remove(id);
        if ((Str(args, "path") ?? Str(args, "file_path")) is not { Length: > 0 } path || path.Contains("://", StringComparison.Ordinal)) return null;
        return [new FileChange(Shown(path), created ? FileChangeKind.Created : FileChangeKind.Modified, written)];
    }

    /// <summary>
    /// The files a finished call changed. A write keeps what its start said (nothing when it failed: the file was not
    /// written); any other tool is read from its result's details, where the row's summary names the file when the
    /// details carry only the diff.
    /// </summary>
    private IReadOnlyList<FileChange>? ChangesAtEnd(ToolItem t, JsonElement result, bool failed) =>
        IsWriteTool(t.Name) ? (failed ? null : t.Changes) : ChangesOf(result, PathLike(t.Summary));

    /// <summary>One change per file from an edit result's details; null when they name no change.</summary>
    private IReadOnlyList<FileChange>? ChangesOf(JsonElement result, string? callPath)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("details", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        var list = new List<FileChange>();
        if (d.TryGetProperty("perFileResults", out var per) && per.ValueKind == JsonValueKind.Array && per.GetArrayLength() > 0)
        {
            foreach (var f in per.EnumerateArray())
                if (FileChangeOf(f, null) is { } c) list.Add(c);
        }
        else if (FileChangeOf(d, callPath) is { } c) list.Add(c);
        return list.Count > 0 ? list : null;
    }

    private FileChange? FileChangeOf(JsonElement d, string? callPath)
    {
        if (d.ValueKind != JsonValueKind.Object || (d.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True)) return null;
        var diff = Str(d, "diff") is { Length: > 0 } text ? HeadTail(text, MaxDiffChars) : null;
        var op = Str(d, "op");
        var move = Str(d, "move");
        var source = Str(d, "sourcePath");
        // Other tools' details (a todo's op, a read's path) say nothing about a change
        if (diff is null && op is not ("create" or "delete") && move is null && source is null) return null;
        if ((Str(d, "path") ?? move ?? callPath) is not { Length: > 0 } path) return null;
        var kind = op switch
        {
            "create" => FileChangeKind.Created,
            "delete" => FileChangeKind.Deleted,
            _ when move is not null || source is not null => FileChangeKind.Moved,
            _ => FileChangeKind.Modified,
        };
        var from = kind == FileChangeKind.Moved ? source ?? (callPath is not null && callPath != path ? callPath : null) : null;
        return new FileChange(Shown(path), kind, diff, from is null ? null : Shown(from));
    }

    /// <summary>
    /// The row's summary once its files are known: "N files" for several; for one, its path unless the summary already
    /// names that file (the path the call was given, maybe the old one of a move). An apply_patch envelope or an intent
    /// is no file name.
    /// </summary>
    private static string SummaryOf(string summary, IReadOnlyList<FileChange>? changes)
    {
        if (changes is not { Count: > 0 }) return summary;
        if (changes.Count > 1) return $"{changes.Count} files";
        var c = changes[0];
        var named = PathLike(summary) is { } p ? Path.GetFileName(p) : "";
        return named.Length > 0 && (named == Path.GetFileName(c.Path) || (c.MovedFrom is { } from && named == Path.GetFileName(from))) ? summary : c.Path;
    }

    /// <summary>The summary when it reads as a file path (not a command, a URL or a sentence).</summary>
    private static string? PathLike(string summary) =>
        summary.Length > 0 && !summary.StartsWith("$ ", StringComparison.Ordinal) && !summary.Contains("://", StringComparison.Ordinal)
        && !summary.StartsWith("*** ", StringComparison.Ordinal) && (summary.Contains('/') || summary.Contains('\\') || summary.Contains('.'))
        && !summary.Contains(' ', StringComparison.Ordinal)
            ? summary
            : null;

    /// <summary>A path under omp's working directory as the project shows it (relative, '/'); any other as given.</summary>
    private string Shown(string path)
    {
        if (Cwd is not { Length: > 0 } cwd || !Path.IsPathRooted(path)) return path;
        var rel = Path.GetRelativePath(cwd, path);
        return rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel.Replace('\\', '/');
    }

    /// <summary>How many files a run changed: every file of every change, each once.</summary>
    private static int FilesChanged(IEnumerable<ToolItem> tools) =>
        tools.SelectMany(t => t.Changes is { Count: > 0 } c ? c.Select(x => x.Path) : t.Diff is { Length: > 0 } ? new[] { t.Summary } : Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal).Count();

    [GeneratedRegex(@"^\[(.+?)#[0-9A-Fa-f]{4}\]\s*$", RegexOptions.Multiline)]
    private static partial Regex HashlineHeader();

    [GeneratedRegex(@"^\*\*\* (?:Update|Add|Delete) File: (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex PatchHeader();

    /// <summary>
    /// The files an edit's input names: omp's hashline sections (<c>[path#1A2B]</c>) or an apply_patch envelope's
    /// <c>*** Update File: path</c>; empty for other input.
    /// </summary>
    private static List<string> EditedFiles(string input)
    {
        var files = new List<string>();
        foreach (Match m in HashlineHeader().Matches(input))
            if (!files.Contains(m.Groups[1].Value)) files.Add(m.Groups[1].Value);
        foreach (Match m in PatchHeader().Matches(input))
            if (!files.Contains(m.Groups[1].Value)) files.Add(m.Groups[1].Value);
        return files;
    }
}
