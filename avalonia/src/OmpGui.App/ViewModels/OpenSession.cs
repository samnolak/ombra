using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// One open chat of the window: its omp process (<see cref="Controller"/>) and what the window keeps of the chat while
/// another one is shown, so that coming back finds it as it was left: the rows built so far (and where the reader was),
/// the message box's draft, the session card, the question omp asked, and what the background checks saw since.
/// Only <see cref="MainViewModel"/> touches it, on the UI thread.
/// </summary>
internal sealed class OpenSession(SessionController controller, CancellationToken windowClosing) : IDisposable
{
    public SessionController Controller { get; } = controller;

    /// <summary>Cancelled when the chat closes (or the window): a start still under way then stops its omp instead of
    /// finishing it, and the chat's update loop ends.</summary>
    public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(windowClosing);

    /// <summary>A saved session this chat's omp is switching to, until omp reports it: a second click on it finds this
    /// chat instead of starting another omp on the same file (omp locks the file it writes).</summary>
    public string? OpeningFile { get; set; }

    /// <summary>When the chat was opened: the sidebar's sort key for a chat omp has not written a file for yet (see
    /// <c>MainViewModel.UnsavedChats</c>).</summary>
    public DateTimeOffset OpenedAt { get; } = DateTimeOffset.Now;

    /// <summary>The loop that takes this chat's updates to the window (in full while shown, as status otherwise).</summary>
    public Task? Pump { get; set; }

    // ── The conversation as last shown: rows built once, kept while another chat is on screen ──

    /// <summary>The rows while the chat is not shown (the window's list holds them while it is).</summary>
    public List<RowViewModel> Rows { get; set; } = [];
    public Dictionary<long, RowViewModel> RowsByKey { get; } = [];
    /// <summary>The items of the snapshot applied last, slot by slot (an unchanged slot needs nothing).</summary>
    public IReadOnlyList<TranscriptItem> AppliedItems { get; set; } = [];
    public long TranscriptEpoch { get; set; }
    /// <summary>The snapshot applied last (never replaced by an older one).</summary>
    public SessionSnapshot? Last { get; set; }
    /// <summary>A Send or Stop shows its phase at once; snapshots up to this version, taken before it, do not undo it.</summary>
    public long OptimisticAfterVersion { get; set; } = -1;
    /// <summary>Where the reader was (the view's own record), or null to open on the latest message.</summary>
    public object? ScrollAnchor { get; set; }

    // ── The message box ──

    public string ComposerText { get; set; } = "";
    public string ComposerMessage { get; set; } = "";
    public List<AttachmentViewModel> Attachments { get; set; } = [];
    public List<PastedTextViewModel> PastedTexts { get; set; } = [];
    public List<CodeCommentViewModel> CodeComments { get; set; } = [];

    /// <summary>Nothing typed or attached (a chat with a draft is never closed to stay under the process limit).</summary>
    public bool HasNoDraft => ComposerText.Trim().Length == 0 && Attachments.Count == 0 && PastedTexts.Count == 0 && CodeComments.Count == 0;

    // ── What omp asked for, the cards, and the chat's own context ring and model options ──

    public DialogViewModel? Dialog { get; set; }
    public SessionCardViewModel? Card { get; set; }
    public string? PendingUrl { get; set; }
    public string PendingUrlInstructions { get; set; } = "";
    /// <summary>omp's requests to fill the message box and to open a link, by number: each is taken once.</summary>
    public long LastEditorSeq { get; set; }
    public long LastUrlSeq { get; set; }
    public UsageViewModel? Usage { get; set; }
    public ModelOptionsViewModel? ModelOptions { get; set; }
    /// <summary>The chat's queued messages as shown (previews decoded once), while another chat is on screen.</summary>
    public List<QueuedItemViewModel> Queued { get; set; } = [];

    /// <summary>A permission mode chosen while this chat's omp was busy: it restarts with it once idle.</summary>
    public string? PendingApprovalMode { get; set; }

    /// <summary>A setting that omp reads only at start changed while this chat's omp was busy: it restarts once idle.</summary>
    public bool RestartWhenIdle { get; set; }

    // ── What the window has noticed (for the sidebar's dot and the notifications) ──

    /// <summary>The newest snapshot looked at, shown or not.</summary>
    public SessionSnapshot? Seen { get; set; }
    public string? LastDialogId { get; set; }
    /// <summary>How many runs had ended at the last look (-1: not looked yet): a higher count is a run that ended.</summary>
    public long RunsEndedSeen { get; set; } = -1;
    /// <summary>A run ended while the chat was not shown, or while the window was away; cleared when the user sees it
    /// (the chat shown, the window back in front).</summary>
    public bool Unread { get; set; }

    /// <summary>The chat is closed: its update loop ends, and the rows and chips it holds let go of their decoded images.</summary>
    public void Dispose()
    {
        // Cancelled, not disposed: a command of this chat still awaiting omp may read its token after the close (it is
        // linked to the window's, which frees it when the window closes)
        Lifetime.Cancel();
        foreach (var row in Rows) (row as IDisposable)?.Dispose();
        foreach (var a in Attachments) a.Dispose();
        foreach (var q in Queued) q.Dispose();
        Rows = [];
        Attachments = [];
        Queued = [];
    }
}
