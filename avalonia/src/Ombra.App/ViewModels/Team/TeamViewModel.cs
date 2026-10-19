using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>A member row of the Team page.</summary>
public sealed partial class TeamMemberRow(TeamViewModel page, TeamMember member, bool keyChanged) : ObservableObject
{
    public TeamMember Member { get; } = member;
    public string Name => Member.Name ?? Loc.T("(no name yet)");
    public string RoleText => TeamViewModel.RoleName(Member.Role);
    /// <summary>The full member ID, for the name's tooltip (Technical details hold the team's and mine).</summary>
    public string Id => TeamsHex.Of(Member.MemberId);
    public bool IsMe => Member.IsMe;
    public bool KeyChanged { get; } = keyChanged;
    public bool CanRemove => page.IsAdmin && !Member.IsMe && !Member.IsOwner;
    public bool CanMakeOwner => page.IsOwner && !Member.IsMe;
    public int DeviceCount => Member.Devices.Count(d => !d.Revoked);

    [ObservableProperty] private string? _safetyNumber;
    /// <summary>Remove… in the row's menu asks first, inline under the row (as a plugin source's removal).</summary>
    [ObservableProperty] private bool _isConfirmingRemove;
    public string RemoveQuestion => Loc.F("Remove “{0}”?", Name);

    [RelayCommand] private void ShowSafetyNumber() => SafetyNumber = SafetyNumber is null ? page.SafetyNumberOf(Member) : null;
    [RelayCommand] private void AskRemove() => IsConfirmingRemove = true;
    [RelayCommand] private void CancelRemove() => IsConfirmingRemove = false;
    [RelayCommand] private Task Remove() { IsConfirmingRemove = false; return page.RemoveAsync(this); }
    [RelayCommand] private Task MakeOwner() => page.OfferOwnershipAsync(this);
    [RelayCommand] private void ConfirmKey() => page.ConfirmKey(this);
}

/// <summary>A device of mine.</summary>
public sealed partial class TeamDeviceRow(TeamViewModel page, TeamDevice device) : ObservableObject
{
    public TeamDevice Device { get; } = device;
    public string Name => Device.Name ?? Loc.T("Unnamed device");
    public string Id => TeamsHex.Of(Device.DeviceId);
    public bool IsThis => Device.IsThisDevice;
    public bool CanRevoke => !Device.IsThisDevice && !Device.Revoked;
    public string State => Device.Revoked ? Loc.T("Revoked") : Device.IsThisDevice ? Loc.T("This device") : "";
    [RelayCommand] private Task Revoke() => page.RevokeAsync(this);
}

/// <summary>A join request waiting for an admin.</summary>
public sealed partial class TeamJoinRow(TeamViewModel page, JoinReview join) : ObservableObject
{
    public JoinReview Join { get; } = join;
    public string Id => TeamsHex.Of(Join.MemberId);
    public string Detail => Join.Valid ? Loc.F("{0} · {1} device(s)", TeamViewModel.RoleName(Join.Role), Join.Devices.Count) : Loc.F("Invalid request: {0}", Join.Problem ?? "");
    public bool CanApprove => Join.Valid;
    [RelayCommand] private Task Approve() => page.ApproveAsync(this);
}

/// <summary>
/// The rail's Team (Views/Team/TeamPage) and Settings › Account: sign in to a team server, create or join a team,
/// members and roles, devices and pairing, the banners of the team's state and the owner's equivocation decision.
/// Everything runs through <see cref="TeamSession"/>; this class only shapes its snapshots for the views.
/// </summary>
public sealed partial class TeamViewModel : ObservableObject
{
    private readonly string _root;
    private readonly ITeamsKeyStore _keys;
    private readonly IMachineIdentity _machine;
    private readonly Func<string, Task> _copy;
    private ExistingDevicePairing? _pairing;
    private NewDevicePairing? _newPairing;
    private TeamSnapshot? _snapshot;

    public TeamViewModel(string root, ITeamsKeyStore keys, IMachineIdentity machine, Func<string, Task> copy)
    {
        _root = root;
        _keys = keys;
        _machine = machine;
        _copy = copy;
    }

