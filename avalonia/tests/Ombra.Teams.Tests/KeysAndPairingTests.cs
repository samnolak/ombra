using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using Ombra.ClientCore.Teams;
using Ombra.Server.Contracts;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>
/// Slice S2 (SERVER_DESIGN §9): key store without fallback, Keychain item not synchronised, a profile moved to another
/// machine needs re-pairing, SAS mismatch when the server substitutes a key (R2-09), revocation closes the socket
/// under 1 s, cascade revocation (R2-10: by addedInVersion, not by a backdated issuedAt), SPKI pin in IP mode (SD-13).
/// </summary>
public sealed class KeysAndPairingTests(ServerStack pg)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Key_store_failure_has_no_fallback_and_writes_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ombra-teams-nofallback-" + Guid.NewGuid().ToString("N"));
        var e = Assert.Throws<KeyStoreUnavailableException>(() => TeamsDevice.Open(root, "server", new FailingKeyStore(), new FixedMachine("m")));
        Assert.Equal(-25308, e.Status);
        Assert.False(Directory.Exists(root)); // no state file, no plaintext key next to it
    }

    [Fact]
    public void Keychain_items_are_not_synchronizable_and_this_device_only()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS Keychain");
            return;
        }
        var service = "app.ombra.teams.test-" + Guid.NewGuid().ToString("N");
        var keychain = new MacKeychain(service);
        var root = Path.Combine(Path.GetTempPath(), "ombra-teams-keychain-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var d = TeamsDevice.Open(root, "server", keychain, new MacMachineIdentity()))
            {
                Assert.Equal(TeamsDeviceStatus.Ready, d.Status);
                d.Initialize(Guid.CreateVersion7(), Guid.CreateVersion7());
                foreach (var account in new[] { d.StateKeyAccount, TeamsDevice.InstallSecretAccount })
                {
                    var item = keychain.Describe(account);
                    Assert.NotNull(item);
                    Assert.False(item!.Value.Synchronizable);
                    if (keychain.UsesDataProtectionKeychain) Assert.Equal(MacKeychain.WhenUnlockedThisDeviceOnly, item.Value.Accessible);
                }
                Assert.Equal(32, keychain.Read(d.StateKeyAccount)!.Length);
            }
            // Reopening on the same machine finds the same identity through the Keychain only.
            using var again = TeamsDevice.Open(root, "server", keychain, new MacMachineIdentity());
            Assert.NotNull(again.Identity);
        }
        finally
        {
            keychain.Delete("state-key/server");
            keychain.Delete(TeamsDevice.InstallSecretAccount);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Profile_moved_to_another_machine_requires_re_pairing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ombra-teams-clone-" + Guid.NewGuid().ToString("N"));
        var keys = new MemoryKeyStore();
        DeviceIdentity original;
        using (var d = TeamsDevice.Open(root, "server", keys, new FixedMachine("mac-A")))
            original = d.Initialize(Guid.CreateVersion7(), Guid.CreateVersion7());

        // Same files and even the same Keychain items (e.g. a migrated login keychain), other hardware → installId differs.
        using (var clone = TeamsDevice.Open(root, "server", keys, new FixedMachine("mac-B")))
        {
            Assert.Equal(TeamsDeviceStatus.RePairRequired, clone.Status);
            Assert.Null(clone.Identity);
            Assert.Throws<InvalidOperationException>(() => clone.RequireState());
        }
        // Files restored from a backup onto a machine whose Keychain does not have the key.
        using (var restored = TeamsDevice.Open(root, "server", new MemoryKeyStore(), new FixedMachine("mac-A")))
            Assert.Equal(TeamsDeviceStatus.RePairRequired, restored.Status);

        // Re-pairing issues new keys; the original machine still opens its own state.
        using (var clone = TeamsDevice.Open(root, "server", keys, new FixedMachine("mac-B")))
        {
            clone.ResetForRePairing();
            Assert.Equal(TeamsDeviceStatus.Ready, clone.Status);
            var fresh = clone.Initialize(Guid.CreateVersion7(), original.MemberId);
            Assert.NotEqual(original.IdentityKey, fresh.IdentityKey);
        }
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static async Task<(Client Owner, string TeamId)> OwnerWithTeamAsync(TeamServerHost host)
    {
        var owner = new Client(host, "owner");
        owner.InitFresh();
        await owner.Server.BootstrapAsync(await host.BootstrapCodeAsync());
        var team = await owner.Teams.CreateTeamAsync();
        return (owner, team.TeamId);
    }

    [Fact]
    public async Task Sas_pairing_matches_and_a_key_substituted_by_the_server_changes_the_words()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;

        // Honest relay.
        using var laptop = new Client(host, "laptop");
        var existing = new ExistingDevicePairing(owner.Server, owner.Device);
        var link = PairingLink.Parse((await existing.StartAsync()).ToString());
        var newSide = new NewDevicePairing(laptop.Server, laptop.Device);
        await newSide.JoinAsync(link);
        await existing.WaitForNewDeviceAsync(Poll);
        var wordsExisting = await existing.RevealAsync();
        var wordsNew = await newSide.WaitForWordsAsync(Poll);
        Assert.Equal(6, wordsNew.Count);
        Assert.Equal(wordsExisting, wordsNew);
        var cert = await existing.CertifyAsync([teamId]);
        await owner.Teams.AddOwnDeviceAsync(teamId, cert);
        var granted = await newSide.CompleteAsync(Poll);
        Assert.Equal([teamId], granted);
        Assert.Equal(owner.Id.MemberId, laptop.Id.MemberId); // adopted from the certificate
        await laptop.Teams.ImportOwnTeamAsync(teamId);
        Assert.Equal(owner.Teams.SafetyNumber(teamId, owner.Id.MemberId), laptop.Teams.SafetyNumber(teamId, owner.Id.MemberId));

        // R2-09: the server swaps the new device's key towards the existing device → the six words differ.
        using var victim = new Client(host, "victim");
        var existing2 = new ExistingDevicePairing(owner.Server, owner.Device);
        var link2 = await existing2.StartAsync();
        var victimSide = new NewDevicePairing(victim.Server, victim.Device);
        await victimSide.JoinAsync(link2);
        using var attacker = new Client(host, "attacker");
        attacker.InitFresh();
        await host.ExecAsync("UPDATE pairing_sessions SET new_identity_key = @k, new_mls_key = @m WHERE code = @c",
            ("k", attacker.Id.IdentityKey), ("m", attacker.Id.MlsSignatureKey), ("c", link2.Code));
        await existing2.WaitForNewDeviceAsync(Poll);
        var seenByExisting = await existing2.RevealAsync();
        var seenByVictim = await victimSide.WaitForWordsAsync(Poll);
        Assert.NotEqual(seenByExisting, seenByVictim);

        // ...and a nonce changed by the relay after the commitment is refused outright by the new device.
        using var third = new Client(host, "third");
        var existing3 = new ExistingDevicePairing(owner.Server, owner.Device);
        var link3 = await existing3.StartAsync();
        var thirdSide = new NewDevicePairing(third.Server, third.Device);
        await thirdSide.JoinAsync(link3);
        await existing3.WaitForNewDeviceAsync(Poll);
        await existing3.RevealAsync();
        await host.ExecAsync("UPDATE pairing_sessions SET existing_nonce = @n WHERE code = @c", ("n", new byte[32]), ("c", link3.Code));
        await Assert.ThrowsAsync<PairingTamperedException>(() => thirdSide.WaitForWordsAsync(Poll));
    }

    [Fact]
    public async Task Revocation_closes_the_socket_within_a_second_and_cascades_by_roster_version_not_issuedAt()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;

        // QR path: the laptop shows its full keys; the owner's device certifies them.
        using var laptop = new Client(host, "laptop");
        var laptopSide = new NewDevicePairing(laptop.Server, laptop.Device);
        var cert = await ExistingDevicePairing.CertifyFromQrAsync(owner.Server, owner.Device, laptopSide.QrPayload(), [teamId]);
        await owner.Teams.AddOwnDeviceAsync(teamId, cert);
        await laptopSide.CompleteAsync(Poll);
        await laptop.Teams.ImportOwnTeamAsync(teamId);

        // The (to be stolen) laptop certifies a phone and backdates the certificate's issuedAt by a year.
        using var phone = new Client(host, "phone");
        var phoneSide = new NewDevicePairing(phone.Server, phone.Device);
        var sid = TeamsHex.Of(await laptop.Server.ServerIdAsync());
        var phoneCert = TeamsHex.Bytes(laptop.Device.Call("device.certificate", new()
        {
            ["serverId"] = sid, ["memberId"] = TeamsHex.Of(laptop.Id.MemberId), ["deviceId"] = TeamsHex.Of(phone.Id.DeviceId),
            ["identityKey"] = TeamsHex.Of(phone.Id.IdentityKey), ["mlsSignatureKey"] = TeamsHex.Of(phone.Id.MlsSignatureKey),
            ["issuedAt"] = laptop.Server.Now - 365UL * 24 * 3600,
        })["cert"]!);
        var (grant, gsig) = laptop.Server.SignEnvelope(EnvelopeKinds.DeviceGrant, phone.Id.DeviceId.ToByteArray(bigEndian: true),
            new DeviceGrantBody([Convert.FromHexString(teamId)]).ToCbor());
        await laptop.Server.PostJsonAsync<DeviceCertificateResponse>("v1/devices/certificate", new DeviceCertificateRequest(phoneCert, "gui", grant, gsig, null));
        await laptop.Teams.AddOwnDeviceAsync(teamId, phoneCert);
        await phoneSide.CompleteAsync(Poll);
        await phone.Teams.ImportOwnTeamAsync(teamId);
        await using var stream = await TeamStream.ConnectAsync(phone.Server);

        await owner.Teams.SyncRosterAsync(teamId);
        var sw = Stopwatch.StartNew(); // from the revocation request (roster DeviceRevocation + server-wide revoke) to the closed socket
        var revoked = await owner.Teams.RevokeDeviceAsync(laptop.Id.DeviceId, compromised: true, [teamId]);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            while (await stream.ReceiveAsync(cts.Token) is not null) { }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"socket closed after {sw.Elapsed}");

        Assert.Contains(laptop.Id.DeviceId, revoked);
        Assert.Contains(phone.Id.DeviceId, revoked); // server-side cascade
        var devices = owner.Teams.Head(teamId)["projection"]!["members"]!.AsArray().Single()!["devices"]!.AsArray();
        JsonNode Entry(Guid d) => devices.Single(x => x!["deviceId"]!.GetValue<string>() == TeamsHex.Of(d))!;
        Assert.NotNull(Entry(laptop.Id.DeviceId)["revoked"]);
        Assert.NotNull(Entry(phone.Id.DeviceId)["revoked"]); // roster cascade by addedInVersion despite the old issuedAt
        Assert.Null(Entry(owner.Id.DeviceId)["revoked"]);
        var hello = await Assert.ThrowsAsync<TeamsServerException>(() => phone.Server.HelloAsync());
        Assert.Equal(Reasons.Revoked, hello.Reason);
    }

    [Fact]
    public async Task Ip_mode_refuses_a_wrong_pin_and_accepts_the_pinned_key_over_tls13()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        using var good = ServerEndpoint.Parse(host.Address, host.Pin).CreateHttpClient();
        Assert.Equal("ok", (await good.GetFromJsonAsync<HealthResponse>("v1/health", TeamsServer.Json))!.Status);
        var wrong = ServerEndpoint.Parse(host.Address, new string('a', 64));
        using var bad = wrong.CreateHttpClient();
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => bad.GetAsync("v1/health"));
        Assert.IsType<AuthenticationException>(e.InnerException);
        Assert.Equal(host.Pin, wrong.LastPresentedPin);
        Assert.Throws<FormatException>(() => ServerEndpoint.Parse("203.0.113.7", null)); // IP without pin
        Assert.Throws<FormatException>(() => ServerEndpoint.Parse("team.example.com", host.Pin)); // domain: chain, not pin
        Assert.False(ServerEndpoint.Parse("team.example.com", null).IpMode);
    }
}
