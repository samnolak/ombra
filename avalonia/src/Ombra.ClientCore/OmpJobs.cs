using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>One background job of the chat's omp (<c>get_jobs</c>, rpc-jobs.ts): a long bash command or an async task.</summary>
public sealed record JobInfo(
    string Id,
    string Type,
    string Status,
    string Label,
    string? Command,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string? AgentId,
    long OutputBytes,
    bool WaitingForInput)
{
    public bool IsRunning => Status == "running";
}

/// <summary>
/// One bounded read of a job's output (<c>get_job_output</c>). <see cref="NextByte"/> is the cursor for the next
/// read; <see cref="SkippedBytes"/> counts bytes omp no longer keeps; a <see cref="Snapshot"/> replaces what was read
/// (the job does not stream); <see cref="Reset"/>: the cursor was past the end and this is the tail.
/// </summary>
public sealed record JobOutputPage(
    string JobId,
    string Status,
    bool WaitingForInput,
    long FromByte,
    long NextByte,
    long TotalBytes,
    long SkippedBytes,
    bool Reset,
    bool Snapshot,
    string Text);

public static class OmpJobs
{
    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary><c>get_jobs</c> data; null when it is not that shape. Rows without an id or status are skipped.</summary>
    public static IReadOnlyList<JobInfo>? ParseJobs(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("jobs", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var jobs = new List<JobInfo>();
        foreach (var j in list.EnumerateArray())
        {
            if (j.ValueKind != JsonValueKind.Object || Str(j, "id") is not { Length: > 0 } id || Str(j, "status") is not { Length: > 0 } status) continue;
            var ended = Long(j, "endTime");
            var exit = Long(j, "exitCode");
            jobs.Add(new JobInfo(id, Str(j, "type") ?? "", status, Str(j, "label") ?? "", Str(j, "command"),
                DateTimeOffset.FromUnixTimeMilliseconds(Long(j, "startTime") ?? 0),
                ended is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e) : null,
                exit is { } x and >= int.MinValue and <= int.MaxValue ? (int)x : null,
                Str(j, "agentId"), Math.Max(0, Long(j, "outputBytes") ?? 0), Bool(j, "waitingForInput") == true));
        }
        return jobs;
    }

    /// <summary><c>get_job_output</c> data; null when it is not that shape or its cursor goes backwards.</summary>
    public static JobOutputPage? ParseOutput(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || Str(data, "jobId") is not { } id || Str(data, "text") is not { } text
            || Long(data, "fromByte") is not { } from || Long(data, "nextByte") is not { } next || from < 0 || next < from) return null;
        return new JobOutputPage(id, Str(data, "status") ?? "", Bool(data, "waitingForInput") == true, from, next,
            Long(data, "totalBytes") ?? next, Math.Max(0, Long(data, "skippedBytes") ?? 0), Bool(data, "reset") == true,
            Bool(data, "snapshot") == true, text);
    }
}
