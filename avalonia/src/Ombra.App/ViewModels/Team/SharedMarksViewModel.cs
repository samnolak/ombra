using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>What shared marks need of the team: the team shown, who I am, its members and its items (<see cref="TeamViewModel"/>).</summary>
public interface ITeamContext
{
    string? SelectedTeamId { get; }
    Guid? MyMemberId { get; }
    IReadOnlyList<TeamMember> Members { get; }
    ITeamItems? Items { get; }
    /// <summary>Raised after the team's state changed (UI thread).</summary>
    event Action? Changed;
}

/// <summary>A recipient choice of "Send to…": everyone (<see cref="MemberId"/> null) or one member.</summary>
public sealed partial class MarkRecipientOption(string? memberId, string name) : ObservableObject
{
    public string? MemberId { get; } = memberId;
    public string Name { get; } = name;
    public bool IsEveryone => MemberId is null;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>A team member's mark addressed to me: where it is, the note, my status; open it, mark it done or dismissed with a short note.</summary>
public sealed partial class ReceivedMarkRow(SharedMarksViewModel owner, SharedMark mark, string author, string me) : ObservableObject
{
    public SharedMark Mark { get; } = mark;
    public string Author { get; } = author;
    public string Where { get; } = SharedMarksViewModel.WhereOf(mark.Mark);
    public string Note => Mark.Mark.Note.Length > 0 ? Mark.Mark.Note : Loc.T("No note");
    public bool IsCode => Mark.Mark.Code is not null;
    public TeamItemStatus? Mine { get; } = mark.StatusOf(me);
    public bool IsSeen { get; } = mark.SeenBy(me);
    public string StatusText => SharedMarksViewModel.StatusText(IsSeen, Mine?.Status);
    public string? MyNote => Mine?.Note;
    public int MaxNote => SharedMarks.MaxStatusNote;

    /// <summary>A code mark left on another commit than the one checked out here: its line may have moved.</summary>
    public bool MayHaveMoved { get; } = owner.MayHaveMoved(mark);
    public string MovedText => Mark.Commit is { Length: >= 7 } c ? Loc.F("Left on commit {0}: the line may have moved.", c[..7]) : "";

    /// <summary>The short note sent with done/dismissed (≤280).</summary>
    [ObservableProperty] private string _replyNote = mark.StatusOf(me)?.Note ?? "";

    partial void OnReplyNoteChanged(string value)
    {
        if (value.Length > SharedMarks.MaxStatusNote) ReplyNote = value[..GraphemeText.PrefixLength(value, SharedMarks.MaxStatusNote)];
    }

    [RelayCommand] private Task Open() => owner.OpenAsync(Mark);
    [RelayCommand] private Task Done() => owner.SetStatusAsync(Mark, TeamItemStatuses.Done, ReplyNote);
    [RelayCommand] private Task Dismiss() => owner.SetStatusAsync(Mark, TeamItemStatuses.Dismissed, ReplyNote);
    /// <summary>The mark goes into my message box as a local mark for my agent (the status does not change).</summary>
    [RelayCommand] private void ToAgent() => owner.ToAgent(this);
}

/// <summary>One recipient of a mark I sent: name, status, note.</summary>
public sealed record SentRecipientRow(string Name, string StatusText, string? Note)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);
    /// <summary>The row's secondary line: "Ivy · Seen", the note after it when there is one.</summary>
    public string Line => HasNote ? $"{Name} · {StatusText} · {Note}" : $"{Name} · {StatusText}";
}

/// <summary>A mark I sent and each recipient's status.</summary>
public sealed class SentMarkRow(SentMark sent, IReadOnlyList<SentRecipientRow> recipients)
{
    public SentMark Sent { get; } = sent;
    public string Where { get; } = SharedMarksViewModel.WhereOf(sent.Mark.Mark);
    public string Note => Sent.Mark.Mark.Note.Length > 0 ? Sent.Mark.Mark.Note : Loc.T("No note");
    public bool IsCode => Sent.Mark.Mark.Code is not null;
    public IReadOnlyList<SentRecipientRow> Recipients { get; } = recipients;
    /// <summary>"2 of 3 done", "1 of 2 seen", "Not seen yet".</summary>
    public string Summary
    {
        get
        {
            var n = Sent.Recipients.Count;
            var done = Sent.Recipients.Count(r => r.Status == TeamItemStatuses.Done);
            var seen = Sent.Recipients.Count(r => r.Seen);
            return done > 0 ? Loc.F("{0} of {1} done", done, n) : seen > 0 ? Loc.F("{0} of {1} seen", seen, n) : Loc.T("Not seen yet");
        }
    }
}

