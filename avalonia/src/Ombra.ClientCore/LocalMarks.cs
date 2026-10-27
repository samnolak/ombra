using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ombra.ClientCore;

/// <summary>The element that holds a page mark, as the page reported it (Controls/Preview/annotate.js); its box in the viewport.</summary>
public sealed record PageElement(string Selector, string Tag, string Html, string Text, string Source, int X, int Y, int Width, int Height);

public enum PageMarkKind { Point, Area, Sketch }

/// <summary>
/// Where the user marked the page: a point (no size), an area or a sketch (its strokes' bounding box), at
/// <see cref="X"/>,<see cref="Y"/> in the viewport and <see cref="PageX"/>,<see cref="PageY"/> on the page;
/// <see cref="OffsetX"/>,<see cref="OffsetY"/> from the holding element's corner (the pin follows that element). A
/// sketch's <see cref="Strokes"/> are freehand lines, each <c>[x0, y0, x1, y1, …]</c> in CSS px on the page (document
/// coordinates, simplified by the page); null for points, areas and marks saved before sketches.
/// </summary>
public sealed record PageMark(PageMarkKind Kind, int X, int Y, int Width, int Height, int PageX, int PageY, int OffsetX, int OffsetY,
    int ViewportWidth, int ViewportHeight, int[][]? Strokes = null);

/// <summary>An element an area shows (one of a few the page found under it).</summary>
public sealed record PageInsideElement(string Selector, string Tag, string Text);

/// <summary>The disk/editor state a code mark was made against (document URI and version, unsaved buffer, review diff hash, hunk).</summary>
public sealed record CodeCommentAnchor(string Uri, long? ObservedDocumentVersion, bool BufferUnsaved, string ReviewSnapshotSha256, string? HunkHeader);

/// <summary>Where a code mark was made: the Files pane's Changes, the editor, or a diff card in the conversation.</summary>
public enum CodeMarkOrigin { Review, Editor, Conversation }

/// <summary>
/// A place in the code: a project-relative '/' path, the line (or <see cref="EndLine"/> for a range; null for the whole
/// file), whether it is a removed line (numbered in the last commit), the quoted text, a few lines around it, and the
/// snapshot it was made against.
/// </summary>
public sealed record CodeAnchor(string Path, int? Line, bool RemovedLine, string LineText, CodeMarkOrigin Origin = CodeMarkOrigin.Review,
    int? EndLine = null, string[]? Before = null, string[]? After = null, CodeCommentAnchor? Snapshot = null);

/// <summary>A point or area of a previewed page and what finds it in the code.</summary>
public sealed record PageAnchor(string Url, string Title, PageMark Mark, PageElement Element, PageInsideElement[] Inside);

public enum MarkStatus { Open, Resolved }

/// <summary>
/// A local mark (K4, Figma-like, no threads): one anchor (<see cref="Code"/> or <see cref="Page"/>), an optional short
/// <see cref="Note"/> and a status. Open marks go with the next request; resolved ones stay listed but are not sent.
/// <see cref="Legacy"/> is the verbatim page-comments block of a draft saved before marks were structured.
/// <see cref="Image"/> is a sketch's picture: the page around it with its strokes, taken when it was made.
/// </summary>
public sealed record LocalMark(string Id, string Note, MarkStatus Status = MarkStatus.Open, CodeAnchor? Code = null, PageAnchor? Page = null, string? Legacy = null,
    ImageAttachment? Image = null)
{
    public bool IsValid => Id is { Length: > 0 } && Note is not null && Enum.IsDefined(Status)
        && (Code is not null ? Page is null && Legacy is null && Image is null && Code.Path is not null && Code.LineText is not null
            : Page is not null ? Legacy is null && Page.Url is not null && Page.Title is not null && Page.Mark is not null && Page.Element is { Selector: not null }
                && Enum.IsDefined(Page.Mark.Kind) && (Page.Mark.Strokes is null || Page.Mark.Strokes.All(s => s is { Length: >= 2 } && s.Length % 2 == 0))
                && Page.Inside is not null && Page.Inside.All(i => i is { Selector: not null })
                && (Image is null || Image is { Name: not null, MimeType: not null, Data.Length: > 0 })
            : Legacy is { Length: > 0 } && Image is null);
}

