namespace Ombra.Server.Contracts;

// JSON DTOs of the HTTPS API (§6.1). byte[] travels as base64; 32-byte team/group ids as lowercase hex.

public static class Protocol
{
    /// <summary>Current protocol major version. The server accepts current and previous major (§1 "Обновления").</summary>
    public const int Current = 1;
    public const int Minimum = Current - 1 < 1 ? 1 : Current - 1;
}

public static class Reasons
{
    // 403
    public const string Removed = "removed";
    public const string Revoked = "revoked";
    public const string Blocked = "blocked";
    public const string NotAdmin = "not-admin";
    public const string NotOwner = "not-owner";
    public const string Quarantine = "quarantine";
    public const string Frozen = "frozen";
    public const string NotMember = "not-member";
    public const string NotTarget = "not-target";
    public const string Forbidden = "forbidden";
    // 409
    public const string EpochConflict = "epoch-conflict";
    public const string ClosedGeneration = "closed-generation";
    public const string RosterConflict = "roster-conflict";
    public const string Equivocation = "equivocation";
    public const string CommitRejected = "commit-rejected";
    public const string Revision = "revision";
    public const string OpIdReused = "op-id-reused";
    public const string DeviceIdReused = "device-id-reused";
    public const string MemberIdReused = "member-id-reused";
    public const string TransferActive = "transfer-active";
    public const string Exists = "exists";
    // 410 / 426 / 429 / 4xx
    public const string CursorExpired = "cursor-expired";
    public const string UpgradeRequired = "upgrade-required";
    public const string RateLimited = "rate-limited";
    public const string SenderQuota = "sender-quota";
    public const string TooLarge = "too-large";
    public const string TeamQuota = "team-quota";
    public const string BadRequest = "bad-request";
    public const string RosterRejected = "roster-rejected";
    public const string BadSignature = "bad-signature";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not-found";
    public const string RestoreInProgress = "restore-in-progress";
    public const string BootstrapInvalid = "bootstrap-invalid";
    public const string PinMismatch = "pin-mismatch";
    public const string InviteInvalid = "invite-invalid";
}

public sealed record ErrorResponse(string Reason, string? Detail = null, IReadOnlyList<byte[]>? RemovalNotices = null);

public sealed record HealthResponse(string Status, Guid ServerId, int ProtocolCurrent, int ProtocolMinimum, bool RestoreInProgress);

public sealed record ChallengeRequest(Guid DeviceId);
public sealed record ChallengeResponse(Guid ServerId, byte[] Nonce, DateTimeOffset ExpiresAt);

/// <summary>Signature = Ed25519(identityKey, hello input of ombra-core over serverId, nonce, deviceId, time).</summary>
public sealed record HelloRequest(int ProtocolVersion, Guid DeviceId, byte[] Nonce, ulong Time, byte[] Signature);

/// <summary>
/// First device of the instance (§2.4): one-time bootstrap code + SPKI pin printed by install.sh, plus proof of
/// identityKey possession in hello format over a fresh challenge.
/// </summary>
public sealed record BootstrapRequest(
    string Code, string? SpkiPin, Guid MemberId, Guid DeviceId, string Product,
    byte[] IdentityKey, byte[] MlsSignatureKey, byte[] Nonce, ulong Time, byte[] Signature, int ProtocolVersion);

public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt);

/// <param name="RemovalNotice">§5.3: when <c>AccessDenied</c> is <c>removed</c>, the admin-signed notice (if published).</param>
public sealed record TeamStatus(string TeamId, long AccessRevision, long RosterVersion, bool Quarantine, string? QuarantineReleasedBy, string? Frozen, string? AccessDenied,
    byte[]? RemovalNotice = null);

public sealed record HelloResponse(Guid ServerId, Guid MemberId, bool ServerOwner, TokenResponse Tokens, IReadOnlyList<TeamStatus> Teams);

public sealed record RefreshRequest(string RefreshToken);

public sealed record OpPutResponse(long Seq, bool Duplicate, int Recipients);

