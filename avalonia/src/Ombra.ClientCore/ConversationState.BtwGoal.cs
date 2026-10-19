using System.Text;
using System.Text.Json;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Goal mode and side questions of the session (Ombra's engine: rpc-goal.ts, rpc-btw.ts). The goal comes from
/// <c>goal_updated</c> frames, <c>get_state.goal</c> and the answers to <c>goal</c> (SessionController.BtwGoal.cs); a
/// frame read after a <c>get_state</c> request was answered wins over that answer. Side-question topics come from
/// <c>get_btw_history</c>, <c>btw_record</c> (each change of a topic) and <c>btw_delta</c> (its answer as it streams).
/// Both belong to the session: a session switch drops them until the new session's arrive.
/// </summary>
public sealed partial class ConversationState
{
    public OmpGoal? Goal { get; private set; }
    /// <summary>Whether this omp has goal mode over RPC (null: not known yet).</summary>
    public bool? GoalSupported { get; private set; }
    /// <summary>When <see cref="Goal"/> was read: an active goal's time goes on from its <c>timeUsedSeconds</c> then.</summary>
    public DateTimeOffset GoalReadAt { get; private set; }
    private long _goalReadAt;

    private sealed class BtwEntry(OmpBtwRecord record, long readAt)
    {
        public OmpBtwRecord Record = record;
        /// <summary>The latest answer while <c>btw_delta</c> frames add to it.</summary>
        public StringBuilder? Live;
        public long ReadAt = readAt;
    }

    /// <summary>Newest first, as get_btw_history lists them.</summary>
    private readonly List<BtwEntry> _btw = [];
    private OmpBtwRecord[]? _btwSnapshot = [];
    /// <summary>get_btw_history has answered for this session.</summary>
    public bool BtwHistoryLoaded { get; private set; }

    private void ApplyGoalFrame(JsonElement frame, long readAt, DateTimeOffset now)
    {
        GoalSupported = true;
        SetGoal(frame.TryGetProperty("goal", out var g) ? OmpGoal.Parse(g) : null, readAt, now);
    }

    /// <summary>A <c>get_state</c> answer read at <paramref name="readAt"/>.</summary>
    internal void ApplyGoalState(OmpSessionState state, long readAt, DateTimeOffset now)
    {
        if (!state.GoalSupported)
        {
            GoalSupported ??= false;
            Touch();
            return;
        }
        GoalSupported = true;
        if (_goalReadAt > readAt) return;
        SetGoal(state.Goal, readAt, now);
    }

    /// <summary>A <c>goal</c> answer read at <paramref name="readAt"/>.</summary>
    internal void ApplyGoalAnswer(OmpGoal? goal, long readAt, DateTimeOffset now)
    {
        GoalSupported = true;
        if (_goalReadAt > readAt) return;
        SetGoal(goal, readAt, now);
    }

    private void SetGoal(OmpGoal? goal, long readAt, DateTimeOffset now)
    {
        _goalReadAt = readAt;
        GoalReadAt = now;
        if (Goal == goal) return;
        Goal = goal;
        Touch();
    }

    /// <summary>A finished goal the user has seen: omp has already let it go, the bar goes too.</summary>
    internal void DismissCompletedGoal()
    {
        if (Goal is not { IsComplete: true }) return;
        Goal = null;
        Touch();
    }

    private void ApplyBtwRecordFrame(JsonElement frame, long readAt)
    {
        if (frame.TryGetProperty("record", out var r) && OmpBtwRecord.Parse(r) is { } record) ApplyBtwRecord(record, readAt);
    }

    /// <summary>A topic as omp saved it (a <c>btw_record</c> frame, or the <c>btw</c> answer).</summary>
    internal void ApplyBtwRecord(OmpBtwRecord record, long readAt)
    {
        var i = _btw.FindIndex(e => e.Record.Id == record.Id);
        if (i >= 0)
        {
            var entry = _btw[i];
            if (entry.ReadAt > readAt) return;
            entry.Record = record;
            entry.Live = null;
            entry.ReadAt = readAt;
        }
        else
        {
            var at = _btw.FindIndex(e => e.Record.CreatedAt < record.CreatedAt);
            _btw.Insert(at < 0 ? _btw.Count : at, new BtwEntry(record, readAt));
        }
        _btwSnapshot = null;
        Touch();
    }

    private void ApplyBtwDelta(JsonElement frame, long readAt)
    {
        if (Str(frame, "recordId") is not { } id || Str(frame, "delta") is not { Length: > 0 } delta) return;
        if (_btw.Find(e => e.Record.Id == id) is not { } entry || !entry.Record.IsRunning) return;
        (entry.Live ??= new StringBuilder(entry.Record.Latest.Answer)).Append(delta);
        entry.ReadAt = readAt;
        _btwSnapshot = null;
        Touch();
    }

    /// <summary>The <c>get_btw_history</c> answer read at <paramref name="readAt"/>: it replaces the topics, except for ones a later frame changed.</summary>
    internal void ApplyBtwHistory(IReadOnlyList<OmpBtwRecord> records, long readAt)
    {
        var newer = _btw.Where(e => e.ReadAt > readAt).ToDictionary(e => e.Record.Id);
        _btw.Clear();
        foreach (var record in records)
            _btw.Add(newer.Remove(record.Id, out var kept) ? kept : new BtwEntry(record, readAt));
        foreach (var kept in newer.Values) _btw.Add(kept);
        _btw.Sort((a, b) => b.Record.CreatedAt.CompareTo(a.Record.CreatedAt));
        BtwHistoryLoaded = true;
        _btwSnapshot = null;
        Touch();
    }

    private OmpBtwRecord[] BtwSnapshot() =>
        _btwSnapshot ??= [.. _btw.Select(e => e.Live is { } live ? e.Record.WithLatestAnswer(live.ToString()) : e.Record)];

    /// <summary>A session switch: the goal and topics shown were the previous session's. A new omp process knows nothing of goal mode.</summary>
    private void ResetBtwGoal(bool newProcess)
    {
        Goal = null;
        _goalReadAt = 0;
        if (newProcess) GoalSupported = null;
        _btw.Clear();
        _btwSnapshot = [];
        BtwHistoryLoaded = false;
    }
}
