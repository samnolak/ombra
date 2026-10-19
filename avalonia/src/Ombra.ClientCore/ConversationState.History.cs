using System.Text.Json;
using Ombra.Rpc;

namespace Ombra.ClientCore;

public sealed partial class ConversationState
{
    private readonly HashSet<string> _historyOrphanTools = new(StringComparer.Ordinal);
    public SessionHistory History { get; private set; } = new();
    internal long TranscriptRevision { get; private set; }
    internal long HistoryMutationReadAt { get; set; }
    internal long HistoryPhaseReadAt { get; private set; }
    internal long HistoryModelReadAt { get; private set; }
    internal long HistoryThinkingReadAt { get; private set; }
    internal long HistoryTitleReadAt { get; private set; }
    internal long LastTranscriptKey => _nextKey;

    internal void ApplyHistorySessionMetadata(OmpSessionState snapshot, DateTimeOffset now)
    {
        var readAt = snapshot.Timings?.ResponseRead ?? 0;
        if (HistoryModelReadAt <= readAt) Model = snapshot.ModelProvider is null ? null : $"{snapshot.ModelProvider}/{snapshot.ModelId}";
        if (HistoryThinkingReadAt <= readAt) ThinkingLevel = snapshot.ThinkingLevel;
        if (HistoryTitleReadAt <= readAt) SessionName = snapshot.SessionName;
        SessionId = snapshot.SessionId;
        SessionFile = snapshot.SessionFile;
        Todos = ParseTodos(snapshot.TodoPhases);
        ContextPercent = snapshot.ContextPercent;
        ApplyUsageLimit(snapshot);
        ApplyPlanModeState(snapshot.PlanMode, readAt);
        ApplyGoalState(snapshot, readAt, now);
        // The only agent_start/agent_end may already have overtaken get_state; never undo its phase.
        if (HistoryPhaseReadAt <= readAt) SetPhase(snapshot.IsStreaming ? SessionPhase.Running : SessionPhase.Starting, now);
    }

    internal void ResetHistorySession(long previousLastKey)
    {
        var arrived = _rows.Where(row => row.Key > previousLastKey).ToArray();
        var streaming = _streamingAssistant is { } active && active.Key > previousLastKey ? active : null;
        var ended = _endedBeforeMessageEnd is { } final && final.Key > previousLastKey ? final : null;
        var awaitingSettle = AwaitingSettle;
        var messageRows = _messageRows.ToArray();
        ResetTranscript(newProcess: false);
        _rows.AddRange(arrived);
        foreach (var row in arrived)
            if (row.Item is ToolItem tool) _toolsByCallId[tool.ToolCallId] = row;
        _streamingAssistant = streaming;
        _endedBeforeMessageEnd = ended;
        RetainMessageRows(messageRows, arrived);
        AwaitingSettle = awaitingSettle;
    }

    internal bool TryReplaceHistory(JsonElement messages, long responseReadTimestamp)
    {
        if (HistoryMutationReadAt > responseReadTimestamp) return false;
        ReplaceHistory(messages);
        return true;
    }

    public void SetHistory(SessionHistory history)
    {
        History = history;
        Touch();
    }

    // Parse and derive every row once, away from the visible state. A malformed nested tool argument/result must
    // not clear the transcript, consume keys, update an orphan result, or leave half a page committed.
    private ConversationState StageHistory(JsonElement messages, bool prepend)
    {
        if (messages.ValueKind != JsonValueKind.Array) throw new InvalidDataException("History messages must be an array.");
        var staged = new ConversationState { _nextKey = _nextKey, _shellSeq = _shellSeq, Cwd = Cwd };
        if (prepend)
            foreach (var id in _historyOrphanTools)
                if (_toolsByCallId.TryGetValue(id, out var row))
                {
                    staged._toolsByCallId[id] = new Row(row.Item.Key, row.Item);
                    staged._historyOrphanTools.Add(id);
                }
        try { staged.Hydrate(messages); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or
            KeyNotFoundException or IndexOutOfRangeException or OverflowException or NotSupportedException)
        {
            throw new InvalidDataException("History contains an invalid message; the displayed conversation was kept.", error);
        }
        return staged;
    }

