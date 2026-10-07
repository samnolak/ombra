using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private readonly SessionController _session;
    private readonly AppArgs _args;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<long, RowViewModel> _rowsByKey = [];
    private long _lastDebugSeq;
    private long _uiApplies;
    private long _ompSpawnTs;
    private long _ompReadyTs;
    private volatile string _windowState = "Normal";
    // Completed while the window is visible. While minimized the pump waits on it instead of applying:
    // Avalonia renders nothing then, and every control touched meanwhile stays queued in the renderer's
    // dirty set until the next frame, so UI updates in a minimized window only grow memory.
    private TaskCompletionSource _visible = NewCompleted();
    private Task? _dispose;
    private DispatcherTimer? _elapsedTimer;
    private DispatcherTimer? _dialogTimer;
    private long _lastEditorSeq;
    private long _lastUrlSeq;
    private long _transcriptEpoch;
    private SessionSnapshot? _last;
    private long _optimisticAfterVersion = -1;
    private Task? _pump;
    private int _disposed;

    public MainViewModel(SessionController session, AppArgs args, string? configError = null, ClientSettingsStore? settings = null)
    {
        _session = session;
        _args = args;
        _settings = settings;
        if (configError is not null) Rows.Add(RowViewModel.Create(new NoticeItem(0, NoticeLevel.Warning, configError)));
        Rows.CollectionChanged += (_, _) => NotifyRecoverLayout();
    }

    public ObservableCollection<RowViewModel> Rows { get; } = [];
    public ObservableCollection<string> DebugLog { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _composerText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartRenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(SteerCommand))]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsIdle), nameof(ComposerHint), nameof(CanQueue), nameof(StopLabel))]
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
    [NotifyCanExecuteChangedFor(nameof(NewSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
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
        SessionPhase.Running when HasDialog => "Answer above — or Esc to stop the run",
        SessionPhase.Running => "Queue a message — Alt+Enter to steer",
        SessionPhase.Aborting => "Stopping…",
        SessionPhase.Ready => "Ask omp anything — Shift+Enter for a new line",
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
            if (_visible.Task.IsCompleted) _visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Nothing to show while minimized; the next apply after restore restarts the elapsed-time tick.
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _dialogTimer?.Stop();
            _dialogTimer = null;
        }
        else
        {
            _visible.TrySetResult();
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
        _pump = Task.Run(() => PumpAsync(_cts.Token));
        if (_args.TimelinePath is not null) _ = Task.Run(() => TimelineAsync(_args.TimelinePath, _cts.Token));
        _ompSpawnTs = Stopwatch.GetTimestamp();
        try
        {
            await _session.StartAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppendStartupLog("omp_start_failed " + e.Message.Split('\n')[0]);
            SmokeExit();
            return;
        }
        _ompReadyTs = Stopwatch.GetTimestamp();
        AppendStartupLog($"omp_spawn_to_session_ready_ms={RpcTimings.Ms(_ompSpawnTs, _ompReadyTs):F0} omp_pid={_session.ProcessId}");
        SmokeExit();
        if (_args.BenchLatency > 0) await RunLatencyBenchAsync(_args.BenchLatency, _args.BenchOut ?? "latency.csv").ConfigureAwait(false);
        if (_args.AutoPrompt is { } p) await _session.PromptAsync(p, _cts.Token).ConfigureAwait(false);
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
        try
        {
            if (Phase == SessionPhase.Running)
            {
                var (queued, queuedImages) = take();
                await _session.QueueAsync(QueueKind.FollowUp, queued, queuedImages, _cts.Token);
                Apply(_session.Snapshot());
                return;
            }
            var (text, images) = take();
            // Reflect Running at once instead of waiting for the next pump tick.
            _optimisticAfterVersion = _session.Snapshot().Version;
            Phase = SessionPhase.Running;
            await _session.PromptAsync(text, images, _cts.Token);
            SettleOptimisticPhase();
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
        try
        {
            if (Phase == SessionPhase.Aborting)
            {
                await _session.ForceStopAsync(_cts.Token);
                Apply(_session.Snapshot());
                return;
            }
            _optimisticAfterVersion = _session.Snapshot().Version;
            Phase = SessionPhase.Aborting;
            await _session.AbortAsync(_cts.Token);
            SettleOptimisticPhase();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>The command finished: Core's phase is authoritative again, even if no newer snapshot comes.</summary>
    private void SettleOptimisticPhase()
    {
        _optimisticAfterVersion = -1;
        Apply(_session.Snapshot());
    }

    [RelayCommand]
    private void ToggleDebug() => IsDebugVisible = !IsDebugVisible;

    private async Task AnswerDialogAsync(string id, DialogAnswer answer)
    {
        try
        {
            await _session.AnswerDialogAsync(id, answer, _cts.Token);
            Apply(_session.Snapshot());
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

    /// <summary>Wakes on Core changes, coalesces bursts and applies the newest snapshot on the UI thread.</summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        long lastApply = 0;
        try
        {
            while (await _session.Changes.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _session.Changes.TryRead(out _);
                if (!_visible.Task.IsCompleted)
                {
                    // Minimized: Core keeps going; apply the newest snapshot the moment the window is back.
                    await WaitWhileMinimizedAsync(ct).ConfigureAwait(false);
                    lastApply = 0;
                }
                var wait = MinApplyInterval - Stopwatch.GetElapsedTime(lastApply);
                if (lastApply != 0 && wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                _session.Changes.TryRead(out _);
                var snapshot = _session.Snapshot();
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() => Apply(snapshot), DispatcherPriority.Background);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One bad update must not stop all later ones; the next snapshot is complete anyway.
                    Console.Error.WriteLine("UI update failed: " + e);
                }
                lastApply = Stopwatch.GetTimestamp();
            }
            // Core completed its change stream (session disposed): show the final state.
            var final = _session.Snapshot();
            await Dispatcher.UIThread.InvokeAsync(() => Apply(final), DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Waits for the window to be visible again. Meanwhile the Core still parses omp's stream (~6 MB/min of
    /// short-lived allocations). With nothing rendered there is no GC pressure from the renderer, and the
    /// workstation GC's gen0 budget can be hundreds of MB, so that garbage would sit in RSS until restore.
    /// Bound it: every 30 s, collect the young generations once 32 MB have been allocated since the last check.
    /// Nothing is applied to the UI meanwhile, but a run that ends or a question omp asks must still reach the user:
    /// on a change, at most once a second, the attention check alone runs on the newest snapshot.
    /// </summary>
    private async Task WaitWhileMinimizedAsync(CancellationToken ct)
    {
        var allocatedAtLastCheck = GC.GetTotalAllocatedBytes();
        Task<bool>? change = null;
        Task? gcCheck = null;
        while (!_visible.Task.IsCompleted)
        {
            change ??= _session.Changes.WaitToReadAsync(ct).AsTask();
            gcCheck ??= Task.Delay(MinimizedGcCheckInterval, ct);
            await Task.WhenAny(_visible.Task, change, gcCheck).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_visible.Task.IsCompleted) return;
            if (change.IsCompleted)
            {
                if (!await change.ConfigureAwait(false)) return; // the session ended: the pump shows its final state
                change = null;
                _session.Changes.TryRead(out _);
                var snapshot = _session.Snapshot();
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() => CheckAttention(snapshot), DispatcherPriority.Background);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.Error.WriteLine("Attention check failed: " + e);
                }
                await Task.WhenAny(_visible.Task, Task.Delay(MinimizedAttentionInterval, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            if (gcCheck.IsCompleted)
            {
                gcCheck = null;
                var allocated = GC.GetTotalAllocatedBytes();
                if (!_visible.Task.IsCompleted && allocated - allocatedAtLastCheck > MinimizedGcAllocationThreshold)
                {
                    GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                    allocatedAtLastCheck = allocated;
                }
            }
        }
    }

    internal void Apply(SessionSnapshot s)
    {
        // Snapshots reach the UI thread by two paths (the pump, and commands applying the state right after they
        // finish). The pump's may have been taken earlier: never let an older snapshot overwrite a newer one, or a
        // run that already ended shows as running again and stays so.
        if (_last is not null && s.Version < _last.Version) return;
        Interlocked.Increment(ref _uiApplies);
        var changed = false;
        var newConversation = s.TranscriptEpoch != _transcriptEpoch;
        if (newConversation)
        {
            // Another session (or a restarted omp): the old rows belong to a different conversation.
            Rows.Clear();
            _rowsByKey.Clear();
            _transcriptEpoch = s.TranscriptEpoch;
            changed = true;
        }
        foreach (var item in s.Items)
        {
            OfferPreviewUrls(item); // MainViewModel.Preview.cs: local URLs in new tool output become preview suggestions
            if (_rowsByKey.TryGetValue(item.Key, out var row))
            {
                if (!ReferenceEquals(row.Model, item))
                {
                    row.Update(item);
                    changed = true;
                }
            }
            else
            {
                row = RowViewModel.Create(item);
                _rowsByKey[item.Key] = row;
                Rows.Add(row);
                changed = true;
            }
        }

        var debugAdded = false;
        foreach (var d in s.DebugTail)
        {
            if (d.Seq <= _lastDebugSeq) continue;
            debugAdded = true;
            DebugLog.Add($"{d.At.ToLocalTime():HH:mm:ss.fff}  {d.Type,-22} {d.Summary}");
            _lastDebugSeq = d.Seq;
        }
        while (DebugLog.Count > DebugLines) DebugLog.RemoveAt(0);

        ApplyExtensionUi(s);
        ApplyQueue(s);
        ApplyCommandsAndProgress(s);
        ApplyPanes(s); // MainViewModel.Panes.cs: the plan and the background tasks

        // A snapshot taken before our optimistic Send/Stop must not flip the phase back for a frame.
        var wasRunning = IsRunning;
        if (s.Version > _optimisticAfterVersion) Phase = s.Phase;
        var fileChanged = s.SessionFile != _last?.SessionFile;
        HasError = s.Phase == SessionPhase.Faulted;
        _last = s;
        ApplySessionInfo(s);
        ApplyModelInfo(s);
        ApplySessionArea(s); // MainViewModel.Session.cs: context ring, pin, session menu
        CheckAttention(s);
        if (s.Phase == SessionPhase.Ready) RememberProject(s.Cwd);
        // Another session, a sent message (the run starts) or a finished run changes the list (new file, the time of the
        // last message that orders it, first-message title).
        if (fileChanged || wasRunning != IsRunning) RequestCatalogRefresh();
        UpdateStatus();
        ApplyPet(s); // MainViewModel.Pets.cs
        UpdateElapsedTimer();
        if (changed || wasRunning != IsRunning) MarkTurnEnds();
        if (newConversation) ScrollToLatestRequested?.Invoke(); // a chat opens on its latest message
        if (changed) TranscriptChanged?.Invoke();
        if (debugAdded) DebugLogAppended?.Invoke();
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
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            switch (Rows[i])
            {
                case UserRowViewModel: turnOver = true; break;
                case ToolRowViewModel: turnOver = false; break; // the turn ends in tool calls: no actions mid-turn
                case AssistantRowViewModel a:
                    var end = turnOver && a.HasText;
                    if (a.IsTurnEnd != end) a.IsTurnEnd = end;
                    if (end) turnOver = false;
                    break;
            }
        }
    }

    private void ApplyExtensionUi(SessionSnapshot s)
    {
        var first = s.Dialogs.Count > 0 ? s.Dialogs[0] : null;
        if (first?.Id != CurrentDialog?.Id) CurrentDialog = first is null ? null : new DialogViewModel(first, AnswerDialogAsync);
        if (CurrentDialog is { } d) d.QueueText = s.Dialogs.Count > 1 ? $"1 of {s.Dialogs.Count}" : "";
        UpdateDialogTimer();

        var widgets = string.Join("\n", s.Widgets.SelectMany(w => w.Lines));
        if (widgets != WidgetsText) WidgetsText = widgets;

        // Separate counters: both requests can land in one apply, in either order.
        if (s.EditorText is { } et && et.Seq > _lastEditorSeq)
        {
            ComposerText = et.Text;
            _lastEditorSeq = et.Seq;
        }
        if (s.OpenUrl is { } ou && ou.Seq > _lastUrlSeq)
        {
            PendingUrl = ou.Target;
            PendingUrlInstructions = ou.Instructions ?? "";
            _lastUrlSeq = ou.Seq;
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
        if (_last is not { } s) return;
        // The shown phase (an optimistic Send or Stop sets it before omp confirms): a first frame said "Ready" while running
        if (Phase != SessionPhase.Running) IsWaitingOnUser = false;
        StatusText = Phase switch
        {
            SessionPhase.Starting => "Starting omp…",
            SessionPhase.Ready => "Ready",
            SessionPhase.Running => $"{Activity(s)} {Elapsed(s)}",
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

    private static string Elapsed(SessionSnapshot s) =>
        s.RunStartedAt is { } started ? (DateTimeOffset.UtcNow - started).ToString(@"mm\:ss", CultureInfo.InvariantCulture) : "00:00";

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
            _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());
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
            var r = await _session.ProbeAsync(_cts.Token).ConfigureAwait(false);
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
        var snap = _session.Snapshot();
        AppendStartupLog($"smoke phase={snap.Phase} start_problem={snap.StartProblem} omp_pid={_session.ProcessId}");
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
                var s = _session.Snapshot();
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
        // Cancel first: a start still in progress then disposes its omp instead of finishing it.
        _cts.Cancel();
        await _session.DisposeAsync();
        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        }
        _cts.Dispose();
        ApplyUpdateOnQuit();
    }
}
