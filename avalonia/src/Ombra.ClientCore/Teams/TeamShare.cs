using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ombra.ClientCore.Teams;

/// <summary>A page in a shared link or collection.</summary>
public sealed record SharedLink(string Url, string? Title);

/// <summary>A file inside a shared skill: its path relative to the skill's folder (with '/') and its bytes.</summary>
public sealed record SharedFile(string Path, byte[] Data);

/// <summary>A share the user sent, one row for all the per-recipient copies it made.</summary>
public sealed record SentShare(string ShareId, string Kind, string Title, ulong CreatedAt, IReadOnlyList<SentCopy> Copies);

/// <summary>One recipient's copy of a sent share and that recipient's answer (null while there is none).</summary>
public sealed record SentCopy(string RecipientMemberId, bool Seen, string? Status, string? Note);

/// <summary>A resource larger than its kind may be (<paramref name="limit"/> bytes).</summary>
public sealed class TeamShareTooLargeException(long bytes, long limit = TeamShare.MaxContentBytes) : Exception($"{bytes} bytes is more than {limit} bytes")
{
    public long Bytes { get; } = bytes;
    public long Limit { get; } = limit;
}

/// <summary>
/// Lines of code shared from the editor: the project-relative path ('/'), the lines (1-based, inclusive), the language
/// id, the text, and where it came from when known (project name, git branch, commit).
/// </summary>
public sealed record SharedSnippet(string Path, int StartLine, int EndLine, string? Language, string Text,
    string? Project = null, string? Branch = null, string? Commit = null);

/// <summary>Markdown shared from a chat: an assistant reply (<c>reply</c>) or a comment I sent (<c>comment</c>), its chat and page.</summary>
public sealed record SharedNote(string Title, string Text, string Source, string? ChatTitle = null, string? Url = null);

/// <summary>
/// Something to share (P5): sent to each recipient as its own copy (R5-10). Content that does not fit an item (a file,
/// a skill, a screenshot) is uploaded first as an encrypted blob for that recipient's pair (C3).
/// </summary>
public abstract record ShareContent(string Kind, string Title)
{
    /// <summary>Sends it to every recipient; <paramref name="comment"/> goes into the item's context text.</summary>
    public abstract Task<int> SendAsync(ITeamItems items, string teamId, IReadOnlyList<string> recipients, string? comment, CancellationToken ct = default);

    protected static JsonObject? Context(string? comment) =>
        comment?.Trim() is { Length: > 0 } c ? new JsonObject { ["text"] = TeamShare.Text(c.Length > TeamShare.MaxCommentChars ? c[..TeamShare.MaxCommentChars] : c) } : null;
}

/// <summary>A body sent as is (link, edit).</summary>
internal sealed record InlineContent(string Kind, string Title, JsonObject Body) : ShareContent(Kind, Title)
{
    public override async Task<int> SendAsync(ITeamItems items, string teamId, IReadOnlyList<string> recipients, string? comment, CancellationToken ct = default) =>
        (await items.SendAsync(teamId, new TeamItemDraft(Kind, Body.DeepClone(), Context: Context(comment)), recipients, ct)).Count;
}

/// <summary>Bytes in a blob of the recipient's pair, the body naming it and the key beside it in attachments.</summary>
internal sealed record BlobContent(string Kind, string Title, byte[] Data, Func<TeamBlob, JsonObject> Body) : ShareContent(Kind, Title)
{
    public override async Task<int> SendAsync(ITeamItems items, string teamId, IReadOnlyList<string> recipients, string? comment, CancellationToken ct = default)
    {
        var sent = 0;
        foreach (var r in recipients.Distinct())
        {
            var blob = await items.UploadAsync(teamId, r, new MemoryStream(Data), ct);
            var attachments = new JsonArray(new JsonObject { ["blobRef"] = blob.BlobRef, ["key"] = "hex:" + blob.Key });
            sent += (await items.SendAsync(teamId, new TeamItemDraft(Kind, Body(blob), Context: Context(comment), Attachments: attachments), [r], ct)).Count;
        }
        return sent;
    }
}

