using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Plan mode over Ombra's engine (rpc-plan.ts, the <c>plan_mode</c> command): omp explores read-only and drafts a
/// plan; the plan it proposes arrives as <see cref="OmpPlanMode.Review"/> (its Markdown document) and runs only once
/// approved; a rejection's feedback becomes the next planning turn. Sent only to an omp that reported plan mode in
/// <c>get_state</c>: one without the command would answer "Unknown command" without our id.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>Approval waits until omp has started executing the plan (its turn admitted), not for the run.</summary>
    private static readonly TimeSpan PlanModeRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Turns plan mode on or off. Leaving it stops a planning turn in flight. Null on success, else why not.</summary>
    public Task<string?> SetPlanModeAsync(bool on, CancellationToken ct = default) => PlanModeAsync(on ? "enter" : "exit", null, null, ct);

    /// <summary>
    /// Approves the plan shown (<paramref name="revision"/>): plan mode ends and omp carries out exactly that text. omp
    /// refuses another revision, and any while a turn runs. Null on success.
    /// </summary>
    public Task<string?> ApprovePlanAsync(string revision, CancellationToken ct = default) => PlanModeAsync("approve", revision, null, ct);

    /// <summary>
    /// Rejects the plan shown (<paramref name="revision"/>) and stays in plan mode; <paramref name="feedback"/> (what to
    /// change) goes to omp as the next planning turn, which proposes a revised plan. Without feedback the review just
    /// closes. Null on success.
    /// </summary>
    public Task<string?> RejectPlanAsync(string revision, string? feedback, CancellationToken ct = default) => PlanModeAsync("reject", revision, feedback, ct);

    private async Task<string?> PlanModeAsync(string op, string? revision, string? feedback, CancellationToken ct)
    {
        if (_omp?.Connection is not { } conn) return "omp is not running";
        var snapshot = Snapshot();
        if (snapshot.IsReadOnly) return "this chat is read-only";
        if (snapshot.PlanModeSupported != true) return "this omp has no plan mode";
        if (snapshot.SigningIn is { } provider) return $"omp is busy with the sign-in to {provider}";
        try
        {
            var r = await conn.RequestAsync("plan_mode", w =>
            {
                w.WriteString("op", op);
                if (revision is not null) w.WriteString("revision", revision);
                if (!string.IsNullOrWhiteSpace(feedback)) w.WriteString("feedback", feedback.Trim());
            }, PlanModeRequestTimeout, ct).ConfigureAwait(false);
            if (!r.Success) return r.Error ?? "omp refused";
            if (r.Data is { } data)
            {
                var state = OmpPlanMode.Parse(data);
                Mutate(s => s.ApplyPlanModeState(state, r.Timings.ResponseRead));
            }
            return null;
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return e.Message;
        }
    }
}
