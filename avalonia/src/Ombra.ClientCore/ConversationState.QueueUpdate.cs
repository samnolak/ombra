using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>
/// omp's own queue is the source of truth for runtime steers and follow-ups: <c>queue_update</c> (and
/// <c>get_state.queuedMessages</c>) list the chips in its order. A local <see cref="Enqueue"/> is an optimistic echo
/// until omp lists it; local entries keep their images and submission id while omp lists them, entries omp no longer
/// lists go, and entries only omp knows (queued by an extension or another client) appear. Local-only input
/// (builtins, follow-ups waiting behind them) and recovered input are never touched. omp lists chip texts only, no ids:
/// an entry is matched by the chip omp makes of it (<see cref="Lists(string, string, bool)"/>).
/// </summary>
public sealed partial class ConversationState
{
    /// <summary>omp's chip for input with images and no text (queued-messages.ts <c>queueChipText</c>).</summary>
    private const string ImageOnlyChip = "[Image]";
    /// <summary>Runtime entries sent to omp that no snapshot listed yet: an older snapshot must not drop them.</summary>
    private readonly HashSet<long> _awaitingEngine = [];
    /// <summary>Queued input whose delivery echo already removed its chip; a snapshot still listing it must not bring it back.</summary>
    private List<(QueueKind Kind, string Text, bool HasImages, bool LiveSteered)> _deliveredQueued = [];
    private long _queueUpdateReadAt;
    /// <summary>This omp reports its queue: a delivery echo then removes only the chip with its exact text, since the
    /// snapshot already took a delivered chip whose text omp expanded.</summary>
    private bool _engineReportsQueue;

    /// <summary>
    /// Set while a stop takes the queue back (<c>abort_and_restore_queue</c>): omp empties its queue before answering,
    /// and the answer, not the emptied snapshot, decides which entries are recovered.
    /// </summary>
    public bool HoldQueueRemovals { get; set; }

    private void OnQueueUpdate(JsonElement j, long readAt)
    {
        if (!j.TryGetProperty("steering", out var steering) || !j.TryGetProperty("followUp", out var followUp)) return;
        var liveSteered = j.TryGetProperty("liveSteered", out var live) && live.ValueKind == JsonValueKind.Number && live.TryGetInt32(out var count) ? count : 0;
        ApplyEngineQueue(Strings(steering), Strings(followUp), readAt, liveSteered);

        static string[] Strings(JsonElement a) => a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
    }

    /// <summary>Mirrors omp's queue snapshot read at <paramref name="readAt"/>; an older read than the last applied one is ignored.</summary>
    public void ApplyEngineQueue(IReadOnlyList<string> steering, IReadOnlyList<string> followUp, long readAt, int liveSteered = 0)
    {
        if (readAt < _queueUpdateReadAt) return;
        _queueUpdateReadAt = readAt;
        _engineReportsQueue = true;
        var engine = steering.Select((t, index) => (Kind: QueueKind.Steer, Text: t, LiveSteered: index < liveSteered))
            .Concat(followUp.Select(t => (Kind: QueueKind.FollowUp, Text: t, LiveSteered: false))).ToList();

        var stillListed = new List<(QueueKind, string, bool, bool)>();
        foreach (var d in _deliveredQueued)
        {
            var i = engine.FindIndex(e => (!d.LiveSteered || e.LiveSteered) && Lists(e.Text, d.Text, d.HasImages));
            if (i < 0) continue;
            engine.RemoveAt(i);
            stillListed.Add(d);
        }
        _deliveredQueued = stillListed;

        var local = new List<int>();
        for (var i = 0; i < _queued.Count; i++)
            if (!_queued[i].Recovered && !_queued[i].Deferred && _queued[i].Kind != QueueKind.BuiltinCommand) local.Add(i);
        var kinds = new Dictionary<int, (QueueKind Kind, bool LiveSteered)>();
        var unmatched = new List<(QueueKind Kind, string Text, bool LiveSteered)>();
        // Same kind and text first; then the text under the other kind (omp promoted it); then, in order, the same
        // kind (omp expanded a template, so the chip text differs from what was sent).
        foreach (var e in engine)
        {
            var at = local.FindIndex(i => !kinds.ContainsKey(i) && _queued[i].Kind == e.Kind && Lists(e.Text, _queued[i]));
            if (at < 0) unmatched.Add(e);
            else kinds[local[at]] = (e.Kind, e.LiveSteered);
        }
        for (var n = 0; n < unmatched.Count;)
        {
            var e = unmatched[n];
            var at = local.FindIndex(i => !kinds.ContainsKey(i) && Lists(e.Text, _queued[i]));
            if (at < 0) { n++; continue; }
            kinds[local[at]] = (e.Kind, e.LiveSteered);
            unmatched.RemoveAt(n);
        }
        for (var n = 0; n < unmatched.Count;)
        {
            var e = unmatched[n];
            var at = local.FindIndex(i => !kinds.ContainsKey(i) && _queued[i].Kind == e.Kind && !_awaitingEngine.Contains(_queued[i].Seq));
            if (at < 0) { n++; continue; }
            kinds[local[at]] = (e.Kind, e.LiveSteered);
            unmatched.RemoveAt(n);
        }

        var changed = false;
        for (var n = local.Count - 1; n >= 0; n--)
        {
            var i = local[n];
            var q = _queued[i];
            if (kinds.TryGetValue(i, out var kind))
            {
                _awaitingEngine.Remove(q.Seq);
                if (kind.Kind != q.Kind || kind.LiveSteered != q.LiveSteered)
                {
                    _queued[i] = q with { Kind = kind.Kind, LiveSteered = kind.LiveSteered };
                    changed = true;
                }
            }
            else if (!HoldQueueRemovals && !_awaitingEngine.Contains(q.Seq))
            {
                _queued.RemoveAt(i);
                changed = true;
            }
        }
        foreach (var e in unmatched)
        {
            _queued.Add(new QueuedMessage(++_queueSeq, e.Kind, e.Text, []) { LiveSteered = e.LiveSteered });
            changed = true;
        }
        if (!changed) return;
        QueueChanged?.Invoke();
        Touch();
    }

    /// <summary>
    /// Whether omp's chip <paramref name="chip"/> lists input sent with this text: omp shows the text, and
    /// <see cref="ImageOnlyChip"/> for images sent without text.
    /// </summary>
    private static bool Lists(string chip, string text, bool hasImages) =>
        chip == text || (chip == ImageOnlyChip && text.Length == 0 && hasImages);

    private static bool Lists(string chip, QueuedMessage q) => Lists(chip, q.Text, q.Images.Count > 0);

    /// <summary>A queued entry's delivery echo arrived: its chip goes, and a snapshot from before the delivery cannot restore it.</summary>
    private void DeliverQueued(QueuedMessage q)
    {
        _awaitingEngine.Remove(q.Seq);
        _deliveredQueued.Add((q.Kind, q.Text, q.Images.Count > 0, q.LiveSteered));
        RemoveQueued(q.Seq);
        if (_deliveredQueued.Count > 32) _deliveredQueued.RemoveAt(0);
    }

    /// <summary>After a transcript reset every runtime entry is recovered input; a new process starts a new queue.</summary>
    private void ResetEngineQueue(bool newProcess)
    {
        _awaitingEngine.Clear();
        _deliveredQueued.Clear();
        if (!newProcess) return;
        _queueUpdateReadAt = 0;
        _engineReportsQueue = false;
        HoldQueueRemovals = false;
    }
}
