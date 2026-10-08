using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Several chats at once. Each open chat has its own omp process (a <see cref="SessionController"/> in
/// <see cref="_host"/>) that keeps working while another chat is shown. The window shows one, <see cref="_open"/>: its
/// updates are applied in full, up to 30 times a second. The others are looked at at most once a second, for their dot
/// in the sidebar and a notification when one finishes, asks something or stops. Showing another chat keeps what the
/// window built for the one it leaves (<see cref="OpenSession"/>: rows, draft, card, question, where the reader was), so
/// coming back is instant and nothing is built twice. Nothing here ever stops a run to change chats.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SessionHost _host;
    private readonly Dictionary<SessionController, OpenSession> _opens = [];
    /// <summary>The chat shown (read by the update loops off the UI thread: <see cref="IsShown"/>).</summary>
    private OpenSession _open;
    /// <summary>Completed and replaced whenever the chat shown or the window's visibility changes: an update loop
    /// pausing in the background wakes at once when its chat comes to the front.</summary>
    private TaskCompletionSource _frontChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised on the UI thread before another chat replaces the conversation: the view returns where the reader
    /// is (its own record, kept with the chat being left).</summary>
    public event Func<object?>? SaveScrollRequested;

    /// <summary>Raised on the UI thread once a chat is shown again, with what <see cref="SaveScrollRequested"/> returned
    /// when it was left (null for a chat not shown before: it opens on its latest message).</summary>
    public event Action<object?>? RestoreScrollRequested;

    /// <summary>Every open chat's omp, the one used longest ago first (the shown one included).</summary>
    internal IReadOnlyList<SessionController> OpenChats => _host.All;

    private OpenSession Register(SessionController c)
    {
        var open = new OpenSession(c, _cts.Token);
        _opens[c] = open;
        return open;
    }

    /// <summary>The open chat that has <paramref name="sessionFile"/> (or is opening it), or null.</summary>
    private OpenSession? FindOpen(string sessionFile)
    {
        foreach (var o in _opens.Values)
            if (o.OpeningFile is { } f && SessionCatalog.SamePath(f, sessionFile)) return o;
        return _host.Find(sessionFile) is { } c ? _opens.GetValueOrDefault(c) : null;
    }

    /// <summary>The process limit may close this chat: not shown (the host checks), idle, nothing typed or under way.</summary>
    private bool MayClose(SessionController c) =>
        _opens.TryGetValue(c, out var o) && o.HasNoDraft && o.Card is not { IsRunning: true } && o.OpeningFile is null
        && SessionHost.IsIdle(c.Snapshot());

    private bool HasNoDraft(OpenSession open) => open != _open ? open.HasNoDraft
        : ComposerText.Trim().Length == 0 && Attachments.Count == 0 && PastedTexts.Count == 0 && CodeComments.Count == 0;

    /// <summary>A new chat nothing was said or typed in, with omp idle: another chat can take its place (and its omp).</summary>
    private bool IsUntouched(OpenSession open)
    {
        var s = open.Controller.Snapshot();
        return s is { Phase: SessionPhase.Ready, Dialogs.Count: 0, Queued.Count: 0, SigningIn: null } && s.Items.All(i => i is NoticeItem)
            && HasNoDraft(open) && open.OpeningFile is null && (open == _open ? SessionCard : open.Card) is not { IsRunning: true };
    }

    /// <summary>How a new chat's omp starts: in <paramref name="cwd"/>, resuming <paramref name="file"/>, with the
    /// permission mode chosen last.</summary>
    private LaunchRequest NewLaunch(string? cwd, string? file = null) =>
        new(cwd, file, _chosenApprovalMode ?? Session.CurrentLaunch.ApprovalMode);

    /// <summary>Starts a chat's update loop and its omp.</summary>
    private Task StartSessionAsync(OpenSession open)
    {
        open.Pump = Task.Run(() => PumpAsync(open));
        return open.Controller.StartAsync(open.Lifetime.Token);
    }

    private bool IsShown(OpenSession open) => ReferenceEquals(Volatile.Read(ref _open), open) && _visible.Task.IsCompleted;

    /// <summary>The chat shown or the window's visibility changed: the update loops waiting in the background look again.</summary>
    private void WakeFront()
    {
        var old = _frontChanged;
        _frontChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    /// <summary>
    /// One chat's updates: wakes on Core's changes and coalesces bursts. Shown, the newest snapshot is applied in full on
    /// the UI thread. In the background (another chat shown, or the window minimized: Avalonia renders nothing then, and
    /// every control touched meanwhile only grows memory) Core keeps going, the sidebar and the notifications follow at
    /// most once a second, and the newest snapshot is applied the moment the chat is in front again.
    /// </summary>
    private async Task PumpAsync(OpenSession open)
    {
        var c = open.Controller;
        var ct = open.Lifetime.Token;
        long lastApply = 0;
        try
        {
            while (await c.Changes.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                c.Changes.TryRead(out _);
                if (!IsShown(open))
                {
                    var front = _frontChanged.Task;
                    var seen = c.Snapshot();
                    await OnUiAsync(() => NoticeInBackground(open, seen)).ConfigureAwait(false);
                    await Task.WhenAny(front, Task.Delay(MinimizedAttentionInterval, ct)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    lastApply = 0;
                    if (!IsShown(open)) continue;
                }
                var wait = MinApplyInterval - Stopwatch.GetElapsedTime(lastApply);
                if (lastApply != 0 && wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                c.Changes.TryRead(out _);
                var snapshot = c.Snapshot();
                await OnUiAsync(() => ApplyOrNotice(open, snapshot)).ConfigureAwait(false);
                lastApply = Stopwatch.GetTimestamp();
            }
            // Core completed its change stream (the chat closed, or the window is closing): its final state
            var final = c.Snapshot();
            await OnUiAsync(() => ApplyOrNotice(open, final)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyOrNotice(OpenSession open, SessionSnapshot s)
    {
        if (open == _open) Apply(s);
        else NoticeInBackground(open, s);
    }

    private static async Task OnUiAsync(Action update)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(update, DispatcherPriority.Background);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One bad update must not stop all later ones; the next snapshot is complete anyway.
            Console.Error.WriteLine("UI update failed: " + e);
        }
    }

    /// <summary>A chat not on screen (or the window minimized): its dot in the sidebar, a notification, a restart it waited for.</summary>
    private void NoticeInBackground(OpenSession open, SessionSnapshot s)
    {
        if (IsClosing || !_opens.ContainsKey(open.Controller)) return;
        var before = open.Seen;
        if (before is not null && s.Version < before.Version) return;
        open.Seen = s;
        CheckAttention(open, s);
        WhenIdle(open, s);
        if (before is not null && before.Phase == s.Phase && before.Dialogs.Count == s.Dialogs.Count && before.SessionFile == s.SessionFile
            && before.SessionName == s.SessionName) return;
        UpdateSessionStatuses();
        // A run that started or ended, or a new file, changes the list (its order by the last message, a first title); so
        // does a name omp gave the session meanwhile (the row of a chat omp has no file for yet is named from the chat)
        if (before?.Phase != s.Phase || before.SessionFile != s.SessionFile || before.SessionName != s.SessionName) RequestCatalogRefresh();
        // A chat that just became idle (a run ended, an omp finished starting) may be over the process limit now
        if (before?.Phase != s.Phase && SessionHost.IsIdle(s)) _host.Trim();
    }

    /// <summary>Applies the chat's newest snapshot when it is the one shown (after a command that changed it).</summary>
    private void ApplyIfShown(OpenSession open)
    {
        if (open == _open) Apply(open.Controller.Snapshot());
    }

    // ───────────── Showing, opening and closing chats ─────────────

    /// <summary>Shows another open chat; the one shown until now keeps running in the background, as it was left.</summary>
    private void ShowSession(OpenSession target)
    {
        if (target == _open || !_opens.ContainsKey(target.Controller)) return;
        var leaving = _open;
        var windowCard = LeaveSession(leaving);
        _host.Activate(target.Controller);
        EnterSession(target, windowCard);
        // A new chat left untouched would only hold an omp: nothing in it can be opened again
        if (IsUntouched(leaving)) _ = CloseSessionAsync(leaving);
        else _host.Trim(); // the chat left may be the idle one over the limit now
    }

    /// <summary>
    /// A new chat with its own omp, shown at once. The chats open so far keep running; idle ones over the process limit
    /// are closed (a working one never is).
    /// </summary>
    private async Task StartNewSessionAsync(LaunchRequest request)
    {
        var leaving = _open;
        var windowCard = LeaveSession(leaving); // first: whether the chat left may be closed depends on its draft
        var open = Register(_host.Open(request));
        EnterSession(open, windowCard);
        if (IsUntouched(leaving)) _ = CloseSessionAsync(leaving);
        try
        {
            await StartSessionAsync(open);
        }
        catch (OperationCanceledException)
        {
            return; // closed while it started
        }
        catch (Exception e)
        {
            // The chat shows why its omp did not start (and offers to try again)
            Console.Error.WriteLine("omp start failed: " + e.Message);
        }
        if (request.ResumeSessionFile is { } file && open.Controller.Snapshot() is { Phase: SessionPhase.Faulted, StartFailed: true } failed
            && _opens.ContainsKey(leaving.Controller))
        {
            ReturnFromFailedOpen(open, leaving, file, failed);
            return;
        }
        ApplyIfShown(open);
        RequestCatalogRefresh();
    }

    /// <summary>
    /// A saved session omp could not open in an omp of its own (a damaged file: omp exits at start). That chat would only
    /// hold a dead omp, stand current in the sidebar for a conversation it never showed, and blame the omp command. As
    /// after a switch in place that omp refused, the window stays with (goes back to) the chat it left and says why there.
    /// </summary>
    private void ReturnFromFailedOpen(OpenSession failed, OpenSession left, string file, SessionSnapshot s)
    {
        if (failed == _open) ShowSession(left);
        _ = CloseSessionAsync(failed);
        var title = _catalog.FirstOrDefault(c => SessionCatalog.SamePath(c.Path, file))?.Title ?? Path.GetFileName(file);
        var card = new SessionCardViewModel("open-failed", "IconAlert", "", "");
        card.Finish(false, $"Couldn't open “{title}”", OmpError(s.LastError), detail: file,
            secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        ShowCard(card);
        UpdateSessionStatuses();
        RequestCatalogRefresh();
    }

    /// <summary>omp's own message from a start failure's stderr (Bun prints the thrown error as "error: …" under the
    /// source excerpt), else the failure's first line.</summary>
    internal static string OmpError(string? failure)
    {
        var lines = (failure ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.FirstOrDefault(l => l.StartsWith("error: ", StringComparison.Ordinal)) is { } e ? e["error: ".Length..]
            : lines.FirstOrDefault() ?? "omp stopped before it was ready.";
    }

    /// <summary>A saved session from the sidebar: shown as it is when a chat has it open, else opened in an omp of its own
    /// (the shown chat's own omp when that chat is still untouched).</summary>
    private async Task OpenSavedSessionAsync(string file, string cwd)
    {
        if (FindOpen(file) is { } open)
        {
            ShowSession(open);
            return;
        }
        if (!IsUntouched(_open))
        {
            await StartNewSessionAsync(NewLaunch(cwd, file));
            return;
        }
        var reused = _open;
        reused.OpeningFile = file; // until omp reports it: a second click finds it here instead of starting another omp on it
        reused.ScrollAnchor = null;
        try
        {
            await reused.Controller.OpenSessionAsync(file, cwd, reused.Lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            reused.OpeningFile = null;
        }
        ApplyIfShown(reused);
        RequestCatalogRefresh();
    }

    /// <summary>A new chat in <paramref name="folder"/>, beside the chats already open (in the untouched shown chat's omp).</summary>
    private async Task OpenProjectAsync(string folder)
    {
        if (!IsUntouched(_open))
        {
            await StartNewSessionAsync(NewLaunch(folder));
            return;
        }
        var reused = _open;
        try
        {
            await reused.Controller.OpenFolderAsync(folder, reused.Lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        ApplyIfShown(reused);
        RequestCatalogRefresh();
    }

    /// <summary>Closes a chat that is not shown: its omp stops (a run in it ends there), and what the window kept of it goes.</summary>
    private async Task CloseSessionAsync(OpenSession open)
    {
        if (open == _open || !_opens.ContainsKey(open.Controller)) return;
        open.Lifetime.Cancel(); // a start still under way stops its omp instead of finishing it
        await _host.CloseAsync(open.Controller);
    }

    /// <summary>The host closed a chat (the user, or the process limit): the window lets go of it.</summary>
    private void OnHostClosed(SessionController c)
    {
        if (!_opens.Remove(c, out var open)) return;
        open.Dispose();
        // A chat omp had no file for (a new one left untouched, say) has nothing to open again: its row goes now, not at
        // the next rescan (until then a click on it started an empty chat on a file that never existed, and kept the row)
        ApplySessionFilter();
        RefreshOmpProcesses();
    }

    /// <summary>
    /// Keeps what the window shows of the chat it leaves, for when it comes back. Returns the card that belongs to the
    /// window rather than to the chat (the quit question stays up whichever chat is shown).
    /// </summary>
    private SessionCardViewModel? LeaveSession(OpenSession open)
    {
        open.ScrollAnchor = SaveScrollRequested?.Invoke();
        if (_mention is not null) EndMention();
        IsCommandMenuOpen = false;
        IsRenaming = false;
        open.Rows = [.. _rows];
        open.ComposerText = ComposerText;
        open.ComposerMessage = ComposerMessage;
        open.Attachments = [.. Attachments];
        open.PastedTexts = [.. PastedTexts];
        open.CodeComments = [.. CodeComments];
        open.Queued = [.. QueuedMessages];
        open.Dialog = CurrentDialog;
        open.PendingUrl = PendingUrl;
        open.PendingUrlInstructions = PendingUrlInstructions;
        open.PendingApprovalMode = PendingApprovalMode;
        // A brief note goes with its moment; a card that asks or works stays with its chat
        _cardTimer?.Stop();
        _cardTimer = null;
        var windowCard = SessionCard is { Kind: "quit" } quit ? quit : null;
        open.Card = SessionCard is { Kind: not ("brief" or "quit") } card ? card : null;
        open.Seen = open.Last ?? open.Seen;
        return windowCard;
    }

    /// <summary>Shows a chat: its rows and draft as they were left, then its newest snapshot applied in full.</summary>
    private void EnterSession(OpenSession open, SessionCardViewModel? windowCard)
    {
        Volatile.Write(ref _open, open);
        _rows.ResetTo(open.Rows);
        open.Rows = [];
        ComposerText = open.ComposerText;
        ComposerMessage = open.ComposerMessage;
        Refill(Attachments, open.Attachments);
        Refill(PastedTexts, open.PastedTexts);
        Refill(CodeComments, open.CodeComments);
        Refill(QueuedMessages, open.Queued);
        open.Attachments = [];
        open.PastedTexts = [];
        open.CodeComments = [];
        open.Queued = [];
        OnPropertyChanged(nameof(HasCodeComments));
        OnPropertyChanged(nameof(HasQueued));
        AttachmentsChanged();
        CurrentDialog = open.Dialog;
        open.Dialog = null;
        SessionCard = windowCard ?? open.Card;
        if (windowCard is null) open.Card = null;
        PendingUrl = open.PendingUrl;
        PendingUrlInstructions = open.PendingUrlInstructions;
        PendingApprovalMode = open.PendingApprovalMode;
        open.Unread = false;
        // What the window derived from the chat shown before is derived again from this one (its own omp: its model's
        // thinking levels, its pins, its counts of ended runs)
        WidgetsText = string.Join("\n", (open.Last?.Widgets ?? []).SelectMany(w => w.Lines));
        _paneRunsEnded = -1;
        _pinsFor = null;
        _thinkingFetchedFor = null;
        _modelsFetchedFor = null;
        DebugLog.Clear();
        _lastDebugSeq = 0;
        OnPropertyChanged(nameof(Usage));
        OnPropertyChanged(nameof(ModelOptions));
        Apply(open.Controller.Snapshot());
        RestoreScrollRequested?.Invoke(open.ScrollAnchor);
        // The rows again, not only their dots: the row of the chat just left, if omp has no file for it yet, was named when
        // the list was last read (before its first message, say), and the header's live title no longer stands in for it
        ApplySessionFilter();
        if (_tasksCreated) Tasks.OnSessionShown(); // its own omp's background jobs
        WakeFront();
        RefreshOmpProcesses();
    }

    private static void Refill<T>(System.Collections.ObjectModel.ObservableCollection<T> list, List<T> items)
    {
        list.Clear();
        foreach (var item in items) list.Add(item);
    }

    // ───────────── Status, working chats, settings that restart omp ─────────────

    /// <summary>An open chat's dot in the sidebar (see <see cref="UpdateSessionStatuses"/>).</summary>
    private SessionStatus StatusOf(OpenSession open)
    {
        if (open == _open)
            return HasDialog ? SessionStatus.Waiting
                : IsRunning ? SessionStatus.Running
                : HasError ? SessionStatus.Error
                : open.Unread ? SessionStatus.Unread // a reply that came while the window was away (MainViewModel.Attention.cs)
                : SessionStatus.None;
        if ((open.Seen ?? open.Last) is not { } s) return SessionStatus.None;
        return s.Dialogs.Count > 0 ? SessionStatus.Waiting
            : s.Phase is SessionPhase.Running or SessionPhase.Aborting ? SessionStatus.Running
            : s.Phase == SessionPhase.Faulted ? SessionStatus.Error
            : open.Unread ? SessionStatus.Unread
            : SessionStatus.None;
    }

    /// <summary>The open chats whose omp is working or waiting on the user (what quitting or a restart would cut short).</summary>
    private List<(OpenSession Open, SessionSnapshot Snapshot)> WorkingSessions() =>
        [.. _opens.Values.Select(o => (o, s: o.Controller.Snapshot())).Where(x => x.s.Phase is SessionPhase.Running or SessionPhase.Aborting || x.s.Dialogs.Count > 0)];

    /// <summary>
    /// Starts every chat's omp again so it reads changed settings: an idle one now, one that is working (or starting)
    /// once it is idle (<see cref="WhenIdle"/>), so no run is cut short. True when the shown chat's omp restarted now.
    /// </summary>
    private async Task<bool> RestartAllSessionsAsync()
    {
        var restarts = new List<Task>();
        var shownNow = false;
        foreach (var open in _opens.Values.ToList())
        {
            var s = open.Controller.Snapshot();
            if (s.Phase == SessionPhase.NotStarted) continue; // its start reads the settings as they are now
            if (!SessionHost.IsIdle(s))
            {
                open.RestartWhenIdle = true;
                continue;
            }
            shownNow |= open == _open;
            restarts.Add(RestartSessionAsync(open));
        }
        await Task.WhenAll(restarts);
        return shownNow;
    }

    private async Task RestartSessionAsync(OpenSession open)
    {
        open.RestartWhenIdle = false;
        try
        {
            await open.Controller.RecoverAsync(open.Lifetime.Token);
            ApplyIfShown(open);
        }
        catch (OperationCanceledException) when (open.Lifetime.IsCancellationRequested)
        {
            // The chat (or the window) closed meanwhile: its omp is not started again
        }
    }

    /// <summary>Called with each of a chat's snapshots: a restart that waited for its run happens once it is idle.</summary>
    private void WhenIdle(OpenSession open, SessionSnapshot s)
    {
        if (!open.RestartWhenIdle) return;
        // An omp that is not running reads the settings when the user starts it again
        if (s.Phase is SessionPhase.Faulted or SessionPhase.Stopped) open.RestartWhenIdle = false;
        else if (s.Phase == SessionPhase.Ready && SessionHost.IsIdle(s)) _ = RestartSessionAsync(open);
    }

    /// <summary>Stops and starts again each idle chat's omp (before the runtime it runs from is replaced).</summary>
    private async Task ForceStopReadySessionsAsync(CancellationToken ct)
    {
        foreach (var open in _opens.Values.ToList())
            if (open.Controller.Snapshot().Phase == SessionPhase.Ready) await open.Controller.ForceStopAsync(ct);
    }

    // ───────────── The process limit (Settings → General) and the processes in the event panel ─────────────

    /// <summary>The segment of the process limit that is chosen (its number is the converter's parameter).</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter IsMaxOpenSessions =
        new Avalonia.Data.Converters.FuncValueConverter<int, string, bool>((n, choice) => choice == n.ToString(CultureInfo.InvariantCulture));

    /// <summary>How many chats keep their omp running before idle ones are closed.</summary>
    [ObservableProperty] private int _maxOpenSessions = SessionHost.DefaultMaxProcesses;

    private int LoadMaxOpenSessions()
    {
        int? saved = null;
        try { saved = _settings?.Load().MaxOpenSessions; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The default; the settings page reports the file's problem when it opens.
        }
        return MaxOpenSessions = saved is > 0 ? saved.Value : SessionHost.DefaultMaxProcesses;
    }

    [RelayCommand]
    private void SetMaxOpenSessions(string? value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n == MaxOpenSessions) return;
        MaxOpenSessions = n;
        _host.MaxProcesses = n;
        _host.Trim();
        Persist(o => o with { MaxOpenSessions = n == SessionHost.DefaultMaxProcesses ? null : n });
    }

    /// <summary>The event panel's count of omp processes and the memory they hold ("3 omp processes · 640 MB").</summary>
    [ObservableProperty] private string _ompProcessesText = "";

    private long _processesReadAt;

    /// <summary>Reads the omp processes' memory again while the event panel is open (at most every 2 s).</summary>
    private void RefreshOmpProcesses(bool force = true)
    {
        if (!IsDebugVisible || (!force && Stopwatch.GetElapsedTime(_processesReadAt) < TimeSpan.FromSeconds(2))) return;
        _processesReadAt = Stopwatch.GetTimestamp();
        var pids = _opens.Keys.Select(c => c.ProcessId).OfType<int>().ToList();
        long bytes = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                bytes += p.WorkingSet64;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited meanwhile
            }
        }
        OmpProcessesText = (pids.Count == 1 ? "1 omp process" : $"{pids.Count} omp processes")
            + (bytes > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {bytes / 1048576} MB") : "");
    }

    // ───────────── The minimized window ─────────────

    /// <summary>
    /// While the window is minimized nothing is applied, but Core still parses omp's stream (~6 MB/min of short-lived
    /// allocations per working chat). With nothing rendered there is no GC pressure from the renderer, and the
    /// workstation GC's gen0 budget can be hundreds of MB, so that garbage would sit in RSS until restore. Bound it: every
    /// 30 s, collect the young generations once 32 MB have been allocated since the last check.
    /// </summary>
    private async Task CollectWhileMinimizedAsync(Task visible, CancellationToken ct)
    {
        var allocatedAtLastCheck = GC.GetTotalAllocatedBytes();
        try
        {
            while (!visible.IsCompleted)
            {
                await Task.WhenAny(visible, Task.Delay(MinimizedGcCheckInterval, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (visible.IsCompleted) return;
                var allocated = GC.GetTotalAllocatedBytes();
                if (allocated - allocatedAtLastCheck <= MinimizedGcAllocationThreshold) continue;
                GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                allocatedAtLastCheck = allocated;
            }
        }
        catch (OperationCanceledException) { }
    }
}