/// <summary>
/// K4 shared marks over P5 (PRODUCT_DIRECTIONS P5 §4): "Send to…" everyone or members from the local marks; a toast
/// "Comments from Anna in app.py" per author and object; opening goes to the place (file at the line, page at the
/// pin) and marks the marks seen; done/dismissed with a short note; the sender sees each recipient's status. No threads.
/// </summary>
public sealed partial class SharedMarksViewModel : ObservableObject
{
    private ITeamContext _team;
    private readonly Action<SharedMark> _showPlace;
    private readonly HashSet<string> _toasted = [];
    private ITeamItems? _items;
    private int _delivering;

    /// <param name="showPlace">Goes to a mark's place: the file at its line, or the page at its pin.</param>
    public SharedMarksViewModel(ITeamContext team, Action<SharedMark> showPlace)
    {
        _team = team;
        _showPlace = showPlace;
        team.Changed += OnTeamChanged;
        OnTeamChanged();
    }

    /// <summary>Follows another team source (the native smoke's in-memory team); marks already toasted stay toasted.</summary>
    public void Attach(ITeamContext team)
    {
        _team.Changed -= OnTeamChanged;
        _team = team;
        team.Changed += OnTeamChanged;
        OnTeamChanged();
    }

    /// <summary>Shows a toast: its text and what clicking it does (MainViewModel wires the app's toast).</summary>
    public Action<string, Action>? Toast { get; set; }

    /// <summary>Adds a mark to my message box (the agent's next request); false when it does not fit.</summary>
    public Func<LocalMark, bool>? ToComposer { get; set; }

    /// <summary>The repository checked out in my project (sent with code marks; a received mark on another commit may have moved).</summary>
    public RepoIdentity? LocalRepo
    {
        get => _localRepo;
        set
        {
            if (_localRepo == value) return;
            _localRepo = value;
            Refresh();
        }
    }
    private RepoIdentity? _localRepo;

    /// <summary>Received page marks changed: the preview draws them as pins.</summary>
    public event Action<IReadOnlyList<SharedMark>>? PagePinsChanged;

    /// <summary>There is a team: the Team page shows the Comments card.</summary>
    public bool IsAvailable => _team.SelectedTeamId is not null && Me is not null;

    public ObservableCollection<MarkRecipientOption> Recipients { get; } = [];
    public ObservableCollection<ReceivedMarkRow> Received { get; } = [];
    public ObservableCollection<SentMarkRow> Sent { get; } = [];

    [ObservableProperty] private string _message = "";

    public bool HasReceived => Received.Count > 0;
    public bool HasSent => Sent.Count > 0;
    public bool IsEmpty => !HasReceived && !HasSent;
    public int UnseenCount => Received.Count(r => !r.IsSeen);

    /// <summary>There is a team with someone to send to.</summary>
    public bool CanShare => _items is not null && _team.SelectedTeamId is not null && Me is not null && Recipients.Count > 1;

    private string? Me => _team.MyMemberId is { } m ? TeamsHex.Of(m) : null;

    private void OnTeamChanged()
    {
        if (!ReferenceEquals(_items, _team.Items))
        {
            if (_items is not null) _items.ItemsChanged -= OnItemsChanged;
            _items = _team.Items;
            if (_items is not null) _items.ItemsChanged += OnItemsChanged;
        }
        SyncRecipients();
        Refresh();
        _ = DeliverAsync();
    }