/// <summary>Pages as a collection: the links go first, the collection names the recipient's copies of them.</summary>
internal sealed record CollectionContent(string Title, IReadOnlyList<SharedLink> Links) : ShareContent(TeamItemKinds.Collection, Title)
{
    public override async Task<int> SendAsync(ITeamItems items, string teamId, IReadOnlyList<string> recipients, string? comment, CancellationToken ct = default)
    {
        var sent = 0;
        foreach (var r in recipients.Distinct())
        {
            var keys = new JsonArray();
            foreach (var l in Links)
            {
                var copy = (await items.SendAsync(teamId, TeamShare.LinkDraft(l.Url, l.Title), [r], ct)).Single();
                keys.Add(new JsonArray("hex:" + TeamShare.AuthorOf(copy, items, teamId), "hex:" + copy.ItemId));
            }
            var body = new JsonObject { ["itemKeys"] = keys };
            if (TeamShare.CollectionTitle(Title) is { } title) body["title"] = title;
            sent += (await items.SendAsync(teamId, new TeamItemDraft(Kind, body, Context: Context(comment)), [r], ct)).Count;
        }
        return sent;
    }
}

/// <summary>
/// P5 resources over team items (<see cref="ITeamItems"/>), in the core's closed schemas (itemop.rs): link {url, title?};
/// file {name, blobRef, size, sha256, mime?}; skill {manifest, blobRef, version, name?, size?, sha256?} (the folder as a
/// zip); snapshot {blobRef, size?, sha256?, sourceUrl?}; edit {baseCommit, patch, repoFingerprint, size?}; collection {itemKeys, title?}; a comment
/// is the item's context text. Also the Inbox (resources addressed to me) and Sent with each recipient's status, and what
/// Accept does with a file or a skill on disk.
/// </summary>
public static class TeamShare
{
    /// <summary>The largest file, skill or screenshot shared (the server's blob limit is 100 MiB).</summary>
    public const int MaxContentBytes = 50 * 1024 * 1024;

    /// <summary>Most files in a received skill.</summary>
    public const int MaxSkillEntries = 1000;

    /// <summary>Most bytes a received skill unpacks to.</summary>
    public const long MaxSkillUnpackedBytes = 2L * MaxContentBytes;

    /// <summary>An edit's patch travels inside the signed op (≤ 64 KiB with its header).</summary>
    public const int MaxPatchBytes = 48 * 1024;

    public const int MaxCommentChars = 280;

    /// <summary>A collection's name (ombra-core: 1–200 Unicode scalar values).</summary>
    public const int MaxCollectionTitleChars = 200;

    /// <summary>The name a collection carries: trimmed, cut to <see cref="MaxCollectionTitleChars"/>; null when empty.</summary>
    public static string? CollectionTitle(string? title)
    {
        var s = title?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        var runes = s.EnumerateRunes().ToList();
        if (runes.Count > MaxCollectionTitleChars) s = string.Concat(runes.Take(MaxCollectionTitleChars).Select(r => r.ToString()));
        return Text(s);
    }

    // ───────────── What to share ─────────────

    public static ShareContent Link(string url, string? title = null) =>
        new InlineContent(TeamItemKinds.Link, title is { Length: > 0 } ? title : url, LinkBody(url, title));

    internal static TeamItemDraft LinkDraft(string url, string? title) => new(TeamItemKinds.Link, LinkBody(url, title));

    private static JsonObject LinkBody(string url, string? title)
    {
        var body = new JsonObject { ["url"] = Text(url) };
        if (title is { Length: > 0 } && title != url) body["title"] = Text(title);
        return body;
    }