/// <summary>The one request format for marks, and the migration of drafts saved before marks.</summary>
public static class LocalMarks
{
    public const int MaxMarks = 40;
    public const int MaxNote = 2000;

    /// <summary>How the code block begins in a sent message (the conversation shows it as a chip).</summary>
    public const string CodeMarker = "Comments on the code, ";
    /// <summary>How a page block begins in a sent message.</summary>
    public const string PageMarker = "Comments on the page ";

    public const string CodeOnlyLine = "Please make the changes my comments on the code ask for.";
    public const string PageOnlyLine = "Please make the changes my comments on the page ask for.";
    public const string AllOnlyLine = "Please make the changes my comments ask for.";

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// The request: the user's words, then the open marks on the code, then those on pages (resolved marks stay out).
    /// Marks sent without words get a first line that says what to do. Numbers are the marks' positions in the list,
    /// as the chips and pins show them.
    /// </summary>
    public static string BuildRequest(string text, IReadOnlyList<LocalMark> marks, IReadOnlyDictionary<string, int>? images = null)
    {
        var code = CodeBlock(marks);
        var page = PageBlock(marks, images);
        if (code.Length == 0 && page.Length == 0) return text;
        var first = text.Length > 0 ? text : code.Length == 0 ? PageOnlyLine : page.Length == 0 ? CodeOnlyLine : AllOnlyLine;
        return string.Join("\n\n", new[] { first, code, page }.Where(s => s.Length > 0));
    }

