using System.Text.Json;

namespace Ombra.Rpc;

/// <summary>A plan the agent proposed in plan mode and the user has not yet approved or rejected (rpc-plan.ts).</summary>
/// <param name="Revision">Opaque id of this proposal: approve and reject name it, and omp refuses any other.</param>
/// <param name="Content">The plan document (Markdown) as it was when proposed; approval runs exactly this text.</param>
public sealed record OmpPlanReview(string Revision, string PlanFilePath, string Title, string Content);

/// <summary>
/// Plan mode of the session (the owned engine's <c>plan_mode</c> command, <c>get_state.planMode</c> and
/// <c>plan_mode_updated</c> frames): on, the plan document the agent drafts, and the plan waiting for approval.
/// </summary>
public sealed record OmpPlanMode(bool Enabled, string? PlanFilePath, OmpPlanReview? Review)
{
    public static readonly OmpPlanMode Off = new(false, null, null);

    /// <summary>An object with <c>state</c> and <c>review</c> (a command's data, the get_state field or the frame).</summary>
    public static OmpPlanMode Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return Off;
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var on = d.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object
            && state.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        var review = d.TryGetProperty("review", out var r) && r.ValueKind == JsonValueKind.Object && S(r, "content") is { } content
            && S(r, "revision") is { } revision
            ? new OmpPlanReview(revision, S(r, "planFilePath") ?? "", S(r, "title") ?? "", content)
            : null;
        return new OmpPlanMode(on, on ? S(state, "planFilePath") : null, on ? review : null);
    }
}
