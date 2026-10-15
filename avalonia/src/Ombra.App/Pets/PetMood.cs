using Ombra.ClientCore;

namespace Ombra.App.Pets;

/// <summary>What the pet shows: what the agent is doing, as a pet would feel about it.</summary>
public enum PetMood { Idle, Sleeping, Thinking, Working, Waiting, Done, Error }

public static class PetMoods
{
    /// <summary>
    /// The agent's state in a snapshot, as a base mood: omp stopped → Error; a question or approval open → Waiting;
    /// a tool running → Working; the model thinking or writing → Thinking; otherwise Idle.
    /// </summary>
    public static PetMood BaseMood(SessionSnapshot s, SessionPhase phase, bool hasDialog)
    {
        if (phase == SessionPhase.Faulted) return PetMood.Error;
        if (hasDialog || s.Dialogs.Count > 0) return PetMood.Waiting;
        if (phase is not (SessionPhase.Running or SessionPhase.Aborting)) return PetMood.Idle;
        for (var i = s.Items.Count - 1; i >= 0; i--)
        {
            switch (s.Items[i])
            {
                case ToolItem { Status: ToolStatus.Running }: return PetMood.Working;
                case AssistantItem { Streaming: true }: return PetMood.Thinking;
                case UserItem: return PetMood.Thinking;
            }
        }
        return PetMood.Working;
    }
}