/// <summary>Ack by the device of the token only; the key of a queue row is (groupId, senderDeviceId, opId).</summary>
public sealed record AckItem(Guid SenderDeviceId, Guid OpId);
public sealed record AckRequest(IReadOnlyList<AckItem> Ops);
public sealed record AckResponse(int Acked, int Deleted);

/// <summary><c>Kind</c>: <c>op</c> (application message), <c>commit</c> (handshake for existing leaves), <c>welcome</c> (for added devices).</summary>
public sealed record SyncOp(long Seq, string GroupId, Guid SenderDeviceId, Guid OpId, ulong Epoch, byte[] Ciphertext, string Kind);
public sealed record SyncRoster(string TeamId, long Version, byte[] RosterUpdate);
/// <summary>R2-08 evidence for the owner's decision: the two signed RosterUpdates with number <c>Version</c>
/// (<c>Known</c> is in the chain, <c>Conflicting</c> froze the team).</summary>
public sealed record RosterEquivocation(string TeamId, long Version, byte[] Known, byte[] Conflicting);
public sealed record SyncBlock(string TeamId, Guid? MemberId, Guid? DeviceId, string By, DateTimeOffset At, Guid AuditId);
public sealed record SyncPendingRotation(Guid RotationId, string TeamId, Guid? BlockedMemberId, Guid? BlockedDeviceId, Guid AuditId, IReadOnlyList<string> Groups, DateTimeOffset CreatedAt);

/// <summary>
/// Delta: queue rows where this device is pending, roster versions, blocks and pending rotations of its teams.
/// <c>Cursor</c> is opaque. When <c>More</c> is false the next cursor restarts the queue scan, so rows that are still
/// pending (not acked) are delivered again on the next pass — the queue is "until ack".
/// </summary>
public sealed record SyncResponse(
    string Cursor, bool More, bool Snapshot,
    IReadOnlyList<SyncOp> Ops, IReadOnlyList<SyncRoster> Roster, IReadOnlyList<SyncBlock> Blocks,
    IReadOnlyList<SyncPendingRotation> PendingRotations, IReadOnlyList<string> QuarantinedTeams, IReadOnlyList<string> FrozenTeams);

/// <summary>POST /v1/groups/{gid}/commit: one PublicMessage commit and, when it adds devices, the Welcome for them.</summary>
public sealed record CommitRequest(byte[] Commit, byte[]? Welcome);
public sealed record GroupResponse(string GroupId, long Epoch, IReadOnlyList<Guid> Leaves);
public sealed record KeyPackageUpload(IReadOnlyList<byte[]> KeyPackages);
public sealed record KeyPackageUploadResponse(int Stored);
public sealed record KeyPackageResponse(Guid DeviceId, byte[] KeyPackage, bool LastResort);

public sealed record TransferInfo(Guid TransferId, Guid SenderDeviceId, Guid TargetDeviceId, long Size, byte[] Sha256, int TotalChunks, int UploadedChunks, DateTimeOffset ExpiresAt);

public sealed record BlobPutResponse(Guid BlobId, long Size, bool Duplicate);

public sealed record InviteResponse(Guid InviteId, string TeamId, byte[] SignedInvite, long RosterHeadVersion, byte[] RosterHeadHash, string Role, DateTimeOffset ExpiresAt, IReadOnlyList<SyncRoster> Chain);
public sealed record JoinResponse(Guid RequestId);
public sealed record PendingJoin(Guid RequestId, Guid InviteId, string TeamId, Guid MemberId, byte[] JoinEnvelope, byte[] JoinSignature, DateTimeOffset At);
public sealed record PendingResponse(IReadOnlyList<SyncPendingRotation> Rotations, IReadOnlyList<PendingJoin> Joins);

// §2.3 pairing channel (SAS commit-then-reveal), DeviceCertificate registration, DeviceRevocation.

