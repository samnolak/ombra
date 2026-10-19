using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ombra.Core.Interop;

namespace Ombra.ClientCore.Teams;

public enum TeamsDeviceStatus
{
    /// <summary>State open; <see cref="TeamsDevice.Identity"/> is set once the device is initialised.</summary>
    Ready,
    /// <summary>
    /// SD-10: the encrypted state cannot be used on this machine (its key is not in this Keychain, or the installId
    /// sealed inside differs — the profile was copied/restored from another machine). Keys must be re-issued:
    /// <see cref="TeamsDevice.ResetForRePairing"/>, then pair again; the old device is revoked from another device.
    /// </summary>
    RePairRequired,
}

/// <summary>This device's keys as reported by ombra-core (<c>device.info</c>).</summary>
public sealed record DeviceIdentity(Guid DeviceId, Guid MemberId, byte[] IdentityKey, byte[] MlsSignatureKey, byte[] Fingerprint)
{
    public static DeviceIdentity From(JsonNode n) => new(
        TeamsHex.ToGuid(n["deviceId"]!), TeamsHex.ToGuid(n["memberId"]!), TeamsHex.Bytes(n["identityKey"]!), TeamsHex.Bytes(n["mlsSignatureKey"]!),
        TeamsHex.Bytes(n["identityFingerprint"]!));
}

/// <summary>
/// The device's team identity on one server (§2.1): ombra-core's encrypted state file (identity key, MLS keys, verified
/// roster chains, groups) under <c>&lt;root&gt;/&lt;profile&gt;/state.db</c>, opened with a 32-byte key and an installId
/// that live only in the OS key store. Order of operations guarantees "no key store → nothing on disk": the key store is
/// read/written before any directory or file is created.
/// </summary>
public sealed class TeamsDevice : IDisposable
{
    public const string InstallSecretAccount = "install-secret";
    private readonly ITeamsKeyStore _keys;
    private readonly IMachineIdentity _machine;
    private OmbraState? _state;

    private TeamsDevice(string directory, string profile, ITeamsKeyStore keys, IMachineIdentity machine)
    {
        Directory = directory;
        Profile = profile;
        _keys = keys;
        _machine = machine;
    }

    public string Directory { get; }
    public string Profile { get; }
    public string StatePath => Path.Combine(Directory, "state.db");
    public string StateKeyAccount => "state-key/" + Profile;
    /// <summary>Per-team row key of state.db (D-5: deleting it makes every copy of that team's rows unreadable).</summary>
    public string TeamKeyAccount(string teamId) => "team-key/" + Profile + "/" + teamId;
    public TeamsDeviceStatus Status { get; private set; }
    public string? RePairReason { get; private set; }
    /// <summary>Null until <see cref="Initialize"/> (or for a state that needs re-pairing).</summary>
    public DeviceIdentity? Identity { get; private set; }

    /// <summary>Default machine-local root (never synced): ~/Library/Application Support/Ombra/teams on macOS.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Ombra", "teams");

    /// <param name="root">Teams root; one sub-directory per profile (server).</param>
    /// <param name="profile">Profile name (the server id), also the key store account suffix.</param>
    /// <exception cref="KeyStoreUnavailableException">The OS key store failed: nothing is created.</exception>
    public static TeamsDevice Open(string root, string profile, ITeamsKeyStore keys, IMachineIdentity machine)
    {
        if (profile.Length == 0 || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.Contains('.'))
            throw new ArgumentException("bad profile name", nameof(profile));
        var d = new TeamsDevice(Path.Combine(root, profile), profile, keys, machine);
        d.OpenState();
        return d;
    }

    private void OpenState()
    {
        var exists = File.Exists(StatePath);
        var secret = _keys.Read(InstallSecretAccount);
        var key = _keys.Read(StateKeyAccount);
        if (exists && (secret is null || key is null))
        {
            MarkRePair("the key of this profile is not in this machine's Keychain (profile copied from another machine?)");
            return;
        }
        if (secret is null)
        {
            secret = RandomNumberGenerator.GetBytes(32);
            _keys.Write(InstallSecretAccount, secret);
        }
        if (key is null)
        {
            key = RandomNumberGenerator.GetBytes(32);
            _keys.Write(StateKeyAccount, key);
        }
        var installId = InstallId.Derive(secret, _machine.MachineId());
        System.IO.Directory.CreateDirectory(Directory);
        try
        {
            _state = OmbraState.Open(StatePath, key, installId);
        }
        catch (OmbraException e) when (e.Code == "clone-detected")
        {
            MarkRePair("this profile was created on another machine (installId mismatch)");
            return;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(secret);
        }
        Status = TeamsDeviceStatus.Ready;
        UnlockTeams();
        var info = _state.CallRaw("device.info");
        if (info["ok"]?.GetValue<bool>() == true) Identity = DeviceIdentity.From(info["result"]!);
    }

    private void MarkRePair(string reason)
    {
        Status = TeamsDeviceStatus.RePairRequired;
        RePairReason = reason;
    }

