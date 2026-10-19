using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>Whether this omp has the background job commands (rpc-jobs.ts); an older one answers "Unknown command" without an id.</summary>
public sealed partial class ConversationState
{
    /// <summary>null: not known yet (or supported); false: omp does not know <c>get_jobs</c>, so the client stops asking.</summary>
    public bool? JobsSupported
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Touch();
        }
    }

    /// <summary>
    /// A failed job command nobody waited for (an omp without the job commands answers without an id). True when it was
    /// one: handled here, never a notice in the conversation.
    /// </summary>
    private bool OnJobCommandFailed(JsonElement response)
    {
        if (Str(response, "command") is not ("get_jobs" or "cancel_job" or "get_job_output")) return false;
        if (Str(response, "error") is { } error && error.StartsWith("Unknown command", StringComparison.Ordinal)) JobsSupported = false;
        return true;
    }
}