    private void SyncRecipients()
    {
        var me = Me;
        var others = _team.Members.Where(m => !m.IsMe && TeamsHex.Of(m.MemberId) != me).ToList();
        var keep = Recipients.Where(r => r.IsChecked).Select(r => r.MemberId ?? "*").ToHashSet();
        Recipients.Clear();
        if (others.Count > 0) Recipients.Add(new MarkRecipientOption(null, Loc.T("Everyone")) { IsChecked = keep.Count == 0 || keep.Contains("*") });
        foreach (var m in others)
        {
            var id = TeamsHex.Of(m.MemberId);
            Recipients.Add(new MarkRecipientOption(id, m.Name ?? TeamViewModel.ShortId(m.MemberId)) { IsChecked = keep.Contains(id) });
        }
        OnPropertyChanged(nameof(CanShare));
    }

    private void OnItemsChanged(string teamId)
    {
        if (teamId != _team.SelectedTeamId) return;
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>Rebuilds the lists from the replica and toasts marks that arrived since.</summary>
    public void Refresh()
    {
        Received.Clear();
        Sent.Clear();
        if (_items is { } items && _team.SelectedTeamId is { } team && Me is { } me)
        {
            var all = items.List(team, 500).Where(i => i.Kind == TeamItemKinds.Mark).ToList();
            var received = SharedMarks.Received(all, me);
            foreach (var r in received) Received.Add(new ReceivedMarkRow(this, r, NameOf(r.Item.AuthorMemberId), me));
            foreach (var s in SharedMarks.Sent(all, me))
                Sent.Add(new SentMarkRow(s, [.. s.Recipients.Select(r => new SentRecipientRow(NameOf(r.RecipientMemberId), StatusText(r.Seen, r.Status), r.Note))]));
            foreach (var t in SharedMarks.NewToasts(received, me, _toasted))
            {
                var toast = t;
                Toast?.Invoke(Loc.F("Comments from {0} in {1}", NameOf(t.AuthorMemberId), t.ObjectTitle), () => _ = OpenAsync(toast.Marks[^1]));
            }
            PagePinsChanged?.Invoke([.. received.Where(r => r.Mark.Page is not null && r.StatusOf(me)?.Status is not (TeamItemStatuses.Done or TeamItemStatuses.Dismissed))]);
        }
        else PagePinsChanged?.Invoke([]);
        OnPropertyChanged(nameof(HasReceived));
        OnPropertyChanged(nameof(HasSent));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(UnseenCount));
        OnPropertyChanged(nameof(CanShare));
        OnPropertyChanged(nameof(IsAvailable));
    }

    /// <summary>
    /// Sends the code and page marks to the checked recipients ("Everyone": every other member), one item per mark and
    /// recipient, then delivers. Returns how many marks went; the reason is in <see cref="Message"/> when none did.
    /// </summary>
    public async Task<int> SendAsync(IReadOnlyList<LocalMark> marks)
    {
        var shareable = marks.Where(SharedMarks.CanShare).ToList();
        if (_items is not { } items || _team.SelectedTeamId is not { } team || Me is null) { Message = Loc.T("Join a team to send comments to its members."); return 0; }
        var to = Recipients.Any(r => r.IsEveryone && r.IsChecked)
            ? Recipients.Where(r => !r.IsEveryone).Select(r => r.MemberId!).ToList()
            : Recipients.Where(r => !r.IsEveryone && r.IsChecked).Select(r => r.MemberId!).ToList();
        if (to.Count == 0) { Message = Loc.T("Choose who gets the comments."); return 0; }
        if (shareable.Count == 0) { Message = Loc.T("Only comments on code and pages can be sent."); return 0; }
        var batch = LocalMarks.NewId();
        try
        {
            foreach (var m in shareable) await items.SendAsync(team, SharedMarks.ToDraft(m, batch, LocalRepo), to);
        }
        catch (Exception e)
        {
            Message = Loc.F("Could not send the comments: {0}", e.Message);
            return 0;
        }
        Message = to.Count == 1 ? Loc.F("Sent to {0}", NameOf(to[0])) : Loc.F("Sent to {0} members", to.Count);
        Refresh();
        _ = DeliverAsync();
        return shareable.Count;
    }

