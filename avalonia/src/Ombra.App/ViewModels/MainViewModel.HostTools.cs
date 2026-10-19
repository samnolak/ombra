using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>
/// The tools the window runs for every chat's agent (omp's host tools, SessionController.HostTools.cs):
/// <c>terminal_read</c> reads what a terminal tab shows (Codex 26.311: the agent sees the dev server's log or the failed
/// build in the user's terminal). Read-only; omp gates it like any tool it does not know to be safe (exec tier: asked
/// for in the default approval mode, allowed in yolo, or by the user's rule for it).
/// </summary>
public sealed partial class MainViewModel
{
    internal const string TerminalReadName = "terminal_read";
    internal const int TerminalReadDefaultLines = 200;
    internal const int TerminalReadMaxLines = 2000;
    /// <summary>The most text one read returns (the newest kept): a scrollback of long lines stays a bounded result.</summary>
    internal const int TerminalReadMaxChars = 64_000;

    private static readonly HostToolDefinition TerminalReadDefinition = new(
        TerminalReadName,
        "Read terminal",
        "Read what a terminal tab in the user's Ombra window shows: its scrollback and screen as plain text (e.g. a dev " +
        "server's log or a failed build the user ran there). Without `tab` it reads the tab the user has selected; the " +
        "result lists every open tab with its number. Read-only: it cannot type into the terminal.",
        """{"type":"object","properties":{"tab":{"type":"integer","minimum":1,"description":"The tab's number, as the result lists them (1 is the first tab). Default: the selected tab."},"lines":{"type":"integer","minimum":1,"maximum":2000,"description":"How many of the last lines to return (default 200, at most 2000)."}},"additionalProperties":false}""");

    private IReadOnlyList<HostTool>? _hostToolList;

    /// <summary>What every chat's omp gets in <c>set_host_tools</c> (MainViewModel.OpenSessions.cs registers it).</summary>
    internal IReadOnlyList<HostTool> HostToolList => _hostToolList ??= [new HostTool(TerminalReadDefinition, ReadTerminalForAgentAsync)];

    private async Task<HostToolResult> ReadTerminalForAgentAsync(JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ReadTerminalForAgent(arguments), DispatcherPriority.Normal, ct);
    }

    /// <summary><c>terminal_read</c> on the UI thread (the terminals' text is written there).</summary>
    internal HostToolResult ReadTerminalForAgent(JsonElement arguments)
    {
        int? tab = null;
        var lines = TerminalReadDefaultLines;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("tab", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n)) tab = n;
            if (arguments.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var count))
                lines = Math.Clamp(count, 1, TerminalReadMaxLines);
        }
        if (Terminals.Count == 0) return new HostToolResult("No terminal tab is open in the Ombra window.", IsError: true);
        TerminalViewModel target;
        if (tab is { } number)
        {
            if (number < 1 || number > Terminals.Count)
                return new HostToolResult($"There is no terminal tab {number}. Open tabs:\n{TabList(null)}", IsError: true);
            target = Terminals[number - 1];
        }
        else target = SelectedTerminal is { } selected && Terminals.Contains(selected) ? selected : Terminals[^1];

        var text = string.Join('\n', target.ReadLines?.Invoke(lines) ?? []);
        var cut = text.Length > TerminalReadMaxChars;
        if (cut)
        {
            text = text[^TerminalReadMaxChars..];
            var firstBreak = text.IndexOf('\n');
            if (firstBreak >= 0) text = text[(firstBreak + 1)..];
        }
        var shown = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Terminal tab {Terminals.IndexOf(target) + 1} of {Terminals.Count}: {Describe(target)}\n");
        if (Terminals.Count > 1) sb.Append("Open tabs:\n").Append(TabList(target));
        if (shown == 0) sb.Append("(The terminal shows nothing yet.)");
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"--- last {shown} lines{(cut ? ", earlier ones cut to stay under " + TerminalReadMaxChars + " characters" : "")} ---\n");
            sb.Append(text);
        }
        return new HostToolResult(sb.ToString());
    }

    private string TabList(TerminalViewModel? current)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Terminals.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"  {i + 1}. {Describe(Terminals[i])}{(ReferenceEquals(Terminals[i], current) ? " — this one" : "")}\n");
        return sb.ToString();
    }

    private string Describe(TerminalViewModel t)
    {
        var state = t.HasExited ? "exited" : t.Started ? "running" : "not started";
        var kind = t.Kind == TerminalKind.OmpTui ? "omp terminal UI" : "shell";
        var selected = ReferenceEquals(t, SelectedTerminal) ? ", selected" : "";
        return $"\"{t.Title}\" ({kind}, {state}{selected})";
    }
}
