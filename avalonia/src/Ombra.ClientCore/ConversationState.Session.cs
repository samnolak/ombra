using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>
/// Shell commands the user runs in the session (<c>!command</c>, omp's RPC <c>bash</c>): a row like a tool call —
/// "shell" and the command, its output when it ends. omp keeps them in the session (<c>bashExecution</c> messages) and
/// in the model's context.
/// </summary>
public sealed partial class ConversationState
{
    private long _shellSeq;

    /// <summary>Shared display policy for persisted messages and completed live messages. Developer context is not UI text.</summary>
    private void AddVisibleContextMessage(JsonElement message, bool live = false)
    {
        switch (Str(message, "role"))
        {
            case "custom" or "hookMessage":
                // The flag is opt-in, including IRC. Never fall back to dumping details or a hidden envelope.
                if (!Bool(message, "display")) return;
                var customType = Str(message, "customType") ?? "Context";
                if (customType.StartsWith("irc:", StringComparison.Ordinal) && AgentMessageOf(message) is { } said)
                {
                    if (live && message.TryGetProperty("details", out var details) && Str(details, "id") is { } id
                        && !_liveIrcMessages.Add(id)) return;
                    Add(said);
                }
                else Add(new CommandOutputItem(0, ContentText(message, "text"))
                {
                    Label = customType, At = MessageTime(message), Images = ContentImages(message),
                });
                break;
            case "branchSummary" or "compactionSummary":
                var summary = Str(message, "summary") ?? "";
                StringBuilder? expandedSummary = null;
                var images = ContentImages(message, "blocks");
                if (message.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                    foreach (var block in blocks.EnumerateArray())
                        if (Str(block, "type") == "text" && Str(block, "text") is { Length: > 0 } text)
                            (expandedSummary ??= new StringBuilder(summary)).Append("\n\n").Append(text);
                if (images.Count == 0) images = ContentImages(message, "images");
                if (Str(message, "warning") is { Length: > 0 } warning)
                    (expandedSummary ??= new StringBuilder(summary)).Append("\n\n").Append(warning);
                Add(new CommandOutputItem(0, expandedSummary?.ToString() ?? summary)
                {
                    Label = Str(message, "role") == "branchSummary" ? "Branch summary" : "Compaction summary",
                    At = MessageTime(message), Images = images,
                });
                break;
            case "fileMention":
                if (!message.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return;
                foreach (var file in files.EnumerateArray())
                {
                    // Like the runtime's file-mention block, show the source, not the auto-read prompt payload.
                    IReadOnlyList<ImageAttachment> previews = file.TryGetProperty("image", out var image)
                        && Str(image, "type") == "image"
                        ? [new ImageAttachment(Str(file, "path") ?? "image", Str(image, "mimeType") ?? "image/png", Base64Bytes(Str(image, "data")))]
                        : [];
                    Add(new CommandOutputItem(0, (Str(file, "path") ?? "") +
                        (Str(file, "skippedReason") is { } reason ? $" ({reason})" : ""))
                    {
                        Label = "File mention", At = MessageTime(message), Images = previews,
                    });
                }
                break;
            case "pythonExecution":
                HydrateExecution(message, python: true);
                break;
            case "bashExecution":
                HydrateExecution(message, python: false);
                break;
        }
    }

    /// <summary>A shell command just sent to omp: a running row. Returns the row's id for <see cref="FinishShellCommand"/>.</summary>
    public string AddShellCommand(string command, DateTimeOffset now)
    {
        var id = "shell-" + (++_shellSeq).ToString(CultureInfo.InvariantCulture);
        _toolsByCallId[id] = Add(new ToolItem(0, id, "shell", Truncate(command, MaxArgsChars), ToolStatus.Running, null, OneLine(command), StartedAt: now));
        _toolsByCallId[id].Elapsed = MonotonicTime.Start(_clock);
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
            Took = row.Elapsed.IsRunning ? row.Elapsed.Elapsed : t.Took,
        });
        row.Elapsed = row.Elapsed.Stop();
    }

    /// <summary>A persisted user execution, rendered with the same tool-row conventions as a live shell command.</summary>
    private void HydrateExecution(JsonElement m, bool python)
    {
        var command = Str(m, python ? "code" : "command") ?? "";
        int? exit = m.TryGetProperty("exitCode", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var code) ? code : null;
        var cancelled = m.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True;
        Add(new ToolItem(0, "shell-h" + (++_shellSeq).ToString(CultureInfo.InvariantCulture), python ? "eval" : "shell", Truncate(command, MaxArgsChars),
            cancelled ? ToolStatus.Interrupted : exit is null or 0 ? ToolStatus.Succeeded : ToolStatus.Failed,
            HeadTail(ShellOutput(Str(m, "output") ?? "", exit), MaxToolOutputChars), OneLine(command), StartedAt: MessageTime(m)));
        var images = ContentImages(m, "images");
        if (images.Count > 0)
            Add(new CommandOutputItem(0, "") { Label = "Execution images", At = MessageTime(m), Images = images });
    }

    private static string ShellOutput(string output, int? exitCode) =>
        exitCode is { } c and not 0 ? output.TrimEnd() + $"\nexit code {c.ToString(CultureInfo.InvariantCulture)}" : output;

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
