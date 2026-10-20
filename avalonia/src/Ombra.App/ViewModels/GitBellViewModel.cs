using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;

namespace Ombra.App.ViewModels;

/// <summary>
/// A project folder of an open chat; <see cref="AutoFetch"/>: the folder is trusted (Workspace trust), so its remote is
/// fetched in the background too (an untrusted one only when the user asks: Check now).
/// </summary>
public sealed record WatchedFolder(string Path, bool AutoFetch);

/// <summary>What the bell needs from the window: the open chats' folders, the settings, and where its actions lead.</summary>
/// <param name="BeginUpdate">
/// Admission for an update of the repository at the given top: null while another update holds it, or while an agent
/// turn runs (or input waits) in it unless the second argument lets the update run under the turn (Update now);
/// otherwise a hold that refuses new turns there until it is disposed.
/// </param>
/// <param name="AgentFiles">The files (relative to the given top, '/') the turns running in that repository changed or opened.</param>
public sealed record GitBellHost(
    Func<IReadOnlyList<WatchedFolder>> Folders,
    Func<bool> FetchAllowed,
    Func<string, bool, IDisposable?> BeginUpdate,
    Func<string, IReadOnlyCollection<string>> AgentFiles,
    Action<string, string> AskAgent,
    Func<string, string, Task> ShowIncoming,
    Action OpenTeamInbox);

