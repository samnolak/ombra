using System.Text.Json;
using System.Text.Json.Nodes;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>What this machine remembers about its team account (account.json next to the profiles; no secrets in it).</summary>
public sealed class TeamAccount
{
    public string Server { get; set; } = "";
    public string? Pin { get; set; }
    public string Profile { get; set; } = "";
    public string? Name { get; set; }
    public List<string> Teams { get; set; } = [];
    /// <summary>Teams whose join request waits for an admin.</summary>
    public List<string> PendingJoins { get; set; } = [];
    /// <summary>teamId → memberId hex → root fingerprint last accepted (N9: a change freezes the pair until compared).</summary>
    public Dictionary<string, Dictionary<string, string>> Fingerprints { get; set; } = [];
    /// <summary>The team the Team page shows when the account is in several (null: the first).</summary>
    public string? SelectedTeam { get; set; }
}

/// <summary>One team as the Team page shows it.</summary>
public sealed record TeamView(string TeamId, TeamRoster Roster, bool IsAdmin, bool IsOwner, TeamConfirmation? Confirmation,
    IReadOnlyList<Guid> KeyChanged, EquivocationReview? Equivocation, OwnershipOffer? OfferToMe, IReadOnlyList<JoinReview> PendingJoins);

/// <summary>The account's state after a refresh.</summary>
public sealed record TeamSnapshot(string Server, Guid MemberId, Guid DeviceId, IReadOnlyList<TeamView> Teams, IReadOnlyList<string> WaitingForApproval,
    IReadOnlyList<string> Removed);

/// <summary>
/// The signed-in team account of this machine (one server): the device, the server session and the team operations,
/// serialised (one ombra-core state, one caller at a time), plus the background refresh the Team page and banners show.
/// </summary>
public sealed class TeamSession : IDisposable
{
    public const string AccountFile = "account.json";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private readonly List<string> _removed = [];
    private readonly Dictionary<string, DateTimeOffset> _heartbeats = [];
    private CancellationTokenSource? _loop;
    private bool _hello;

    private TeamSession(string root, TeamAccount account, TeamsDevice device, TeamsServer server)
    {
        _root = root;
        Account = account;
        Device = device;
        Server = server;
        Server.Attach(device);
        Client = new TeamClient(server, device);
        Client.TeamWiped += OnWiped;
        _items = new TeamItems(Client);
        Items = new SerializedItems(this);
    }

    public TeamAccount Account { get; }
    public TeamsDevice Device { get; }
    public TeamsServer Server { get; }
    public TeamClient Client { get; }
    /// <summary>
    /// Shared items of every team of this account (share, marks, progress): one instance, fed by the refresh, every call
    /// serialised with the session's other ombra-core calls (reads wait for a running refresh).
    /// </summary>
    public ITeamItems Items { get; }
    private readonly TeamItems _items;
    public TeamSnapshot? Last { get; private set; }

    /// <summary>A refresh finished (on a background thread).</summary>
    public event Action<TeamSnapshot>? Changed;
    /// <summary>A background refresh failed (network, server): the last snapshot stays.</summary>
    public event Action<Exception>? Failed;

    private static string AccountPath(string root) => Path.Combine(root, AccountFile);

    /// <summary>The saved account of this machine, or null when signed out.</summary>
    public static TeamSession? Load(string root, ITeamsKeyStore keys, IMachineIdentity machine)
    {
        var path = AccountPath(root);
        if (!File.Exists(path)) return null;
        var a = JsonSerializer.Deserialize<TeamAccount>(File.ReadAllText(path), Json)!;
        var device = TeamsDevice.Open(root, a.Profile, keys, machine);
        if (device.Status != TeamsDeviceStatus.Ready) { device.Dispose(); throw new InvalidOperationException("re-pairing required: " + device.RePairReason); }
        return new TeamSession(root, a, device, new TeamsServer(ServerEndpoint.Parse(a.Server, a.Pin)));
    }

