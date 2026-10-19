using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// A local mark (K4) as a team item of kind <see cref="TeamItemKinds.Mark"/> (P5 §4, SERVER_DESIGN §3.0): one item per
/// mark and recipient, no body. Anchor: <c>{code: {repoFingerprint, commit, path, range, excerpt}}</c> or
/// <c>{web: {url, selector, textQuote, viewport}}</c>; Context: <c>{text: note, selection: {anchor, mark, batch}}</c>,
/// where <c>anchor</c> is the whole <see cref="CodeAnchor"/>/<see cref="PageAnchor"/> as JSON text (lines around, offsets,
/// the area; negative numbers survive the core's JSON→CBOR rules as text), <c>mark</c> the sender's mark id and
/// <c>batch</c> one send. A mark from another client with only the anchor fields still opens at its place.
/// </summary>
public static class SharedMarks
{
    /// <summary>A recipient's note with done/dismissed (P5: up to 280 characters, not a thread).</summary>
    public const int MaxStatusNote = 280;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What a mark is on: <c>file:path</c> or <c>page:url-without-fragment</c> (marks of one author on one object share a toast).</summary>
    public static string ObjectKey(LocalMark m) =>
        m.Code is { } c ? "file:" + c.Path
        : m.Page is { } p ? "page:" + (Uri.TryCreate(p.Url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Query) : p.Url)
        : "";

    /// <summary>The object as people name it: the file's path, the page's title or address.</summary>
    public static string ObjectTitle(LocalMark m) =>
        m.Code is { } c ? c.Path
        : m.Page is { } p ? (p.Title.Trim().Length > 0 ? p.Title.Trim()
            : Uri.TryCreate(p.Url, UriKind.Absolute, out var u) ? u.Authority + (u.AbsolutePath == "/" ? "" : u.AbsolutePath.TrimEnd('/')) : p.Url)
        : "";

    /// <summary>Only code and page marks can be shared (a saved draft's legacy block has no place to open).</summary>
    public static bool CanShare(LocalMark m) => m.IsValid && (m.Code is not null || m.Page is not null);

    /// <param name="repo">The sender's checked-out repository (code marks carry its fingerprint and HEAD commit); null outside git.</param>
    public static TeamItemDraft ToDraft(LocalMark m, string batch, RepoIdentity? repo = null)
    {
        if (!CanShare(m)) throw new ArgumentException("Only code and page marks can be shared", nameof(m));
        var full = m.Code is { } c0 ? JsonSerializer.Serialize(c0, Json) : JsonSerializer.Serialize(m.Page!, Json);
        var anchor = m.Code is { } c
            ? new JsonObject
            {
                ["code"] = new JsonObject
                {
                    ["repoFingerprint"] = Text(repo?.Fingerprint ?? ""), ["commit"] = Text(repo?.Commit ?? ""),
                    ["path"] = Text(c.Path), ["range"] = Range(c), ["excerpt"] = Text(c.LineText),
                },
            }
            : new JsonObject
            {
                ["web"] = new JsonObject
                {
                    ["url"] = Text(m.Page!.Url), ["selector"] = Text(m.Page.Element.Selector), ["textQuote"] = Text(m.Page.Element.Text),
                    ["viewport"] = $"{m.Page.Mark.ViewportWidth}x{m.Page.Mark.ViewportHeight}",
                },
            };
        var note = m.Note.Length <= LocalMarks.MaxNote ? m.Note : m.Note[..GraphemeText.PrefixLength(m.Note, LocalMarks.MaxNote)];
        return new TeamItemDraft(TeamItemKinds.Mark, Anchor: anchor,
            Context: new JsonObject
            {
                ["text"] = Text(note),
                ["selection"] = new JsonObject { ["anchor"] = Text(full), ["mark"] = Text(m.Id), ["batch"] = Text(batch) },
            });
    }

