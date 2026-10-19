using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>A member a share can go to.</summary>
public sealed record ShareMember(string MemberId, string Name);

/// <summary>A member in the recipient picker.</summary>
public sealed partial class ShareRecipientRow(ShareMember member) : ObservableObject
{
    public string MemberId => member.MemberId;
    public string Name => member.Name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Who a share goes to: everyone in the team, or the members picked (found by name). Reused by every "Send to…" of
/// the team (resources here, marks in MarksShared).
/// </summary>
public sealed partial class ShareRecipientsViewModel : ObservableObject
{
    private List<ShareRecipientRow> _all = [];

    public ObservableCollection<ShareRecipientRow> Members { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpecific), nameof(CanSend))]
    private bool _everyone = true;

    public bool IsSpecific
    {
        get => !Everyone;
        set => Everyone = !value;
    }

    /// <summary>Narrows the member list by name.</summary>
    [ObservableProperty] private string _filter = "";

    public bool HasMembers => _all.Count > 0;

    /// <summary>The member ids the share goes to.</summary>
    public IReadOnlyList<string> SelectedIds => Everyone ? _all.Select(m => m.MemberId).ToList() : _all.Where(m => m.IsSelected).Select(m => m.MemberId).ToList();

    public bool CanSend => SelectedIds.Count > 0;

    /// <summary>The team's members other than <paramref name="me"/>, by name; a member picked before stays picked.</summary>
    public void Load(IEnumerable<ShareMember> members, string? me)
    {
        var picked = _all.Where(m => m.IsSelected).Select(m => m.MemberId).ToHashSet(StringComparer.Ordinal);
        foreach (var m in _all) m.PropertyChanged -= OnRowChanged;
        _all = members.Where(m => m.MemberId != me).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => new ShareRecipientRow(m) { IsSelected = picked.Contains(m.MemberId) }).ToList();
        foreach (var m in _all) m.PropertyChanged += OnRowChanged;
        ApplyFilter();
        OnPropertyChanged(nameof(HasMembers));
        OnPropertyChanged(nameof(CanSend));
    }

    public void Reset()
    {
        Everyone = true;
        Filter = "";
        foreach (var m in _all) m.IsSelected = false;
    }

    /// <summary>Picks one member (and switches to specific members).</summary>
    public void Pick(string memberId)
    {
        Everyone = false;
        foreach (var m in _all.Where(m => m.MemberId == memberId)) m.IsSelected = true;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Members.Clear();
        foreach (var m in _all.Where(m => Filter.Length == 0 || m.Name.Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase))) Members.Add(m);
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShareRecipientRow.IsSelected)) return;
        if (sender is ShareRecipientRow { IsSelected: true }) Everyone = false;
        OnPropertyChanged(nameof(CanSend));
    }
}

/// <summary>A resource in my Inbox: who sent what, a preview, and Accept / Decline (then Open, or Apply for a patch).</summary>
public sealed partial class TeamInboxRow : ObservableObject
{
    private readonly TeamShareViewModel _page;

    internal TeamInboxRow(TeamShareViewModel page, TeamItem item, string from, string me)
    {
        _page = page;
        Item = item;
        From = from;
        Status = TeamShare.MyStatus(item, me);
    }

