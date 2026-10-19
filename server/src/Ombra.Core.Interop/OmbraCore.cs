using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Ombra.Core.Interop;

/// <summary>An ombra-core rejection or failure. <see cref="Code"/> is the stable code from the C ABI
/// (e.g. <c>commit-rejected</c>, <c>welcome-rejected</c>, <c>op-rejected</c>, <c>need-roster</c>).</summary>
public sealed class OmbraException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
    public string Detail { get; } = message;
}

internal sealed class OmbraStateHandle() : SafeHandle(0, ownsHandle: true)
{
    public override bool IsInvalid => handle == 0;
    internal ulong Value => (ulong)handle;
    internal void Set(ulong h) => SetHandle((nint)h);
    protected override bool ReleaseHandle() => Native.ombra_state_close((ulong)handle) == 0;
}

/// <summary>JSON plumbing shared by stateful and stateless calls.</summary>
public static class OmbraJson
{
    internal static byte[] Encode(string op, JsonObject? args)
    {
        var o = args is null ? new JsonObject() : (JsonObject)args.DeepClone();
        o["op"] = op;
        return Encoding.UTF8.GetBytes(o.ToJsonString());
    }

    /// <summary>Unwraps <c>{"ok":true,"result":…}</c> or throws <see cref="OmbraException"/>.</summary>
    public static JsonNode Unwrap(JsonObject response)
    {
        if (response["ok"]?.GetValue<bool>() == true) return response["result"] ?? new JsonObject();
        throw new OmbraException(response["code"]?.GetValue<string>() ?? "internal", response["error"]?.GetValue<string>() ?? "");
    }

    internal static JsonObject Parse(byte[] bytes) =>
        JsonNode.Parse(bytes) as JsonObject ?? throw new OmbraException("internal", "ombra-core returned non-object JSON");
}

/// <summary>
/// Encrypted persistent device state (MLS groups, identity key, verified roster chains, replica):
/// one file, AES-256-GCM under a key from the OS key store. Every <see cref="Call"/> is the C ABI
/// <c>verify_and_apply(state, input) -> result</c>: atomic, and a rejected input never changes state.
/// Thread-safe (calls are serialised natively). Dispose closes the native session.
/// </summary>
public sealed class OmbraState : IDisposable
{
    private readonly OmbraStateHandle _h;

    private OmbraState(OmbraStateHandle h) => _h = h;

    /// <param name="path">State file (created if missing).</param>
    /// <param name="key">32-byte key from the OS key store; never written to disk.</param>
    /// <param name="installId">Non-synced install id; a mismatch fails with <c>clone-detected</c>.</param>
    public static unsafe OmbraState Open(string path, ReadOnlySpan<byte> key, ReadOnlySpan<byte> installId)
    {
        if (key.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key));
        var p = Encoding.UTF8.GetBytes(path);
        var h = new OmbraStateHandle();
        OmbraBuf err = default;
        ulong v;
        fixed (byte* pp = p)
        fixed (byte* kp = key)
        fixed (byte* ip = installId)
        {
            v = Native.ombra_state_open(pp, (nuint)p.Length, kp, (nuint)key.Length, ip, (nuint)installId.Length, &err);
        }
        if (v == 0)
        {
            var e = Ffi.Take(err);
            OmbraJson.Unwrap(e.Length == 0 ? new JsonObject { ["ok"] = false, ["code"] = "internal" } : OmbraJson.Parse(e));
            throw new OmbraException("internal", "open failed");
        }
        h.Set(v);
        return new OmbraState(h);
    }

    /// <summary>Full response object (<c>ok</c>, <c>result</c> | <c>code</c>, <c>error</c>).</summary>
    public unsafe JsonObject CallRaw(string op, JsonObject? args = null)
    {
        var req = OmbraJson.Encode(op, args);
        OmbraBuf output = default;
        var added = false;
        _h.DangerousAddRef(ref added);
        try
        {
            int rc;
            fixed (byte* r = req) rc = Native.ombra_state_call(_h.Value, r, (nuint)req.Length, &output);
            if (rc != 0) throw new OmbraException("internal", Ffi.Rc(rc));
            return OmbraJson.Parse(Ffi.Take(output));
        }
        finally
        {
            if (added) _h.DangerousRelease();
        }
    }

    /// <summary>Result node, or <see cref="OmbraException"/> on rejection.</summary>
    public JsonNode Call(string op, JsonObject? args = null) => OmbraJson.Unwrap(CallRaw(op, args));

    public void Dispose() => _h.Dispose();
}

