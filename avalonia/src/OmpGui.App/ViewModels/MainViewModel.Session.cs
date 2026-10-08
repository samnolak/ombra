using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The session menu under the title in the header (Claude Code's title menu), on omp's own builtins over RPC:
/// rename, compact (<c>/compact</c>), hand off (<c>/handoff</c>), a fresh provider session (<c>/fresh</c>), retry
/// (<c>/retry</c>), export (<c>/export</c>), share (<c>/share</c>), copy the session's id or file, its workspace folders
/// (<c>/dirs</c>, <c>/add-dir</c>, <c>/remove-dir</c>), move (<c>/move</c>), pin (<c>/pin</c>), omp's usage dashboard
/// (<c>/stats</c>) and memory (<c>/memory</c>). Each shows what it did on the session card above the message box.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan MaintenanceTimeout = TimeSpan.FromMinutes(20);
    private DispatcherTimer? _cardTimer;
    private bool _lastCanAct;

    /// <summary>The session card shown above the message box (one at a time), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessionCard))]
    private SessionCardViewModel? _sessionCard;

    public bool HasSessionCard => SessionCard is not null;

    /// <summary>The context ring next to the model and its popover (context breakdown, token use and cost); each chat has its own.</summary>
    public UsageViewModel Usage => _open.Usage ??= new UsageViewModel(this);

    /// <summary>Fast mode, extended context, advisor, auto-compact and auto-retry in the model menu (read from the chat's omp).</summary>
    public ModelOptionsViewModel ModelOptions => _open.ModelOptions ??= new ModelOptionsViewModel(this);

    /// <summary>The shown chat's omp.</summary>
    internal SessionController Session => _open.Controller;
    internal CancellationToken Closing => _cts.Token;

    /// <summary>This session is pinned in omp's session list (<c>~/.omp/agent/session-pins.json</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    private bool _isSessionPinned;

    public string PinLabel => IsSessionPinned ? "Unpin session" : "Pin session";

    /// <summary>omp is idle and running, and no other session action is under way.</summary>
    private bool CanActOnSession() => CanRunOmpCommands && Phase == SessionPhase.Ready && !IsSigningIn && SessionCard is not { IsRunning: true };

    private bool HasSessionFile() => _open.Last?.SessionFile is { Length: > 0 };

    private string? _pinsFor;
    /// <summary>omp's pinned session ids, read with each scan of the session list (listed first in the sidebar).</summary>
    private HashSet<string> _pinnedIds = [];

    /// <summary>Called from Apply: the ring, the pin state and which menu items can run.</summary>
    private void ApplySessionArea(SessionSnapshot s)
    {
        Usage.Apply(s);
        if (s.SessionFile != _pinsFor)
        {
            _pinsFor = s.SessionFile;
            if (s.SessionFile is { } file) _ = LoadPinStateAsync(file);
            CopySessionIdCommand.NotifyCanExecuteChanged();
            CopySessionFileCommand.NotifyCanExecuteChanged();
        }
        var can = CanActOnSession();
        if (can == _lastCanAct) return;
        _lastCanAct = can;
        NotifySessionCommands();
    }

    private void NotifySessionCommands()
    {
        foreach (var c in new IRelayCommand[] { CompactConversationCommand, HandOffCommand, FreshProviderSessionCommand, RetryLastTurnCommand, RewindCommand,
                     ExportHtmlCommand, ShareSessionCommand, WorkspaceFoldersCommand, MoveSessionCommand, TogglePinCommand,
                     OpenUsageDashboardCommand, ShowMemoryCommand, CopySessionIdCommand, CopySessionFileCommand, DeleteSessionCommand })
            c.NotifyCanExecuteChanged();
        RewindToCommand.NotifyCanExecuteChanged();
        _open.Usage?.CompactNowCommand.NotifyCanExecuteChanged();
        _open.Usage?.OpenDashboardCommand.NotifyCanExecuteChanged();
    }

    partial void OnSessionCardChanged(SessionCardViewModel? value)
    {
        _lastCanAct = CanActOnSession();
        NotifySessionCommands();
    }

    internal void ShowCard(SessionCardViewModel card)
    {
        _cardTimer?.Stop();
        _cardTimer = null;
        card.DismissCommand ??= new RelayCommand(CloseSessionCard);
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionCardViewModel.State) && ReferenceEquals(card, SessionCard)) OnSessionCardChanged(card);
        };
        SessionCard = card;
    }

    [RelayCommand]
    internal void CloseSessionCard()
    {
        _cardTimer?.Stop();
        _cardTimer = null;
        SessionCard = null;
    }

    /// <summary>A short confirmation ("Session ID copied") that goes away by itself.</summary>
    private void ShowBrief(string iconKey, string title, string message, string detail = "")
    {
        var card = new SessionCardViewModel("brief", iconKey, title, message) { Detail = detail };
        ShowCard(card);
        _cardTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            if (ReferenceEquals(SessionCard, card)) CloseSessionCard();
        });
        _cardTimer.Start();
    }

    // ───────────── Compact and hand off: background builtins that print their result when they end ─────────────

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private void CompactConversation()
    {
        var card = new SessionCardViewModel("compact", "IconCompact", "Compact the conversation",
            "omp summarizes the conversation so far and goes on from the summary, which frees room in the context. " +
            "Everything stays on screen here.")
        {
            HasInput = true,
            InputHint = "What the summary should keep (optional)",
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction("Compact", new AsyncRelayCommand(() => RunCompactAsync(card)));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }

    /// <summary>"Compact now" from the context popover: no questions, straight to the running card.</summary>
    internal Task CompactNowAsync()
    {
        var card = new SessionCardViewModel("compact", "IconCompact", "Compact the conversation", "");
        ShowCard(card);
        return RunCompactAsync(card);
    }

    private Task RunCompactAsync(SessionCardViewModel card)
    {
        var focus = card.Input.Trim();
        return RunMaintenanceAsync(card, focus.Length > 0 ? "/compact " + focus : "/compact", "Compacting the conversation…",
            SessionOutputs.ParseCompactEnd,
            r => r.Ok ? ("Conversation compacted", CompactSummary(r)) : ("Not compacted", r.Message),
            "Compaction stopped");
    }

    private static string CompactSummary(MaintenanceResult r) =>
        r is { TokensBefore: { } b, TokensAfter: { } a }
            ? $"The context went from {SessionOutputs.Tokens(b)} to {SessionOutputs.Tokens(a)} tokens" + (b > a ? $" ({SessionOutputs.Tokens(b - a)} freed)." : ".")
            : "omp goes on from the summary.";

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private void HandOff()
    {
        var card = new SessionCardViewModel("handoff", "IconHandoff", "Hand off",
            "omp writes a handoff document — what was done, what is open, what comes next — and goes on from it in this " +
            "session, leaving the older messages out of the context. Like compacting, with a written brief.")
        {
            HasInput = true,
            InputHint = "What the handoff should focus on (optional)",
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction("Hand off", new AsyncRelayCommand(() =>
        {
            var focus = card.Input.Trim();
            return RunMaintenanceAsync(card, focus.Length > 0 ? "/handoff " + focus : "/handoff", "Writing the handoff…",
                SessionOutputs.ParseHandoffEnd,
                r => r.Ok ? ("Handed off", "omp goes on from the handoff document; the older messages are out of the context.") : ("Not handed off", r.Message),
                "Handoff stopped");
        }));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }

    /// <summary>
    /// Runs a builtin that omp 18.2.0 dispatches in the background over RPC (<c>/compact</c>, <c>/handoff</c>): it
    /// answers at once, and prints its result as a line in the conversation when it ends. The card shows it running
    /// (with Stop, which sends omp's abort) until that line comes.
    /// </summary>
    private async Task RunMaintenanceAsync(SessionCardViewModel card, string command, string running,
        Func<string, MaintenanceResult?> parse, Func<MaintenanceResult, (string Title, string Message)> describe, string stoppedTitle)
    {
        card.State = SessionCardState.Running;
        card.Message = running;
        card.Detail = "";
        // This chat's omp, whichever chat is shown when it answers; the wait ends with the chat
        var open = _open;
        var session = open.Controller;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(open.Lifetime.Token);
        card.Primary = new SessionCardAction("Stop", new AsyncRelayCommand(async () =>
        {
            wait.Cancel();
            await session.StopBackgroundCommandAsync(_cts.Token);
            card.Finish(false, stoppedTitle, "omp stopped it; the conversation is as it was.");
            card.Secondary = new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard));
        }));
        card.Secondary = null;
        try
        {
            var before = session.Snapshot();
            var r = await session.RunSlashCommandAsync(command, ct: wait.Token);
            if (!r.Ok)
            {
                card.Finish(false, describe(new MaintenanceResult(false, r.Error ?? "")).Title, r.Error ?? "omp did not run it.",
                    secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
                return;
            }
            var result = parse(r.Output);
            if (result is null && await session.WaitForCommandOutputAsync(before, t => parse(t) is not null, MaintenanceTimeout, wait.Token) is { } line)
                result = parse(line);
            if (result is null)
            {
                card.Finish(false, "No result yet", "omp did not say how it ended. The conversation shows what happened.",
                    secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
                return;
            }
            var (title, message) = describe(result);
            card.Finish(result.Ok, title, message, secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            session.RefreshState();
            open.Usage?.RefreshIfOpen();
        }
        catch (OperationCanceledException)
        {
            // Stopped (the Stop button finished the card), the chat closed or the window is closing
        }
        finally
        {
            if (ReferenceEquals(card, SessionCard)) OnSessionCardChanged(card);
        }
    }

    // ───────────── Fresh provider session, retry ─────────────

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task FreshProviderSessionAsync()
    {
        var r = await RunOmpCommandAsync("/fresh");
        var card = new SessionCardViewModel("fresh", "IconFresh", "Fresh provider session", "");
        var pruned = Regex.Match(r.Output, @"\((\d+) provider state");
        if (r.Ok && r.Output.StartsWith("Fresh provider session started", StringComparison.Ordinal))
            card.Finish(true, "Fresh provider session",
                "The next reply starts a new session with the model provider and sends the whole conversation again; the conversation here is unchanged." +
                (pruned.Success && pruned.Groups[1].Value != "0" ? $" {pruned.Groups[1].Value} cached provider state(s) dropped." : ""),
                secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        else
            card.Finish(false, "Not refreshed", Said(r), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        ShowCard(card);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task RetryLastTurnAsync()
    {
        var r = await RunOmpCommandAsync("/retry");
        Apply(Session.Snapshot());
        if (r is { Ok: true, AgentInvoked: true })
        {
            CloseSessionCard(); // the run shows like any other
            return;
        }
        var card = new SessionCardViewModel("retry", "IconRetry", "", "");
        if (r.Ok && r.Output.StartsWith("Nothing to retry", StringComparison.Ordinal))
            card.Finish(true, "Nothing to retry", "Retry runs the last turn again after it failed or was stopped; the last reply here ended normally.",
                secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        else card.Finish(false, "Not retried", Said(r), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        ShowCard(card);
    }

    // ───────────── Rewind (omp's /branch over RPC) ─────────────

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task RewindAsync()
    {
        // This chat's omp throughout: another chat may be shown while omp reads its messages or rewinds
        var open = _open;
        var session = open.Controller;
        var card = new SessionCardViewModel("rewind", "IconRewind", "Rewind to an earlier message", "Reading the conversation…") { State = SessionCardState.Running };
        ShowCard(card);
        var messages = await session.GetBranchMessagesAsync(open.Lifetime.Token);
        if (messages.Count == 0)
        {
            card.Finish(true, "Nothing to rewind to", "Rewind starts over from one of your messages; this conversation has none yet.",
                secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            return;
        }
        card.State = SessionCardState.Ask;
        card.Message = "Pick one of your messages: omp starts a new session with the conversation before it, and the message comes back " +
                       "into the box to change and send. This session stays as it is in the list.";
        card.SetChoices(messages.Reverse().Select(m => new SessionCardChoice(OneLine(m.Text), "", new AsyncRelayCommand(async () =>
        {
            card.SetChoices([]);
            card.State = SessionCardState.Running;
            card.Message = "Rewinding…";
            card.Primary = null;
            card.Secondary = null;
            var text = await session.RewindAsync(m.EntryId, open.Lifetime.Token);
            ApplyIfShown(open);
            RequestCatalogRefresh();
            if (text is null)
            {
                card.Finish(false, "Not rewound", "omp did not start the new session; the conversation shows why.",
                    secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
                return;
            }
            PutInComposer(open, text); // MainViewModel.Transcript.cs
            card.Finish(true, "Rewound", "A new session goes on from before that message; the earlier one is kept in the sessions list. " +
                "Your message is back in the box.", secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        }))));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
    }

    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 140 ? line : line[..140] + "…";
    }

    // ───────────── Export and share ─────────────

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task ExportHtmlAsync()
    {
        var card = new SessionCardViewModel("export", "IconExport", "Exporting as HTML…", "") { State = SessionCardState.Running };
        ShowCard(card);
        var r = await RunOmpCommandAsync("/export");
        if (r.Ok && SessionOutputs.ParseExportPath(r.Output) is { } path)
        {
            var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ProjectFolder ?? Session.CurrentLaunch.WorkingDirectory ?? "", path));
            card.Finish(true, "Exported as HTML", "A single page with the whole conversation, tool calls included, to open in a browser.", full,
                new SessionCardAction("Open", new RelayCommand(() => OpenUrlRequested?.Invoke(new Uri(full).AbsoluteUri))),
                new SessionCardAction("Show in folder", new RelayCommand(() =>
                {
                    if (Path.GetDirectoryName(full) is { } dir) OpenUrlRequested?.Invoke(new Uri(dir).AbsoluteUri);
                })),
                new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        }
        else card.Finish(false, "Not exported", Said(r, "Failed to export session: "), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
    }

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private void ShareSession()
    {
        var card = new SessionCardViewModel("share", "IconShare", "Share this conversation", ShareExplanation("omp's share server", true))
        {
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction("Upload and copy link", new AsyncRelayCommand(() => RunShareAsync(card)));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
        ShowCard(card);
        _ = DescribeShareTargetAsync(card);
    }

    private static string ShareExplanation(string where, bool redacts) =>
        $"omp uploads an encrypted copy of the whole conversation, tool output included, to {where} and gives you a link. " +
        "The key is only in the link: anyone who has the link can read it. " +
        (redacts ? "omp removes the secrets it recognizes before uploading." : "omp's secret redaction for shares is off in its settings.");

    /// <summary>Where omp's settings send a share (<c>share.serverUrl</c>, <c>share.store</c>, <c>share.redactSecrets</c>).</summary>
    private async Task DescribeShareTargetAsync(SessionCardViewModel card)
    {
        var url = await ConfigValueAsync("share.serverUrl");
        var store = await ConfigValueAsync("share.store");
        var redact = await ConfigValueAsync("share.redactSecrets");
        if (card.State != SessionCardState.Ask || url is null) return;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        var where = store == "gist" ? $"a secret GitHub gist (or {host})" : host;
        card.Message = ShareExplanation(where, redact != "false");
    }

    /// <summary>One value of omp's settings via its CLI (<c>omp config get KEY --json</c>), as JSON text; null when unknown.</summary>
    internal async Task<string?> ConfigValueAsync(string key)
    {
        var r = await RunOmpCliAsync(["config", "get", key, "--json"], TimeSpan.FromSeconds(30), _cts.Token);
        if (!r.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            if (!doc.RootElement.TryGetProperty("value", out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => v.GetRawText(),
                _ => v.GetRawText(),
            };
        }
        catch (JsonException) { return null; }
    }

    private async Task RunShareAsync(SessionCardViewModel card)
    {
        card.State = SessionCardState.Running;
        card.Message = "Uploading an encrypted copy…";
        card.Primary = null;
        card.Secondary = null;
        var r = await RunOmpCommandAsync("/share", TimeSpan.FromMinutes(3));
        var (url, gist, note) = SessionOutputs.ParseShare(r.Output);
        if (r.Ok && url is not null)
        {
            CopyTextRequested?.Invoke(url);
            card.Finish(true, "Link copied", "Anyone with this link can read the conversation." + (note is null ? "" : " " + note) +
                (gist is null ? "" : $" Stored in a secret gist: {gist}"), url,
                new SessionCardAction("Copy link", new RelayCommand(() => CopyTextRequested?.Invoke(url))),
                new SessionCardAction("Open", new RelayCommand(() => OpenUrlRequested?.Invoke(url))),
                new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        }
        else card.Finish(false, "Not shared", Said(r, "Failed to share session: "), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
    }

    // ───────────── Copy id and file ─────────────

    [RelayCommand(CanExecute = nameof(HasSessionFile))]
    private async Task CopySessionIdAsync()
    {
        if (_open.Last is not { SessionFile: { } file } last) return;
        // The id in the session file's header: what omp --resume and /pin take (get_state's sessionId changes with /fresh)
        var id = (await SessionCatalog.ReadSummaryAsync(file, _cts.Token))?.Id is { Length: > 0 } header ? header : last.SessionId;
        if (id is null) return;
        CopyTextRequested?.Invoke(id);
        ShowBrief("IconCopy", "Session ID copied", "omp --resume takes it (a unique start of it is enough).", id);
    }

    [RelayCommand(CanExecute = nameof(HasSessionFile))]
    private void CopySessionFile()
    {
        if (_open.Last?.SessionFile is not { } file) return;
        CopyTextRequested?.Invoke(file);
        ShowBrief("IconCopy", "Session file path copied", "omp keeps the whole conversation in this file (JSON lines).", file);
    }

    // ───────────── Workspace folders, move ─────────────

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task WorkspaceFoldersAsync()
    {
        var card = new SessionCardViewModel("dirs", "IconFolders", "Workspace folders", "Reading omp's workspace…") { State = SessionCardState.Running };
        ShowCard(card);
        await ApplyFoldersAsync(card, await RunOmpCommandAsync("/dirs"));
    }

    private async Task ApplyFoldersAsync(SessionCardViewModel card, SlashCommandResult r)
    {
        var (cwd, added, note) = SessionOutputs.ParseDirectories(r.Output);
        var ok = r.Ok && cwd is not null;
        card.State = ok ? SessionCardState.Done : SessionCardState.Failed;
        card.Title = "Workspace folders";
        card.Message = !r.Ok ? r.Error ?? "omp did not answer."
            : note is { } n && !n.StartsWith("Added ", StringComparison.Ordinal) && !n.StartsWith("Removed ", StringComparison.Ordinal) ? n
            : "omp reads and edits files in these folders during this session. Added folders last until the session ends.";
        if (cwd is not null)
            card.SetFolders(new[] { new WorkspaceFolderViewModel(cwd, true, null) }.Concat(added.Select(a =>
                new WorkspaceFolderViewModel(a, false, new AsyncRelayCommand(async () =>
                {
                    card.State = SessionCardState.Running;
                    await ApplyFoldersAsync(card, await RunOmpCommandAsync("/remove-dir " + a));
                })))));
        card.Primary = new SessionCardAction("Add folder…", new AsyncRelayCommand(async () =>
        {
            if (PickFolderRequested is not { } pick || await pick() is not { } folder) return;
            card.State = SessionCardState.Running;
            await ApplyFoldersAsync(card, await RunOmpCommandAsync("/add-dir " + folder));
        }));
        card.Secondary = new SessionCardAction("Done", new RelayCommand(CloseSessionCard));
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task MoveSessionAsync()
    {
        if (PickFolderRequested is not { } pick || await pick() is not { } folder) return;
        var name = SessionItemViewModel.ProjectName(folder);
        var card = new SessionCardViewModel("move", "IconMove", $"Move this session to {name}?",
            "omp moves the conversation there and works in that folder from now on. Your files stay where they are.")
        {
            Detail = folder,
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction("Move", new AsyncRelayCommand(async () =>
        {
            // The card's chat (shown when its button is clicked), whichever chat is shown when omp answers
            var open = _open;
            card.State = SessionCardState.Running;
            card.Message = "Moving…";
            card.Primary = null;
            card.Secondary = null;
            var r = await open.Controller.RunSlashCommandAsync("/move " + folder);
            var moved = Regex.Match(r.Output, @"^Moved to (?<dir>.+)\.$", RegexOptions.Multiline);
            if (r.Ok && moved.Success)
            {
                var dir = moved.Groups["dir"].Value;
                await open.Controller.FollowMovedSessionAsync(dir, open.Lifetime.Token);
                ApplyIfShown(open);
                RequestCatalogRefresh();
                card.Finish(true, "Session moved", $"omp now works in {dir}.", secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            }
            else card.Finish(false, "Not moved", Said(r), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        }));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }

    // ───────────── Pin ─────────────

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task TogglePinAsync()
    {
        var r = await RunOmpCommandAsync("/pin");
        if (r.Ok && r.Output.StartsWith("Session pinned", StringComparison.Ordinal))
        {
            IsSessionPinned = true;
            ShowBrief("IconPin", "Session pinned", "It is listed under Pinned at the top of the sidebar, and first in omp's own session list.");
        }
        else if (r.Ok && r.Output.StartsWith("Session unpinned", StringComparison.Ordinal))
        {
            IsSessionPinned = false;
            ShowBrief("IconPin", "Session unpinned", "It is listed by date again.");
        }
        else
        {
            var card = new SessionCardViewModel("pin", "IconPin", "", "");
            card.Finish(false, "Not pinned", Said(r), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            ShowCard(card);
        }
        RequestCatalogRefresh();
    }

    private async Task LoadPinStateAsync(string file)
    {
        var (id, pins) = await Task.Run(async () => ((await SessionCatalog.ReadSummaryAsync(file))?.Id, ReadPinnedSessionIds(file)));
        if (_open.Last?.SessionFile == file) IsSessionPinned = id is not null && pins.Contains(id);
    }

    partial void OnIsModelMenuOpenChanged(bool value)
    {
        if (value) _ = ModelOptions.LoadAsync();
    }

    /// <summary>omp's pins (<c>session-pins.json</c> next to its sessions folder): ids of pinned sessions.</summary>
    internal static HashSet<string> ReadPinnedSessionIds(string? sessionFile) => SessionPins.Read(SessionPins.PathFor(sessionFile));

    // ───────────── Delete (omp's /session delete, then a new session: the TUI's /drop) ─────────────

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private void DeleteSession()
    {
        var card = new SessionCardViewModel("delete", "IconTrash", "Delete this session?",
            "omp deletes the conversation's file and its artifacts, then a new session starts here. This cannot be undone.")
        {
            Detail = _open.Last?.SessionFile ?? "",
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction("Delete session", new AsyncRelayCommand(async () =>
        {
            // The card's chat, whichever chat is shown when omp answers
            var open = _open;
            card.State = SessionCardState.Running;
            card.Message = "Deleting…";
            card.Primary = null;
            card.Secondary = null;
            var r = await open.Controller.RunSlashCommandAsync("/session delete");
            if (!r.Ok || !r.Output.StartsWith("Session deleted", StringComparison.Ordinal))
            {
                card.Finish(false, "Not deleted", Said(r, "Failed to delete session: "), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
                return;
            }
            await open.Controller.NewSessionAsync(open.Lifetime.Token);
            ApplyIfShown(open);
            RequestCatalogRefresh();
            if (open == _open) ShowBrief("IconTrash", "Session deleted", "A new session started in this project.");
            else if (ReferenceEquals(open.Card, card)) open.Card = null;
        }));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }

    // ───────────── omp's usage dashboard, memory ─────────────

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task OpenUsageDashboardAsync()
    {
        var card = new SessionCardViewModel("stats", "IconChart", "Usage dashboard", "omp is reading its session files…") { State = SessionCardState.Running };
        ShowCard(card);
        var r = await RunOmpCommandAsync("/stats", TimeSpan.FromMinutes(3));
        if (r.Ok && SessionOutputs.ParseDashboardUrl(r.Output) is { } url)
        {
            var synced = r.Output.Split('\n').FirstOrDefault(l => l.StartsWith("Synced ", StringComparison.Ordinal));
            card.Finish(true, "Usage dashboard",
                "omp's local dashboard of tokens, cost and models across all your sessions runs on this computer" +
                (synced is null ? "." : $" ({synced.Trim().TrimEnd('.').ToLowerInvariant()})."), url,
                new SessionCardAction("Open in browser", new RelayCommand(() => OpenUrlRequested?.Invoke(url))),
                new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        }
        else card.Finish(false, "Dashboard not started", Said(r, "Stats dashboard failed: "), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
    }

    [RelayCommand(CanExecute = nameof(CanActOnSession))]
    private async Task ShowMemoryAsync()
    {
        var card = new SessionCardViewModel("memory", "IconMemory", "Memory", "Reading omp's memory…") { State = SessionCardState.Running };
        ShowCard(card);
        await LoadMemoryAsync(card, null);
    }

    private async Task LoadMemoryAsync(SessionCardViewModel card, string? done)
    {
        var stats = await RunOmpCommandAsync("/memory stats");
        if (stats.Ok && stats.Output.Contains("backend is off", StringComparison.Ordinal))
        {
            card.Finish(true, "Memory is off",
                "omp's memory is turned off in its settings (memory.backend). When it is on, omp keeps what it learns across sessions and adds it to the context.",
                secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            return;
        }
        var view = await RunOmpCommandAsync("/memory view");
        if (!view.Ok)
        {
            card.Finish(false, "Memory", Said(view), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
            return;
        }
        var empty = view.Output.Trim() is "" or "Memory payload is empty.";
        card.Finish(true, "Memory",
            (done is null ? "" : done + " ") + (empty ? "Nothing from memory goes into the context yet." : "What omp adds to the context from its memory:"),
            empty ? "" : view.Output.Trim(),
            new SessionCardAction("Update now", new AsyncRelayCommand(async () =>
            {
                card.State = SessionCardState.Running;
                var r = await RunOmpCommandAsync("/memory sync", TimeSpan.FromMinutes(5));
                await LoadMemoryAsync(card, r.Ok ? "Memory updated." : "Update failed: " + Said(r));
            })),
            new SessionCardAction("Clear memory…", new RelayCommand(() => ConfirmClearMemory(card))),
            new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        card.IsDetailLong = !empty;
    }

    private void ConfirmClearMemory(SessionCardViewModel card)
    {
        card.State = SessionCardState.Ask;
        card.Title = "Clear omp's memory?";
        card.Message = "omp deletes what it has learned (its memory files and notes) for every session. This cannot be undone.";
        card.Detail = "";
        card.Primary = new SessionCardAction("Clear memory", new AsyncRelayCommand(async () =>
        {
            card.State = SessionCardState.Running;
            var r = await RunOmpCommandAsync("/memory clear");
            await LoadMemoryAsync(card, r.Ok ? "Memory cleared." : "Not cleared: " + Said(r));
        }));
        card.Secondary = new SessionCardAction("Cancel", new AsyncRelayCommand(() => LoadMemoryAsync(card, null)));
        card.Tertiary = null;
    }

    /// <summary>What omp said, or why nothing ran, for a card's message (an omp prefix such as "Failed to share session: " dropped).</summary>
    private static string Said(SlashCommandResult r, string? prefix = null)
    {
        var text = r.Output.Trim();
        if (text.Length == 0) return r.Error ?? "omp did not say why.";
        if (prefix is not null && text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
        return text.Length > 0 ? char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..] : text;
    }
}
