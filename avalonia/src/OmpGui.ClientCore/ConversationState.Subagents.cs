using System.Text;
using System.Text.Json;

namespace OmpGui.ClientCore;

/// <summary>
/// A subagent omp started (its task tool), as omp reports it over RPC: <c>subagent_lifecycle</c> and
/// <c>subagent_progress</c> frames (after <c>set_subagent_subscription</c>) and <c>get_subagents</c>. omp forgets a
/// subagent once it ends; the client keeps it (the session's history of background work).
/// </summary>
public sealed record SubagentInfo
{
    public required string Id { get; init; }
    public int Index { get; init; }
    /// <summary>The agent type ("task", "explore", "reviewer"…).</summary>
    public required string Agent { get; init; }
    /// <summary>omp's status: pending, running, completed, failed, aborted; "ended" when omp no longer lists one it
    /// never reported the end of (omp restarted, or the end was missed).</summary>
    public required string Status { get; init; }
    public string? Description { get; init; }
    public string? Task { get; init; }
    public string? Assignment { get; init; }
    /// <summary>When the client first heard of it (omp does not send a start time).</summary>
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>When its last progress arrived; with <see cref="Duration"/> it gives the live running time.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>omp's own running time at the last progress (<c>durationMs</c>).</summary>
    public TimeSpan? Duration { get; init; }
    public string? CurrentTool { get; init; }
    public string? CurrentToolArgs { get; init; }
    public string? LastIntent { get; init; }
    /// <summary>The newest output lines, newest first (omp's <c>recentOutput</c>).</summary>
    public IReadOnlyList<string> RecentOutput { get; init; } = [];
    /// <summary>Tools it used lately, oldest first.</summary>
    public IReadOnlyList<SubagentToolUse> RecentTools { get; init; } = [];
    public int ToolCount { get; init; }
    public long Tokens { get; init; }
    public long? ContextTokens { get; init; }
    public long? ContextWindow { get; init; }
    public double Cost { get; init; }
    public string? Model { get; init; }
    /// <summary>A retry the subagent is waiting on, or the one it gave up on (rate limits…).</summary>
    public string? RetryNote { get; init; }
    /// <summary>Runs as a background job: the main turn went on without waiting for it.</summary>
    public bool Detached { get; init; }
    public string? SessionFile { get; init; }

    public bool IsRunning => Status is "running" or "pending";
}

public sealed record SubagentToolUse(string Tool, string Args);

/// <summary>One message of a subagent's own conversation (<c>get_subagent_messages</c>), reduced to what a reader needs.</summary>
public sealed record SubagentMessage(string Role, string Text);

/// <summary>Subagents: frames, <c>get_subagents</c> merges, and whether this omp reports them at all.</summary>
public sealed partial class ConversationState
{
    private const int MaxSubagents = 200;
    private const int MaxOutputLines = 8;
    private readonly List<SubagentInfo> _subagents = [];
    private IReadOnlyList<SubagentInfo> _subagentsView = [];
    private bool _subagentsDirty;
    /// <summary>The session the list belongs to: another session (a new one, a switch) starts an empty list.</summary>
    private string? _subagentsSession;

    /// <summary>null: not known yet; true: omp reports subagents; false: this omp does not (the subscription failed).</summary>
    public bool? SubagentsSupported
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>The requests the client sends for subagents; their failures are not errors worth a notice.</summary>
    public static bool IsSubagentCommand(string? command) => command is "set_subagent_subscription" or "get_subagents" or "get_subagent_messages";

    /// <summary>
    /// A failed response nobody waited for: an omp without subagent support answers these commands without an id
    /// ("Unknown command"). True when it was one of them (handled here: no notice in the conversation).
    /// </summary>
    private bool OnSubagentCommandFailed(JsonElement response)
    {
        var command = Str(response, "command");
        if (!IsSubagentCommand(command)) return false;
        if (command == "set_subagent_subscription") SubagentsSupported = false;
        return true;
    }