/// <summary>Stateless "public verification" mode (§1): what the server may check without group secrets.</summary>
public static class OmbraPublic
{
    public static unsafe JsonObject CallRaw(string op, JsonObject? args = null)
    {
        var req = OmbraJson.Encode(op, args);
        OmbraBuf output = default;
        int rc;
        fixed (byte* r = req) rc = Native.ombra_call(r, (nuint)req.Length, &output);
        if (rc != 0) throw new OmbraException("internal", Ffi.Rc(rc));
        return OmbraJson.Parse(Ffi.Take(output));
    }

    public static JsonNode Call(string op, JsonObject? args = null) => OmbraJson.Unwrap(CallRaw(op, args));

    public static bool Ed25519Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        Call("ed25519.verify", new() { ["publicKey"] = Hex(publicKey), ["message"] = Hex(message), ["signature"] = Hex(signature) })["valid"]!.GetValue<bool>();

    /// <summary>Canonical-CBOR check of <paramref name="cborBody"/>, then Ed25519 over "ombra/v1/envelope" ‖ 0x00 ‖ body.</summary>
    public static bool VerifyEnvelope(ReadOnlySpan<byte> cborBody, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> identityKey) =>
        Call("envelope.verify", new() { ["body"] = Hex(cborBody), ["signature"] = Hex(signature), ["identityKey"] = Hex(identityKey) })["valid"]!.GetValue<bool>();

    /// <summary>Ed25519 over "ombra/v1/hello" ‖ 0x00 ‖ serverId[16] ‖ nonce[32] ‖ deviceId[16] ‖ time u64 BE.</summary>
    public static bool VerifyHello(ReadOnlySpan<byte> serverId, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> deviceId, ulong time,
        ReadOnlySpan<byte> signature, ReadOnlySpan<byte> identityKey) =>
        Call("hello.verify", new()
        {
            ["serverId"] = Hex(serverId), ["nonce"] = Hex(nonce), ["deviceId"] = Hex(deviceId), ["time"] = time,
            ["signature"] = Hex(signature), ["identityKey"] = Hex(identityKey),
        })["valid"]!.GetValue<bool>();

    /// <summary>
    /// Server-side roster chain verification (§4.1). <paramref name="chainState"/> is the opaque state
    /// returned by the previous call (null for genesis). On equivocation the returned state is frozen. A valid
    /// alternative without the authority to count as equivocation throws <c>roster-conflict</c> (nothing frozen).
    /// </summary>
    public static RosterAppendResult VerifyRosterAppend(byte[]? chainState, byte[] rosterUpdate, ReadOnlySpan<byte> serverId, ulong nowUnix)
    {
        var a = new JsonObject { ["update"] = Hex(rosterUpdate), ["serverId"] = Hex(serverId), ["now"] = nowUnix };
        if (chainState is not null) a["chainState"] = Hex(chainState);
        var r = Call("roster.append", a);
        return new RosterAppendResult(
            Convert.FromHexString(r["chainState"]!.GetValue<string>()),
            r["equivocation"]?.GetValue<bool>() == true,
            r["teamId"] is { } t ? Convert.FromHexString(t.GetValue<string>()) : null,
            r["version"]?.GetValue<ulong>() ?? 0,
            r["hash"] is { } h ? Convert.FromHexString(h.GetValue<string>()) : null,
            r["projection"]?.ToJsonString());
    }

    /// <summary>
    /// Apply the owner's signed EquivocationResolution (R2-08) to a frozen chain state: unfreezes it on the chosen
    /// branch (<see cref="RosterResolveResult.Switched"/> when the chain was truncated to the parent and the
    /// conflicting version appended). Throws <c>roster-rejected</c>/<c>bad-signature</c> unless an owner device of the
    /// common parent signed it for exactly the two recorded versions.
    /// </summary>
    public static RosterResolveResult ResolveEquivocation(byte[] chainState, byte[] resolution, ReadOnlySpan<byte> serverId, ulong nowUnix)
    {
        var r = Call("roster.resolve", new() { ["chainState"] = Hex(chainState), ["resolution"] = Hex(resolution), ["serverId"] = Hex(serverId), ["now"] = nowUnix });
        return new RosterResolveResult(
            new RosterAppendResult(Convert.FromHexString(r["chainState"]!.GetValue<string>()), false, Convert.FromHexString(r["teamId"]!.GetValue<string>()),
                r["version"]!.GetValue<ulong>(), Convert.FromHexString(r["hash"]!.GetValue<string>()), r["projection"]!.ToJsonString()),
            r["switched"]!.GetValue<bool>(), r["resolvedVersion"]!.GetValue<ulong>());
    }

