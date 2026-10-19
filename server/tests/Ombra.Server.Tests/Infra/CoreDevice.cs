using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ombra.Core.Interop;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;

namespace Ombra.Server.Tests.Infra;

/// <summary>
/// A real client device backed by ombra-core's stateful API (OmbraState): it creates genesis, signs RosterUpdates and
/// imports chains exactly like Ombra/Nightly will. Used where the server must see genuinely signed roster data.
/// </summary>
public sealed class CoreDevice : IDisposable
{
    private readonly string _dir;

    public CoreDevice(Guid? memberId = null)
    {
        _dir = Path.Combine(Path.GetTempPath(), "ombra-core-dev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        MemberId = memberId ?? Guid.CreateVersion7();
        DeviceId = Guid.CreateVersion7();
        State = OmbraState.Open(Path.Combine(_dir, "state.bin"), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        State.Call("device.init", new() { ["deviceId"] = Hex(Ids.ToBytes(DeviceId)), ["memberId"] = Hex(Ids.ToBytes(MemberId)) });
        var info = State.Call("device.info");
        IdentityKey = Convert.FromHexString(info["identityKey"]!.GetValue<string>());
        MlsSignatureKey = Convert.FromHexString(info["mlsSignatureKey"]!.GetValue<string>());
    }

    public OmbraState State { get; }
    public Guid MemberId { get; }
    public Guid DeviceId { get; }
    public byte[] IdentityKey { get; }
    public byte[] MlsSignatureKey { get; }
    public string? AccessToken { get; private set; }

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);

    public string Certificate(Guid serverId, ulong now) =>
        State.Call("device.certificate", new() { ["self"] = true, ["serverId"] = Hex(Ids.ToBytes(serverId)), ["issuedAt"] = now })["cert"]!.GetValue<string>();

    /// <summary>Registers the device (and member) on the server as JoinRequest/bootstrap would, and gets a session.</summary>
    public async Task RegisterAsync(ServerHarness h, bool serverOwner = false)
    {
        await using (var conn = await h.Db.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO members (member_id, created_at) VALUES (@m, now()) ON CONFLICT DO NOTHING;" +
            "INSERT INTO devices (device_id, member_id, product, identity_key, mls_signature_key, created_at) VALUES (@d, @m, 'gui', @ik, @mk, now());" +
            (serverOwner ? "INSERT INTO server_owners (member_id, granted_at) VALUES (@m, now());" : ""), conn))
        {
            cmd.Parameters.AddWithValue("m", MemberId);
            cmd.Parameters.AddWithValue("d", DeviceId);
            cmd.Parameters.AddWithValue("ik", IdentityKey);
            cmd.Parameters.AddWithValue("mk", MlsSignatureKey);
            await cmd.ExecuteNonQueryAsync();
        }
        await LoginAsync(h);
    }

    /// <summary>Session without hello: hello itself is covered by SessionTests with an independent Ed25519 implementation.</summary>
    public async Task LoginAsync(ServerHarness h)
    {
        await using var conn = await h.Db.OpenConnectionAsync();
        AccessToken = (await h.Service<TokenService>().IssueAsync(conn, null, DeviceId)).AccessToken;
    }

    public JsonNode Call(string op, JsonObject args) => State.Call(op, args);

    public void Dispose()
    {
        State.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