    public static ShareContent Collection(string title, IReadOnlyList<SharedLink> links) =>
        links.Count == 0 ? throw new ArgumentException("no pages", nameof(links)) : new CollectionContent(title, links);

    public static ShareContent File(string name, byte[] data, string? mime = null)
    {
        Check(data.Length);
        return new BlobContent(TeamItemKinds.File, name, data, b =>
        {
            var body = new JsonObject { ["name"] = Text(name), ["blobRef"] = b.BlobRef, ["size"] = b.Size, ["sha256"] = b.Sha256 };
            if (mime is { Length: > 0 }) body["mime"] = mime;
            return body;
        });
    }

    public static ShareContent FileFrom(string path)
    {
        Check(new FileInfo(path).Length);
        return File(Path.GetFileName(path), System.IO.File.ReadAllBytes(path));
    }

    /// <summary>A screenshot (or any picture) of a page.</summary>
    public static ShareContent Snapshot(string title, byte[] image, string? sourceUrl = null)
    {
        Check(image.Length);
        return new BlobContent(TeamItemKinds.Snapshot, title, image, b =>
        {
            var body = new JsonObject { ["blobRef"] = b.BlobRef, ["size"] = b.Size, ["sha256"] = b.Sha256 };
            if (sourceUrl is { Length: > 0 }) body["sourceUrl"] = Text(sourceUrl);
            return body;
        });
    }

    public static ShareContent Skill(string name, IReadOnlyList<SharedFile> files)
    {
        var manifest = files.FirstOrDefault(f => f.Path == "SKILL.md") ?? throw new InvalidDataException("the skill has no SKILL.md");
        using var zip = new MemoryStream();
        using (var a = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var f in files)
            {
                using var s = a.CreateEntry(f.Path, CompressionLevel.Optimal).Open();
                s.Write(f.Data);
            }
        var bytes = zip.ToArray();
        Check(bytes.Length);
        var version = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
        return new BlobContent(TeamItemKinds.Skill, name, bytes, b => new JsonObject
        {
            ["manifest"] = Text(Encoding.UTF8.GetString(manifest.Data)), ["blobRef"] = b.BlobRef, ["version"] = version,
            ["name"] = Text(name), ["size"] = b.Size, ["sha256"] = b.Sha256,
        });
    }

