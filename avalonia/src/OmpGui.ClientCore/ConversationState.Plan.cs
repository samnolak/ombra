using System.Text.Json;

namespace OmpGui.ClientCore;

/// <summary>The todo list as one todo tool call committed it (omp's <c>details.phases</c>), and when.</summary>
/// <param name="At">When omp recorded it (the message's time in a loaded session; the event's time live); null if unknown.</param>
public sealed record PlanSnapshot(DateTimeOffset? At, IReadOnlyList<TodoPhase> Phases);

/// <summary>
/// The plan's history: every todo list the todo tool committed in this conversation, from the session's saved messages
/// (omp keeps each result's <c>details.phases</c>, which is how omp itself restores the list) and from live
/// <c>tool_execution_end</c> events. The Plan pane builds its earlier plans and its change list from it.
/// </summary>
public sealed partial class ConversationState
{
    private const int MaxPlanSnapshots = 500;
    private readonly List<PlanSnapshot> _plans = [];
    private IReadOnlyList<PlanSnapshot> _plansView = [];
    private long _plansEpoch = -1;
    private bool _plansDirty;

    /// <summary>A todo tool result (an event's <c>result</c> or a saved <c>toolResult</c> message): the list it committed.</summary>
    private void RecordPlan(JsonElement result, DateTimeOffset? at)
    {
        OwnPlans();
        if (result.ValueKind != JsonValueKind.Object || Bool(result, "isError")) return;
        if (!result.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object) return;
        // "view" only reads the list; results without phases (errors, legacy entries) commit nothing
        if (Str(details, "op") == "view" || !details.TryGetProperty("phases", out var phases) || phases.ValueKind != JsonValueKind.Array) return;
        _plans.Add(new PlanSnapshot(at, ParseTodos(phases)));
        if (_plans.Count > MaxPlanSnapshots) _plans.RemoveAt(0);
        _plansDirty = true;
        Touch();
    }

    /// <summary>A saved message's time (omp stores milliseconds since 1970 in <c>timestamp</c>).</summary>
    private static DateTimeOffset? MessageTime(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ms)
            && ms > 0 && ms < 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    /// <summary>The history belongs to the transcript: a replaced transcript (other session, restart) starts it again.</summary>
    private void OwnPlans()
    {
        if (_plansEpoch == TranscriptEpoch) return;
        _plansEpoch = TranscriptEpoch;
        _plans.Clear();
        _plansDirty = true;
    }

    /// <summary>The same instance until something was recorded (the UI compares references).</summary>
    private IReadOnlyList<PlanSnapshot> PlansSnapshot()
    {
        if (_plansEpoch != TranscriptEpoch) OwnPlans();
        if (_plansDirty)
        {
            _plansView = _plans.ToArray();
            _plansDirty = false;
        }
        return _plansView;
    }
}