    private void ApplySubagentFrame(string type, JsonElement frame, DateTimeOffset now)
    {
        if (!frame.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return;
        SubagentsSupported = true;
        OwnSubagents();
        switch (type)
        {
            case "subagent_lifecycle": OnSubagentLifecycle(p, now); break;
            case "subagent_progress": OnSubagentProgress(p, now); break;
            // subagent_event is only sent at the "events" level, which the client does not ask for.
        }
    }

    /// <summary>Merges omp's <c>get_subagents</c> list: what runs now. One the client shows as running but omp no
    /// longer lists has ended without the client hearing how (omp restarted, or the end frame was missed).</summary>
    public void MergeSubagents(JsonElement list, DateTimeOffset now)
    {
        if (list.ValueKind != JsonValueKind.Array) return;
        OwnSubagents();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in list.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || Str(s, "id") is not { Length: > 0 } id) continue;
            listed.Add(id);
            var status = Str(s, "status") ?? "running";
            var existing = FindSubagent(id);
            var info = (existing ?? NewSubagent(id, s, now)) with
            {
                Index = Int(s, "index") ?? existing?.Index ?? 0,
                Agent = Str(s, "agent") ?? existing?.Agent ?? "task",
                Status = status,
                Description = Str(s, "description") ?? existing?.Description,
                Task = Str(s, "task") ?? existing?.Task,
                Assignment = Str(s, "assignment") ?? existing?.Assignment,
                SessionFile = Str(s, "sessionFile") ?? existing?.SessionFile,
            };
            if (s.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Object) info = WithProgress(info, progress, now);
            PutSubagent(info);
        }
        for (var i = 0; i < _subagents.Count; i++)
            if (_subagents[i].IsRunning && !listed.Contains(_subagents[i].Id))
            {
                _subagents[i] = _subagents[i] with { Status = "ended", EndedAt = _subagents[i].EndedAt ?? now };
                _subagentsDirty = true;
            }
        Touch();
    }

    private void OnSubagentLifecycle(JsonElement p, DateTimeOffset now)
    {
        if (Str(p, "id") is not { Length: > 0 } id) return;
        var status = Str(p, "status") switch { "started" => "running", { Length: > 0 } s => s, _ => "running" };
        var existing = FindSubagent(id);
        var info = (existing ?? NewSubagent(id, p, now)) with
        {
            Index = Int(p, "index") ?? existing?.Index ?? 0,
            Agent = Str(p, "agent") ?? existing?.Agent ?? "task",
            Status = status,
            Description = Str(p, "description") ?? existing?.Description,
            SessionFile = Str(p, "sessionFile") ?? existing?.SessionFile,
            Detached = Bool(p, "detached") || existing?.Detached == true,
            // A revived agent (started again) runs again; an end keeps the first end time
            EndedAt = status == "running" ? null : existing?.EndedAt ?? now,
            UpdatedAt = now,
        };
        PutSubagent(info);
    }

    private void OnSubagentProgress(JsonElement p, DateTimeOffset now)
    {
        if (!p.TryGetProperty("progress", out var progress) || progress.ValueKind != JsonValueKind.Object) return;
        if (Str(progress, "id") is not { Length: > 0 } id) return;
        var existing = FindSubagent(id);
        // After an end, a late progress frame must not bring the agent back to "running"
        if (existing is { IsRunning: false } && Str(progress, "status") is "running" or "pending") return;
        var info = (existing ?? NewSubagent(id, progress, now)) with
        {
            Index = Int(p, "index") ?? existing?.Index ?? 0,
            Agent = Str(p, "agent") ?? existing?.Agent ?? "task",
            Task = Str(p, "task") ?? existing?.Task,
            Assignment = Str(p, "assignment") ?? existing?.Assignment,
            SessionFile = Str(p, "sessionFile") ?? existing?.SessionFile,
            Detached = Bool(p, "detached") || existing?.Detached == true,
        };
        PutSubagent(WithProgress(info, progress, now));
    }

    /// <summary>omp's <c>AgentProgress</c>: status, activity, output, counters.</summary>
    private static SubagentInfo WithProgress(SubagentInfo info, JsonElement g, DateTimeOffset now)
    {
        var status = Str(g, "status") ?? info.Status;
        var ended = status is not ("running" or "pending");
        string? retry = null;
        if (g.TryGetProperty("retryFailure", out var rf) && rf.ValueKind == JsonValueKind.Object)
            retry = "Gave up retrying: " + (Str(rf, "errorMessage") ?? "provider error");
        else if (g.TryGetProperty("retryState", out var rs) && rs.ValueKind == JsonValueKind.Object)
            retry = $"Retrying ({Int(rs, "attempt") ?? 1} of {Int(rs, "maxAttempts") ?? 1}): {Str(rs, "errorMessage") ?? "provider error"}";
        return info with
        {
            Status = status,
            Description = Str(g, "description") ?? info.Description,
            Task = Str(g, "task") ?? info.Task,
            Assignment = Str(g, "assignment") ?? info.Assignment,
            LastIntent = Str(g, "lastIntent") ?? info.LastIntent,
            CurrentTool = ended ? null : Str(g, "currentTool"),
            CurrentToolArgs = ended ? null : Str(g, "currentToolArgs"),
            RecentOutput = g.TryGetProperty("recentOutput", out var ro) && ro.ValueKind == JsonValueKind.Array
                ? [.. ro.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).Take(MaxOutputLines)]
                : info.RecentOutput,
            RecentTools = g.TryGetProperty("recentTools", out var rt) && rt.ValueKind == JsonValueKind.Array
                ? [.. rt.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).Select(t => new SubagentToolUse(Str(t, "tool") ?? "tool", OneLine(Str(t, "args") ?? "", 160)))]
                : info.RecentTools,
            ToolCount = Int(g, "toolCount") ?? info.ToolCount,
            Tokens = Long(g, "tokens") ?? info.Tokens,
            ContextTokens = Long(g, "contextTokens") ?? info.ContextTokens,
            ContextWindow = Long(g, "contextWindow") ?? info.ContextWindow,
            Cost = g.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : info.Cost,
            Duration = Long(g, "durationMs") is { } ms ? TimeSpan.FromMilliseconds(ms) : info.Duration,
            Model = Str(g, "resolvedModel") ?? info.Model,
            RetryNote = retry,
            UpdatedAt = now,
            EndedAt = ended ? info.EndedAt ?? now : null,
        };
    }

    private static SubagentInfo NewSubagent(string id, JsonElement from, DateTimeOffset now) => new()
    {
        Id = id,
        Agent = Str(from, "agent") ?? "task",
        Status = "running",
        FirstSeenAt = now,
        UpdatedAt = now,
    };

    private SubagentInfo? FindSubagent(string id) => _subagents.FirstOrDefault(s => s.Id == id);

    private void PutSubagent(SubagentInfo info)
    {
        var i = _subagents.FindIndex(s => s.Id == info.Id);
        if (i >= 0) _subagents[i] = info;
        else
        {
            _subagents.Add(info);
            // The oldest finished ones go first when the list is long
            while (_subagents.Count > MaxSubagents && _subagents.FindIndex(s => !s.IsRunning) is >= 0 and var old) _subagents.RemoveAt(old);
        }
        _subagentsDirty = true;
        Touch();
    }

    /// <summary>Subagents belong to the session they ran in: another session's list starts empty.</summary>
    private void OwnSubagents()
    {
        if (_subagentsSession == SessionFile) return;
        _subagentsSession = SessionFile;
        if (_subagents.Count == 0) return;
        _subagents.Clear();
        _subagentsDirty = true;
    }

    /// <summary>The list for the snapshot; the same instance until something changed (the UI compares references).</summary>
    private IReadOnlyList<SubagentInfo> SubagentsSnapshot()
    {
        if (_subagentsSession != SessionFile && _subagents.Count > 0) OwnSubagents();
        if (_subagentsDirty)
        {
            _subagentsView = _subagents.ToArray();
            _subagentsDirty = false;
        }
        return _subagentsView;
    }

    /// <summary><c>get_subagent_messages</c>: the subagent's conversation, one entry per text or tool call.</summary>
    public static IReadOnlyList<SubagentMessage> ParseSubagentMessages(JsonElement data, int max = 40)
    {
        var list = new List<SubagentMessage>();
        if (!data.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var role = Str(m, "role");
            switch (role)
            {
                case "user" or "assistant":
                    if (ContentText(m, "text") is { Length: > 0 } text) list.Add(new SubagentMessage(role, Truncate(text.Trim(), 1200)));
                    if (m.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        foreach (var part in parts.EnumerateArray())
                            if (Str(part, "type") == "toolCall")
                            {
                                var name = Str(part, "name") ?? "tool";
                                var what = part.TryGetProperty("arguments", out var a) ? ToolSummary(name, a) : "";
                                list.Add(new SubagentMessage("tool", what.Length > 0 ? $"{name} · {what}" : name));
                            }
                    break;
                case "toolResult":
                    var result = ContentText(m, "text").Trim();
                    if (result.Length > 0) list.Add(new SubagentMessage(Bool(m, "isError") ? "error" : "result", HeadTail(result, 600)));
                    break;
            }
        }
        return list.Count > max ? list[^max..] : list;
    }

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v) ? (long)v : null;

    private static string OneLine(string s, int max)
    {
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        foreach (var ch in s)
        {
            if (sb.Length >= max) return sb.Append('…').ToString();
            sb.Append(ch is '\n' or '\r' or '\t' ? ' ' : ch);
        }
        return sb.ToString();
    }
}
