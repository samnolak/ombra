using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.ClientCore;
using Ombra.App.Services.Documents;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Upper bound on UI refreshes while streaming. Deadline-based on a pool thread; no UI timer involved.</summary>
    private static readonly TimeSpan MinApplyInterval = TimeSpan.FromMilliseconds(33);
    private const int DebugLines = 500;
    private static readonly TimeSpan MinimizedGcCheckInterval = TimeSpan.FromSeconds(30);
    private const long MinimizedGcAllocationThreshold = 32L * 1024 * 1024;
    private static readonly TimeSpan MinimizedAttentionInterval = TimeSpan.FromSeconds(1);

    private readonly AppArgs _args;
    // Background work belongs to this window's dispatcher, even after another Application is created.
    internal Dispatcher UiDispatcher { get; } = Dispatcher.UIThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly RowList _rows = [];
    private long _lastDebugSeq;
    private long _uiApplies;
    private long _ompSpawnTs;
    private long _ompReadyTs;
    private volatile string _windowState = "Normal";
    // Completed while the window is visible. While minimized the pumps wait on it instead of applying:
    // Avalonia renders nothing then, and every control touched meanwhile stays queued in the renderer's
    // dirty set until the next frame, so UI updates in a minimized window only grow memory.
    private TaskCompletionSource _visible = NewCompleted();
    private Task? _dispose;
    private DispatcherTimer? _elapsedTimer;
    private DispatcherTimer? _dialogTimer;
    private int _disposed;
    private readonly bool _ownsDraftStore;
    public WorkspaceDocumentService Documents { get; }
    public EditorLanguageService EditorLanguageService { get; }

    public MainViewModel(SessionController session, AppArgs args, string? configError = null, ClientSettingsStore? settings = null, DraftStore? drafts = null)
    {
        _args = args;
        _settings = settings;
        var catalog = new SessionCatalogCache(settings is null ? null : SessionCatalogCache.PathFor(settings.Path));
        ScanCatalogAsync = (root, ct) => catalog.ScanAsync(root, CatalogLimit, ct); // CatalogLimit: MainViewModel.SidebarNav.cs
        _ownsDraftStore = drafts is null;
        _draftStore = drafts ?? new DraftStore(settings is null ? null : DraftStore.PathFor(settings.Path));
        Documents = new WorkspaceDocumentService(settings is null ? null : settings.Path + ".editor-recovery");
        EditorLanguageService = new EditorLanguageService(Documents);
        _draftStore.SaveFailed += error => PostToUi(() => ComposerMessage = Loc.F("Drafts could not be saved: {0}", error));
        if (configError is not null) Rows.Add(RowViewModel.Create(new NoticeItem(0, NoticeLevel.Warning, configError)));
        if (settings is not null) settings.WarningsChanged += OnSettingsWarningsChanged;
        RefreshSettingsWarning();
        ApprovalRules = session.ApprovalRules ??= new ApprovalRuleSet(settings); // MainViewModel.Approvals.cs
        _approvalActions = new ApprovalActions(AllowWithRuleAsync, DenyWithFeedbackAsync);
        _host = new SessionHost(session); // MainViewModel.OpenSessions.cs
        _host.Closed += OnHostClosed;
        _host.Stopped += OnChatStopped; // MainViewModel.Chats.cs
        _open = Register(session, recoverUnfiled: true);
        _changingDraft = true;
        ComposerText = _open.ComposerText;
        ComposerMessage = _draftStore.LastError ?? _open.ComposerMessage;
        Refill(Attachments, _open.Attachments);
        Refill(PastedTexts, _open.PastedTexts);
        Refill(Marks.Items, _open.Marks);
        _open.Attachments = [];
        _open.PastedTexts = [];
        _open.Marks = [];
        _changingDraft = false;
        foreach (var draft in _draftStore.All.Where(d => d.SessionFile is null && !IsDraftClaimed(d.Id)).ToArray())
            Register(_host.Open(new LaunchRequest(draft.Project)), recoverUnfiled: true);
        Rows.CollectionChanged += (_, _) => NotifyRecoverLayout();
    }

    /// <summary>The shown chat's rows (each open chat keeps its own while another is shown: MainViewModel.OpenSessions.cs).</summary>
    public ObservableCollection<RowViewModel> Rows => _rows;

    public ObservableCollection<string> DebugLog { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _composerText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendAsFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(SteerCommand))]
    [NotifyCanExecuteChangedFor(nameof(InterruptAndSendCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApprovePlanCommand), nameof(RejectPlanCommand))]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsIdle), nameof(ComposerHint), nameof(CanQueue), nameof(StopLabel), nameof(ApprovalPendingNote))]
    [NotifyPropertyChangedFor(nameof(IsOpeningSession))]
    private SessionPhase _phase = SessionPhase.NotStarted;

    /// <summary>The dialog omp is waiting on (the first of possibly several).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog), nameof(ComposerHint))]
    private DialogViewModel? _currentDialog;

    public bool HasDialog => CurrentDialog is not null;

    /// <summary>Lines published by extensions with setWidget (shown above the composer).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWidgets))]
    private string _widgetsText = "";

    public bool HasWidgets => WidgetsText.Length > 0;

    /// <summary>A URL omp asked to open (login flows). Opened only when the user clicks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingUrl))]
    private string? _pendingUrl;

    public bool HasPendingUrl => PendingUrl is not null;

    /// <summary>What omp says to do with the link (device flows put the code to type here).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingUrlInstructions))]
    private string _pendingUrlInstructions = "";

    public bool HasPendingUrlInstructions => PendingUrlInstructions.Length > 0;

    /// <summary>Provider omp is signing in to; other commands wait until it ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSigningIn), nameof(SigningInText))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendAsFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand))]
    private string? _signingIn;

    public bool IsSigningIn => SigningIn is not null;
    public string SigningInText => Loc.F("Signing in to {0}… omp waits for the browser or the code; other actions wait too.", SigningIn);

    /// <summary>Raised on the UI thread for a link the system opens (<see cref="OpenLink(Uri, LinkTarget)"/>: the default
    /// browser, the file manager, the mail app); the view launches it.</summary>
    public event Action<string>? OpenUrlRequested;

    /// <summary>The view writes the clipboard and reports whether it succeeded, after showing any failure.</summary>
    public event Func<string, Task<bool>>? CopyTextRequested;

    [ObservableProperty] private string _statusText = Loc.T("Starting…");
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private bool _isDebugVisible;
    [ObservableProperty] private bool _hasError;
    /// <summary>The run waits on the user (an approval or a question): the activity line says so in the warning colour.</summary>
    [ObservableProperty] private bool _isWaitingOnUser;

    public bool IsRunning => Phase is SessionPhase.Running or SessionPhase.Aborting;
    public bool IsIdle => !IsRunning;
    public string ComposerHint => IsReadOnly ? Loc.T("This conversation is read only.")
        : IsLeaseReadOnly ? Loc.T("This chat is open elsewhere. Choose Open here to continue it in this window.") : Phase switch
    {
        SessionPhase.Running when HasDialog => Loc.T("Answer the request above, or press Esc to stop"),
        SessionPhase.Running => Loc.F("Queue a message — {0} to queue, {1} to steer", SendKeyHint, KeyboardShortcuts.Steer),
        SessionPhase.Aborting => Loc.T("Stopping…"),
        SessionPhase.Ready => SendWithModifier ? Loc.F("Ask omp anything — {0} to send", SendKeyHint) : Loc.T("Ask omp anything — Shift+Enter for a new line"),
        SessionPhase.Faulted => Loc.T("omp is not running."),
        _ => Loc.T("Waiting for omp…"),
    };

    /// <summary>Raised on the UI thread after rows were added or the last row changed (for auto-scroll: followed only
    /// while the reader is at the latest message).</summary>
    public event Action? TranscriptChanged;

    /// <summary>Raised on the UI thread when the conversation must show its latest message whatever the reader was
    /// looking at: the user sent a message (prompt, follow-up, steer), or another conversation came in.</summary>
    public event Action? ScrollToLatestRequested;

    /// <summary>Raised on the UI thread after an apply added event-log lines.</summary>
    public event Action? DebugLogAppended;

    public long UiApplies => Interlocked.Read(ref _uiApplies);

    public void SetWindowState(string state)
    {
        _windowState = state;
        if (state == "Minimized")
        {
            if (_visible.Task.IsCompleted)
            {
                _visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = CollectWhileMinimizedAsync(_visible.Task, _cts.Token); // MainViewModel.OpenSessions.cs
            }
            // Nothing to show while minimized; the next apply after restore restarts the elapsed-time tick.
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _dialogTimer?.Stop();
            _dialogTimer = null;
        }
        else
        {
            var wasMinimized = _visible.TrySetResult();
            WakeFront();
            // The shown chat's updates were only looked at meanwhile: its newest state, now
            if (wasMinimized && !IsClosing) Apply(Session.Snapshot());
            UpdateDialogTimer();
            CurrentDialog?.UpdateRemaining(DateTimeOffset.UtcNow);
        }
    }

    private static TaskCompletionSource NewCompleted()
    {
        var t = new TaskCompletionSource();
        t.SetResult();
        return t;
    }

    public void OnWindowOpened()
    {
        _ = EnablePowerNotificationsAsync();
        // "Usable": the window is open and the first layout pass has run.
        Dispatcher.UIThread.Post(() =>
        {
            var usableMs = Stopwatch.GetElapsedTime(Program.StartTimestamp).TotalMilliseconds;
            var sinceProcessStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            AppendStartupLog($"window_usable_ms_since_main={usableMs:F0} window_usable_ms_since_process_start={sinceProcessStart:F0}");
            RefreshRuntimeStatus();
            StartUpdates();
            StartTeamProgressIfShared(); // MainViewModel.TeamProgress.cs
            StartGitBell(); // MainViewModel.GitBell.cs
            MoveOmpToNewPack();
            _ = StartAsync();
        }, DispatcherPriority.Background);
    }

    private async Task StartAsync()
    {
        var first = _open;
        foreach (var recovered in _opens.Values.Where(o => o != first).ToArray()) _ = StartSessionAsync(recovered);
        if (_args.TimelinePath is not null) _ = Task.Run(() => TimelineAsync(_args.TimelinePath, _cts.Token));
        _ompSpawnTs = Stopwatch.GetTimestamp();
        try
        {
            await StartSessionAsync(first).ConfigureAwait(false); // MainViewModel.OpenSessions.cs
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppendStartupLog("omp_start_failed " + e.Message.Split('\n')[0]);
            SmokeExit();
            return;
        }
        _ompReadyTs = Stopwatch.GetTimestamp();
        AppendStartupLog($"omp_spawn_to_session_ready_ms={RpcTimings.Ms(_ompSpawnTs, _ompReadyTs):F0} omp_pid={first.Controller.ProcessId}");
        SmokeExit();
        if (_args.BenchLatency > 0) await RunLatencyBenchAsync(_args.BenchLatency, _args.BenchOut ?? "latency.csv").ConfigureAwait(false);
        if (_args.AutoPrompt is { } p)
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // Startup continues off-thread, but admission and scope release belong to the UI thread.
                using var admission = AdmitWorktreeChat(null);
                if (admission is null) return;
                await first.Controller.PromptAsync(p, _cts.Token);
            });
    }

    /// <summary>Ready: a new prompt. Running: queued as a follow-up. Needs text or an image. Not while the project's
    /// repository is being updated from its remote (MainViewModel.GitBell.cs).</summary>
    private bool CanSend() => !IsReadOnly && Phase is SessionPhase.Ready or SessionPhase.Running && HasContent && !IsSigningIn && !GitUpdatingHere;

    // Concurrent: while the first prompt's acknowledgement is still on its way the run is already shown as working,
    // and a message typed then must queue, not be ignored (seen on macOS CI).
    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        if (IsReadOnly) return;
        if (!PrepareComposerForSend()) return;
        // Terminal-only commands (/settings, /plan…) never reach omp: they would go to the model as a message
        if (HandleTerminalOnlyCommand(ComposerText)) return;
        await SubmitAsync(TakeComposer, fromComposer: true);
    }

    /// <summary>Ready: a new prompt. Running: follow-up text or a builtin deferred locally until idle.
    /// <paramref name="take"/> takes the message box's or pet's content.</summary>
    private async Task SubmitAsync(Func<(string Text, ImageAttachment[] Images)> take, bool fromComposer = false, DraftContent? recovery = null)
    {
        if (IsReadOnly) return;
        using var admission = AdmitWorktreeChat(null);
        if (admission is null) return;
        var open = _open;
        string? submission = null;
        try
        {
            var content = take();
            submission = StageSubmission(open, content, fromComposer);
            if (Phase == SessionPhase.Running || open.Controller.ShouldQueueSubmission(content.Text))
            {
                var accepted = await open.Controller.QueueAsync(QueueKind.FollowUp, content.Text, content.Images, _cts.Token, submission);
                FinishSubmission(open, submission, accepted, recovery: recovery);
                ApplyIfShown(open);
                return;
            }
            open.OptimisticAfterVersion = open.Controller.Snapshot().Version;
            Phase = SessionPhase.Running;
            var sent = await open.Controller.PromptAsync(content.Text, content.Images, _cts.Token, submission);
            FinishSubmission(open, submission, sent, waitForDelivery: sent, recovery: recovery);
            SettleOptimisticPhase(open);
        }
        catch (Exception e) when (e is OmpNotRunningException or ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (submission is not null) FinishSubmission(open, submission, accepted: false, recovery: recovery);
            if (open == _open) ComposerMessage = e.Message;
            SettleOptimisticPhase(open);
        }
    }

    /// <summary>Running: stop the run. Aborting (a second press): force-stop omp and resume the session.</summary>
    private bool CanAbort() => Phase is SessionPhase.Running or SessionPhase.Aborting;

    public string StopLabel => Phase == SessionPhase.Aborting ? Loc.T("Force stop") : Loc.T("Stop");

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortAsync()
    {
        var open = _open;
        try
        {
            FlushDrafts();
            if (Phase == SessionPhase.Aborting)
            {
                await open.Controller.ForceStopAsync(_cts.Token);
                ApplyIfShown(open);
                return;
            }
            open.OptimisticAfterVersion = open.Controller.Snapshot().Version;
            Phase = SessionPhase.Aborting;
            await open.Controller.AbortAsync(_cts.Token);
            SettleOptimisticPhase(open);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>The command finished: Core's phase is authoritative again, even if no newer snapshot comes.</summary>
    private void SettleOptimisticPhase(OpenSession open)
    {
        open.OptimisticAfterVersion = -1;
        ApplyIfShown(open);
    }

    [RelayCommand]
    private void ToggleDebug() => IsDebugVisible = !IsDebugVisible;

    private async Task AnswerDialogAsync(string id, DialogAnswer answer)
    {
        var open = _open;
        var dialog = open == _open ? CurrentDialog : open.Dialog;
        try
        {
            open.InFlightReply = dialog?.ReplyForRecovery;
            SaveDraft(open);
            if (!string.IsNullOrEmpty(open.InFlightReply)) _draftStore.Flush();
            var accepted = await open.Controller.AnswerDialogAsync(id, answer, _cts.Token);
            dialog?.CompleteReply(accepted && answer is not DialogAnswer.Cancelled);
            open.InFlightReply = null;
            if (dialog is not null && !open.Controller.Snapshot().Dialogs.Any(d => d.Id == id)) RecoverReply(open, dialog);
            SaveDraft(open);
            if (dialog?.PreserveReply == true) _draftStore.Flush();
            ApplyIfShown(open);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OmpNotRunningException or OperationCanceledException)
        {
            open.InFlightReply = null;
            dialog?.CompleteReply(false);
            if (dialog is not null && !open.Controller.Snapshot().Dialogs.Any(d => d.Id == id)) RecoverReply(open, dialog);
            SaveDraft(open);
            if (open == _open) ComposerMessage = e.Message;
        }
    }

    [RelayCommand]
    private void OpenPendingUrl()
    {
        // A sign-in: the system's browser, where the user's accounts, password manager and passkeys are
        if (PendingUrl is { } url && IsSafeBrowserUrl(url, out var uri))
            OpenLink(uri, LinkTarget.SystemBrowser);
        PendingUrl = null;
    }

    [RelayCommand]
    private Task CopyPendingUrl() => PendingUrl is { } url ? CopyTextAsync(url, Loc.T("Link copied")) : Task.CompletedTask;

    [RelayCommand]
    private void DismissPendingUrl() => PendingUrl = null;

    internal void Apply(SessionSnapshot s)
    {
        var open = _open;
        // Snapshots reach the UI thread by two paths (the pump, and commands applying the state right after they
        // finish). The pump's may have been taken earlier: never let an older snapshot overwrite a newer one, or a
        // run that already ended shows as running again and stays so.
        if (open.Last is { } last && s.Version < last.Version) return;
        if (_appliedReadOnly != IsReadOnly)
        {
            _appliedReadOnly = IsReadOnly;
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(IsComposerLocked));
            OnPropertyChanged(nameof(ProjectFolder));
            OnPropertyChanged(nameof(ComposerHint));
            NewShellCommand.NotifyCanExecuteChanged();
            OpenOmpTuiCommand.NotifyCanExecuteChanged();
            RemoveProjectCommand.NotifyCanExecuteChanged();
            RenameSessionItemCommand.NotifyCanExecuteChanged();
            TogglePinItemCommand.NotifyCanExecuteChanged();
            DeleteSessionItemCommand.NotifyCanExecuteChanged();
        }
        if (IsReadOnly) OnPropertyChanged(nameof(ReadOnlyExplanation));
        RecoverRuntimeInput(open, s);
        ApplyLease(s); // MainViewModel.Lease.cs
        var perf = PerfLog.StartApply(); // OMBRA_PERF=1 only
        Interlocked.Increment(ref _uiApplies);
        var newConversation = s.TranscriptEpoch != open.TranscriptEpoch;
        if (newConversation)
        {
            // Another session (or a restarted omp): the old rows belong to a different conversation. Rows that hold
            // decoded images let them go.
            foreach (var old in Rows) (old as IDisposable)?.Dispose();
            Rows.Clear();
            open.RowsByKey.Clear();
            open.AppliedItems = [];
            open.TranscriptEpoch = s.TranscriptEpoch;
        }
        // Core hands out the same item instance until the item changes, so a slot that still holds the instance the
        // last applied snapshot had there needs nothing: while a reply streams only its own row is looked at, not
        // every row of a long conversation 30 times a second.
        var items = s.Items;
        var applied = open.AppliedItems;
        int added = 0, updated = 0;
        var reordered = items.Count < applied.Count;
        object? historyAnchor = reordered ? SaveScrollRequested?.Invoke() : null;
        // The first row whose change may change the step groups (StepGroups.cs)
        var regroupFrom = int.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!reordered && i < applied.Count && applied[i].Key != item.Key)
            {
                reordered = true;
                historyAnchor = SaveScrollRequested?.Invoke();
            }
            if (i < applied.Count && ReferenceEquals(applied[i], item)) continue;
            OfferPreviewUrls(item); // MainViewModel.Preview.cs: local URLs in new tool output become preview suggestions
            if (open.RowsByKey.TryGetValue(item.Key, out var row))
            {
                if (ReferenceEquals(row.Model, item)) continue;
                row.Update(item);
                updated++;
                if (StepGroups.Matters(row)) regroupFrom = Math.Min(regroupFrom, StepGroups.IndexFromEnd(Rows, row));
            }
            else
            {
                row = RowViewModel.Create(item);
                open.RowsByKey[item.Key] = row;
                Rows.Add(row);
                added++;
                if (StepGroups.Matters(row)) regroupFrom = Math.Min(regroupFrom, Rows.Count - 1);
            }
        }
        if (reordered)
        {
            ReorderHistoryRows(items);
            regroupFrom = 0;
        }
        if (regroupFrom != int.MaxValue) StepGroups.Regroup(Rows, regroupFrom);
        open.AppliedItems = items;
        var changed = newConversation || reordered || added + updated > 0;
        var debugAdded = IsDebugVisible && AppendDebugLines(s.DebugTail);
        perf?.Lap("rows");

        ApplyExtensionUi(s);
        MarkAwaitingApproval();
        ApplyQueue(s);
        ApplyCommandsAndProgress(s);
        ApplyPanes(s); // MainViewModel.Panes.cs: the plan and the background tasks
        ApplyPlanMode(s); // MainViewModel.PlanMode.cs: plan mode and the proposed plan's card
        perf?.Lap("panes");

        // A snapshot taken before our optimistic Send/Stop must not flip the phase back for a frame.
        var wasRunning = IsRunning;
        if (s.Version > open.OptimisticAfterVersion) Phase = s.Phase;
        var fileChanged = s.SessionFile != open.Last?.SessionFile;
        // From omp's snapshots, not the phase shown: Send shows Running before omp reports it, so the shown phase never
        // changes when the run's first snapshot comes, and no rescan followed a sent message (a new chat's row kept the
        // title it had before its first message, its order the time before it).
        var runChanged = IsRun(open.Last) != IsRun(s);
        HasError = s.Phase == SessionPhase.Faulted;
        open.Last = open.Seen = s;
        ApplyHistory();
        ApplySessionInfo(s);
        ApplyModelInfo(s);
        ApplySessionArea(s); // MainViewModel.Session.cs: context ring, pin, session menu
        ApplyBtwGoal(s); // MainViewModel.BtwGoal.cs: the goal bar and the BTW panel (after the phase: the BTW chip follows it)
        CheckAttention(open, s);
        WhenIdle(open, s); // MainViewModel.OpenSessions.cs: a restart that waited for the run to end
        if (s.Phase == SessionPhase.Ready) RememberProject(s.Cwd);
        // Another session, a sent message (the run starts) or a finished run changes the list (new file, the time of the
        // last message that orders it, first-message title).
        if (fileChanged || runChanged) RequestCatalogRefresh();
        UpdateStatus();
        ApplyPet(s); // MainViewModel.Pets.cs
        UpdateElapsedTimer();
        perf?.Lap("header");
        if (changed || wasRunning != IsRunning) MarkTurnEnds(all: newConversation || reordered || added > 0 || wasRunning != IsRunning);
        if (reordered && !newConversation) RestoreScrollRequested?.Invoke(historyAnchor);
        perf?.Lap("turns");
        if (newConversation) ScrollToLatestRequested?.Invoke(); // a chat opens on its latest message
        if (changed) TranscriptChanged?.Invoke();
        if (debugAdded)
        {
            RefreshOmpProcesses(force: false); // MainViewModel.OpenSessions.cs
            DebugLogAppended?.Invoke();
        }
        perf?.End(items.Count, added, updated);
    }

    private static bool IsRun(SessionSnapshot? s) => s?.Phase is SessionPhase.Running or SessionPhase.Aborting;

    /// <summary>
    /// The event log is filled only while its panel is shown (<see cref="OnIsDebugVisibleChanged"/> fills it from the
    /// newest snapshot when it opens): hidden, a list updated line by line 30 times a second is work nobody sees.
    /// </summary>
    private bool AppendDebugLines(IReadOnlyList<DebugEntry> tail)
    {
        // The new entries are the last ones: find where they start instead of reading all of Core's tail
        var from = tail.Count;
        while (from > 0 && tail[from - 1].Seq > _lastDebugSeq) from--;
        if (from == tail.Count) return false;
        for (var i = from; i < tail.Count; i++)
            DebugLog.Add($"{tail[i].At.ToLocalTime():HH:mm:ss.fff}  {tail[i].Type,-22} {tail[i].Summary}");
        _lastDebugSeq = tail[^1].Seq;
        while (DebugLog.Count > DebugLines) DebugLog.RemoveAt(0);
        return true;
    }

    partial void OnIsDebugVisibleChanged(bool value)
    {
        if (!value) return;
        RefreshOmpProcesses();
        if (_open.Last is not { } s) return;
        // Opened: the lines Core still holds (as many as the list keeps), not what was left from the last time
        DebugLog.Clear();
        _lastDebugSeq = 0;
        AppendDebugLines(s.DebugTail);
    }

    /// <summary>
    /// The reply that ends a turn (the last row before the next user message, or of a finished run) gets the actions;
    /// a failed reply that ends the conversation offers "Retry". A turn's end not yet told which files its turn changed
    /// takes them from the turn's tool rows (once: the turn is over). Each turn gets its "Worked for…" line (TurnFolds),
    /// and a message after a pause its day and time. <paramref name="all"/>: rows came or a run started or ended, so
    /// every turn is looked at; otherwise (a reply streaming) only the last one.
    /// </summary>
    private void MarkTurnEnds(bool all = true)
    {
        var retryable = !IsRunning;
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i] is NoticeRowViewModel) continue; // "Retrying (1 of 3)…", "Gave up retrying…" follow the failed reply
            if (Rows[i] is AssistantRowViewModel { HasError: true } failed)
            {
                if (failed.CanRetry != retryable) failed.CanRetry = retryable;
                retryable = false;
                continue;
            }
            if (Rows[i] is AssistantRowViewModel { CanRetry: true } was) was.CanRetry = false;
            retryable = false;
        }
        // A finished turn's answer (its last reply with text) carries the actions; a running turn's reply does not yet
        var turnOver = !IsRunning;
        // A turn's end taking its turn's files, and the turn's tool rows seen so far (last first)
        TurnEndRowViewModel? listing = null;
        List<ToolRowViewModel>? turnTools = null;
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            switch (Rows[i])
            {
                case UserRowViewModel: turnOver = true; ListFiles(); break;
                case ToolRowViewModel t:
                    turnTools?.Add(t);
                    break;
                case TurnEndRowViewModel te:
                    ListFiles();
                    if (!te.FilesKnown) (listing, turnTools) = (te, []);
                    break;
                case AssistantRowViewModel a:
                    var end = turnOver && a.HasText;
                    if (a.IsTurnEnd != end) a.IsTurnEnd = end;
                    if (end) turnOver = false;
                    break;
            }
        }
        ListFiles();
        TurnFolds.Reconcile(Rows, IsRunning, all);
        if (all) MarkDays();

        void ListFiles()
        {
            if (listing is null) return;
            turnTools!.Reverse();
            listing.SetFiles(turnTools);
            (listing, turnTools) = (null, null);
        }
    }

    /// <summary>
    /// Codex's day and time over a message: the first one of the conversation, and one written after a pause (a new day,
    /// or <see cref="MessageTimes.Pause"/> after the message or reply before it). Only where omp said when they were written.
    /// </summary>
    private void MarkDays()
    {
        DateTimeOffset? before = null;
        foreach (var row in Rows)
        {
            switch (row)
            {
                case AssistantRowViewModel { At: { } replied }:
                    before = replied;
                    break;
                case UserRowViewModel u:
                    var label = u.At is { } sent && (before is not { } prev || sent - prev >= MessageTimes.Pause || sent.ToLocalTime().Date != prev.ToLocalTime().Date)
                        ? MessageTimes.Day(sent) : "";
                    if (u.DateLabel != label) u.DateLabel = label;
                    if (u.At is not null) before = u.At;
                    break;
            }
        }
    }

    /// <summary>The running call an approval card asks about: the newest running tool row while omp asks to allow a
    /// call. It reads "Waiting for approval" rather than "Running", as the activity line and the card say.</summary>
    private ToolRowViewModel? _awaitingApproval;

    private void MarkAwaitingApproval()
    {
        ToolRowViewModel? row = null;
        if (CurrentDialog is { IsApproval: true })
            for (var i = Rows.Count - 1; i >= 0 && row is null; i--)
                if (Rows[i] is ToolRowViewModel { IsRunning: true } t) row = t;
                else if (Rows[i] is UserRowViewModel) break;
        if (ReferenceEquals(row, _awaitingApproval)) return;
        if (_awaitingApproval is not null) _awaitingApproval.AwaitsApproval = false;
        _awaitingApproval = row;
        if (row is not null) row.AwaitsApproval = true;
    }

    // Project only at the display boundary; snapshots and protocol text remain unchanged.
    private static string WidgetText(IReadOnlyList<ExtensionWidget> widgets) =>
        Ombra.App.Controls.VisibleText.FromUntrustedOutput(string.Join("\n", widgets.SelectMany(w => w.Lines)));

    private void ApplyExtensionUi(SessionSnapshot s)
    {
        var first = s.Dialogs.Count > 0 ? s.Dialogs[0] : null;
        if (first?.Id != CurrentDialog?.Id)
        {
            var owner = _open;
            CurrentDialog = first is null ? null : new DialogViewModel(first, AnswerDialogAsync, _approvalActions) { PreserveReply = s.SigningIn is null };
            if (CurrentDialog is { } created) created.DraftChanged += () => SaveDraft(owner);
        }
        if (CurrentDialog is { } d) d.QueueText = s.Dialogs.Count > 1 ? Loc.F("{0} of {1}", 1, s.Dialogs.Count) : "";
        UpdateDialogTimer();

        // Core copies the same widget records into every snapshot: the lines are joined again only when one changed
        if (_open.Last is not { } last || !s.Widgets.SequenceEqual(last.Widgets, ReferenceEqualityComparer.Instance))
        {
            var widgets = WidgetText(s.Widgets);
            if (widgets != WidgetsText) WidgetsText = widgets;
        }

        // Separate counters: both requests can land in one apply, in either order.
        if (s.EditorText is { } et && et.Seq > _open.LastEditorSeq)
        {
            // An extension must not replace text typed while its request was in flight.
            ComposerText = ComposerText.Length == 0 ? et.Text : ComposerText + "\n\n" + et.Text;
            _open.LastEditorSeq = et.Seq;
        }
        if (s.OpenUrl is { } ou && ou.Seq > _open.LastUrlSeq)
        {
            _open.LastUrlSeq = ou.Seq;
            if (!OpenOwnSignInPage(ou))
            {
                PendingUrl = ou.Target;
                PendingUrlInstructions = ou.Instructions ?? "";
            }
        }
        if (!s.McpAuthRequests.SequenceEqual(McpAuthRequests)) McpAuthRequests = s.McpAuthRequests;
        SigningIn = s.SigningIn;
    }

    /// <summary>Once-a-second countdown while a dialog with a deadline is shown. Cosmetic only.</summary>
    private void UpdateDialogTimer()
    {
        var needed = CurrentDialog?.HasDeadline == true && _windowState != "Minimized";
        if (needed && _dialogTimer is null)
        {
            _dialogTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => CurrentDialog?.UpdateRemaining(DateTimeOffset.UtcNow));
            _dialogTimer.Start();
        }
        else if (!needed && _dialogTimer is not null)
        {
            _dialogTimer.Stop();
            _dialogTimer = null;
        }
    }

    private void UpdateStatus()
    {
        if (_open.Last is not { } s) return;
        if (IsReadOnly)
        {
            IsWaitingOnUser = false;
            StatusText = Loc.T("Read only");
            StatusDetail = Ombra.App.Controls.VisibleText.FromUntrustedOutput(ReadOnlyExplanation);
            return;
        }
        // The shown phase (an optimistic Send or Stop sets it before omp confirms): a first frame said "Ready" while running
        if (Phase != SessionPhase.Running) IsWaitingOnUser = false;
        StatusText = Ombra.App.Controls.VisibleText.FromUntrustedOutput(Phase switch
        {
            SessionPhase.Starting => IsOpeningSession ? Loc.T("Opening…") : Loc.T("Starting omp…"),
            SessionPhase.Ready => Loc.T("Ready"),
            SessionPhase.Running => $"{Activity(s)} · {Elapsed(s)}",
            SessionPhase.Aborting => Loc.T("Stopping…"),
            SessionPhase.Stopping => Loc.T("Shutting down…"),
            SessionPhase.Stopped => Loc.T("Stopped"),
            SessionPhase.Faulted => Loc.T("omp stopped: ") + (FailureHeadline(s) ?? Loc.T("unknown error")),
            _ => Loc.T("Starting…"),
        });
        StatusDetail = Ombra.App.Controls.VisibleText.FromUntrustedOutput(string.Join("  ·  ", new[]
        {
            s.Model ?? Loc.T("no model"),
            s.ExtensionStatus.Count > 0 ? string.Join("  ", s.ExtensionStatus.Values) : null,
            $"RPC v{s.ProtocolVersion}",
            Loc.F("{0:N0} events", s.FramesReceived),
            s.ContextPercent is { } pct ? Loc.F("context {0:0}%", pct) : null,
            s.LastFrameAt is { } at ? Loc.F("last {0:HH:mm:ss}", at.ToLocalTime()) : null,
        }.Where(x => x is not null)));
    }

    /// <summary>Why omp stopped, in one line: the client's own words in the UI's language, omp's as omp wrote them.</summary>
    internal static string? FailureHeadline(SessionSnapshot s) =>
        (s.LastErrorText is { } text ? Loc.F(text.Format, text.Args) : s.LastError)?.Split('\n')[0];

    /// <summary>What the run is doing right now (Claude Code's spinner verb), from the newest rows of this turn.</summary>
    private string Activity(SessionSnapshot s)
    {
        IsWaitingOnUser = CurrentDialog is not null;
        if (CurrentDialog is { } d) return d.IsApproval ? Loc.T("Waiting for your approval") : Loc.T("Waiting for your answer");
        for (var i = s.Items.Count - 1; i >= 0; i--)
        {
            switch (s.Items[i])
            {
                case ToolItem { Status: ToolStatus.Running } t: return Loc.F("Running {0}", t.Name);
                case AssistantItem { Streaming: true } a: return a.Text.Length > 0 ? Loc.T("Writing") : a.Thinking.Length > 0 ? Loc.T("Thinking") : Loc.T("Working");
                case UserItem: return Loc.T("Working");
            }
        }
        return Loc.T("Working");
    }

    /// <summary>How long the run has gone, as Claude Code writes it: "4s", "1m 05s", "1h 02m".</summary>
    internal static string Elapsed(SessionSnapshot s)
    {
        var t = s.RunTime.Elapsed;
        return t.TotalHours >= 1 ? Loc.F("{0}h {1:00}m", (int)t.TotalHours, t.Minutes)
            : t.TotalMinutes >= 1 ? Loc.F("{0}m {1:00}s", (int)t.TotalMinutes, t.Seconds)
            : Loc.F("{0}s", t.Seconds);
    }

    partial void OnPhaseChanged(SessionPhase value)
    {
        UpdateStatus();
        UpdateElapsedTimer();
        ApplyHistory();
    }

    /// <summary>Cosmetic once-a-second tick for the elapsed time while running. Nothing depends on it.</summary>
    private void UpdateElapsedTimer()
    {
        if (IsRunning && _elapsedTimer is null)
        {
            _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
            {
                UpdateStatus();
                // The live "Thinking… 4s" label of the reply being written
                for (var i = Rows.Count - 1; i >= 0 && i >= Rows.Count - 8; i--)
                    if (Rows[i] is AssistantRowViewModel { IsThinkingLive: true } live) live.Tick();
            });
            _elapsedTimer.Start();
        }
        else if (!IsRunning && _elapsedTimer is not null)
        {
            _elapsedTimer.Stop();
            _elapsedTimer = null;
        }
    }

    /// <summary>T0..T4 for a non-model command (get_state); T4 is taken on the UI thread, where the ViewModel sees the result.</summary>
    private async Task RunLatencyBenchAsync(int n, string path)
    {
        var sb = new StringBuilder("i,t0_t1_ms,t1_t2_ms,t2_t3_ms,t3_t4_ms,t0_t4_ms\n");
        for (var i = 0; i < n; i++)
        {
            var r = await Session.ProbeAsync(_cts.Token).ConfigureAwait(false);
            var t4 = await Dispatcher.UIThread.InvokeAsync(() => Stopwatch.GetTimestamp(), DispatcherPriority.Normal);
            var t = r.Timings;
            sb.Append(CultureInfo.InvariantCulture, $"{i},{RpcTimings.Ms(t.Sent, t.Flushed):F4},{RpcTimings.Ms(t.Flushed, t.ResponseRead):F4},")
              .Append(CultureInfo.InvariantCulture, $"{RpcTimings.Ms(t.ResponseRead, t.Delivered):F4},{RpcTimings.Ms(t.Delivered, t4):F4},{RpcTimings.Ms(t.Sent, t4):F4}\n");
            await Task.Delay(20, _cts.Token).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
        AppendStartupLog($"latency_bench_done n={n} out={path}");
        if (_args.ExitAfterBench)
            Dispatcher.UIThread.Post(() => (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close());
    }

    /// <summary>Package smoke (<c>--smoke-exit-after-ms</c>): record how the start ended, then close the window normally.</summary>
    private void SmokeExit()
    {
        if (_args.SmokeExitAfterMs <= 0) return;
        var snap = Session.Snapshot();
        AppendStartupLog($"smoke phase={snap.Phase} start_problem={snap.StartProblem} omp_pid={Session.ProcessId}");
        _ = Task.Delay(_args.SmokeExitAfterMs).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close()),
            TaskScheduler.Default);
    }

    /// <summary>Every 5 s: Core's state (advances regardless of UI) next to how many UI applies happened.</summary>
    private async Task TimelineAsync(string path, CancellationToken ct)
    {
        await using var w = new StreamWriter(path, append: false) { AutoFlush = true };
        await w.WriteLineAsync("utc,window,phase,frames,bytes,last_frame_utc,items,assistant_chars,tools,messages_completed,stream_mismatches,ui_applies,managed_heap_mib,gen2_collections,working_set_mib").ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                var s = Session.Snapshot();
                var chars = s.Items.OfType<AssistantItem>().Sum(a => (long)a.Text.Length);
                var tools = s.Items.OfType<ToolItem>().Count();
                await w.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.UtcNow:O},{_windowState},{s.Phase},{s.FramesReceived},{s.BytesReceived},{s.LastFrameAt:O},{s.Items.Count},{chars},{tools},{s.MessagesCompleted},{s.StreamMismatches},{UiApplies},{GC.GetTotalMemory(false) / 1048576.0:F1},{GC.CollectionCount(2)},{Environment.WorkingSet / 1048576.0:F1}")).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private void AppendStartupLog(string line)
    {
        if (_args.StartupLogPath is null) return;
        try { File.AppendAllText(_args.StartupLogPath, $"{DateTimeOffset.UtcNow:O} {line}\n"); }
        catch (IOException) { }
    }

    /// <summary>Every caller waits for the same graceful stop (a second close click must not skip it).</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _dispose = DisposeCoreAsync();
        return new ValueTask(_dispose!);
    }

    private async Task DisposeCoreAsync()
    {
        DisposePowerNotifications();
        FlushDrafts();
        if (_settings is not null) _settings.WarningsChanged -= OnSettingsWarningsChanged;
        // Timers and watchers first: none may fire into the window after it is gone (a pet's mood change on a closed
        // window made Avalonia lay it out and fail in Grid.ArrangeOverride)
        _elapsedTimer?.Stop();
        _dialogTimer?.Stop();
        _cardTimer?.Stop();
        _pets?.Shutdown();
        _files?.Shutdown();
        try { await Documents.FlushRecoveryAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ComposerMessage = Loc.F("Editor recovery could not be saved: {0}", e.Message);
            Console.Error.WriteLine(ComposerMessage);
        }
        if (_tasksCreated) Tasks.Shutdown();
        _visible.TrySetResult();
        await DisposeDictationAsync();
        // Cancel first: a start still in progress then disposes its omp instead of finishing it. Every chat's omp stops
        // at once (MainViewModel.OpenSessions.cs).
        _cts.Cancel();
        await _host.DisposeAsync();
        EditorLanguageService.Dispose();
        try { await Documents.DisposeAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Editor recovery save failed; retained journal: " + e.Message); }
        var pumps = _opens.Values.Select(o => o.Pump).OfType<Task>().ToArray();
        try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        foreach (var open in _opens.Values) { open.Controller.QueueCheckpoint = null; open.Controller.SubmissionDelivered = null; open.Controller.PrepareLaunch = null; }
        try
        {
            if (_ownsDraftStore) _draftStore.Dispose();
            else _draftStore.Flush(); // Program retains application admission until all desktop shutdown handlers finish.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Draft save failed: " + e.Message); }
        _cts.Dispose();
        ApplyUpdateOnQuit();
    }
}
