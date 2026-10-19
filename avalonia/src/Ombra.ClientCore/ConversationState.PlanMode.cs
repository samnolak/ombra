using System.Text.Json;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Plan mode of the session (Ombra's engine, rpc-plan.ts): from <c>plan_mode_updated</c> frames, the
/// <c>planMode</c> field of <c>get_state</c> and the answers to <c>plan_mode</c> (SessionController.PlanMode.cs).
/// A frame read after a <c>get_state</c> request was answered wins over that answer.
/// </summary>
public sealed partial class ConversationState
{
    public OmpPlanMode PlanMode { get; private set; } = OmpPlanMode.Off;
    /// <summary>Whether this omp has plan mode over RPC (null: not known yet).</summary>
    public bool? PlanModeSupported { get; private set; }
    private long _planModeReadAt;

    private void ApplyPlanModeFrame(JsonElement frame, long readAt)
    {
        PlanModeSupported = true;
        SetPlanMode(OmpPlanMode.Parse(frame), readAt);
    }

    /// <summary>A <c>get_state</c> or <c>plan_mode</c> answer read at <paramref name="readAt"/>; null when omp has no plan mode.</summary>
    internal void ApplyPlanModeState(OmpPlanMode? state, long readAt)
    {
        if (state is null)
        {
            PlanModeSupported ??= false;
            if (PlanModeSupported == false) PlanMode = OmpPlanMode.Off;
            Touch();
            return;
        }
        PlanModeSupported = true;
        if (_planModeReadAt > readAt) return;
        SetPlanMode(state, readAt);
    }

    private void SetPlanMode(OmpPlanMode state, long readAt)
    {
        _planModeReadAt = readAt;
        if (PlanMode == state) return;
        PlanMode = state;
        Touch();
    }

    /// <summary>A new omp process knows nothing of the previous one's plan mode.</summary>
    private void ResetPlanMode()
    {
        PlanMode = OmpPlanMode.Off;
        PlanModeSupported = null;
        _planModeReadAt = 0;
    }
}