/// <summary>Opened by an existing (authenticated) device: its commitment to its nonce and keys.</summary>
public sealed record PairOpenRequest(byte[] Commitment);
public sealed record PairOpenResponse(string Code, Guid MemberId, DateTimeOffset ExpiresAt);
/// <summary>The new device's keys and nonce, in clear, before the existing device reveals its nonce.</summary>
public sealed record PairJoinRequest(Guid DeviceId, string Product, byte[] IdentityKey, byte[] MlsSignatureKey, byte[] Nonce);
public sealed record PairRevealRequest(byte[] Nonce);
public sealed record PairSession(string Code, Guid MemberId, Guid ExistingDeviceId, byte[] ExistingIdentityKey, byte[] ExistingMlsSignatureKey,
    byte[] ExistingCommitment, Guid? NewDeviceId, byte[]? NewIdentityKey, byte[]? NewMlsSignatureKey, byte[]? NewNonce, byte[]? ExistingNonce,
    bool Certified, DateTimeOffset ExpiresAt);

/// <summary>
/// A DeviceCertificate issued by the requesting device for another device of the same member, plus the issuer-signed
/// grant envelope (<see cref="EnvelopeKinds.DeviceGrant"/>) naming the teams the new device may trust.
/// </summary>
public sealed record DeviceCertificateRequest(byte[] Certificate, string Product, byte[] GrantEnvelope, byte[] GrantSignature, string? PairingCode);
public sealed record DeviceCertificateResponse(Guid DeviceId, Guid MemberId);
/// <summary>
/// A paired device's certificate and its current team grant. The grant is signed by a device of the same member
/// (<see cref="GrantSignerDeviceId"/>, the issuer at pairing and any sibling later, §2.3 п.4).
/// </summary>
public sealed record DeviceRecord(Guid DeviceId, Guid MemberId, byte[] Certificate, Guid IssuerDeviceId, byte[] IssuerIdentityKey,
    byte[] GrantEnvelope, byte[] GrantSignature, Guid GrantSignerDeviceId, byte[] GrantSignerIdentityKey);
public sealed record DeviceRevokeResponse(IReadOnlyList<Guid> Revoked);
/// <summary>A new grant (all teams the device may trust) from a sibling device: envelope kind <c>device.grant</c>, scope = deviceId.</summary>
public sealed record DeviceGrantRequest(byte[] GrantEnvelope, byte[] GrantSignature);
/// <summary>A device of the requesting member: certificate (null for a root registered at bootstrap/join without one).</summary>
public sealed record OwnDevice(Guid DeviceId, byte[]? Certificate, Guid? IssuerDeviceId, bool Revoked, DateTimeOffset CreatedAt);

// §4.1 heartbeat, §4.3 ownership, §5.1 п.6 RemovalNotice.

public sealed record HeartbeatRecord(Guid DeviceId, long RosterVersion, DateTimeOffset Time, byte[] Heartbeat);

/// <summary>OwnershipOffer exactly as ombra-core serialises it (ids and hashes lowercase hex).</summary>
public sealed record OwnershipOffer(string TeamId, string From, string To, ulong RosterVersion, string PrevHash, string NewRoleOfFrom, ulong ExpiresAt);
public sealed record OwnershipSig(string DeviceId, string Sig);
public sealed record OwnershipOfferRequest(OwnershipOffer Offer, OwnershipSig OfferSig);
public sealed record OwnershipOfferResponse(Guid OfferId, OwnershipOffer Offer, OwnershipSig OfferSig);

/// <summary>WSS /v1/stream event. Not an event store: only notifications (§6.1).</summary>
public sealed record StreamEvent(string Type, string? TeamId = null, string? GroupId = null, Guid? DeviceId = null, string? State = null,
    string? Reason = null, Guid? SenderDeviceId = null, byte[]? Ciphertext = null);

public static class StreamEventTypes
{
    public const string Revision = "revision";
    public const string Presence = "presence";
    public const string Pending = "pending";
    public const string Removed = "removed";
    public const string Ephemeral = "ephemeral";
    public const string Ready = "ready";
    /// <summary>A new owner/admin heartbeat of the team (§4.1): fetch GET /v1/teams/{id}/heartbeats.</summary>
    public const string Heartbeat = "heartbeat";
    /// <summary>An OwnershipOffer addressed to this member (§4.3): fetch GET /v1/teams/{id}/ownership.</summary>
    public const string Ownership = "ownership";
}

/// <summary>First client frame on /v1/stream: authenticates the socket with an access token.</summary>
public sealed record StreamAuth(string Type, string Token);
