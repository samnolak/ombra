using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>The SAS commitment did not match the revealed data: the relay (server) tampered with the pairing.</summary>
public sealed class PairingTamperedException(string message) : Exception(message);

internal static class PairingParty
{
    public static JsonObject Of(Guid deviceId, byte[] identityKey, byte[] mlsKey, byte[] nonce) => new()
    {
        ["deviceId"] = TeamsHex.Of(deviceId), ["identityKey"] = TeamsHex.Of(identityKey), ["mlsSignatureKey"] = TeamsHex.Of(mlsKey), ["nonce"] = TeamsHex.Of(nonce),
    };
}

/// <summary>
/// The existing (signed-in) device of a pairing (§2.3): opens the relay session with a commitment, reveals its nonce
/// only after the new device answered, shows six words, and — after the user confirmed they match — issues the
/// DeviceCertificate with a signed grant of this member's teams. QR and manual-code paths certify without SAS.
/// </summary>
public sealed class ExistingDevicePairing(TeamsServer server, TeamsDevice device)
{
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);
    private byte[]? _commitment;
    private PairSession? _session;

    public string? Code { get; private set; }

    private DeviceIdentity Me => device.Identity ?? throw new InvalidOperationException("device not initialised");

    public async Task<PairingLink> StartAsync(CancellationToken ct = default)
    {
        _commitment = TeamsHex.Bytes(OmbraPublic.Call("pair.commit", PairingParty.Of(Me.DeviceId, Me.IdentityKey, Me.MlsSignatureKey, _nonce))["commitment"]!);
        var open = await server.PostJsonAsync<PairOpenResponse>("v1/devices/pair", new PairOpenRequest(_commitment), ct);
        Code = open.Code;
        return new PairingLink(server.Endpoint.ToString(), server.Endpoint.IpMode ? server.Endpoint.Pins.First() : null, open.Code);
    }

    /// <summary>Polls until the new device answered (its keys and nonce, in clear).</summary>
    public async Task<PairSession> WaitForNewDeviceAsync(TimeSpan poll, CancellationToken ct = default)
    {
        while (true)
        {
            var s = await server.GetAnonymousAsync<PairSession>($"v1/devices/pair/{Code}", ct);
            if (s.NewDeviceId is not null) return _session = s;
            await Task.Delay(poll, server.Clock, ct);
        }
    }

    /// <summary>
    /// Reveals this device's nonce and returns the six words to compare with the new device's screen. The words are
    /// computed from the answer seen before revealing; that transcript is frozen, and the /reveal response must repeat
    /// it exactly — otherwise the relay swapped keys after the fact and nothing will be certified.
    /// </summary>
    public async Task<IReadOnlyList<string>> RevealAsync(CancellationToken ct = default)
    {
        var s = _session ?? throw new InvalidOperationException("the new device has not answered yet");
        _session = null;
        var revealed = await server.PostJsonAsync<PairSession>($"v1/devices/pair/{Code}/reveal", new PairRevealRequest(_nonce), ct);
        if (revealed.NewDeviceId != s.NewDeviceId || !Same(revealed.NewIdentityKey, s.NewIdentityKey) || !Same(revealed.NewMlsSignatureKey, s.NewMlsSignatureKey)
            || !Same(revealed.NewNonce, s.NewNonce) || !Same(revealed.ExistingNonce, _nonce) || revealed.ExistingDeviceId != Me.DeviceId)
            throw new PairingTamperedException("the server changed the new device's keys after this device committed to them");
        var r = OmbraPublic.Call("pair.sas", new()
        {
            ["existing"] = PairingParty.Of(Me.DeviceId, Me.IdentityKey, Me.MlsSignatureKey, _nonce), ["commitment"] = TeamsHex.Of(_commitment!),
            ["new"] = PairingParty.Of(s.NewDeviceId!.Value, s.NewIdentityKey!, s.NewMlsSignatureKey!, s.NewNonce!),
        });
        _session = s with { ExistingNonce = (byte[])_nonce.Clone() }; // exactly the transcript the words are made of
        return r["words"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
    }

    private static bool Same(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>Manual path: the code typed from the new device's screen must cover its keys (≥128 bits of fingerprint).</summary>
    public bool ManualCodeMatches(string typed)
    {
        var s = _session ?? throw new InvalidOperationException("the new device has not answered yet");
        return OmbraPublic.Call("pair.code_check", PairingParty.Of(s.NewDeviceId!.Value, s.NewIdentityKey!, s.NewMlsSignatureKey!, s.NewNonce!).Also("code", typed))
            ["matches"]!.GetValue<bool>();
    }

    /// <summary>After the user confirmed the words (or the manual code): certify the new device and vouch for <paramref name="teams"/>.</summary>
    public Task<byte[]> CertifyAsync(IReadOnlyList<string> teams, CancellationToken ct = default)
    {
        var s = _session ?? throw new InvalidOperationException("the new device has not answered yet");
        if (s.ExistingNonce is null) throw new InvalidOperationException("reveal first");
        return Certify(server, device, s.NewDeviceId!.Value, s.NewIdentityKey!, s.NewMlsSignatureKey!, s.NewNonce!, teams, Code, ct);
    }

    /// <summary>QR path (§2.3 п.1–2): the QR carries the new device's full keys, so the server cannot substitute them.</summary>
    public static Task<byte[]> CertifyFromQrAsync(TeamsServer server, TeamsDevice device, byte[] qrPayload, IReadOnlyList<string> teams, CancellationToken ct = default)
    {
        var p = OmbraPublic.Call("pair.qr_parse", new() { ["payload"] = TeamsHex.Of(qrPayload) });
        return Certify(server, device, TeamsHex.ToGuid(p["deviceId"]!), TeamsHex.Bytes(p["identityKey"]!), TeamsHex.Bytes(p["mlsSignatureKey"]!),
            TeamsHex.Bytes(p["nonce"]!), teams, null, ct);
    }

    private static async Task<byte[]> Certify(TeamsServer server, TeamsDevice device, Guid deviceId, byte[] ik, byte[] mk, byte[] nonce, IReadOnlyList<string> teams,
        string? code, CancellationToken ct)
    {
        var me = device.Identity!;
        var sid = TeamsHex.Of(await server.ServerIdAsync(ct));
        var cert = TeamsHex.Bytes(device.Call("device.certificate", new()
        {
            ["serverId"] = sid, ["memberId"] = TeamsHex.Of(me.MemberId), ["deviceId"] = TeamsHex.Of(deviceId), ["identityKey"] = TeamsHex.Of(ik),
            ["mlsSignatureKey"] = TeamsHex.Of(mk), ["pairingNonce"] = TeamsHex.Of(nonce), ["issuedAt"] = server.Now,
        })["cert"]!);
        var (grant, sig) = server.SignEnvelope(EnvelopeKinds.DeviceGrant, deviceId.ToByteArray(bigEndian: true),
            new DeviceGrantBody(teams.Select(Convert.FromHexString).ToList()).ToCbor());
        await server.PostJsonAsync<DeviceCertificateResponse>("v1/devices/certificate", new DeviceCertificateRequest(cert, "gui", grant, sig, code), ct);
        return cert;
    }
}

/// <summary>
/// The new device of a pairing (§2.3): generates its keys under a provisional memberId, shows a QR / manual code or
/// joins the relay session, checks the existing device's commitment and shows the same six words; once certified it
/// adopts its member's id and returns the teams its member vouched for (signed grant).
/// </summary>
public sealed class NewDevicePairing
{
    private readonly TeamsServer _server;
    private readonly TeamsDevice _device;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);
    private PairSession? _session;

    public NewDevicePairing(TeamsServer server, TeamsDevice device)
    {
        _server = server;
        _device = device;
        if (device.Identity is null) device.Initialize(Guid.CreateVersion7(), Guid.CreateVersion7());
        server.Attach(device);
    }

    private DeviceIdentity Me => _device.Identity!;
    private JsonObject Party => PairingParty.Of(Me.DeviceId, Me.IdentityKey, Me.MlsSignatureKey, _nonce);

    public byte[] QrPayload() => TeamsHex.Bytes(OmbraPublic.Call("pair.qr", Party)["payload"]!);

    /// <summary>32 base32 characters (160 bits of the key fingerprint) for typing on the existing device.</summary>
    public string ManualCode() => OmbraPublic.Call("pair.code", Party)["code"]!.GetValue<string>();

    public async Task JoinAsync(PairingLink link, CancellationToken ct = default)
    {
        _session = await _server.PostAnonymousJsonAsync<PairSession>($"v1/devices/pair/{link.Code}/join",
            new PairJoinRequest(Me.DeviceId, "gui", Me.IdentityKey, Me.MlsSignatureKey, _nonce), ct);
    }

    /// <summary>Waits for the reveal and returns the six words; throws <see cref="PairingTamperedException"/> when the commitment does not match.</summary>
    public async Task<IReadOnlyList<string>> WaitForWordsAsync(TimeSpan poll, CancellationToken ct = default)
    {
        var code = _session?.Code ?? throw new InvalidOperationException("join first");
        while (true)
        {
            var s = await _server.GetAnonymousAsync<PairSession>($"v1/devices/pair/{code}", ct);
            if (s.ExistingNonce is not null)
            {
                _session = s;
                var r = OmbraPublic.Call("pair.sas", new()
                {
                    ["existing"] = PairingParty.Of(s.ExistingDeviceId, s.ExistingIdentityKey, s.ExistingMlsSignatureKey, s.ExistingNonce),
                    ["commitment"] = TeamsHex.Of(s.ExistingCommitment), ["new"] = Party,
                });
                if (!r["valid"]!.GetValue<bool>()) throw new PairingTamperedException("the pairing partner's data does not match its commitment");
                return r["words"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
            }
            await Task.Delay(poll, _server.Clock, ct);
        }
    }

    /// <summary>
    /// Waits until the existing device registered this one (hello succeeds), fetches the certificate and grant, checks
    /// them (issuer = the SAS-confirmed partner when the relay path was used) and adopts the member id.
    /// </summary>
    public async Task<IReadOnlyList<string>> CompleteAsync(TimeSpan poll, CancellationToken ct = default)
    {
        while (true)
        {
            try
            {
                await _server.HelloAsync(ct);
                break;
            }
            catch (TeamsServerException e) when (e.Status == 401) { }
            await Task.Delay(poll, _server.Clock, ct);
        }
        var rec = await _server.GetAsync<DeviceRecord>($"v1/devices/{Me.DeviceId}/certificate", ct);
        if (_session is { } s && !rec.IssuerIdentityKey.AsSpan().SequenceEqual(s.ExistingIdentityKey))
            throw new PairingTamperedException("the certificate was not issued by the device whose words were confirmed");
        if (!OmbraPublic.VerifyEnvelope(rec.GrantEnvelope, rec.GrantSignature, rec.IssuerIdentityKey))
            throw new PairingTamperedException("the team grant is not signed by the issuing device");
        var env = Envelope.Decode(rec.GrantEnvelope);
        if (env.Kind != EnvelopeKinds.DeviceGrant || env.DeviceId != rec.IssuerDeviceId || !env.Scope.AsSpan().SequenceEqual(Me.DeviceId.ToByteArray(bigEndian: true)))
            throw new PairingTamperedException("the team grant is for another device");
        _device.Call("device.adopt", new() { ["cert"] = TeamsHex.Of(rec.Certificate), ["issuerIdentityKey"] = TeamsHex.Of(rec.IssuerIdentityKey) });
        _device.Refresh();
        return DeviceGrantBody.Parse(env.BodyMap()).TeamIds.Select(t => TeamsHex.Of(t)).ToList();
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject o, string key, JsonNode? value)
    {
        o[key] = value;
        return o;
    }
}