    /// <summary>Public header of an MLS message: wire format, groupId, epoch, content type, sender
    /// (PublicMessage) or authenticated data (PrivateMessage).</summary>
    public static JsonNode InspectMessage(ReadOnlySpan<byte> mlsMessage) => Call("message.inspect", new() { ["message"] = Hex(mlsMessage) });

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);

    /// <summary>Signature-checked KeyPackage summary (N1 accounting): deviceId (credential), MLS key,
    /// lifetime, last-resort flag, <c>kpRef</c> (RFC 9420 KeyPackageRef).</summary>
    public static KeyPackageInfo InspectKeyPackage(ReadOnlySpan<byte> keyPackage)
    {
        var r = Call("keypackage.inspect", new() { ["keyPackage"] = Hex(keyPackage) });
        return new KeyPackageInfo(Convert.FromHexString(r["deviceId"]!.GetValue<string>()), Convert.FromHexString(r["mlsSignatureKey"]!.GetValue<string>()),
            r["notBefore"]!.GetValue<ulong>(), r["notAfter"]!.GetValue<ulong>(), r["lastResort"]!.GetValue<bool>(), Convert.FromHexString(r["kpRef"]!.GetValue<string>()));
    }

    /// <summary>
    /// Start public tracking of a group from its creator's epoch-0 GroupInfo (with ratchet tree): checks the
    /// signature, groupId = SHA-256(descriptor), that the tree is exactly the creator's leaf, admissible for the
    /// descriptor in the verified roster (N2: only a descriptor member can create a pair/personal group), and that a
    /// team/project creator is owner/admin in that roster version and the head. <see cref="PublicGroupResult.Committer"/>
    /// is the creator's deviceId. Throws <see cref="OmbraException"/> (<c>commit-rejected</c>, <c>need-roster</c>) otherwise.
    /// </summary>
    public static PublicGroupResult PublicGroupInit(byte[] chainState, ReadOnlySpan<byte> groupInfo) =>
        PublicGroupResult.From(Call("pgroup.init", new() { ["chainState"] = Hex(chainState), ["groupInfo"] = Hex(groupInfo) }));

    /// <summary>Apply one PublicMessage commit under the same policy as clients (§3.1); returns the new
    /// opaque group state. A rejected commit throws and leaves <paramref name="groupState"/> valid.</summary>
    public static PublicGroupResult PublicGroupCommit(byte[] chainState, byte[] groupState, ReadOnlySpan<byte> commit) =>
        PublicGroupResult.From(Call("pgroup.commit", new() { ["chainState"] = Hex(chainState), ["groupState"] = Hex(groupState), ["commit"] = Hex(commit) }));
}

public sealed record KeyPackageInfo(byte[] DeviceId, byte[] MlsSignatureKey, ulong NotBefore, ulong NotAfter, bool LastResort, byte[] KpRef);

/// <param name="GroupState">Opaque; store per group, pass to the next <see cref="OmbraPublic.PublicGroupCommit"/>.</param>
/// <param name="Added">(deviceId, kpRef) of each Add in the commit.</param>
/// <param name="Leaves">deviceIds of the leaves after the commit.</param>
/// <param name="DescriptorJson">Only on init: {type, teamId, projectId?, members?, owner?, generation}.</param>
public sealed record PublicGroupResult(byte[] GroupState, byte[] GroupId, ulong Epoch, ulong RosterVersion, byte[]? Committer,
    IReadOnlyList<(byte[] DeviceId, byte[] KpRef)> Added, IReadOnlyList<byte[]> Leaves, string? DescriptorJson)
{
    internal static PublicGroupResult From(JsonNode r)
    {
        static byte[] B(JsonNode? n) => Convert.FromHexString(n!.GetValue<string>());
        var added = (r["added"] as JsonArray ?? []).Select(x => (B(x!["deviceId"]), B(x["kpRef"]))).ToList();
        var leaves = (r["leaves"] as JsonArray ?? []).Select(B).ToList();
        return new PublicGroupResult(B(r["groupState"]), B(r["groupId"]), r["epoch"]!.GetValue<ulong>(), r["rosterVersion"]!.GetValue<ulong>(),
            r["committer"] is { } c ? B(c) : null, added, leaves, r["descriptor"]?.ToJsonString());
    }
}

/// <param name="ChainState">Opaque, store per team and pass back on the next append.</param>
/// <param name="ProjectionJson">Head projection (members, roles, devices, access) as JSON.</param>
public sealed record RosterAppendResult(byte[] ChainState, bool Equivocation, byte[]? TeamId, ulong Version, byte[]? Hash, string? ProjectionJson);

/// <param name="Head">The chain after the decision (state, head version/hash, projection).</param>
/// <param name="Switched">True when the conflicting version became canonical (versions ≥ ResolvedVersion replaced).</param>
public sealed record RosterResolveResult(RosterAppendResult Head, bool Switched, ulong ResolvedVersion);