    /// <summary>The machine-local teams root (account.json and each server's profile).</summary>
    public string Root => _root;

    /// <summary>Opens the saved account (if any) and starts its background refresh.</summary>
    public void Load()
    {
        if (Session is not null) return;
        try { Attach(TeamSession.Load(_root, _keys, _machine)); }
        catch (Exception e) { Error = e.Message; }
    }

    public TeamSession? Session { get; private set; }
    public event Action<TeamSession?>? SessionChanged;
    public bool IsSignedIn => Session is not null;
    public bool IsSignedOut => Session is null;

    // ── forms ──
    [ObservableProperty] private string _serverAddress = "";
    [ObservableProperty] private string _serverPin = "";
    [ObservableProperty] private string _bootstrapCode = "";
    [ObservableProperty] private string _inviteLinkText = "";
    [ObservableProperty] private string _pairLinkText = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InviteRoleText))] private string _inviteRole = "member";

    // ── state ──
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    public bool HasError => Error is not null;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRecoveryKey))] private string? _recoveryKey;
    public bool HasRecoveryKey => RecoveryKey is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInvite))] private string? _invite;
    public bool HasInvite => Invite is not null;

    public string? SelectedTeamId => _snapshot?.Teams.FirstOrDefault()?.TeamId;
    public Guid? MyMemberId => _snapshot?.MemberId;
    public IReadOnlyList<TeamMember> Members => Team?.Roster.Members ?? [];
    private TeamView? Team => _snapshot?.Teams.FirstOrDefault();
    public bool HasTeam => Team is not null;
    public bool NoTeam => IsSignedIn && Team is null && !IsWaitingForApproval;
    public bool IsWaitingForApproval => _snapshot?.WaitingForApproval.Count > 0 && Team is null;
    public bool IsAdmin => Team?.IsAdmin == true;
    public bool IsOwner => Team?.IsOwner == true;
    public string TeamId => Team?.TeamId ?? "";
    public string MyId => MyMemberId is { } m ? TeamsHex.Of(m) : "";
    public string Server => Session?.Account.Server ?? "";
    public string Pin => Session?.Account.Pin ?? Loc.T("(domain certificate)");
    public string InviteRoleText => RoleName(InviteRole);

    public ObservableCollection<TeamMemberRow> MemberRows { get; } = [];
    public ObservableCollection<TeamDeviceRow> DeviceRows { get; } = [];
    public ObservableCollection<TeamJoinRow> JoinRows { get; } = [];
    public bool HasJoins => JoinRows.Count > 0;

    // ── banners ──
    public bool NotConfirmed => Team?.Confirmation is { Confirmed: false };
    public string NotConfirmedText => Team?.Confirmation is { } c ? ConfirmationText(c.Required, c.AdminVotes) : "";

    /// <summary>Who has to confirm the team's state, and how many admins already did when more than one is needed.</summary>
    internal static string ConfirmationText(int required, int votes)
    {
        var text = required <= 1
            ? Loc.T("In the last 72 hours neither the owner nor an admin has confirmed the team's state. Sending is paused.")
            : Loc.Plural(required,
                "In the last 72 hours neither the owner nor at least {0} admin has confirmed the team's state. Sending is paused.",
                "In the last 72 hours neither the owner nor at least {0} admins have confirmed the team's state. Sending is paused.");
        return required > 1 && votes > 0
            ? text + " " + Loc.Plural(votes, "So far {0} admin has confirmed it.", "So far {0} admins have confirmed it.")
            : text;
    }

    public IReadOnlyList<string> RemovedTeams => _snapshot?.Removed ?? [];
    public bool WasRemoved => RemovedTeams.Count > 0;
    public string RemovedText => Loc.T("You were removed from the team. Its data on this device was erased (keys, messages and shared items).");
    public bool KeyChanged => Team?.KeyChanged.Count > 0;
    public string KeyChangedText => Team is { } t ? Loc.F("The key of {0} changed. Compare the safety number with them, then confirm.",
        string.Join(", ", t.Roster.Members.Where(m => t.KeyChanged.Contains(m.MemberId)).Select(m => m.Name ?? ShortId(m.MemberId)))) : "";
    public bool HasOwnershipOffer => Team?.OfferToMe is not null;

    // ── equivocation ──
    public bool HasEquivocation => Team?.Equivocation is not null;
    public bool CanResolve => HasEquivocation && IsOwner;
    [ObservableProperty] private bool _isEquivocationOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Undone))] private bool _keepKnown = true;
    [ObservableProperty] private bool _undoneConfirmed;
    public IReadOnlyList<string> Undone => Team?.Equivocation is { } e
        ? (KeepKnown ? e.UndoneIfKeep : e.UndoneIfSwitch).Select(Describe).ToList() : [];

    /// <summary>A change the choice undoes (ombra-core's roster::undone), as a sentence with names and roles.</summary>
    internal string Describe(System.Text.Json.Nodes.JsonObject op)
    {
        string? S(string key) => op[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        string Who(string key) => S(key) is { } hex
            ? Members.FirstOrDefault(m => string.Equals(TeamsHex.Of(m.MemberId), hex, StringComparison.OrdinalIgnoreCase)) is { } m
                ? m.Name ?? Loc.T("(no name yet)") : Loc.T("a former member")
            : Loc.T("a member");
        return S("kind") switch
        {
            "revokeDevice" => Loc.F("The revocation of a device of {0} is undone: it gets access to the team again.", Who("memberId")),
            "removeMember" => Loc.F("The removal of {0} is undone: they return to the team.", Who("memberId")),
            "demote" => S("restored") == "owner"
                ? Loc.F("The demotion of {0} is undone: they are the owner again.", Who("memberId"))
                : Loc.F("The demotion of {0} is undone: they are an admin again.", Who("memberId")),
            "owner" => Loc.F("The transfer of ownership to {0} is undone: {1} stays the owner.", Who("to"), Who("restored")),
            "pins" => Loc.T("The change of the team's pinned keys is undone."),
            "revokeAccess" => Loc.F("The revocation of {0}'s access to a project is undone: they get it back.", Who("memberId")),
            "grantAccess" => Loc.F("The project access given to {0} is undone: they lose it.", Who("memberId")),
            _ => Loc.T("Another change to the team is undone."),
        };
    }
    public string KnownSigners => Team?.Equivocation is { } e ? string.Join(", ", e.KnownSigners.Select(s => s[..Math.Min(8, s.Length)])) : "";
    public string ConflictingSigners => Team?.Equivocation is { } e ? string.Join(", ", e.ConflictingSigners.Select(s => s[..Math.Min(8, s.Length)])) : "";

    // ── pairing ──
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPairing))] private string? _pairingLink;
    [ObservableProperty] private Bitmap? _pairingQr;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPairingWords))] private string? _pairingWords;
    public bool IsPairing => PairingLink is not null;
    public bool HasPairingWords => PairingWords is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNewDeviceWords))] private string? _newDeviceWords;
    public string KeepKnownText => Loc.F("Keep the version this device knows (signed by {0})", KnownSigners);
    public string SwitchText => Loc.F("Switch to the other version (signed by {0})", ConflictingSigners);
    public bool HasNewDeviceWords => NewDeviceWords is not null;

    /// <summary>The last 8 hex digits (ids are UUIDv7: the first ones are a timestamp, alike for ids made close together).</summary>
    internal static string ShortId(Guid id) => TeamsHex.Of(id)[^8..];

    internal static string RoleName(string? role) => role switch { "owner" => Loc.T("Owner"), "admin" => Loc.T("Admin"), _ => Loc.T("Member") };

    internal string SafetyNumberOf(TeamMember m)
    {
        var s = Session!.SafetyNumber(Team!.TeamId, m.MemberId);
        return string.Join(" ", s.Where(char.IsDigit).Chunk(5).Select(c => new string(c)));
    }

    private void Attach(TeamSession? s)
    {
        Session = s;
        if (s is not null)
        {
            DisplayName = s.Account.Name ?? "";
            s.Changed += snap => Dispatcher.UIThread.Post(() => { if (Session == s) Show(snap); });
            s.Failed += e => Dispatcher.UIThread.Post(() => { if (Session == s) Error = e.Message; });
            s.Start();
        }
        else Show(null);
        OnPropertyChanged(string.Empty);
        SessionChanged?.Invoke(s);
    }

    /// <summary>Puts a refresh on the page (UI thread).</summary>
    internal void Show(TeamSnapshot? snap)
    {
        _snapshot = snap;
        MemberRows.Clear();
        DeviceRows.Clear();
        JoinRows.Clear();
        if (Team is { } t)
        {
            foreach (var m in t.Roster.Members.OrderBy(m => m.Role switch { "owner" => 0, "admin" => 1, _ => 2 }).ThenBy(m => m.Name))
                MemberRows.Add(new TeamMemberRow(this, m, t.KeyChanged.Contains(m.MemberId)));
            foreach (var d in t.Roster.Members.First(m => m.IsMe).Devices) DeviceRows.Add(new TeamDeviceRow(this, d));
            foreach (var j in t.PendingJoins) JoinRows.Add(new TeamJoinRow(this, j));
        }
        if (!HasEquivocation) IsEquivocationOpen = false;
        OnPropertyChanged(string.Empty);
        Changed?.Invoke();
    }

    /// <summary>Raised after every snapshot shown (UI thread).</summary>
    public event Action? Changed;

    private async Task Run(Func<Task> f)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        try { await f(); }
        catch (Exception e) { Error = e.Message; }
        finally { IsBusy = false; }
    }

    private async Task RefreshNow()
    {
        if (Session is { } s) Show(await Task.Run(() => s.RefreshAsync()));
    }

    // ── commands ──

    [RelayCommand]
    private Task Connect() => Run(async () => Attach(await Task.Run(() => TeamSession.ConnectAsync(_root, ServerAddress, ServerPin, _keys, _machine))));

    [RelayCommand]
    private Task CreateTeam() => Run(async () =>
    {
        if (Session is null) Attach(await Task.Run(() => TeamSession.ConnectAsync(_root, ServerAddress, ServerPin, _keys, _machine)));
        var t = await Task.Run(() => Session!.CreateTeamAsync(BootstrapCode));
        BootstrapCode = "";
        RecoveryKey = t.RecoveryKeyText;
        await RefreshNow();
    });

    /// <summary>The recovery key is shown once: after "I saved it" it is gone from memory.</summary>
    [RelayCommand] private void DismissRecoveryKey() => RecoveryKey = null;

    [RelayCommand] private Task CopyRecoveryKey() => RecoveryKey is { } k ? _copy(k) : Task.CompletedTask;

    [RelayCommand]
    private Task Join() => Run(async () =>
    {
        var s = await Task.Run(() => TeamSession.JoinAsync(_root, Session, InviteLinkText, _keys, _machine));
        InviteLinkText = "";
        if (s != Session) Attach(s);
        await RefreshNow();
    });

    [RelayCommand]
    private Task CreateInvite() => Run(async () => Invite = await Task.Run(() => Session!.InviteAsync(Team!.TeamId, InviteRole)));

    [RelayCommand] private Task CopyInvite() => Invite is { } i ? _copy(i) : Task.CompletedTask;

    [RelayCommand] private Task CopyMyId() => MyId.Length > 0 ? _copy(MyId) : Task.CompletedTask;
    [RelayCommand] private Task CopyTeamId() => TeamId.Length > 0 ? _copy(TeamId) : Task.CompletedTask;
    [RelayCommand] private Task CopyServer() => Server.Length > 0 ? _copy(Server) : Task.CompletedTask;
    [RelayCommand] private Task CopyPin() => Session?.Account.Pin is { } p ? _copy(p) : Task.CompletedTask;

    internal Task ApproveAsync(TeamJoinRow r) => Run(async () => { await Task.Run(() => Session!.ApproveAsync(r.Join)); await RefreshNow(); });

    internal Task RemoveAsync(TeamMemberRow r) => Run(async () => { await Task.Run(() => Session!.RemoveMemberAsync(Team!.TeamId, r.Member.MemberId)); await RefreshNow(); });

    internal Task OfferOwnershipAsync(TeamMemberRow r) => Run(async () => { await Task.Run(() => Session!.OfferOwnershipAsync(Team!.TeamId, r.Member.MemberId)); await RefreshNow(); });

    [RelayCommand]
    private Task AcceptOwnership() => Run(async () => { await Task.Run(() => Session!.AcceptOwnershipAsync(Team!.TeamId)); await RefreshNow(); });

    internal Task RevokeAsync(TeamDeviceRow r) => Run(async () => { await Task.Run(() => Session!.RevokeDeviceAsync(r.Device.DeviceId)); await RefreshNow(); });

    internal void ConfirmKey(TeamMemberRow r)
    {
        Session!.ConfirmKey(Team!.TeamId, r.Member);
        _ = Run(RefreshNow);
    }

    [RelayCommand]
    private Task SaveName() => Run(() => Task.Run(() => Session!.SetNameAsync(DisplayName)));

    [RelayCommand]
    private void DismissRemoved()
    {
        foreach (var t in RemovedTeams) Session?.DismissRemoved(t);
        _ = Run(RefreshNow);
    }

    [RelayCommand] private void OpenEquivocation() => IsEquivocationOpen = true;
    [RelayCommand] private void CloseEquivocation() => IsEquivocationOpen = false;

    [RelayCommand]
    private Task ResolveEquivocation() => Run(async () =>
    {
        await Task.Run(() => Session!.ResolveEquivocationAsync(Team!.Equivocation!, KeepKnown, UndoneConfirmed));
        UndoneConfirmed = false;
        IsEquivocationOpen = false;
        await RefreshNow();
    });

    // ── pairing: this device adds another ──

    [RelayCommand]
    private Task StartPairing() => Run(async () =>
    {
        var (p, link) = await Task.Run(() => Session!.StartPairingAsync());
        _pairing = p;
        PairingLink = link.ToString();
        PairingQr = QrImage.Of(PairingLink);
        _ = WaitForNewDevice(p);
    });

    private async Task WaitForNewDevice(ExistingDevicePairing p)
    {
        try
        {
            var words = await Task.Run(async () => { await p.WaitForNewDeviceAsync(TimeSpan.FromSeconds(1)); return await p.RevealAsync(); });
            if (_pairing == p) PairingWords = string.Join(" ", words);
        }
        catch (Exception e) { if (_pairing == p) Error = e.Message; }
    }

    [RelayCommand]
    private Task ConfirmPairing() => Run(async () =>
    {
        var p = _pairing!;
        await Task.Run(() => Session!.CertifyAsync(p));
        CancelPairing();
        await RefreshNow();
    });

    [RelayCommand]
    private void CancelPairing()
    {
        _pairing = null;
        PairingLink = null;
        PairingWords = null;
        PairingQr = null;
    }

    [RelayCommand] private Task CopyPairingLink() => PairingLink is { } l ? _copy(l) : Task.CompletedTask;

    // ── pairing: this device is the new one ──

    [RelayCommand]
    private Task PairThisDevice() => Run(async () =>
    {
        var (s, p) = await Task.Run(() => TeamSession.PairNewDeviceAsync(_root, PairLinkText, _keys, _machine));
        _newPairing = p;
        Session?.Dispose();
        Session = s;
        PairLinkText = "";
        OnPropertyChanged(string.Empty);
        NewDeviceWords = string.Join(" ", await Task.Run(() => p.WaitForWordsAsync(TimeSpan.FromSeconds(1))));
    });

    [RelayCommand]
    private Task FinishPairing() => Run(async () =>
    {
        var s = Session!;
        await Task.Run(() => s.FinishPairingAsync(_newPairing!));
        _newPairing = null;
        NewDeviceWords = null;
        Session = null;
        Attach(s);
        await RefreshNow();
    });

    [RelayCommand]
    private void SignOut()
    {
        CancelPairing();
        Session?.SignOut();
        _newPairing = null;
        NewDeviceWords = null;
        Invite = null;
        RecoveryKey = null;
        Attach(null);
    }
}
