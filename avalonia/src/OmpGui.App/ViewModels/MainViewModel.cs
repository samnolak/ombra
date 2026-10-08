using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Services;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Upper bound on UI refreshes while streaming. Deadline-based on a pool thread; no UI timer involved.</summary>
    private static readonly TimeSpan MinApplyInterval = TimeSpan.FromMilliseconds(33);
    private const int DebugLines = 500;
    private static readonly TimeSpan MinimizedGcCheckInterval = TimeSpan.FromSeconds(30);
    private const long MinimizedGcAllocationThreshold = 32L * 1024 * 1024;
    private static readonly TimeSpan MinimizedAttentionInterval = TimeSpan.FromSeconds(1);

    private readonly AppArgs _args;
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

    public MainViewModel(SessionController session, AppArgs args, string? configError = null, ClientSettingsStore? settings = null)
    {
        _args = args;
        _settings = settings;
        if (configError is not null) Rows.Add(RowViewModel.Create(new NoticeItem(0, NoticeLevel.Warning, configError)));
        ApprovalRules = session.ApprovalRules ??= new ApprovalRuleSet(settings); // MainViewModel.Approvals.cs
        _approvalActions = new ApprovalActions(AllowWithRuleAsync, DenyWithFeedbackAsync);
        _host = new SessionHost(session, MayClose) { MaxProcesses = LoadMaxOpenSessions() }; // MainViewModel.OpenSessions.cs
        _host.Closed += OnHostClosed;
        _open = Register(session);
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
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(SteerCommand))]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsIdle), nameof(ComposerHint), nameof(CanQueue), nameof(StopLabel), nameof(ApprovalPendingNote))]
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
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand))]
    private string? _signingIn;

    public bool IsSigningIn => SigningIn is not null;
    public string SigningInText => $"Signing in to {SigningIn}… omp waits for the browser or the code; other actions wait too.";

    /// <summary>Raised on the UI thread when the user chose to open <see cref="PendingUrl"/>; the view launches it.</summary>
    public event Action<string>? OpenUrlRequested;

    /// <summary>Raised on the UI thread when the user chose to copy <see cref="PendingUrl"/>.</summary>
    public event Action<string>? CopyTextRequested;

    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private bool _isDebugVisible;
    [ObservableProperty] private bool _hasError;
    /// <summary>The run waits on the user (an approval or a question): the activity line says so in the warning colour.</summary>
    [ObservableProperty] private bool _isWaitingOnUser;

    public bool IsRunning => Phase is SessionPhase.Running or SessionPhase.Aborting;
    public bool IsIdle => !IsRunning;
    public string ComposerHint => Phase switch
    {
        SessionPhase.Running when HasDialog => "Answer the request above, or press Esc to stop",
        SessionPhase.Running => $"Queue a message — {SendKeyHint} to queue, {KeyboardShortcuts.Steer} to steer",
        SessionPhase.Aborting => "Stopping…",
        SessionPhase.Ready => SendWithModifier ? $"Ask omp anything — {SendKeyHint} to send" : "Ask omp anything — Shift+Enter for a new line",
        SessionPhase.Faulted => "omp is not running.",
        _ => "Waiting for omp…",
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
        // "Usable": the window is open and the first layout pass has run.
        Dispatcher.UIThread.Post(() =>
        {
            var usableMs = Stopwatch.GetElapsedTime(Program.StartTimestamp).TotalMilliseconds;
            var sinceProcessStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            AppendStartupLog($"window_usable_ms_since_main={usableMs:F0} window_usable_ms_since_process_start={sinceProcessStart:F0}");
            RefreshRuntimeStatus();
            StartUpdates();
            MoveOmpToNewPack();
            _ = StartAsync();
        }, DispatcherPriority.Background);
    }

    private async Task StartAsync()
    {
        var first = _open;
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
        if (_args.AutoPrompt is { } p) await first.Controller.PromptAsync(p, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Ready: a new prompt. Running: queued as a follow-up. Needs text or an image.</summary>
    private bool CanSend() => Phase is SessionPhase.Ready or SessionPhase.Running && HasContent && !IsSigningIn;

    // Concurrent: while the first prompt's acknowledgement is still on its way the run is already shown as working,
    // and a message typed then must queue, not be ignored (seen on macOS CI).
    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        // Terminal-only commands (/settings, /plan…) never reach omp: they would go to the model as a message
        if (HandleTerminalOnlyCommand(ComposerText)) return;
        await SubmitAsync(TakeComposer);
    }

    /// <summary>Ready: a new prompt. Running: queued as a follow-up. <paramref name="take"/> takes the content (the
    /// message box's, or the pet's message box's).</summary>
    private async Task SubmitAsync(Func<(string Text, ImageAttachment[] Images)> take)
    {
        // The chat it goes to: another one may be shown by the time omp answers
        var open = _open;
        (string Text, ImageAttachment[] Images) content = ("", []);
        try
        {
            if (Phase == SessionPhase.Running)
            {
                content = take();
                await open.Controller.QueueAsync(QueueKind.FollowUp, content.Text, content.Images, _cts.Token);
                ApplyIfShown(open);
                return;
            }
            content = take();
            // Reflect Running at once instead of waiting for the next pump tick.
            open.OptimisticAfterVersion = open.Controller.Snapshot().Version;
            Phase = SessionPhase.Running;
            await open.Controller.PromptAsync(content.Text, content.Images, _cts.Token);
            SettleOptimisticPhase(open);
        }
        catch (OmpNotRunningException)
        {
            if (open == _open) PutBackUnsent(content);
            SettleOptimisticPhase(open);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>Running: stop the run. Aborting (a second press): force-stop omp and resume the session.</summary>
    private bool CanAbort() => Phase is SessionPhase.Running or SessionPhase.Aborting;

    public string StopLabel => Phase == SessionPhase.Aborting ? "Force stop" : "Stop";

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortAsync()
    {
        var open = _open;
        try
        {
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
        try
        {
            await open.Controller.AnswerDialogAsync(id, answer, _cts.Token);
            ApplyIfShown(open);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    [RelayCommand]
    private void OpenPendingUrl()
    {
        if (PendingUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            OpenUrlRequested?.Invoke(uri.AbsoluteUri);
        PendingUrl = null;
    }

    [RelayCommand]
    private void CopyPendingUrl()
    {
        if (PendingUrl is { } url) CopyTextRequested?.Invoke(url);
    }

    [RelayCommand]
    private void DismissPendingUrl() => PendingUrl = null;

    internal void Apply(SessionSnapshot s)
    {
        var open = _open;
        // Snapshots reach the UI thread by two paths (the pump, and commands applying the state right after they
        // finish). The pump's may have been taken earlier: never let an older snapshot overwrite a newer one, or a
        // run that already ended shows as running again and stays so.
        if (open.Last is { } last && s.Version < last.Version) return;
        var perf = PerfLog.StartApply(); // OMPGUI_PERF=1 only
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
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i < applied.Count && ReferenceEquals(applied[i], item)) continue;
            OfferPreviewUrls(item); // MainViewModel.Preview.cs: local URLs in new tool output become preview suggestions
            if (open.RowsByKey.TryGetValue(item.Key, out var row))
            {
                if (ReferenceEquals(row.Model, item)) continue;
                row.Update(item);
                updated++;
            }
            else
            {
                row = RowViewModel.Create(item);
                open.RowsByKey[item.Key] = row;
                Rows.Add(row);
                added++;
            }
        }
        open.AppliedItems = items;
        var changed = newConversation || added + updated > 0;
        var debugAdded = IsDebugVisible && AppendDebugLines(s.DebugTail);
        perf?.Lap("rows");

        ApplyExtensionUi(s);
        ApplyQueue(s);
        ApplyCommandsAndProgress(s);
        ApplyPanes(s); // MainViewModel.Panes.cs: the plan and the background tasks
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
        ApplySessionInfo(s);
        ApplyModelInfo(s);
        ApplySessionArea(s); // MainViewModel.Session.cs: context ring, pin, session menu
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
        if (changed || wasRunning != IsRunning) MarkTurnEnds();
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
    /// a failed reply that ends the conversation offers "Retry".
    /// </summary>
    private void MarkTurnEnds()
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
        var turnOver = !IsRunning;
        // The "Worked for…" line after the reply that ends a turn carries that reply's copy action
        TurnEndRowViewModel? endLine = null;
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            switch (Rows[i])
            {
                case UserRowViewModel: turnOver = true; Release(); break;
                case ToolRowViewModel: turnOver = false; Release(); break; // the turn ends in tool calls: no actions mid-turn
                case TurnEndRowViewModel te:
                    Release();
                    endLine = te;
                    break;
                case AssistantRowViewModel a:
                    var end = turnOver && a.HasText;
                    if (a.IsTurnEnd != end) a.IsTurnEnd = end;
                    var carried = end && endLine is not null;
                    if (carried && endLine!.Reply != a) endLine.Reply = a;
                    if (a.HasTurnEndRow != carried) a.HasTurnEndRow = carried;
                    if (end) turnOver = false;
                    if (!carried) Release();
                    endLine = null;
                    break;
            }
        }
        Release();

        // A "Worked for…" line with no reply of its own (the turn ended in tool calls) has nothing to copy
        void Release()
        {
            if (endLine?.Reply is not null) endLine.Reply = null;
            endLine = null;
        }
    }

    private void ApplyExtensionUi(SessionSnapshot s)
    {
        var first = s.Dialogs.Count > 0 ? s.Dialogs[0] : null;
        if (first?.Id != CurrentDialog?.Id) CurrentDialog = first is null ? null : new DialogViewModel(first, AnswerDialogAsync, _approvalActions);
        if (CurrentDialog is { } d) d.QueueText = s.Dialogs.Count > 1 ? $"1 of {s.Dialogs.Count}" : "";
        UpdateDialogTimer();

        // Core copies the same widget records into every snapshot: the lines are joined again only when one changed
        if (_open.Last is not { } last || !s.Widgets.SequenceEqual(last.Widgets, ReferenceEqualityComparer.Instance))
        {
            var widgets = string.Join("\n", s.Widgets.SelectMany(w => w.Lines));
            if (widgets != WidgetsText) WidgetsText = widgets;
        }

        // Separate counters: both requests can land in one apply, in either order.
        if (s.EditorText is { } et && et.Seq > _open.LastEditorSeq)
        {
            ComposerText = et.Text;
            _open.LastEditorSeq = et.Seq;
        }
        if (s.OpenUrl is { } ou && ou.Seq > _open.LastUrlSeq)
        {
            PendingUrl = ou.Target;
            PendingUrlInstructions = ou.Instructions ?? "";
            _open.LastUrlSeq = ou.Seq;
        }
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
        // The shown phase (an optimistic Send or Stop sets it before omp confirms): a first frame said "Ready" while running
        if (Phase != SessionPhase.Running) IsWaitingOnUser = false;
        StatusText = Phase switch
        {
            SessionPhase.Starting => "Starting omp…",
            SessionPhase.Ready => "Ready",
            SessionPhase.Running => $"{Activity(s)} · {Elapsed(s)}",
            SessionPhase.Aborting => "Stopping…",
            SessionPhase.Stopping => "Shutting down…",
            SessionPhase.Stopped => "Stopped",
            SessionPhase.Faulted => "omp stopped: " + (s.LastError?.Split('\n')[0] ?? "unknown error"),
            _ => "Starting…",
        };
        StatusDetail = string.Join("  ·  ", new[]
        {
            s.Model ?? "no model",
            s.ExtensionStatus.Count > 0 ? string.Join("  ", s.ExtensionStatus.Values) : null,
            $"RPC v{s.ProtocolVersion}",
            $"{s.FramesReceived:N0} events",
            s.ContextPercent is { } pct ? string.Create(CultureInfo.InvariantCulture, $"context {pct:0}%") : null,
            s.LastFrameAt is { } at ? $"last {at.ToLocalTime():HH:mm:ss}" : null,
        }.Where(x => x is not null));
    }

    /// <summary>What the run is doing right now (Claude Code's spinner verb), from the newest rows of this turn.</summary>
    private string Activity(SessionSnapshot s)
    {
        IsWaitingOnUser = CurrentDialog is not null;
        if (CurrentDialog is { } d) return d.IsApproval ? "Waiting for your approval" : "Waiting for your answer";
        for (var i = s.Items.Count - 1; i >= 0; i--)
        {
            switch (s.Items[i])
            {
                case ToolItem { Status: ToolStatus.Running } t: return "Running " + t.Name;
                case AssistantItem { Streaming: true } a: return a.Text.Length > 0 ? "Writing" : a.Thinking.Length > 0 ? "Thinking" : "Working";
                case UserItem: return "Working";
            }
        }
        return "Working";
    }

    /// <summary>How long the run has gone, as Claude Code writes it: "4s", "1m 05s", "1h 02m".</summary>
    private static string Elapsed(SessionSnapshot s)
    {
        var t = s.RunStartedAt is { } started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes:00}m")
            : t.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}m {t.Seconds:00}s")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Seconds}s");
    }

    partial void OnPhaseChanged(SessionPhase value)
    {
        UpdateStatus();
        UpdateElapsedTimer();
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
        // Timers and watchers first: none may fire into the window after it is gone (a pet's mood change on a closed
        // window made Avalonia lay it out and fail in Grid.ArrangeOverride)
        _elapsedTimer?.Stop();
        _dialogTimer?.Stop();
        _cardTimer?.Stop();
        _pets?.Shutdown();
        _files?.Shutdown();
        if (_tasksCreated) Tasks.Shutdown();
        _visible.TrySetResult();
        await DisposeDictationAsync();
        // Cancel first: a start still in progress then disposes its omp instead of finishing it. Every chat's omp stops
        // at once (MainViewModel.OpenSessions.cs).
        _cts.Cancel();
        await _host.DisposeAsync();
        var pumps = _opens.Values.Select(o => o.Pump).OfType<Task>().ToArray();
        try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        _cts.Dispose();
        ApplyUpdateOnQuit();
    }
}