    /// <summary>Atomically adopts a stable recent tail without losing startup warnings, command output or queued input.</summary>
    public void ReplaceHistory(JsonElement messages)
    {
        var staged = StageHistory(messages, prepend: false);
        var transient = _rows.Where(row => row.Item is NoticeItem or CommandOutputItem { Label: null }).ToArray();
        _rows.Clear();
        _rows.AddRange(staged._rows);
        _rows.AddRange(transient);
        _toolsByCallId.Clear();
        foreach (var (id, row) in staged._toolsByCallId) _toolsByCallId.Add(id, row);
        _newFileCalls.Clear();
        _historyOrphanTools.Clear();
        _historyOrphanTools.UnionWith(staged._historyOrphanTools);
        _streamingAssistant = null;
        _endedBeforeMessageEnd = null;
        _messageRows.Clear();
        _nextKey = staged._nextKey;
        _shellSeq = staged._shellSeq;
        TranscriptEpoch = Interlocked.Increment(ref s_epochs);
        OwnPlans();
        _plans.AddRange(staged._plans);
        _plansDirty = true;
        TranscriptRevision++;
        Touch();
    }

    /// <summary>Atomically prepends older rows, preserving live builders, existing tool row identities and plans.</summary>
    public void PrependHistory(JsonElement messages)
    {
        var staged = StageHistory(messages, prepend: true);
        // Only now, after every derived row succeeded, reconnect staged orphan updates to their existing row objects.
        for (var index = 0; index < staged._rows.Count; index++)
        {
            var row = staged._rows[index];
            if (row.Item is not ToolItem tool) continue;
            if (_toolsByCallId.TryGetValue(tool.ToolCallId, out var existing) && existing.Item.Key == tool.Key)
            {
                Set(existing, tool);
                _rows.Remove(existing);
                staged._rows[index] = existing;
            }
            else
                _toolsByCallId.TryAdd(tool.ToolCallId, row); // an older reused call id never takes over a newer live call
            if (staged._historyOrphanTools.Contains(tool.ToolCallId)) _historyOrphanTools.Add(tool.ToolCallId);
            else _historyOrphanTools.Remove(tool.ToolCallId);
        }
        _rows.InsertRange(0, staged._rows);
        OwnPlans();
        _plans.InsertRange(0, staged._plans);
        if (_plans.Count > MaxPlanSnapshots) _plans.RemoveRange(0, _plans.Count - MaxPlanSnapshots);
        _plansDirty = true;
        _nextKey = staged._nextKey;
        _shellSeq = staged._shellSeq;
        TranscriptRevision++;
        Touch();
    }

    private void HydrateToolCall(JsonElement part)
    {
        var id = Str(part, "id") ?? "";
        var hasArgs = part.TryGetProperty("arguments", out var args);
        var name = Str(part, "name") ?? "tool";
        var written = hasArgs ? WrittenContent(name, args) : null;
        var call = new ToolItem(0, id, name, Truncate(hasArgs ? Compact(args) : "", MaxArgsChars),
            ToolStatus.Interrupted, null, hasArgs ? ToolSummary(name, args) : "", written)
        {
            Changes = hasArgs ? WrittenChange(id, name, args, written, live: false) : null,
        };
        if (_historyOrphanTools.Remove(id) && _toolsByCallId.TryGetValue(id, out var orphan) && orphan.Item is ToolItem result)
        {
            var changes = result.Status == ToolStatus.Succeeded ? result.Changes ?? call.Changes : result.Changes;
            Set(orphan, call with
            {
                Key = result.Key, Status = result.Status, Output = result.Output, Diff = result.Diff ?? call.Diff,
                ResultNote = result.ResultNote, Changes = changes, Summary = SummaryOf(call.Summary, changes),
            });
            _rows.Remove(orphan);
            _rows.Add(orphan);
        }
        else
            _toolsByCallId[id] = Add(call);
    }

    private void HydrateToolResult(JsonElement message)
    {
        if (Str(message, "toolCallId") is not { } id) return;
        if (!_toolsByCallId.TryGetValue(id, out var row))
        {
            row = Add(new ToolItem(0, id, Str(message, "toolName") ?? "tool", "", ToolStatus.Interrupted, null));
            _toolsByCallId[id] = row;
            _historyOrphanTools.Add(id);
        }
        if (row.Item is not ToolItem tool) return;
        var failed = message.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        var changes = ChangesAtEnd(tool, message, failed);
        Set(row, tool with
        {
            Status = failed ? IsSkipped(message) ? ToolStatus.Skipped : ToolStatus.Failed : ToolStatus.Succeeded,
            Output = HeadTail(ContentText(message, "text"), MaxToolOutputChars), Diff = DiffOf(message) ?? tool.Diff,
            ResultNote = failed ? null : ResultNote(tool.Name, message), Changes = changes,
            Summary = SummaryOf(tool.Summary, changes),
        });
    }
}