/// <summary>
/// The header's bell: per repository of an open chat, what teammates pushed that I do not have (the branch's upstream,
/// and the remote's default branch while I am on another one), with the careful update; under them the team Inbox's
/// waiting items. The watch runs <c>git fetch</c> every few minutes, when the window comes to the front and after a
/// turn ends, only while Settings allow it (on, strict network privacy off).
/// </summary>
public sealed partial class GitBellViewModel : ObservableObject
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FocusThrottle = TimeSpan.FromMinutes(1);

    private readonly GitBellHost _host;
    private readonly ObservableCollection<TeamInboxRow> _team;
    private readonly Dictionary<string, string?> _tops = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checking = new(1, 1);
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public GitBellViewModel(GitBellHost host, ObservableCollection<TeamInboxRow> team)
    {
        _host = host;
        _team = team;
        _team.CollectionChanged += OnTeamChanged;
        Rebuild();
    }

    /// <summary>One row per repository of an open chat with something new on its remote.</summary>
    public ObservableCollection<RepoUpdateRow> Repos { get; } = [];

    /// <summary>The team Inbox's items waiting for Accept / Decline.</summary>
    public ObservableCollection<TeamInboxRow> Team { get; } = [];

    [ObservableProperty] private bool _isOpen;

    /// <summary>The list is shown: a queued update's Update now is weighed against what the turn did since.</summary>
    partial void OnIsOpenChanged(bool value)
    {
        if (!value) return;
        foreach (var row in Repos.Where(r => r.IsQueued)) Weigh(row);
    }

    public int Count => Repos.Count(r => r.HasNews) + Team.Count;
    public bool HasBadge => Count > 0;
    public string BadgeText => Count > 9 ? "9+" : Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool IsEmpty => Repos.Count == 0 && Team.Count == 0;
    public bool HasRepos => Repos.Count > 0;
    public bool HasTeam => Team.Count > 0;
    public string Tip => Count == 0 ? Loc.T("Notifications") : Loc.Plural(Count, "{0} notification", "{0} notifications");

    /// <summary>Raised after the rows change (the composer's "behind" hint reads them).</summary>
    public event Action? Changed;

    /// <summary>The last check (tests wait for it).</summary>
    internal Task Checked { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void OpenTeam()
    {
        IsOpen = false;
        _host.OpenTeamInbox();
    }

    private void OnTeamChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Team.Clear();
        foreach (var row in _team.Where(r => r.IsPending)) Team.Add(row);
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasBadge));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRepos));
        OnPropertyChanged(nameof(HasTeam));
        OnPropertyChanged(nameof(Tip));
        Changed?.Invoke();
    }

    /// <summary>The window came to the front: a check, at most once a minute.</summary>
    public void OnWindowFocused()
    {
        if (DateTimeOffset.Now - _lastCheck < FocusThrottle) return;
        Check();
    }

    /// <summary>A turn ended in <paramref name="folder"/>: an update asked for while it ran goes now, else that repository is checked.</summary>
    public void OnTurnEnded(string folder) => Checked = TurnEndedAsync(folder);

    private async Task TurnEndedAsync(string folder)
    {
        if (await TopOfAsync(folder) is not { } top) return;
        if (Repos.FirstOrDefault(r => r.Top == top) is { IsQueued: true } queued)
        {
            queued.IsQueued = false;
            await UpdateAsync(queued);
            return;
        }
        await CheckAsync(top);
    }

    /// <summary>Fetches and reads every open chat's repository (or only <paramref name="onlyTop"/>).</summary>
    public void Check(string? onlyTop = null) => Checked = CheckAsync(onlyTop);

    /// <summary>The user asked: every open chat's repository is fetched, untrusted folders too.</summary>
    [RelayCommand]
    private Task CheckNow() => Checked = CheckAsync(manual: true);

    public async Task CheckAsync(string? onlyTop = null, CancellationToken ct = default, bool manual = false)
    {
        if (!_host.FetchAllowed())
        {
            if (Repos.Count > 0)
            {
                Repos.Clear();
                Notify();
            }
            return;
        }
        await _checking.WaitAsync(ct);
        try
        {
            _lastCheck = DateTimeOffset.Now;
            var tops = new List<string>();
            var fetch = new HashSet<string>();
            foreach (var f in _host.Folders())
            {
                if (await TopOfAsync(f.Path) is not { } top || onlyTop is not null && onlyTop != top) continue;
                if (!tops.Contains(top)) tops.Add(top);
                if (manual || f.AutoFetch) fetch.Add(top);
            }
            foreach (var top in tops)
            {
                // An untrusted folder's remote is fetched only when asked; what was fetched before is still read
                var error = fetch.Contains(top) ? await GitUpstream.FetchAsync(top, ct) : null;
                var state = await GitUpstream.ReadAsync(top, ct);
                Apply(top, state, error);
            }
            if (onlyTop is null)
                foreach (var gone in Repos.Where(r => !tops.Contains(r.Top)).ToList()) Repos.Remove(gone);
            Notify();
        }
        finally { _checking.Release(); }
    }

    /// <summary>Keeps checking every <see cref="Interval"/> until the window closes.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            while (!ct.IsCancellationRequested)
            {
                Check();
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<string?> TopOfAsync(string folder)
    {
        if (_tops.TryGetValue(folder, out var known)) return known;
        var top = Directory.Exists(folder) ? await Git.TopLevelAsync(folder, CancellationToken.None) : null;
        _tops[folder] = top;
        return top;
    }

    private void Apply(string top, UpstreamState? state, string? error)
    {
        var row = Repos.FirstOrDefault(r => r.Top == top);
        var news = state is { Behind: > 0 } || state?.DefaultBranch is not null;
        if (!news && error is null)
        {
            if (row is not null) Repos.Remove(row);
            return;
        }
        if (row is null)
        {
            row = new RepoUpdateRow(this, top);
            Repos.Add(row);
        }
        row.Set(state, error);
        if (row.IsQueued) Weigh(row);
    }

    /// <summary>The "behind" hint for the composer of a chat in <paramref name="folder"/>; null when it is up to date.</summary>
    public RepoUpdateRow? BehindIn(string? folder) =>
        folder is not null && _tops.TryGetValue(folder, out var top) && top is not null
            ? Repos.FirstOrDefault(r => r.Top == top && r.Behind > 0)
            : null;

    /// <summary>
    /// Update / Update (keep my changes): right away when no agent works in the repository, else queued — it runs when
    /// the turn ends (<see cref="OnTurnEnded"/>). <paramref name="now"/> (Update now on a queued row): under the
    /// running turn, when the incoming commits touch none of the files it changed or opened and none of the uncommitted
    /// ones (both read again first); otherwise it stays queued and the row says which files.
    /// </summary>
    internal async Task UpdateAsync(RepoUpdateRow row, bool now = false)
    {
        // Taken before the first await and held through the read and the pull: no turn starts in this repository
        // while git writes its files, and, but for Update now, none is running (it may be editing them or running git)
        using var hold = _host.BeginUpdate(row.Top, now);
        if (hold is null)
        {
            Queue(row);
            return;
        }
        row.IsBusy = true;
        row.ShowMessage(Loc.T("Updating…"));
        try
        {
            if (now)
            {
                Apply(row.Top, await GitUpstream.ReadAsync(row.Top, CancellationToken.None), null);
                if (!row.IsQueued || row.AgentOverlap.Count > 0)
                {
                    // Not updatable any more (the risk line says why) or the turn touched an incoming file (the row lists them)
                    row.ShowMessage(row.IsQueued ? Loc.T("Will update when the agent's turn ends.") : "");
                    Notify();
                    return;
                }
            }
            row.IsQueued = false;
            var error = await GitUpstream.UpdateAsync(row.Top, CancellationToken.None);
            var state = await GitUpstream.ReadAsync(row.Top, CancellationToken.None);
            Apply(row.Top, state, null);
            row.ShowMessage(error ?? Loc.T("Updated."), error is not null);
            Notify();
        }
        finally { row.IsBusy = false; }
    }

    private void Queue(RepoUpdateRow row)
    {
        row.IsQueued = true;
        row.ShowMessage(Loc.T("Will update when the agent's turn ends."));
        Weigh(row);
    }

    /// <summary>For Update now: the incoming files the turns running in the repository changed or opened.</summary>
    private void Weigh(RepoUpdateRow row)
    {
        var agent = new HashSet<string>(_host.AgentFiles(row.Top), OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        row.AgentOverlap = row.State?.Incoming?.Files.Where(agent.Contains).ToList() ?? [];
    }

    /// <summary>Whether <paramref name="folder"/> is in a repository being updated (<paramref name="updating"/>: their tops).</summary>
    internal bool InAnyRepo(string? folder, IEnumerable<string> updating) => updating.Any(top => InRepo(folder, top));

    internal void AskAgent(RepoUpdateRow row)
    {
        IsOpen = false;
        _host.AskAgent(row.Top, row.AgentRequest);
    }

    /// <summary>Whether <paramref name="folder"/> (as checked last) is in the repository at <paramref name="top"/>.</summary>
    internal bool InRepo(string? folder, string top) => folder is not null && _tops.TryGetValue(folder, out var t) && t == top;

    internal Task ShowIncoming(RepoUpdateRow row)
    {
        IsOpen = false;
        return _host.ShowIncoming(row.Top, row.IncomingRef ?? "@{upstream}");
    }
}

/// <summary>A repository with news on its remote: what came, from whom, which files, and how safely it updates.</summary>
public sealed partial class RepoUpdateRow(GitBellViewModel bell, string top) : ObservableObject
{
    public string Top { get; } = top;
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Top));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(FilesText), nameof(DefaultText), nameof(HasDefault), nameof(Risk), nameof(RiskText), nameof(OverlapText),
        nameof(HasOverlap), nameof(CanUpdate), nameof(CanUpdateKeepingChanges), nameof(CanAskAgent), nameof(Behind), nameof(HasNews), nameof(HasIncoming), nameof(IsWarning))]
    private UpstreamState? _state;

    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    /// <summary>The message is a failure (git's reason): shown as a warning.</summary>
    [ObservableProperty] private bool _isMessageError;

    /// <summary>git is updating the repository now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(CanUpdateNow))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool _isBusy;

    /// <summary>Update was pressed while an agent works in the repository: it runs when the turn ends (Cancel drops it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(CanUpdateNow), nameof(NowText), nameof(HasNowText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool _isQueued;

    /// <summary>The incoming files the running turn changed or opened (as last weighed): Update now is not offered while there are any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdateNow), nameof(NowText), nameof(HasNowText))]
    private IReadOnlyList<string> _agentOverlap = [];

    internal void Set(UpstreamState? state, string? error)
    {
        State = state;
        Error = error is null ? null : Loc.T("Couldn't check the remote: ") + error;
        OnPropertyChanged(nameof(HasError));
        // A queued update the branch can no longer take by a fast-forward is dropped: the risk line says why
        if (IsQueued && Risk is not (UpdateRisk.Clean or UpdateRisk.KeepChanges))
        {
            IsQueued = false;
            ShowMessage("");
        }
    }

    internal void ShowMessage(string text, bool error = false)
    {
        Message = text;
        IsMessageError = error;
    }

    public int Behind => State?.Behind ?? 0;
    public bool HasNews => Behind > 0 || State?.DefaultBranch is not null;
    public bool HasIncoming => Behind > 0;
    public bool HasError => Error is not null;
    public bool HasMessage => Message.Length > 0;
    public UpdateRisk Risk => State?.Risk ?? UpdateRisk.NoUpstream;
    public bool IsWarning => Risk is UpdateRisk.Overlap or UpdateRisk.Diverged;
    internal string? IncomingRef => State?.Incoming?.Ref ?? State?.DefaultBranch?.Ref;

    private static string Who(IReadOnlyList<string> authors) => string.Join(", ", authors.Take(3)) + (authors.Count > 3 ? " +" + (authors.Count - 3) : "");

    private static string FileList(IReadOnlyList<string> files) =>
        string.Join(", ", files.Take(4)) + (files.Count > 4 ? " " + Loc.F("and {0} more", files.Count - 4) : "");

    /// <summary>"main: 3 new commits from Bob, Alice".</summary>
    public string Title => State?.Incoming is { } i
        ? $"{Name} · {State.Branch}: " + Loc.Plural(i.Count, "{0} new commit from {1}", "{0} new commits from {1}", Who(i.Authors))
        : Name;

    public string FilesText => State?.Incoming is { Files.Count: > 0 } i ? Loc.F("Files: {0}", FileList(i.Files)) : "";

    public bool HasDefault => State?.DefaultBranch is not null;

    /// <summary>The default branch's news while I am on a feature branch.</summary>
    public string DefaultText => State?.DefaultBranch is { } d
        ? Loc.Plural(d.Count, "{1}: {0} new commit from {2}, not in your branch", "{1}: {0} new commits from {2}, not in your branch", d.Ref, Who(d.Authors))
          + (d.Files.Count > 0 ? " — " + FileList(d.Files) : "")
        : "";

    public string RiskText => Risk switch
    {
        UpdateRisk.Clean => Loc.T("No local changes: a fast-forward update is safe."),
        UpdateRisk.KeepChanges => Loc.T("You have uncommitted changes in other files: they stay as they are; if git would have to touch one, nothing changes."),
        UpdateRisk.Overlap => Loc.T("Not updated automatically: the incoming commits change files you changed too."),
        UpdateRisk.Diverged => Loc.T("Not updated automatically: you have unpushed commits, the histories diverged."),
        _ => "",
    };

    public bool HasOverlap => State?.Overlap.Count > 0;
    public string OverlapText => State is { } s && s.Overlap.Count > 0 ? Loc.F("Changed on both sides: {0}", FileList(s.Overlap)) : "";

    /// <summary>Update is offered (disabled while queued or updating).</summary>
    public bool CanUpdate => Risk == UpdateRisk.Clean;

    /// <summary>Update (keep my changes) is offered (disabled while queued or updating).</summary>
    public bool CanUpdateKeepingChanges => Risk == UpdateRisk.KeepChanges;

    public bool CanAskAgent => Risk is UpdateRisk.Overlap or UpdateRisk.Diverged;

    /// <summary>Queued or updating: the spinner by the message.</summary>
    public bool IsWaiting => IsBusy || IsQueued;

    /// <summary>Queued, and the incoming commits touch none of the files the running turn changed or opened.</summary>
    public bool CanUpdateNow => IsQueued && !IsBusy && AgentOverlap.Count == 0;

    /// <summary>Why a queued update offers no Update now.</summary>
    public string NowText => IsQueued && AgentOverlap.Count > 0
        ? Loc.F("Can't update now: the agent's turn changed or opened files the incoming commits change ({0}).", FileList(AgentOverlap))
        : "";

    public bool HasNowText => NowText.Length > 0;

    private bool CanStartUpdate => !IsBusy && !IsQueued;

    /// <summary>The request the composer gets for an update that needs care.</summary>
    public string AgentRequest
    {
        get
        {
            var s = State;
            var upstream = s?.Upstream ?? "the upstream branch";
            var overlap = s is { Overlap.Count: > 0 } ? " " + Loc.F("Files changed on both sides: {0}.", string.Join(", ", s.Overlap)) : "";
            return Loc.F("Update this repository's branch {0} from {1} carefully: run git fetch, then rebase or merge onto {1}, keeping my uncommitted and unpushed changes. Resolve any conflicts so that both my changes and the incoming ones are preserved; do not discard either side.{2} Then run the tests and tell me what changed.",
                s?.Branch ?? "", upstream, overlap);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartUpdate))]
    private Task Update() => bell.UpdateAsync(this);

    [RelayCommand]
    private Task UpdateNow() => bell.UpdateAsync(this, now: true);

    [RelayCommand]
    private void CancelQueued()
    {
        IsQueued = false;
        ShowMessage("");
    }

    [RelayCommand]
    private void AskAgent() => bell.AskAgent(this);

    [RelayCommand]
    private Task ShowIncoming() => bell.ShowIncoming(this);
}