    public TeamItem Item { get; }
    public string From { get; }
    public string? Status { get; }
    public string Title => TeamShare.Title(Item);
    public string KindText => TeamShareViewModel.KindText(Item.Kind);
    public string Headline => Loc.F("{0} shared {1}", From, TeamShareViewModel.KindPhrase(Item.Kind));
    public string When => SessionItemViewModel.Relative(DateTimeOffset.FromUnixTimeSeconds((long)Item.CreatedAt), DateTimeOffset.Now);
    public string? Comment => TeamShare.Comment(Item);
    public bool HasComment => Comment is { Length: > 0 };
    public string Preview => TeamShareViewModel.PreviewOf(Item, _all);
    private IReadOnlyList<TeamItem> _all = [];
    internal TeamInboxRow WithAll(IReadOnlyList<TeamItem> all) { _all = all; return this; }
    public bool IsPending => Status is not (TeamItemStatuses.Accepted or TeamItemStatuses.Declined);
    public bool IsAccepted => Status == TeamItemStatuses.Accepted;
    public string StatusText => Status switch
    {
        TeamItemStatuses.Accepted => Loc.T("Accepted"),
        TeamItemStatuses.Declined => Loc.T("Declined"),
        _ => Loc.T("Waiting for you"),
    };
    /// <summary>After Accept: a link or collection opens again, a file shows, a patch can be applied (again).</summary>
    public bool CanOpen => IsAccepted && Item.Kind is TeamItemKinds.Link or TeamItemKinds.Collection or TeamItemKinds.File or TeamItemKinds.Skill or TeamItemKinds.Snapshot;
    public bool CanApply => IsAccepted && Item.Kind == TeamItemKinds.Edit;

    [ObservableProperty] private bool _isBusy;

    [RelayCommand] private Task Accept() => _page.AcceptAsync(this);
    [RelayCommand] private Task Decline() => _page.DeclineAsync(this);
    [RelayCommand] private Task Open() => _page.OpenAsync(this);
    [RelayCommand] private Task Apply() => _page.ApplyPatchAsync(this);
}

/// <summary>One recipient of something I shared, and what they did with it.</summary>
public sealed record TeamSentRecipient(string Name, string StatusText, string? Note)
{
    public bool HasNote => Note is { Length: > 0 };
    /// <summary>The row's secondary line: "Ivy · Not opened yet", the note after it when there is one.</summary>
    public string Line => HasNote ? $"{Name} · {StatusText} · {Note}" : $"{Name} · {StatusText}";
}

/// <summary>Something I shared: what, when, and every recipient's status.</summary>
public sealed class TeamSentRow(SentShare share, IReadOnlyList<TeamSentRecipient> recipients)
{
    public SentShare Share { get; } = share;
    public string Title => Share.Title;
    public string KindText => TeamShareViewModel.KindText(Share.Kind);
    public IReadOnlyList<TeamSentRecipient> Recipients { get; } = recipients;
    public string Summary => Loc.F("{0} of {1} answered", Share.Copies.Count(c => c.Status is not null), Share.Copies.Count);
}