    /// <summary>Generates this device's keys (once). A paired device starts with a provisional memberId and adopts its member's id from the certificate.</summary>
    public DeviceIdentity Initialize(Guid deviceId, Guid memberId)
    {
        var s = RequireState();
        Identity = DeviceIdentity.From(s.Call("device.init", new() { ["deviceId"] = TeamsHex.Of(deviceId), ["memberId"] = TeamsHex.Of(memberId) }));
        return Identity;
    }

    internal void Refresh() => Identity = DeviceIdentity.From(RequireState().Call("device.info"));

    public OmbraState RequireState() =>
        _state ?? throw new InvalidOperationException(Status == TeamsDeviceStatus.RePairRequired ? "re-pairing required: " + RePairReason : "state closed");

    public JsonNode Call(string op, JsonObject? args = null)
    {
        var r = RequireState().Call(op, args);
        if (op is "team.create" or "team.import" && r["teamKey"] is { } k) StoreTeamKey(r["teamId"]!.GetValue<string>(), k);
        if (op == "team.forget") _keys.Delete(TeamKeyAccount(args!["teamId"]!.GetValue<string>()));
        return r;
    }

    /// <summary>D-5: destroy the team's row key (after <c>team.forget</c>).</summary>
    public void ForgetTeamKey(string teamId) => _keys.Delete(TeamKeyAccount(teamId));

    /// <summary>Teams of state.db whose key is not in the key store stay locked (their calls fail with <c>team-locked</c>).</summary>
    public IReadOnlyList<string> LockedTeams { get; private set; } = [];

    private void StoreTeamKey(string teamId, JsonNode hex)
    {
        var key = TeamsHex.Bytes(hex);
        try { _keys.Write(TeamKeyAccount(teamId), key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private void UnlockTeams()
    {
        var locked = new List<string>();
        foreach (var t in _state!.Call("state.teams")["teams"]!.AsArray())
        {
            if (t!["unlocked"]!.GetValue<bool>()) continue;
            var id = t["teamId"]!.GetValue<string>();
            var key = _keys.Read(TeamKeyAccount(id));
            if (key is null) { locked.Add(id); continue; }
            try
            {
                if (_state.CallRaw("team.unlock", new() { ["teamId"] = id, ["key"] = TeamsHex.Of(key) })["ok"]!.GetValue<bool>() != true) locked.Add(id);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        LockedTeams = locked;
    }

    /// <summary>
    /// SD-10: drop the unusable state and its key and start a fresh device (new keys, new deviceId at the next
    /// <see cref="Initialize"/>). The install secret stays: it is per machine, not per profile.
    /// </summary>
    public void ResetForRePairing()
    {
        _state?.Dispose();
        _state = null;
        Identity = null;
        WipeFiles();
        _keys.Delete(StateKeyAccount);
        RePairReason = null;
        OpenState();
    }

    /// <summary>D-5: the whole profile (state file, its key in the key store) — used when the member is removed from every team.</summary>
    public void Erase()
    {
        _state?.Dispose();
        _state = null;
        Identity = null;
        WipeFiles();
        _keys.Delete(StateKeyAccount);
    }

    /// <summary>
    /// D-5: re-encrypt the state under a fresh key and destroy the old one in the key store, so earlier copies of
    /// state.db (APFS snapshots, backups) that still hold a wiped team cannot be decrypted any more.
    /// </summary>
    public void RotateStateKey()
    {
        var state = RequireState();
        var old = _keys.Read(StateKeyAccount) ?? throw new KeyStoreUnavailableException("state key missing from the key store");
        var fresh = RandomNumberGenerator.GetBytes(32);
        try
        {
            _keys.Write(StateKeyAccount, fresh);
            try
            {
                state.Call("state.rekey", new() { ["key"] = TeamsHex.Of(fresh) });
            }
            catch
            {
                _keys.Write(StateKeyAccount, old); // the file is still sealed with the old key
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(old);
            CryptographicOperations.ZeroMemory(fresh);
        }
    }

    private void WipeFiles()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory))
        {
            // Overwrite before unlinking: the state is AES-GCM ciphertext, but its key may have leaked with a stolen backup.
            try
            {
                var len = new FileInfo(f).Length;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Write))
                {
                    var zeros = new byte[64 * 1024];
                    for (long o = 0; o < len; o += zeros.Length) fs.Write(zeros, 0, (int)Math.Min(zeros.Length, len - o));
                    fs.Flush(flushToDisk: true);
                }
            }
            catch (IOException) { }
            File.Delete(f);
        }
    }

    public void Dispose()
    {
        _state?.Dispose();
        _state = null;
    }
}

/// <summary>Hex helpers for the ombra-core JSON layer (lowercase hex for bytes, 16-byte big-endian UUIDs).</summary>
public static class TeamsHex
{
    public static string Of(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);
    public static string Of(Guid id) => Convert.ToHexStringLower(id.ToByteArray(bigEndian: true));
    public static byte[] Bytes(JsonNode n) => Convert.FromHexString(n.GetValue<string>());
    public static Guid ToGuid(JsonNode n) => ToGuid(n.GetValue<string>());
    public static Guid ToGuid(string hex) => new(Convert.FromHexString(hex), bigEndian: true);
}