    /// <summary>Goes to a received mark's place and marks the author's unseen marks on that object seen.</summary>
    public async Task OpenAsync(SharedMark mark)
    {
        _showPlace(mark);
        if (_items is not { } items || _team.SelectedTeamId is not { } team || Me is not { } me) return;
        var same = Received.Select(r => r.Mark).Where(r => r.Item.AuthorMemberId == mark.Item.AuthorMemberId && r.ObjectKey == mark.ObjectKey && !r.SeenBy(me)).ToList();
        if (!mark.SeenBy(me) && same.All(s => s.Item.ItemId != mark.Item.ItemId)) same.Add(mark);
        if (same.Count == 0) return;
        try
        {
            foreach (var s in same) await items.SetStatusAsync(team, s.Item.AuthorMemberId, s.Item.ItemId, TeamItemStatuses.Seen);
        }
        catch (Exception e)
        {
            Message = Loc.F("Could not update the comment: {0}", e.Message);
            return;
        }
        Refresh();
        _ = DeliverAsync();
    }

    /// <summary>Done or dismissed, with the short note (≤280; empty: none).</summary>
    public async Task SetStatusAsync(SharedMark mark, string status, string? note)
    {
        if (_items is not { } items || _team.SelectedTeamId is not { } team) return;
        try
        {
            await items.SetStatusAsync(team, mark.Item.AuthorMemberId, mark.Item.ItemId, status, SharedMarks.StatusNote(note));
        }
        catch (Exception e)
        {
            Message = Loc.F("Could not update the comment: {0}", e.Message);
            return;
        }
        Refresh();
        _ = DeliverAsync();
    }

    /// <summary>Sends the outbox and receives (one round at a time; the server may be away: the next change retries).</summary>
    private async Task DeliverAsync()
    {
        if (_items is not { } items || Interlocked.Exchange(ref _delivering, 1) == 1) return;
        try { await Task.Run(() => items.DeliverAsync()); }
        catch (Exception) { /* offline: the outbox keeps the operations for the next round */ }
        finally { Volatile.Write(ref _delivering, 0); }
    }

    private string NameOf(string memberId) =>
        _team.Members.FirstOrDefault(m => TeamsHex.Of(m.MemberId) == memberId) is { } m
            ? m.Name ?? TeamViewModel.ShortId(m.MemberId)
            : memberId.Length > 8 ? memberId[..8] : memberId;

    /// <summary>A code mark from the same repository left on another commit than mine: its line may have moved.</summary>
    internal bool MayHaveMoved(SharedMark m) =>
        m.Mark.Code is not null && m.Commit is { Length: > 0 } c && LocalRepo is { } mine
        && (m.RepoFingerprint is not { Length: > 0 } fp || fp == mine.Fingerprint) && !string.Equals(c, mine.Commit, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// «В запрос агенту»: the received mark (anchor, context and note, with who left it) becomes a local mark in my
    /// message box, so my agent gets it with my next request. Its status for the author does not change.
    /// </summary>
    internal void ToAgent(ReceivedMarkRow row)
    {
        var m = row.Mark.Mark;
        // The agent's request is English, like the rest of LocalMarks.BuildRequest
        var note = $"{(m.Note.Length > 0 ? m.Note : "(no note)")} (left by {row.Author})";
        Message = ToComposer?.Invoke(m with { Id = LocalMarks.NewId(), Note = note, Status = MarkStatus.Open }) == true
            ? Loc.T("Added to your message for the agent.")
            : Loc.T("Could not add the comment to your message.");
    }

    internal static string StatusText(bool seen, string? status) => status switch
    {
        TeamItemStatuses.Done => Loc.T("Done"),
        TeamItemStatuses.Dismissed => Loc.T("Dismissed"),
        _ => seen ? Loc.T("Seen") : Loc.T("Not opened"),
    };

    /// <summary>"app.py:41", "app.py:3-7", "localhost:5173/about".</summary>
    internal static string WhereOf(LocalMark m) => m.Code is { } c
        ? c.Path + (c.Line is { } n ? ":" + n.ToString(CultureInfo.InvariantCulture)
            + (c.EndLine is { } end && end > n ? "-" + end.ToString(CultureInfo.InvariantCulture) : "") : "")
        : m.Page is { } p && Uri.TryCreate(p.Url, UriKind.Absolute, out var u) ? u.Authority + (u.AbsolutePath == "/" ? "" : u.AbsolutePath.TrimEnd('/'))
        : "";
}
