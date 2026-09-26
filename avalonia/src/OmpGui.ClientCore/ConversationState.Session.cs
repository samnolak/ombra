using System.Globalization;
using System.Text.Json;

namespace OmpGui.ClientCore;

/// <summary>
/// Shell commands the user runs in the session (<c>!command</c>, omp's RPC <c>bash</c>): a row like a tool call —
/// "shell" and the command, its output when it ends. omp keeps them in the session (<c>bashExecution</c> messages) and
/// in the model's context.
/// </summary>
public sealed partial class ConversationState
{
    private long _shellSeq;

    /// <summary>A shell command just sent to omp: a running row. Returns the row's id for <see cref="FinishShellCommand"/>.</summary>
    public string AddShellCommand(string command, DateTimeOffset now)
    {
        var id = "shell-" + (++_shellSeq).ToString(CultureInfo.InvariantCulture);
        _toolsByCallId[id] = Add(new ToolItem(0, id, "shell", Truncate(command, MaxArgsChars), ToolStatus.Running, null, OneLine(command), StartedAt: now));
        return id;
    }

    /// <summary>The command ended: its output (head and tail), and failed when it exited non-zero.</summary>
    public void FinishShellCommand(string id, string output, int? exitCode, bool cancelled, DateTimeOffset now)
    {
        if (!_toolsByCallId.TryGetValue(id, out var row) || row.Item is not ToolItem t) return;
        Set(row, t with
        {
            Status = cancelled ? ToolStatus.Interrupted : exitCode is null or 0 ? ToolStatus.Succeeded : ToolStatus.Failed,
            Output = HeadTail(ShellOutput(output, exitCode), MaxToolOutputChars),
            Took = t.StartedAt is { } started ? now - started : null,
        });
    }

    /// <summary>A <c>bashExecution</c> message from the session's history.</summary>
    private void HydrateShell(JsonElement m)
    {
        var command = Str(m, "command") ?? "";
        int? exit = m.TryGetProperty("exitCode", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var code) ? code : null;
        var cancelled = m.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True;
        Add(new ToolItem(0, "shell-h" + (++_shellSeq).ToString(CultureInfo.InvariantCulture), "shell", Truncate(command, MaxArgsChars),
            cancelled ? ToolStatus.Interrupted : exit is null or 0 ? ToolStatus.Succeeded : ToolStatus.Failed,
            HeadTail(ShellOutput(Str(m, "output") ?? "", exit), MaxToolOutputChars), OneLine(command)));
    }

    private static string ShellOutput(string output, int? exitCode) =>
        exitCode is { } c and not 0 ? output.TrimEnd() + $"\nexit code {c.ToString(CultureInfo.InvariantCulture)}" : output;

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