    /// <summary>The mark an item carries, or null when it is not a mark or not one this client can read.</summary>
    public static SharedMark? FromItem(TeamItem item)
    {
        if (item.Kind != TeamItemKinds.Mark || item.Deleted || item.Anchor is not JsonObject a) return null;
        try
        {
            var selection = item.Context?["selection"];
            var full = Str(selection, "anchor");
            var note = Str(item.Context, "text") ?? "";
            var markId = Str(selection, "mark") ?? item.ItemId;
            LocalMark? mark = (a["code"], a["web"]) switch
            {
                (JsonObject code, null) => new LocalMark(markId, note, Code: full is not null ? JsonSerializer.Deserialize<CodeAnchor>(full, Json) : CodeFrom(code)),
                (null, JsonObject web) => new LocalMark(markId, note, Page: full is not null ? JsonSerializer.Deserialize<PageAnchor>(full, Json) : PageFrom(web)),
                _ => null,
            };
            if (mark is null || !CanShare(mark)) return null;
            var code0 = a["code"];
            return new SharedMark(item, mark, ObjectKey(mark), ObjectTitle(mark), Str(selection, "batch") ?? item.ItemId)
            {
                RepoFingerprint = Str(code0, "repoFingerprint") is { Length: > 0 } fp ? fp : null,
                Commit = Str(code0, "commit") is { Length: > 0 } commit ? commit : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"41", "41-43", or "" for the whole file.</summary>
    private static string Range(CodeAnchor c) =>
        c.Line is not { } n ? "" : c.EndLine is { } end && end > n ? $"{n}-{end}" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static CodeAnchor? CodeFrom(JsonObject code)
    {
        if (Str(code, "path") is not { Length: > 0 } path) return null;
        var range = (Str(code, "range") ?? "").Split('-');
        int? line = int.TryParse(range[0], out var l) && l > 0 ? l : null;
        int? end = range.Length > 1 && int.TryParse(range[1], out var e) && e > (line ?? 0) ? e : null;
        return new CodeAnchor(path, line, false, Str(code, "excerpt") ?? "", EndLine: end);
    }

    private static PageAnchor? PageFrom(JsonObject web)
    {
        if (Str(web, "url") is not { Length: > 0 } url || Str(web, "selector") is not { } selector) return null;
        var vp = (Str(web, "viewport") ?? "").Split('x');
        int.TryParse(vp[0], out var w);
        var h = vp.Length > 1 && int.TryParse(vp[1], out var hh) ? hh : 0;
        return new PageAnchor(url, "", new PageMark(PageMarkKind.Point, 0, 0, 0, 0, 0, 0, 0, 0, w, h),
            new PageElement(selector, "", "", Str(web, "textQuote") ?? "", "", 0, 0, 0, 0), []);
    }

    /// <summary>The note as sent with done/dismissed: trimmed, at most <see cref="MaxStatusNote"/> characters, null when empty.</summary>
    public static string? StatusNote(string? note)
    {
        var t = note?.Trim() ?? "";
        if (t.Length == 0) return null;
        return t.Length <= MaxStatusNote ? t : t[..GraphemeText.PrefixLength(t, MaxStatusNote)];
    }

    /// <summary>Marks addressed to <paramref name="me"/> by others, newest first.</summary>
    public static IReadOnlyList<SharedMark> Received(IEnumerable<TeamItem> items, string me) =>
        [.. items.Where(i => i.RecipientMemberId == me && i.AuthorMemberId != me).Select(FromItem).OfType<SharedMark>()];

    /// <summary>Marks <paramref name="me"/> sent, one row per mark of a send with each recipient's copy.</summary>
    public static IReadOnlyList<SentMark> Sent(IEnumerable<TeamItem> items, string me) =>
        [.. items.Where(i => i.AuthorMemberId == me && i.RecipientMemberId is not null && i.RecipientMemberId != me)
            .Select(FromItem).OfType<SharedMark>()
            .GroupBy(s => (s.Batch, s.Mark.Id))
            .Select(g => new SentMark(g.First(), [.. g.Select(s => SentStatus.Of(s.Item))]))];

    /// <summary>
    /// The toasts for marks not yet toasted and not yet seen, one per author and object (P5: several marks of one author
    /// on one object make one toast, also when they arrive one by one: while a toasted mark of that author on that object
    /// is unseen, more marks there add no toast). <paramref name="toasted"/> remembers the items already handled.
    /// </summary>
    public static IReadOnlyList<MarkToast> NewToasts(IReadOnlyList<SharedMark> received, string me, ISet<string> toasted)
    {
        var unseen = received.Where(r => !r.SeenBy(me)).ToList();
        var shown = unseen.Where(r => toasted.Contains(ItemKey(r))).Select(r => (r.Item.AuthorMemberId, r.ObjectKey)).ToHashSet();
        return [.. unseen.Where(r => toasted.Add(ItemKey(r)))
            .GroupBy(r => (r.Item.AuthorMemberId, r.ObjectKey))
            .Where(g => !shown.Contains(g.Key))
            .Select(g => new MarkToast(g.Key.AuthorMemberId, g.Key.ObjectKey, g.First().ObjectTitle, [.. g]))];

        static string ItemKey(SharedMark r) => r.Item.AuthorMemberId + "/" + r.Item.ItemId;
    }

    private static string Text(string s) => s.StartsWith("hex:", StringComparison.Ordinal) ? "\u200B" + s : s;

    private static string? Str(JsonNode? o, string name)
    {
        if (o is not JsonObject obj || obj[name] is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        return s.StartsWith("\u200Bhex:", StringComparison.Ordinal) ? s[1..] : s;
    }
}

/// <summary>A mark received or sent: the item it came in, the mark, what it is on and which send it belongs to.</summary>
public sealed record SharedMark(TeamItem Item, LocalMark Mark, string ObjectKey, string ObjectTitle, string Batch)
{
    /// <summary>The sender's repository and commit of a code mark (null: unknown, not a git project or a page mark).</summary>
    public string? RepoFingerprint { get; init; }
    public string? Commit { get; init; }
    public TeamItemStatus? StatusOf(string member) => Item.Statuses.FirstOrDefault(s => s.RecipientMemberId == member);
    public bool SeenBy(string member) => StatusOf(member) is { } s && (s.Seen || s.Status is not null);
}

/// <summary>One recipient's state of a sent mark: not seen yet, seen, done or dismissed, with the note.</summary>
public sealed record SentStatus(string RecipientMemberId, bool Seen, string? Status, string? Note)
{
    public static SentStatus Of(TeamItem copy)
    {
        var s = copy.Statuses.FirstOrDefault(x => x.RecipientMemberId == copy.RecipientMemberId);
        return new(copy.RecipientMemberId!, s is { } x && (x.Seen || x.Status is not null), s?.Status, s?.Note);
    }
}

/// <summary>A sent mark and each recipient's status.</summary>
public sealed record SentMark(SharedMark Mark, IReadOnlyList<SentStatus> Recipients);

/// <summary>"Anna left you comments in app.py": an author's new marks on one object.</summary>
public sealed record MarkToast(string AuthorMemberId, string ObjectKey, string ObjectTitle, IReadOnlyList<SharedMark> Marks);