    /// <summary>A skill folder (the one with SKILL.md) with every file under it; hidden files and folders are left out.</summary>
    public static ShareContent SkillFrom(string folder)
    {
        if (!System.IO.File.Exists(Path.Combine(folder, "SKILL.md"))) throw new FileNotFoundException("no SKILL.md", Path.Combine(folder, "SKILL.md"));
        var files = new List<SharedFile>();
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(folder, path).Replace('\\', '/');
            if (rel.Split('/').Any(p => p.StartsWith('.'))) continue;
            Check(total += new FileInfo(path).Length);
            files.Add(new SharedFile(rel, System.IO.File.ReadAllBytes(path)));
        }
        return Skill(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), files);
    }

    /// <summary>
    /// Changes as <c>git diff</c> prints them against HEAD of <paramref name="repo"/>: the repository's fingerprint and
    /// that commit are signed with the patch, so the recipient applies it only to a clone of the same repository.
    /// </summary>
    public static ShareContent Edit(string title, string patch, RepoIdentity repo)
    {
        var bytes = Encoding.UTF8.GetByteCount(patch);
        if (bytes > MaxPatchBytes) throw new TeamShareTooLargeException(bytes, MaxPatchBytes);
        return new InlineContent(TeamItemKinds.Edit, title, new JsonObject
        {
            ["baseCommit"] = repo.Commit, ["patch"] = Text(patch), ["repoFingerprint"] = repo.Fingerprint, ["size"] = (ulong)bytes,
        });
    }

    private static void Check(long bytes)
    {
        if (bytes > MaxContentBytes) throw new TeamShareTooLargeException(bytes);
    }

    // ───────────── Snippets and notes ─────────────
    // The core's kinds are closed (itemop.rs), so a snippet or a note travels as a file whose mime names it: its JSON is
    // the blob. A client that does not know the mime shows a file and saves the JSON on Accept.

    public const string SnippetKind = "snippet";
    public const string NoteKind = "note";
    public const string SnippetMime = "application/vnd.ombra.snippet+json";
    public const string NoteMime = "application/vnd.ombra.note+json";
    public const string NoteFromReply = "reply";
    public const string NoteFromComment = "comment";

    /// <summary>The most text a snippet or a note carries (UTF-8 bytes).</summary>
    public const int MaxSnippetBytes = 200 * 1024;
    public const int MaxNoteBytes = 200 * 1024;

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>What a snippet is called: <c>Program.cs:10-20</c> (one line: <c>Program.cs:10</c>).</summary>
    public static string SnippetName(SharedSnippet s) =>
        System.IO.Path.GetFileName(s.Path) + ":" + (s.EndLine > s.StartLine ? $"{s.StartLine}-{s.EndLine}" : $"{s.StartLine}");

    public static ShareContent Snippet(SharedSnippet s)
    {
        if (RelativePath(s.Path) is not { } path) throw new ArgumentException("the snippet's path must be relative to the project", nameof(s));
        if (s.StartLine < 1 || s.EndLine < s.StartLine) throw new ArgumentException("bad line range", nameof(s));
        var bytes = Encoding.UTF8.GetByteCount(s.Text);
        if (bytes > MaxSnippetBytes) throw new TeamShareTooLargeException(bytes, MaxSnippetBytes);
        var snippet = s with { Path = path };
        return File(SnippetName(snippet), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snippet, Json), SnippetMime);
    }

    public static ShareContent Note(SharedNote n)
    {
        if (n.Source is not (NoteFromReply or NoteFromComment)) throw new ArgumentException("unknown note source", nameof(n));
        var bytes = Encoding.UTF8.GetByteCount(n.Text);
        if (bytes > MaxNoteBytes) throw new TeamShareTooLargeException(bytes, MaxNoteBytes);
        var title = CollectionTitle(n.Title) ?? "note";
        return File(title, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(n with { Title = title }, Json), NoteMime);
    }

    /// <summary>A received snippet's JSON; the path must stay inside a project, the text within the cap.</summary>
    public static SharedSnippet ReadSnippet(byte[] data)
    {
        if (data.Length > MaxSnippetBytes * 2) throw new InvalidDataException("the snippet is too large");
        var s = Deserialize<SharedSnippet>(data);
        if (s.Text is null || s.Path is null || RelativePath(s.Path) is not { } path || s.StartLine < 1 || s.EndLine < s.StartLine
            || Encoding.UTF8.GetByteCount(s.Text) > MaxSnippetBytes) throw new InvalidDataException("not a snippet");
        return s with { Path = path };
    }

    public static SharedNote ReadNote(byte[] data)
    {
        if (data.Length > MaxNoteBytes * 2) throw new InvalidDataException("the note is too large");
        var n = Deserialize<SharedNote>(data);
        if (n.Text is null || n.Title is null || n.Source is not (NoteFromReply or NoteFromComment)
            || Encoding.UTF8.GetByteCount(n.Text) > MaxNoteBytes) throw new InvalidDataException("not a note");
        return n.Url is { } u && !(Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") ? n with { Url = null } : n;
    }

    private static T Deserialize<T>(byte[] data)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException("empty"); }
        catch (System.Text.Json.JsonException e) { throw new InvalidDataException(e.Message, e); }
    }

    /// <summary>A path inside a project with '/': no root, no drive, no '..'; null otherwise.</summary>
    public static string? RelativePath(string path)
    {
        var p = path.Replace('\\', '/').Trim();
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => x != ".").ToList();
        if (p.StartsWith('/') || p.Contains(':') || parts.Count == 0 || parts.Any(x => x == "..")) return null;
        return string.Join('/', parts);
    }

    public static string? Mime(TeamItem i) => S(i.Body, "mime");

    /// <summary>The item's kind as the user sees it: a file carrying a snippet or a note is one.</summary>
    public static string ShownKind(TeamItem i) => i.Kind == TeamItemKinds.File
        ? Mime(i) switch { SnippetMime => SnippetKind, NoteMime => NoteKind, _ => i.Kind }
        : i.Kind;

    /// <summary>Writes a snippet's text into <paramref name="folder"/> under its file's name (a free one), atomically; the path.</summary>
    public static string SaveSnippet(SharedSnippet s, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = FreePath(folder, SafeName(System.IO.Path.GetFileName(s.Path)), isFolder: false);
        System.IO.File.WriteAllText(path + ".part", s.Text);
        System.IO.File.Move(path + ".part", path);
        return path;
    }

    /// <summary>Text for the core's JSON→CBOR: a string that starts with <c>hex:</c> would become bytes, so it gets a zero-width prefix.</summary>
    internal static string Text(string s) => s.StartsWith("hex:", StringComparison.Ordinal) ? "\u200B" + s : s;

    internal static string AuthorOf(TeamItemCopy copy, ITeamItems items, string teamId) =>
        items.List(teamId).FirstOrDefault(i => i.ItemId == copy.ItemId && i.RecipientMemberId == copy.RecipientMemberId)?.AuthorMemberId
        ?? throw new InvalidOperationException("the link just sent is not in the replica");

    // ───────────── Reading ─────────────

    private static string? S(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.TrimStart('\u200B') : null;

    public static string? Url(TeamItem i) => S(i.Body, "url");
    public static string? FileName(TeamItem i) => S(i.Body, "name");
    public static string? PatchText(TeamItem i) => S(i.Body, "patch");
    public static string? BaseCommit(TeamItem i) => S(i.Body, "baseCommit") is { Length: > 0 } b ? b : null;
    /// <summary>The repository an edit is for (<see cref="RepoIdentity.Fingerprint"/>); null for one that does not say.</summary>
    public static string? RepoFingerprint(TeamItem i) => S(i.Body, "repoFingerprint") is { Length: > 0 } r ? r : null;
    public static string? Manifest(TeamItem i) => S(i.Body, "manifest");
    public static string? SourceUrl(TeamItem i) => S(i.Body, "sourceUrl");
    public static string? Comment(TeamItem i) => S(i.Context, "text");
    public static ulong Size(TeamItem i) => i.Body?["size"] is JsonValue v && v.TryGetValue<ulong>(out var n) ? n : 0;

    /// <summary>The files a patch changes.</summary>
    public static IReadOnlyList<string> PatchFiles(TeamItem i) =>
        (PatchText(i) ?? "").Split('\n').Where(l => l.StartsWith("+++ b/", StringComparison.Ordinal)).Select(l => l[6..].TrimEnd('\r')).Distinct().ToList();

    /// <summary>The skill's description (SKILL.md front matter), if it has one.</summary>
    public static string? Description(TeamItem i) =>
        Manifest(i)?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("description:", StringComparison.Ordinal))?[12..].Trim();

    public static string Title(TeamItem i) => i.Kind switch
    {
        TeamItemKinds.Link => S(i.Body, "title") ?? Url(i) ?? "",
        TeamItemKinds.File or TeamItemKinds.Skill => FileName(i) ?? i.Kind,
        TeamItemKinds.Snapshot => SourceUrl(i) ?? i.Kind,
        TeamItemKinds.Edit => PatchFiles(i) is { Count: > 0 } f ? string.Join(", ", f.Take(3)) + (f.Count > 3 ? $" +{f.Count - 3}" : "") : i.Kind,
        TeamItemKinds.Collection => S(i.Body, "title") ?? (i.Body?["itemKeys"] as JsonArray)?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
        _ => i.Kind,
    };

    /// <summary>The items a collection names, as they are in <paramref name="all"/>.</summary>
    public static IReadOnlyList<TeamItem> Members(TeamItem collection, IEnumerable<TeamItem> all)
    {
        if (collection.Body?["itemKeys"] is not JsonArray keys) return [];
        var wanted = keys.OfType<JsonArray>().Select(k => (Hex(k[0]), Hex(k[1]))).ToList();
        var byKey = all.GroupBy(i => (i.AuthorMemberId, i.ItemId)).ToDictionary(g => g.Key, g => g.First());
        return wanted.Select(k => byKey.GetValueOrDefault(k)).OfType<TeamItem>().ToList();
    }

    private static string Hex(JsonNode? n) => (n?.GetValue<string>() ?? "") is var s && s.StartsWith("hex:", StringComparison.Ordinal) ? s[4..] : s ?? "";

    /// <summary>The pages of a link or a collection.</summary>
    public static IReadOnlyList<SharedLink> Links(TeamItem i, IEnumerable<TeamItem> all) => i.Kind switch
    {
        TeamItemKinds.Link when Url(i) is { } u => [new SharedLink(u, S(i.Body, "title"))],
        TeamItemKinds.Collection => Members(i, all).Where(m => m.Kind == TeamItemKinds.Link && Url(m) is not null).Select(m => new SharedLink(Url(m)!, S(m.Body, "title"))).ToList(),
        _ => [],
    };

    // ───────────── Inbox and Sent ─────────────

    /// <summary>Resources others sent me, newest first; the links of a collection show as the collection.</summary>
    public static IReadOnlyList<TeamItem> Incoming(IReadOnlyList<TeamItem> items, string me)
    {
        var mine = items.Where(i => i.IsResource && !i.Deleted && i.AuthorMemberId != me && (i.RecipientMemberId is null || i.RecipientMemberId == me)).ToList();
        var inCollections = mine.Where(i => i.Kind == TeamItemKinds.Collection).SelectMany(c => Members(c, mine)).Select(m => (m.AuthorMemberId, m.ItemId)).ToHashSet();
        return mine.Where(i => !inCollections.Contains((i.AuthorMemberId, i.ItemId))).OrderByDescending(i => i.CreatedAt).ToList();
    }

    /// <summary>My answer to an item (accepted, declined), null while there is none.</summary>
    public static string? MyStatus(TeamItem i, string me) => i.Statuses.FirstOrDefault(s => s.RecipientMemberId == me)?.Status;

    public static bool Seen(TeamItem i, string me) => i.Statuses.FirstOrDefault(s => s.RecipientMemberId == me)?.Seen == true;

    /// <summary>Still waiting for me: neither accepted nor declined.</summary>
    public static bool IsPending(TeamItem i, string me) => MyStatus(i, me) is not (TeamItemStatuses.Accepted or TeamItemStatuses.Declined);

    /// <summary>The resources I sent, one row per share (same kind, time and title) with every recipient's copy and status, newest first.</summary>
    public static IReadOnlyList<SentShare> Sent(IReadOnlyList<TeamItem> items, string me)
    {
        var mine = items.Where(i => i.IsResource && !i.Deleted && i.AuthorMemberId == me && i.RecipientMemberId is not null).ToList();
        var inCollections = mine.Where(i => i.Kind == TeamItemKinds.Collection).SelectMany(c => Members(c, mine)).Select(m => (m.AuthorMemberId, m.ItemId)).ToHashSet();
        return mine.Where(i => !inCollections.Contains((i.AuthorMemberId, i.ItemId)))
            .GroupBy(i => (Kind: ShownKind(i), i.CreatedAt, Title(i)))
            .Select(g => new SentShare($"{g.Key.Kind}/{g.Key.CreatedAt}/{g.Key.Item3}", g.Key.Kind, g.Key.Item3, g.Key.CreatedAt, g.Select(i =>
            {
                var s = i.Statuses.FirstOrDefault(x => x.RecipientMemberId == i.RecipientMemberId);
                return new SentCopy(i.RecipientMemberId!, s?.Seen == true, s?.Status, s?.Note);
            }).ToList()))
            .OrderByDescending(s => s.CreatedAt).ToList();
    }

    // ───────────── Accept ─────────────

    /// <summary>Writes an accepted file's bytes into <paramref name="folder"/> under its name (a free one: "name (2).ext"), atomically; the path.</summary>
    public static string SaveFile(TeamItem item, byte[] data, string folder)
    {
        if (item.Kind is not (TeamItemKinds.File or TeamItemKinds.Snapshot)) throw new InvalidOperationException("not a file");
        var name = SafeName(FileName(item) ?? (item.Kind == TeamItemKinds.Snapshot ? "screenshot.jpg" : "shared"));
        Directory.CreateDirectory(folder);
        var path = FreePath(folder, name, isFolder: false);
        var tmp = path + ".part";
        System.IO.File.WriteAllBytes(tmp, data);
        System.IO.File.Move(tmp, path);
        return path;
    }

    /// <summary>
    /// Installs an accepted skill (its zip) as a folder under <paramref name="skillsRoot"/> (a free name); the folder.
    /// The zip is the sender's: at most <see cref="MaxSkillEntries"/> entries and <see cref="MaxSkillUnpackedBytes"/>
    /// unpacked, counted on what is actually written, not only on what the zip claims. On any failure nothing stays.
    /// </summary>
    public static string InstallSkill(TeamItem item, byte[] zip, string skillsRoot)
    {
        if (item.Kind != TeamItemKinds.Skill) throw new InvalidOperationException("not a skill");
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxSkillEntries) throw new InvalidDataException($"the skill has more than {MaxSkillEntries} files");
        if (archive.Entries.All(e => e.FullName != "SKILL.md")) throw new InvalidDataException("the skill has no SKILL.md");
        if (archive.Entries.Sum(e => e.Length) > MaxSkillUnpackedBytes) throw new InvalidDataException($"the skill unpacks to more than {MaxSkillUnpackedBytes} bytes");
        Directory.CreateDirectory(skillsRoot);
        var folder = FreePath(skillsRoot, SafeName(FileName(item) ?? "skill"), isFolder: true);
        var staging = folder + ".part";
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        // Every path is checked before anything is written: a skill with a path out of its folder installs nothing.
        var targets = archive.Entries.Where(e => e.Name.Length > 0).Select(e =>
        {
            var target = Path.GetFullPath(Path.Combine(staging, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (Path.IsPathRooted(e.FullName) || !target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("a path leaves the skill's folder: " + e.FullName);
            return (e, target);
        }).ToList();
        try
        {
            long left = MaxSkillUnpackedBytes;
            var buffer = new byte[81920];
            foreach (var (e, target) in targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var from = e.Open();
                using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                for (int n; (n = from.Read(buffer)) > 0;)
                {
                    if ((left -= n) < 0) throw new InvalidDataException($"the skill unpacks to more than {MaxSkillUnpackedBytes} bytes");
                    to.Write(buffer, 0, n);
                }
            }
            Directory.Move(staging, folder); // the folder appears whole, or not at all
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return folder;
    }

    private static string SafeName(string name)
    {
        var n = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().TrimStart('.');
        return n.Length == 0 ? "shared" : n;
    }

    private static string FreePath(string folder, string name, bool isFolder)
    {
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isFolder ? "" : Path.GetExtension(name);
        var path = Path.Combine(folder, name);
        for (var n = 2; System.IO.File.Exists(path) || Directory.Exists(path) || Directory.Exists(path + ".part"); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
        return path;
    }
}