    /// <summary>Signs in to a server (IP + pin, or a domain): this machine's profile there, keys created once.</summary>
    public static async Task<TeamSession> ConnectAsync(string root, string address, string? pin, ITeamsKeyStore keys, IMachineIdentity machine,
        bool initialize = true, CancellationToken ct = default)
    {
        var endpoint = ServerEndpoint.Parse(address.Trim(), string.IsNullOrWhiteSpace(pin) ? null : pin.Trim());
        var server = new TeamsServer(endpoint);
        TeamsDevice? device = null;
        try
        {
            var profile = (await server.ServerIdAsync(ct)).ToString("N");
            device = TeamsDevice.Open(root, profile, keys, machine);
            if (device.Status != TeamsDeviceStatus.Ready) throw new InvalidOperationException("re-pairing required: " + device.RePairReason);
            if (initialize && device.Identity is null) device.Initialize(Guid.CreateVersion7(), Guid.CreateVersion7());
            var s = new TeamSession(root, new TeamAccount { Server = endpoint.ToString(), Pin = endpoint.IpMode ? endpoint.Pins.First() : null, Profile = profile }, device, server);
            s.Save();
            return s;
        }
        catch
        {
            device?.Dispose();
            server.Dispose();
            throw;
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_root);
        var tmp = AccountPath(_root) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Account, Json));
        File.Move(tmp, AccountPath(_root), overwrite: true);
    }

    private async Task<T> Locked<T>(Func<Task<T>> f, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await f(); }
        finally { _gate.Release(); }
    }

    private Task Locked(Func<Task> f, CancellationToken ct) => Locked(async () => { await f(); return 0; }, ct);

    private async Task HelloAsync(CancellationToken ct)
    {
        if (_hello) return;
        await Server.HelloAsync(ct);
        _hello = true;
    }

    /// <summary>Any other team operation, serialised with the refresh (one ombra-core state, one caller at a time).</summary>
    public Task<T> RunAsync<T>(Func<TeamClient, Task<T>> f, CancellationToken ct = default) => Locked(() => f(Client), ct);

    /// <summary>Remembers the team the page shows (account.json, serialised with the other saves).</summary>
    public Task SelectTeamAsync(string teamId, CancellationToken ct = default) =>
        Locked(() => { Account.SelectedTeam = teamId; Save(); return Task.CompletedTask; }, ct);

    // ---------------- team lifecycle ----------------

    /// <summary>A new team owned by this device. The first team on a fresh server needs the server's one-time bootstrap code.</summary>
    public Task<TeamCreated> CreateTeamAsync(string? bootstrapCode, CancellationToken ct = default) => Locked(async () =>
    {
        if (!string.IsNullOrWhiteSpace(bootstrapCode)) { await Server.BootstrapAsync(bootstrapCode.Trim(), ct); _hello = true; }
        else await HelloAsync(ct);
        var t = await Client.CreateTeamAsync(ct);
        Account.Teams.Add(t.TeamId);
        Save();
        if (Account.Name is { Length: > 0 } n) await Client.SetNamesAsync(t.TeamId, n, DeviceName(), ct);
        return t;
    }, ct);

    /// <summary>The full invite link (with its secret) to send the invitee over a trusted channel.</summary>
    public Task<string> InviteAsync(string teamId, string role, CancellationToken ct = default) => Locked(async () =>
    {
        await HelloAsync(ct);
        return (await Client.CreateInviteAsync(teamId, role, ct: ct)).ToShareableString();
    }, ct);

    /// <summary>Invitee: connect to the link's server and send the join request; an admin approves it.</summary>
    public static async Task<TeamSession> JoinAsync(string root, TeamSession? current, string linkText, ITeamsKeyStore keys, IMachineIdentity machine, CancellationToken ct = default)
    {
        var link = InviteLink.Parse(linkText.Trim());
        var s = current is not null && current.Server.Endpoint.ToString() == link.Endpoint().ToString()
            ? current
            : await ConnectAsync(root, link.Server, link.Pin, keys, machine, ct: ct);
        if (s != current) current?.Dispose();
        await s.Locked(async () =>
        {
            var p = await s.Client.JoinAsync(link, ct);
            if (!s.Account.PendingJoins.Contains(p.TeamId)) s.Account.PendingJoins.Add(p.TeamId);
            s.Save();
        }, ct);
        return s;
    }

    /// <summary>Admin: approve, then hand the newcomer the team's names (it needs them before it can add its own).</summary>
    public Task ApproveAsync(JoinReview join, CancellationToken ct = default) => Locked(async () =>
    {
        await Client.ApproveJoinAsync(join, ct: ct);
        await Client.SendNamesAsync(join.TeamId, ct);
    }, ct);

    public Task RemoveMemberAsync(string teamId, Guid memberId, CancellationToken ct = default) => Locked(() => Client.RemoveMemberAsync(teamId, memberId, ct), ct);

    public Task OfferOwnershipAsync(string teamId, Guid to, CancellationToken ct = default) => Locked(() => Client.OfferOwnershipAsync(teamId, to, ct: ct), ct);

    public Task AcceptOwnershipAsync(string teamId, CancellationToken ct = default) => Locked(() => Client.AcceptOwnershipAsync(teamId, ct), ct);

    public Task ResolveEquivocationAsync(EquivocationReview review, bool keepKnown, bool undoneConfirmed, CancellationToken ct = default) =>
        Locked(() => Client.ResolveEquivocationAsync(review, keepKnown, undoneConfirmed, ct), ct);

    public Task<IReadOnlyList<Guid>> RevokeDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Locked(() => Client.RevokeDeviceAsync(deviceId, compromised: false, Account.Teams, ct), ct);

    public string SafetyNumber(string teamId, Guid memberId) => Client.SafetyNumber(teamId, memberId);

    /// <summary>N9: the member's new safety number was compared out of band: accept its key and release the pair.</summary>
    public void ConfirmKey(string teamId, TeamMember member)
    {
        _gate.Wait();
        try
        {
            Fingerprints(teamId)[TeamsHex.Of(member.MemberId)] = member.RootFingerprint;
            Client.SetPeerFrozen(teamId, member.MemberId, false);
            Save();
        }
        finally { _gate.Release(); }
    }

    /// <summary>This member's name, in every team (each team's names travel under its MLS group).</summary>
    public Task SetNameAsync(string name, CancellationToken ct = default) => Locked(async () =>
    {
        Account.Name = name.Trim();
        Save();
        if (Account.Teams.Count > 0) await HelloAsync(ct);
        foreach (var t in Account.Teams.Where(t => Client.Roster(t).NamesConfirmed).ToList()) await Client.SetNamesAsync(t, Account.Name, DeviceName(), ct);
    }, ct);

    private Dictionary<Guid, string?> DeviceName() => new() { [Device.Identity!.DeviceId] = Environment.MachineName };

    private Dictionary<string, string> Fingerprints(string teamId)
    {
        if (!Account.Fingerprints.TryGetValue(teamId, out var f)) Account.Fingerprints[teamId] = f = [];
        return f;
    }

    private void OnWiped(string teamId)
    {
        Account.Teams.Remove(teamId);
        Account.PendingJoins.Remove(teamId);
        Account.Fingerprints.Remove(teamId);
        if (!_removed.Contains(teamId)) _removed.Add(teamId);
        Save();
    }

    public void DismissRemoved(string teamId) => _removed.Remove(teamId);

    // ---------------- pairing ----------------

    /// <summary>Existing device: opens a pairing session; show its link (and QR) to the new device.</summary>
    public async Task<(ExistingDevicePairing Pairing, PairingLink Link)> StartPairingAsync(CancellationToken ct = default)
    {
        await Locked(() => HelloAsync(ct), ct);
        var p = new ExistingDevicePairing(Server, Device);
        return (p, await p.StartAsync(ct));
    }

    /// <summary>Existing device, after the user saw the same six words on both screens: certify it into every team.</summary>
    public Task CertifyAsync(ExistingDevicePairing p, CancellationToken ct = default) => Locked(async () =>
    {
        var cert = await p.CertifyAsync(Account.Teams, ct);
        foreach (var t in Account.Teams) await Client.AddOwnDeviceAsync(t, cert, ct);
    }, ct);

    /// <summary>New device: connect to the pairing link's server with fresh keys, join the session.</summary>
    public static async Task<(TeamSession Session, NewDevicePairing Pairing)> PairNewDeviceAsync(string root, string linkText, ITeamsKeyStore keys,
        IMachineIdentity machine, CancellationToken ct = default)
    {
        var link = PairingLink.Parse(linkText.Trim());
        var s = await ConnectAsync(root, link.Server, link.Pin, keys, machine, initialize: false, ct);
        try
        {
            var p = new NewDevicePairing(s.Server, s.Device);
            await p.JoinAsync(link, ct);
            return (s, p);
        }
        catch
        {
            s.SignOut();
            throw;
        }
    }

    /// <summary>New device, after the words matched: wait for the certificate and take over the member's teams.</summary>
    public Task FinishPairingAsync(NewDevicePairing p, CancellationToken ct = default) => Locked(async () =>
    {
        var teams = await p.CompleteAsync(Poll, ct);
        _hello = true;
        foreach (var t in teams)
        {
            await Client.ImportOwnTeamAsync(t, ct);
            if (!Account.Teams.Contains(t)) Account.Teams.Add(t);
        }
        Save();
        await Client.UploadKeyPackageAsync(ct);
    }, ct);

    // ---------------- refresh ----------------

    public Task<TeamSnapshot> RefreshAsync(CancellationToken ct = default) => Locked(async () =>
    {
        try { return await RefreshCoreAsync(ct); }
        catch (TeamsServerException e) when (e.Reason == Reasons.Removed)
        {
            // §5.3/D-5: verified notices first; an honest client then wipes what the server says it was removed from.
            if (Client.HandleRemoved(e).Count == 0) Client.HandleRemoved(e, Account.Teams.ToList());
            _hello = false;
            return Publish([]);
        }
    }, ct);

    private async Task<TeamSnapshot> RefreshCoreAsync(CancellationToken ct)
    {
        if (Account.Teams.Count == 0 && Account.PendingJoins.Count == 0) return Publish([]); // not registered on the server yet
        try { await HelloAsync(ct); }
        catch (TeamsServerException e) when (e.Status == 401 && Account.PendingJoins.Count > 0) { return Publish([]); } // not approved yet
        await _items.DeliverAsync(ct); // outbox out, then the sync loop (Welcomes, names, items)
        foreach (var t in Account.PendingJoins.ToList())
            if (Client.Status(t)["joined"]!.GetValue<bool>())
            {
                Account.PendingJoins.Remove(t);
                Account.Teams.Add(t);
            }
        Save();
        var joins = Account.Teams.Any(Client.IsAdmin) ? await Client.PendingJoinsAsync(ct) : [];
        var views = new List<TeamView>();
        foreach (var t in Account.Teams.ToList())
        {
            var r = await Client.SyncRosterAsync(t, ct);
            if (r.RemovedSelf || !Account.Teams.Contains(t)) continue;
            var admin = Client.IsAdmin(t);
            if (admin) await Client.AddMissingDevicesAsync(t, ct);
            var roster = Client.Roster(t);
            var me = roster.Members.First(m => m.IsMe);
            // SD-16: my name goes in once this device holds the team's confirmed names (a newcomer gets them after its Welcome).
            if (Account.Name is { Length: > 0 } n && roster.NamesConfirmed && me.Name != n)
            {
                await Client.SetNamesAsync(t, n, DeviceName(), ct);
                roster = Client.Roster(t);
                me = roster.Members.First(m => m.IsMe);
            }
            if (admin && (!_heartbeats.TryGetValue(t, out var at) || Server.Clock.GetUtcNow() - at > HeartbeatMonitor.PublishInterval))
            {
                await Client.PublishHeartbeatAsync(t, ct);
                _heartbeats[t] = Server.Clock.GetUtcNow();
            }
            var confirmation = await Client.CheckConfirmationAsync(t, ct);
            var changed = KeyChanges(t, roster);
            var equivocation = r.Frozen ? await Client.EquivocationAsync(t, ct) : null;
            var offer = await Client.OpenOwnershipOfferAsync(t, ct);
            views.Add(new TeamView(t, roster, admin, me.IsOwner, confirmation, changed, equivocation,
                offer?.Offer.To == TeamsHex.Of(me.MemberId) ? offer.Offer : null, joins.Where(j => j.TeamId == t).ToList()));
        }
        Save();
        return Publish(views);
    }

    /// <summary>N9: a member whose root key differs from the one accepted before is frozen until its safety number is compared.</summary>
    private List<Guid> KeyChanges(string teamId, TeamRoster roster)
    {
        var known = Fingerprints(teamId);
        var frozen = Client.Status(teamId)["frozenPeers"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet();
        var changed = new List<Guid>();
        foreach (var m in roster.Members.Where(m => !m.IsMe))
        {
            var id = TeamsHex.Of(m.MemberId);
            if (!known.TryGetValue(id, out var fp)) known[id] = m.RootFingerprint;
            else if (fp != m.RootFingerprint && !frozen.Contains(id)) { Client.SetPeerFrozen(teamId, m.MemberId, true); frozen.Add(id); }
            if (frozen.Contains(id)) changed.Add(m.MemberId);
        }
        return changed;
    }

    private TeamSnapshot Publish(List<TeamView> views)
    {
        var id = Device.Identity!;
        Last = new TeamSnapshot(Account.Server, id.MemberId, id.DeviceId, views, Account.PendingJoins.ToList(), _removed.ToList());
        Changed?.Invoke(Last);
        return Last;
    }

    /// <summary>Refreshes now and every <see cref="RefreshInterval"/> until <see cref="SignOut"/>.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        var cts = _loop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await RefreshAsync(cts.Token); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception e) { Failed?.Invoke(e); }
                try { await Task.Delay(RefreshInterval, cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }

    /// <summary>Stops background work and forgets the account on this machine (the device's keys stay in the Keychain for signing in again).</summary>
    public void SignOut()
    {
        Dispose();
        File.Delete(AccountPath(_root));
    }

    public void Dispose()
    {
        _loop?.Cancel();
        _loop = null;
        Client.TeamWiped -= OnWiped;
        Server.Dispose();
        Device.Dispose();
    }

    private T Sync<T>(Func<T> f)
    {
        _gate.Wait();
        try { return f(); }
        finally { _gate.Release(); }
    }

    /// <summary><see cref="TeamItems"/> behind the session's lock (ombra-core takes one caller at a time).</summary>
    private sealed class SerializedItems(TeamSession s) : ITeamItems
    {
        public event Action<string>? ItemsChanged
        {
            add => s._items.ItemsChanged += value;
            remove => s._items.ItemsChanged -= value;
        }

        public Task<IReadOnlyList<TeamItemCopy>> SendAsync(string teamId, TeamItemDraft draft, IReadOnlyList<string> recipientMemberIds, CancellationToken ct = default) =>
            s.Locked(() => s._items.SendAsync(teamId, draft, recipientMemberIds, ct), ct);
        public Task EditAsync(string teamId, string itemId, ulong baseRev, TeamItemDraft delta, CancellationToken ct = default) =>
            s.Locked(() => s._items.EditAsync(teamId, itemId, baseRev, delta, ct), ct);
        public Task DeleteAsync(string teamId, string authorMemberId, string itemId, CancellationToken ct = default) =>
            s.Locked(() => s._items.DeleteAsync(teamId, authorMemberId, itemId, ct), ct);
        public Task SetStatusAsync(string teamId, string authorMemberId, string itemId, string status, string? note = null, CancellationToken ct = default) =>
            s.Locked(() => s._items.SetStatusAsync(teamId, authorMemberId, itemId, status, note, ct), ct);
        public IReadOnlyList<TeamItem> List(string teamId, int limit = 200, ulong? before = null) => s.Sync(() => s._items.List(teamId, limit, before));
        public TeamItem? Get(string teamId, string authorMemberId, string itemId) => s.Sync(() => s._items.Get(teamId, authorMemberId, itemId));
        public int PendingCount(string? teamId = null) => s.Sync(() => s._items.PendingCount(teamId));
        public Task<TeamDeliveryResult> DeliverAsync(CancellationToken ct = default) => s.Locked(() => s._items.DeliverAsync(ct), ct);
        public Task<TeamBlob> UploadAsync(string teamId, string recipientMemberId, Stream content, CancellationToken ct = default) =>
            s.Locked(() => s._items.UploadAsync(teamId, recipientMemberId, content, ct), ct);
        public Task<byte[]> DownloadAsync(TeamItem item, CancellationToken ct = default) => s.Locked(() => s._items.DownloadAsync(item, ct), ct);
    }
}