    /// <summary>
    /// The pictures of the open sketches that go with the request, in mark order, numbered from <paramref name="first"/>
    /// (after the message's own images), while their bytes fit in <paramref name="budget"/>: the request is one message
    /// of bounded size. <c>Numbers</c>: mark id → image number, for <see cref="BuildRequest"/>.
    /// </summary>
    public static (ImageAttachment[] Images, Dictionary<string, int> Numbers) SketchImages(IReadOnlyList<LocalMark> marks, int first, long budget)
    {
        var images = new List<ImageAttachment>();
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in marks)
        {
            if (m is not { Status: MarkStatus.Open, Page.Mark.Kind: PageMarkKind.Sketch, Image: { } image } || image.Data.Length > budget) continue;
            budget -= image.Data.Length;
            numbers[m.Id] = first + images.Count;
            images.Add(image);
        }
        return ([.. images], numbers);
    }

    public static string CodeBlock(IReadOnlyList<LocalMark> marks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < marks.Count; i++)
        {
            if (marks[i] is not { Status: MarkStatus.Open, Code: { } c } m) continue;
            if (sb.Length == 0) sb.Append(CodeMarker + "each with the marked line quoted:\n");
            var lines = c.Line is not { } n ? "" : c.EndLine is { } end && end > n ? $", lines {n}-{end}" : $", line {n}";
            var where = $"{c.Path}{lines}{(c.RemovedLine ? " of the last commit (a removed line)" : "")}";
            var origin = c.Origin switch { CodeMarkOrigin.Editor => " (in the editor)", CodeMarkOrigin.Conversation => " (on a change in the conversation)", _ => "" };
            sb.Append(CultureInfo.InvariantCulture, $"\n{i + 1}. {where}{origin}: {(m.Note.Length > 0 ? m.Note : "(no note)")}\n");
            foreach (var l in c.LineText.Replace("\r\n", "\n").Split('\n')) sb.Append(CultureInfo.InvariantCulture, $"   > {(c.EndLine is null ? l.Trim() : l.TrimEnd())}\n");
            if (c.Before is { Length: > 0 } before) sb.Append("   Lines before it:\n").AppendJoin("", before.Select(l => $"     {l.TrimEnd()}\n"));
            if (c.After is { Length: > 0 } after) sb.Append("   Lines after it:\n").AppendJoin("", after.Select(l => $"     {l.TrimEnd()}\n"));
            if (c.Snapshot is { } anchor)
            {
                sb.Append(c.Origin == CodeMarkOrigin.Editor
                    ? "   Editor buffer anchor (line numbers are in this buffer version): "
                    : "   Disk-review snapshot anchor (not an unsaved-buffer line mapping): ");
                sb.AppendLine(JsonSerializer.Serialize(anchor));
            }
        }
        if (sb.Length > 0) sb.Append("\nMake the changes these comments ask for.");
        return sb.ToString();
    }

    public static string PageBlock(IReadOnlyList<LocalMark> marks, IReadOnlyDictionary<string, int>? images = null)
    {
        var sb = new StringBuilder();
        var open = marks.Select((m, i) => (Mark: m, Number: i + 1)).Where(x => x.Mark.Status == MarkStatus.Open).ToList();
        var pages = open.Where(x => x.Mark.Page is not null).ToList();
        var sketches = pages.Any(x => x.Mark.Page!.Mark.Kind == PageMarkKind.Sketch);
        foreach (var group in pages.GroupBy(x => new Uri(x.Mark.Page!.Url).GetLeftPart(UriPartial.Query)))
        {
            if (sb.Length > 0) sb.Append('\n');
            var title = group.First().Mark.Page!.Title;
            sb.Append(CultureInfo.InvariantCulture, $"{PageMarker}{group.Key}{(title.Length > 0 ? $" (\"{title}\")" : "")}, left in the preview.");
            sb.Append(sketches
                ? " Each marks a point, an area or a sketch (a rough drawing of what I want there) of the page; the element details are quoted from the page:\n"
                : " Each marks a point or an area of the page; the element details are quoted from the page:\n");
            foreach (var (mark, number) in group)
            {
                var (m, e) = (mark.Page!.Mark, mark.Page.Element);
                var note = mark.Note.Length > 0 ? mark.Note : "(no note)";
                var onPage = m.PageX != m.X || m.PageY != m.Y ? string.Create(CultureInfo.InvariantCulture, $" ({m.PageX},{m.PageY} on the page)") : "";
                switch (m.Kind)
                {
                    case PageMarkKind.Sketch:
                        var image = images?.GetValueOrDefault(mark.Id) is { } n and > 0 ? string.Create(CultureInfo.InvariantCulture, $" — see image {n}") : "";
                        sb.Append(CultureInfo.InvariantCulture, $"\n{number}. Sketch{image}: {note}\n");
                        var strokes = m.Strokes ?? [];
                        sb.Append(CultureInfo.InvariantCulture, $"   Sketch: {strokes.Length} stroke{(strokes.Length == 1 ? "" : "s")} over {m.Width}×{m.Height} at {m.X},{m.Y} in a {m.ViewportWidth}×{m.ViewportHeight} viewport{onPage}\n");
                        // Without its picture the drawing still reaches the agent: its lines as an SVG path in page px
                        if (image.Length == 0 && strokes.Length > 0)
                            sb.Append("   Strokes (SVG path, CSS px on the page): ").AppendJoin(' ', strokes.Select(Path)).Append('\n');
                        break;
                    case PageMarkKind.Area:
                        sb.Append(CultureInfo.InvariantCulture, $"\n{number}. {note}\n");
                        sb.Append(CultureInfo.InvariantCulture, $"   Area: {m.Width}×{m.Height} at {m.X},{m.Y} in a {m.ViewportWidth}×{m.ViewportHeight} viewport{onPage}\n");
                        break;
                    default:
                        sb.Append(CultureInfo.InvariantCulture, $"\n{number}. {note}\n");
                        sb.Append(CultureInfo.InvariantCulture, $"   Point: {m.X},{m.Y} in a {m.ViewportWidth}×{m.ViewportHeight} viewport{onPage}\n");
                        break;
                }
                sb.Append(CultureInfo.InvariantCulture, $"   {m.Kind switch { PageMarkKind.Area => "Within", PageMarkKind.Sketch => "Over", _ => "On" }}: {e.Html}");
                if (e.Text.Length > 0) sb.Append(CultureInfo.InvariantCulture, $" with the text \"{e.Text}\"");
                sb.Append('\n');
                sb.Append(CultureInfo.InvariantCulture, $"   Selector: {e.Selector}\n");
                if (e.Source.Length > 0) sb.Append(CultureInfo.InvariantCulture, $"   Source: {e.Source}\n");
                sb.Append(CultureInfo.InvariantCulture, $"   Element box: {e.Width}×{e.Height} at {e.X},{e.Y}\n");
                if (mark.Page.Inside.Length > 0)
                    sb.Append(m.Kind == PageMarkKind.Sketch ? "   Under the sketch: " : "   Inside the area: ")
                        .AppendJoin("; ", mark.Page.Inside.Select(i => i.Text.Length > 0 ? $"{i.Tag} \"{i.Text}\" ({i.Selector})" : i.Selector)).Append('\n');
            }
        }
        if (sb.Length > 0)
            sb.Append(sketches
                ? "\nFind these places in the project's source and make the changes the comments ask for; build what each sketch roughly draws, where it is drawn."
                : "\nFind these places in the project's source and make the changes the comments ask for.");
        // A draft's page comments saved before marks were structured: the exact block that would have been sent
        foreach (var (mark, _) in open.Where(x => x.Mark.Legacy is not null))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(mark.Legacy);
        }
        return sb.ToString();

        static string Path(int[] s)
        {
            var p = new StringBuilder();
            for (var i = 0; i + 1 < s.Length; i += 2) p.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? "M" : " L")}{s[i]} {s[i + 1]}");
            return p.ToString();
        }
    }

    /// <summary>
    /// Moves a draft composer saved before marks (<c>Comments</c>: structured code comments; <c>PageComments</c>: the page
    /// comments' request text) into <c>Marks</c> without losing anything: every code comment field becomes the code anchor,
    /// and the page text becomes one legacy mark sent verbatim. A composer already with <c>Marks</c> keeps them first.
    /// </summary>
    public static void MigrateComposer(JsonObject composer)
    {
        var marks = composer["Marks"] as JsonArray ?? [];
        composer.Remove("Marks");
        if (composer["Comments"] is JsonArray comments)
        {
            foreach (var node in comments)
            {
                if (node is not JsonObject c) throw new JsonException("Incomplete draft comment");
                var anchor = new CodeAnchor(
                    c["Path"]?.GetValue<string>() ?? throw new JsonException("Incomplete draft comment"),
                    c["Line"]?.GetValue<int?>(),
                    c["RemovedLine"]?.GetValue<bool>() ?? false,
                    c["LineText"]?.GetValue<string>() ?? throw new JsonException("Incomplete draft comment"),
                    Snapshot: c["Anchor"]?.Deserialize<CodeCommentAnchor>());
                var mark = new LocalMark(NewId(), c["Comment"]?.GetValue<string>() ?? throw new JsonException("Incomplete draft comment"), Code: anchor);
                marks.Add(JsonSerializer.SerializeToNode(mark));
            }
        }
        composer.Remove("Comments");
        if (composer["PageComments"]?.GetValue<string>() is { Length: > 0 } page)
            marks.Add(JsonSerializer.SerializeToNode(new LocalMark(NewId(), "", Legacy: page)));
        composer.Remove("PageComments");
        composer["Marks"] = marks;
    }
}