/// <summary>
/// P5 resources (link, file, skill, patch, collection): the Share dialog (what, to whom: everyone or members by name),
/// the Inbox of what others sent me (Accept / Decline; nothing lands on disk or opens before Accept), Sent with each
/// recipient's status, and the toast and OS notification for what arrives. Marks (K4) are MarksShared's and not
/// listed here.
/// </summary>
public sealed partial class TeamShareViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private ITeamContext? _context;
    private ITeamItems? _items;
    private HashSet<string>? _known;
    private Func<ShareContent>? _make;
    private IReadOnlyList<TeamItem> _all = [];

    public TeamShareViewModel(MainViewModel owner) => _owner = owner;

    public ShareRecipientsViewModel Recipients { get; } = new();
    public ObservableCollection<TeamInboxRow> Inbox { get; } = [];
    public ObservableCollection<TeamSentRow> Sent { get; } = [];

    /// <summary>Resources waiting for my Accept or Decline (the rail's badge).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInbox), nameof(InboxBadge), nameof(InboxWaitingText))]
    private int _inboxCount;

    public bool HasInbox => InboxCount > 0;
    public string InboxBadge => InboxCount > 9 ? "9+" : InboxCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string InboxWaitingText => Loc.Plural(InboxCount, "{0} waiting", "{0} waiting");
    public bool HasInboxRows => Inbox.Count > 0;
    public bool HasSent => Sent.Count > 0;
    public bool CanShare => _context is { Items: not null } && TeamId is not null && Me is not null;

    private string? TeamId => _context?.SelectedTeamId;

    /// <summary>My member id as items name it (lowercase hex).</summary>
    private string? Me => _context?.MyMemberId is { } g ? TeamsHex.Of(g) : null;

    private IReadOnlyList<ShareMember> Members =>
        _context?.Members.Select(m => new ShareMember(TeamsHex.Of(m.MemberId), m.Name ?? TeamsHex.Of(m.MemberId)[..8])).ToList() ?? [];

    // ───────────── The team ─────────────

    /// <summary>The team account sharing goes through (null: signed out).</summary>
    public void Attach(ITeamContext? context)
    {
        if (_context is not null) _context.Changed -= OnContextChanged;
        _context = context;
        if (_context is not null) _context.Changed += OnContextChanged;
        OnContextChanged();
    }

    private void OnContextChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnContextChanged);
            return;
        }
        var items = _context?.Items;
        if (!ReferenceEquals(items, _items))
        {
            if (_items is not null) _items.ItemsChanged -= OnItemsChanged;
            _items = items;
            _known = null;
            if (_items is not null) _items.ItemsChanged += OnItemsChanged;
        }
        Recipients.Load(Members, Me);
        OnPropertyChanged(nameof(CanShare));
        Refresh();
    }

    private void OnItemsChanged(string teamId) => Dispatcher.UIThread.Post(Refresh);

    private int _refreshVersion;

    /// <summary>The latest read of the replica (tests wait for it).</summary>
    internal Task Refreshed { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the replica again off the UI thread (the account's lock may be held by a delivery); see <see cref="RefreshAsync"/>.</summary>
    public void Refresh() => Refreshed = RefreshAsync();

    private string NameOf(string memberId) =>
        Members.FirstOrDefault(m => m.MemberId == memberId)?.Name ?? (memberId.Length > 8 ? memberId[..8] : memberId);

    /// <summary>Reads the replica again: Inbox, Sent, the badge; what arrived since the last read is told (toast, OS notification).</summary>
    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        if (_items is not { } items || TeamId is not { } team || Me is not { } me)
        {
            Inbox.Clear();
            Sent.Clear();
            InboxCount = 0;
            OnPropertyChanged(nameof(HasInboxRows));
            OnPropertyChanged(nameof(HasSent));
            return;
        }
        IReadOnlyList<TeamItem> all;
        try { all = await Task.Run(() => items.List(team)); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TeamsServerException) { return; }
        if (version != _refreshVersion || !ReferenceEquals(items, _items)) return; // a newer read wins
        var incoming = TeamShare.Incoming(all, me);
        var first = _known is null;
        _known ??= [];
        var fresh = incoming.Where(i => _known.Add(i.AuthorMemberId + "/" + i.ItemId) && TeamShare.IsPending(i, me)).ToList();
        Inbox.Clear();
        _all = all;
        foreach (var i in incoming) Inbox.Add(new TeamInboxRow(this, i, NameOf(i.AuthorMemberId), me).WithAll(all));
        InboxCount = incoming.Count(i => TeamShare.IsPending(i, me));
        Sent.Clear();
        foreach (var s in TeamShare.Sent(all, me))
            Sent.Add(new TeamSentRow(s, s.Copies.Select(c => new TeamSentRecipient(NameOf(c.RecipientMemberId), StatusText(c), c.Note)).ToList()));
        OnPropertyChanged(nameof(HasInboxRows));
        OnPropertyChanged(nameof(HasSent));
        // What was there when the account opened is in the Inbox (missed while offline); only what arrives now is told.
        if (!first) foreach (var i in fresh) Announce(i);
    }

    private void Announce(TeamItem item)
    {
        var text = Loc.F("{0} shared {1} “{2}”", NameOf(item.AuthorMemberId), KindPhrase(item.Kind), TeamShare.Title(item));
        _owner.ShowTeamToast(text, () => _owner.ShowTeamInbox());
    }

    internal static string StatusText(SentCopy c) => c.Status switch
    {
        TeamItemStatuses.Accepted => Loc.T("Accepted"),
        TeamItemStatuses.Declined => Loc.T("Declined"),
        _ when c.Seen => Loc.T("Seen"),
        _ => Loc.T("Not opened yet"),
    };

    internal static string KindText(string kind) => kind switch
    {
        TeamItemKinds.Link => Loc.T("Link"),
        TeamItemKinds.File => Loc.T("File"),
        TeamItemKinds.Skill => Loc.T("Skill"),
        TeamItemKinds.Edit => Loc.T("Patch"),
        TeamItemKinds.Snapshot => Loc.T("Screenshot"),
        TeamItemKinds.Collection => Loc.T("Collection"),
        _ => kind,
    };

    /// <summary>"a file", "a skill"… as the toast says it ("Anna shared a file “notes.md”").</summary>
    internal static string KindPhrase(string kind) => kind switch
    {
        TeamItemKinds.Link => Loc.T("a link"),
        TeamItemKinds.File => Loc.T("a file"),
        TeamItemKinds.Skill => Loc.T("a skill"),
        TeamItemKinds.Edit => Loc.T("a patch"),
        TeamItemKinds.Snapshot => Loc.T("a screenshot"),
        TeamItemKinds.Collection => Loc.T("a collection"),
        _ => kind,
    };

    /// <summary>What is inside, before Accept: the address, the file's size, the skill's files, the patch's files, the pages.</summary>
    internal static string PreviewOf(TeamItem i, IReadOnlyList<TeamItem> all) => i.Kind switch
    {
        TeamItemKinds.Link => TeamShare.Url(i) ?? "",
        TeamItemKinds.File => Loc.F("{0} · {1}", TeamShare.FileName(i) ?? "", Size(TeamShare.Size(i))),
        TeamItemKinds.Snapshot => Loc.F("{0} · {1}", TeamShare.SourceUrl(i) ?? "", Size(TeamShare.Size(i))),
        TeamItemKinds.Skill => (TeamShare.Description(i) is { Length: > 0 } d ? d + " · " : "") + Size(TeamShare.Size(i)),
        TeamItemKinds.Edit => (TeamShare.BaseCommit(i) is { Length: >= 7 } b ? Loc.F("On {0}", b[..7]) + " · " : "") + string.Join(", ", TeamShare.PatchFiles(i)),
        TeamItemKinds.Collection => string.Join("\n", TeamShare.Links(i, all).Select(l => l.Title is { Length: > 0 } t && t != l.Url ? t + " — " + l.Url : l.Url)),
        _ => "",
    };

    private static string Size(ulong bytes) => bytes < 1024 ? Loc.F("{0} B", bytes) : Loc.F("{0} KB", Math.Round(bytes / 1024.0, 1));

    // ───────────── Sharing ─────────────

    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private string _dialogTitle = "";
    [ObservableProperty] private string _dialogDetail = "";
    /// <summary>The full path behind a shortened <see cref="DialogDetail"/> (its tooltip); null when nothing is shortened.</summary>
    [ObservableProperty] private string? _dialogDetailTip;
    [ObservableProperty] private string _comment = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    public bool HasError => Error.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    /// <summary>
    /// Opens the Share dialog for something to share; <paramref name="make"/> builds its draft on Send (the bytes are read
    /// then). <paramref name="detailTip"/>: the full path when <paramref name="detail"/> shows a short one. Without a team
    /// the user is told where to sign in, and nothing opens.
    /// </summary>
    public void Begin(string kind, string title, string detail, Func<ShareContent> make, string? detailTip = null)
    {
        if (!CanShare)
        {
            _owner.Notify("IconAlert", Loc.T("Join a team to share"), Loc.T("Sign in to a team server and join a team on the Team page first."));
            return;
        }
        _make = make;
        DialogTitle = Loc.F("Share {0} “{1}”", KindPhrase(kind), title);
        DialogDetail = detail;
        DialogDetailTip = detailTip;
        Comment = "";
        Error = "";
        Recipients.Reset();
        Recipients.Load(Members, Me);
        IsDialogOpen = true;
    }

    [RelayCommand]
    private void CancelShare()
    {
        IsDialogOpen = false;
        _make = null;
    }

    private bool CanSendNow() => !IsSending;

    [RelayCommand(CanExecute = nameof(CanSendNow))]
    private async Task SendAsync()
    {
        if (_make is null || _items is null || TeamId is not { } team) return;
        var to = Recipients.SelectedIds;
        if (to.Count == 0)
        {
            Error = Recipients.HasMembers ? Loc.T("Pick at least one member.") : Loc.T("There is no one else in the team yet.");
            return;
        }
        IsSending = true;
        Error = "";
        try
        {
            var content = await Task.Run(_make);
            var items = _items;
            var comment = Comment;
            await Task.Run(() => content.SendAsync(items, team, to, comment));
            IsDialogOpen = false;
            _make = null;
            _owner.Notify("IconCheck", Loc.T("Shared"), Recipients.Everyone ? Loc.T("Sent to everyone in the team.") : Loc.Plural(to.Count, "Sent to {0} member.", "Sent to {0} members."));
            _ = DeliverQuietlyAsync();
        }
        catch (TeamShareTooLargeException e)
        {
            Error = Loc.F("It is too large to share: {0} KB, at most {1} KB.", e.Bytes / 1024, TeamShare.MaxContentBytes / 1024);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TeamsServerException)
        {
            Error = e.Message;
        }
        finally { IsSending = false; }
    }

    /// <summary>Sends the outbox now (it is sent on the next refresh anyway; offline it waits there).</summary>
    private async Task DeliverQuietlyAsync()
    {
        if (_items is not { } items) return;
        try { await Task.Run(() => items.DeliverAsync()); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TeamsServerException or HttpRequestException or OperationCanceledException) { }
    }

    // ───────────── Inbox actions ─────────────

    private async Task SetStatusAsync(TeamInboxRow row, string status)
    {
        if (_items is null || TeamId is not { } team) return;
        await _items.SetStatusAsync(team, row.Item.AuthorMemberId, row.Item.ItemId, status);
        _ = DeliverQuietlyAsync();
    }

    /// <summary>
    /// Accept: the status goes to the sender first (the content of a file or skill may be fetched only after it), then the
    /// resource lands. When landing fails (offline, disk) the item stays accepted and Open tries again.
    /// </summary>
    internal async Task AcceptAsync(TeamInboxRow row)
    {
        if (row.IsBusy) return;
        row.IsBusy = true;
        try
        {
            await SetStatusAsync(row, TeamItemStatuses.Accepted);
            await LandAsync(row.Item);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or TeamsServerException
                                       or HttpRequestException or System.Security.Cryptography.CryptographicException)
        {
            _owner.Notify("IconAlert", Loc.T("Couldn't accept"), e.Message);
        }
        finally
        {
            row.IsBusy = false;
            await RefreshAsync();
        }
    }

    internal async Task DeclineAsync(TeamInboxRow row)
    {
        if (row.IsBusy) return;
        row.IsBusy = true;
        try
        {
            await SetStatusAsync(row, TeamItemStatuses.Declined);
            await RefreshAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TeamsServerException)
        {
            _owner.Notify("IconAlert", Loc.T("Couldn't decline"), e.Message);
        }
        finally { row.IsBusy = false; }
    }

    internal async Task OpenAsync(TeamInboxRow row)
    {
        try { await LandAsync(row.Item); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or TeamsServerException
                                       or HttpRequestException or System.Security.Cryptography.CryptographicException)
        {
            _owner.Notify("IconAlert", Loc.T("Couldn't open"), e.Message);
        }
    }

    /// <summary>The Inbox was looked at: what is in it is <c>seen</c> for its senders (not yet accepted or declined).</summary>
    public async Task MarkInboxSeenAsync()
    {
        if (_items is null || TeamId is not { } team || Me is not { } me) return;
        var unseen = Inbox.Where(r => r.IsPending && !TeamShare.Seen(r.Item, me)).Select(r => r.Item).ToList();
        if (unseen.Count == 0) return;
        try
        {
            foreach (var i in unseen) await _items.SetStatusAsync(team, i.AuthorMemberId, i.ItemId, TeamItemStatuses.Seen);
            _ = DeliverQuietlyAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TeamsServerException) { }
    }

    /// <summary>
    /// Applies an accepted edit only to a clone of the repository it names (its signed <c>repoFingerprint</c>) that has
    /// its base commit: the open project when it is one, else the folder the user picks; never another repository.
    /// </summary>
    internal async Task ApplyPatchAsync(TeamInboxRow row)
    {
        if (TeamShare.PatchText(row.Item) is not { } patch) return;
        if (TeamShare.RepoFingerprint(row.Item) is not { } repo)
        {
            _owner.Notify("IconAlert", Loc.T("The patch doesn't apply"), Loc.T("It doesn't say which repository it is for."));
            return;
        }
        var project = _owner.ProjectFolder is { } open && await IsRepoAsync(open, repo) ? open : null;
        if (project is null)
        {
            _owner.Notify("IconAlert", Loc.T("Pick the patch's repository"), Loc.T("The open project is another repository. Pick a folder with a clone of the one the patch is for."));
            if (await _owner.PickFolderAsync() is not { } picked) return;
            if (!await IsRepoAsync(picked, repo))
            {
                _owner.Notify("IconAlert", Loc.T("The patch doesn't apply"), Loc.T("This folder is another repository."));
                return;
            }
            project = picked;
        }
        if (TeamShare.BaseCommit(row.Item) is { } baseCommit && !await RepoIdentity.HasCommitAsync(project, baseCommit, _owner.Lifetime, _owner.RunTool))
        {
            _owner.Notify("IconAlert", Loc.T("The patch doesn't apply"), Loc.F("Commit {0} is not in this clone. Fetch it, then apply again.", baseCommit[..Math.Min(12, baseCommit.Length)]));
            return;
        }
        var error = await GitReviewApply(project, patch);
        if (error is null)
        {
            _owner.Notify("IconCheck", Loc.T("Patch applied"), Loc.T("The changes are in the Changes tab."));
            if (project == _owner.ProjectFolder) _owner.ShowChangesPane();
        }
        else _owner.Notify("IconAlert", Loc.T("The patch doesn't apply"), error);
    }

    private async Task<bool> IsRepoAsync(string folder, string fingerprint) =>
        await RepoIdentity.OfAsync(folder, _owner.Lifetime, _owner.RunTool) is { } id && id.Fingerprint == fingerprint;

    private Task<string?> GitReviewApply(string project, string patch) =>
        Services.GitReview.ApplyPatchAsync(_owner.RunTool, project, patch, _owner.Lifetime);

    /// <summary>
    /// Puts an accepted resource in the workspace: a link or collection opens in the browser, a file is saved in the
    /// project and shown, a skill is installed in the user's skills, a patch is offered (Apply). Null when it could not.
    /// </summary>
    private async Task LandAsync(TeamItem item)
    {
        // The replica's copy, with my Accept on it (the content is handed out only for an accepted item)
        item = _items is { } its && TeamId is { } team ? await Task.Run(() => its.Get(team, item.AuthorMemberId, item.ItemId)) ?? item : item;
        switch (item.Kind)
        {
            case TeamItemKinds.Link:
            case TeamItemKinds.Collection:
                _owner.OpenSharedLinks(TeamShare.Links(item, _all).Select(l => l.Url).ToList());
                return;
            case TeamItemKinds.File:
            case TeamItemKinds.Snapshot:
                var data = await Task.Run(() => _items!.DownloadAsync(item));
                var folder = _owner.ProjectFolder ?? DownloadsFolder();
                var path = await Task.Run(() => TeamShare.SaveFile(item, data, folder));
                _owner.Notify("IconCheck", Loc.T("File saved"), path);
                if (_owner.ProjectFolder is not null) _owner.OpenPath(new Controls.PathTarget(path, null, Controls.PathKind.File));
                return;
            case TeamItemKinds.Skill:
                var zip = await Task.Run(() => _items!.DownloadAsync(item));
                var root = await _owner.UserSkillsFolderAsync();
                var dir = await Task.Run(() => TeamShare.InstallSkill(item, zip, root));
                _owner.Notify("IconCheck", Loc.T("Skill installed"), Loc.F("{0}. omp offers it from its next start.", dir));
                return;
        }
        // An edit: Apply on the row, into the open project
    }

    private static string DownloadsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
