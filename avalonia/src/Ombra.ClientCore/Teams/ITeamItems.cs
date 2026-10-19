using System.Text.Json.Nodes;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// Item kinds of SERVER_DESIGN §3.0 ред. 10 (closed CBOR schemas in ombra-core itemop.rs; unknown fields are rejected).
/// Bodies: link {url, title?}; file {name, blobRef, size, sha256, mime?}; skill {manifest, blobRef, version, name?, source?,
/// size?, sha256?}; snapshot {blobRef, size?, sha256?, width?, height?, sourceUrl?}; edit {baseCommit, patch,
/// repoFingerprint?, files?, size?, sha256?}; collection {itemKeys}; mark — no body, an anchor
/// ({web:{url,selector,textQuote,viewport?}} | {code:{repoFingerprint,commit,path,range,excerpt?}} | {doc:{fileRef,locator,excerpt?}})
/// and optional context {text ≤ 2000 chars, selection?, blobRefs?}. Signed op ≤ 64 KiB.
/// </summary>
public static class TeamItemKinds
{
    public const string Link = "link";
    public const string File = "file";
    public const string Skill = "skill";
    public const string Snapshot = "snapshot";
    public const string Edit = "edit";
    public const string Collection = "collection";
    public const string Mark = "mark";
}

/// <summary>Recipient statuses. Resources: seen, accepted, declined (first final wins, never changes). Marks: seen, done, dismissed (last wins).</summary>
public static class TeamItemStatuses
{
    public const string Seen = "seen";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Done = "done";
    public const string Dismissed = "dismissed";
}

/// <summary>One recipient's status of an item.</summary>
public sealed record TeamItemStatus(string RecipientMemberId, bool Seen, string? Status, string? Note);

/// <summary>This device's own edit that lost to the same rev from another device of the author (banner + copy of the text).</summary>
public sealed record TeamItemReplaced(ulong Rev, string ByDeviceId, JsonNode? Anchor, JsonNode? Context, JsonNode? Body);

/// <summary>Replica view of one item (core JSON of <c>item.get</c>/<c>item.list</c>). Bytes inside Anchor/Context/Body are hex strings.</summary>
public sealed record TeamItem(
    string TeamId, string AuthorMemberId, string ItemId, string Kind, ulong Rev, ulong CreatedAt,
    string? SpaceId, string? RecipientMemberId, bool Deleted,
    JsonNode? Anchor, JsonNode? Context, JsonNode? Body, JsonNode? Attachments,
    IReadOnlyList<TeamItemStatus> Statuses, TeamItemReplaced? Replaced)
{
    public bool IsResource => Kind != TeamItemKinds.Mark;
}

/// <summary>New item content. Values follow core JSON→CBOR: strings starting with <c>hex:</c> are bytes, other strings text, unsigned ints, objects, arrays.</summary>
public sealed record TeamItemDraft(string Kind, JsonNode? Body = null, JsonNode? Anchor = null, JsonNode? Context = null, JsonNode? Attachments = null);

/// <summary>Result of addressing an item: one independent copy per recipient (R5-10), each in the author–recipient pair group.</summary>
public sealed record TeamItemCopy(string RecipientMemberId, string GroupId, string ItemId, string OpId);

/// <summary>Outcome of one delivery round (sync loop to More=false + outbox flush).</summary>
public sealed record TeamDeliveryResult(int Received, int Sent, int Rejected, IReadOnlyList<string> SecurityEvents);

/// <summary>
/// An uploaded encrypted blob (C3): put <see cref="BlobRef"/>, <see cref="Size"/>, <see cref="Sha256"/> into the item body
/// and <c>attachments[{blobRef, key: "hex:"+Key}]</c> next to it (the key travels only inside the E2EE op).
/// </summary>
public sealed record TeamBlob(string BlobRef, ulong Size, string Sha256, string Key)
{
    /// <summary>The key of <paramref name="blobRef"/> from an item's attachments (hex, with or without the core's <c>hex:</c>).</summary>
    public static byte[]? KeyOf(TeamItem item, string blobRef) =>
        item.Attachments is JsonArray a
            ? a.OfType<JsonObject>().Where(o => o["blobRef"]?.GetValue<string>() == blobRef)
                .Select(o => o["key"]?.GetValue<string>()).OfType<string>()
                .Select(k => Convert.FromHexString(k.StartsWith("hex:", StringComparison.Ordinal) ? k[4..] : k)).FirstOrDefault()
            : null;
}

/// <summary>
/// Team items over ombra-core (C0, design-sync-decision §1–§6). Own operations are applied to the local replica at once
/// (also offline) and queued in the durable outbox; <see cref="DeliverAsync"/> sends the outbox and runs the sync loop.
/// Item keys are <c>(authorMemberId, itemId)</c>; ids are lowercase hex.
/// </summary>
public interface ITeamItems
{
    /// <summary>Raised after local replica changes (own enqueue, received ops). Argument: teamId.</summary>
    event Action<string>? ItemsChanged;

    /// <summary>Address an item to members: creates missing pair groups, queues one copy per recipient.</summary>
    Task<IReadOnlyList<TeamItemCopy>> SendAsync(string teamId, TeamItemDraft draft, IReadOnlyList<string> recipientMemberIds, CancellationToken ct = default);

    /// <summary>Edit an own item (author only, by rev). Fields left null are unchanged.</summary>
    Task EditAsync(string teamId, string itemId, ulong baseRev, TeamItemDraft delta, CancellationToken ct = default);

    /// <summary>Delete (tombstone) an own item.</summary>
    Task DeleteAsync(string teamId, string authorMemberId, string itemId, CancellationToken ct = default);

    /// <summary>Set this member's status of an item addressed to it (see <see cref="TeamItemStatuses"/>).</summary>
    Task SetStatusAsync(string teamId, string authorMemberId, string itemId, string status, string? note = null, CancellationToken ct = default);

    /// <summary>Items of a team, newest first.</summary>
    IReadOnlyList<TeamItem> List(string teamId, int limit = 200, ulong? before = null);

    /// <summary>One item, or null.</summary>
    TeamItem? Get(string teamId, string authorMemberId, string itemId);

    /// <summary>Operations still in the outbox (not yet accepted by the server).</summary>
    int PendingCount(string? teamId = null);

    /// <summary>Send the outbox and sync until More=false. Safe to call repeatedly; survives crashes (persistent cursor, receipts).</summary>
    Task<TeamDeliveryResult> DeliverAsync(CancellationToken ct = default);

    /// <summary>
    /// C3: seal <paramref name="content"/> in AES-256-GCM chunks with a fresh key and upload it, scoped to the pair group
    /// with <paramref name="recipientMemberId"/> (only the two members' devices may fetch it).
    /// </summary>
    Task<TeamBlob> UploadAsync(string teamId, string recipientMemberId, Stream content, CancellationToken ct = default);

    /// <summary>C3: download, open and verify (size + SHA-256 of the signed body) the blob of an item this member accepted.</summary>
    Task<byte[]> DownloadAsync(TeamItem item, CancellationToken ct = default);
}
