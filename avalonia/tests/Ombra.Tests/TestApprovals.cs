using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// Test fixtures: the typed request the owned engine sends with an approval (tools/approval.ts buildToolApprovalRequest),
/// built from the same text prompt it sends beside it, so a test can state a request as omp prints it.
/// </summary>
internal static class TestApprovals
{
    /// <summary>"Allow tool: bash\nReason: …\nCommand: …" → the typed request; a "[…Nch elided…]" command was too long to send whole.</summary>
    public static ApprovalRequest Request(string title)
    {
        var lines = title.Split('\n');
        var tool = lines[0]["Allow tool: ".Length..].Trim();
        string? reason = null;
        var mcp = false;
        var details = new List<string>();
        var checks = new List<string>();
        var inChecks = false;
        foreach (var line in lines.Skip(1))
        {
            if (inChecks) checks.Add(line);
            else if (line == "Provider safety checks:") inChecks = true;
            else if (details.Count == 0 && line.StartsWith("Reason: ", StringComparison.Ordinal)) reason = line["Reason: ".Length..];
            else if (details.Count == 0 && line == "Origin: MCP server tool") mcp = true;
            else details.Add(line);
        }
        var commandAt = title.IndexOf("Command: ", StringComparison.Ordinal);
        var command = tool == "bash" && commandAt >= 0 ? title[(commandAt + "Command: ".Length)..] : null;
        // bash.ts formatApprovalDetails: "Cwd: …" follows the command when the call names a folder
        string? cwd = null;
        if (command?.IndexOf("\nCwd: ", StringComparison.Ordinal) is { } at and >= 0)
        {
            cwd = command[(at + "\nCwd: ".Length)..];
            command = command[..at];
        }
        var truncated = command?.Contains("ch elided…]", StringComparison.Ordinal) == true;
        if (truncated) command = null;
        return new ApprovalRequest(tool, command is { Length: > 0 } ? command : null, tool == "bash" && command is null, checks.Count > 0)
        {
            Risk = tool is "read" ? ApprovalRisk.Read : tool is "write" or "edit" ? ApprovalRisk.Write : ApprovalRisk.Exec,
            Reason = reason,
            FromMcp = mcp,
            Details = details,
            SafetyCheckLines = checks,
            Cwd = cwd,
        };
    }

    /// <summary>An approval dialog as the owned engine sends it: the text prompt with its typed request.</summary>
    public static PendingDialog Dialog(string title, string id = "d") =>
        new(id, DialogKind.Approval, title, null, [new DialogOption("Approve", null, false), new DialogOption("Deny", null, false)], null, null, DateTimeOffset.UtcNow, null)
        { Approval = title.StartsWith("Allow tool: ", StringComparison.Ordinal) ? Request(title) : null };
}
