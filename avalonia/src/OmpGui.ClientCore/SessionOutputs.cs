using System.Globalization;
using System.Text.RegularExpressions;

namespace OmpGui.ClientCore;

/// <summary>One line of omp's <c>/context</c> breakdown: a category and what it takes of the context window.</summary>
public sealed record ContextCategory(string Label, long Tokens, double Fraction);

/// <summary>omp's <c>/context</c> report: the window, how much of it is used, and by what.</summary>
/// <param name="Unavailable">omp's own sentence when it cannot tell (no model selected); the rest is empty then.</param>
public sealed record ContextReport(long Window, double UsedPercent, long UsedTokens, IReadOnlyList<ContextCategory> Categories, string? Unavailable = null);

/// <summary>What <c>/compact</c> or <c>/handoff</c> reported when it ended (they run in the background over RPC).</summary>
public sealed record MaintenanceResult(bool Ok, string Message, long? TokensBefore = null, long? TokensAfter = null);

/// <summary>What <c>/advisor status</c> says.</summary>
public enum AdvisorState { Off, NeedsModel, On, Unknown }

/// <summary>
/// Reads what omp 18.2.0's builtin slash commands print over RPC (the texts are in
/// packages/coding-agent/src/slash-commands/builtin-*.ts and helpers/*-report.ts). Pure functions: the GUI shows
/// the parts as controls, and falls back to the text itself when a line is not recognised.
/// </summary>
public static partial class SessionOutputs
{
    [GeneratedRegex(@"^Context window: (?<window>\d+) tokens \((?<pct>\d+(?:\.\d+)?)% used\)", RegexOptions.CultureInvariant)]
    private static partial Regex ContextHeader();

    [GeneratedRegex(@"^\s+(?<label>.+?)\s+\[[^\]]*\]\s+(?<pct>\d+)%\s+(?<tokens>\d+) tokens\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ContextLine();

    /// <summary><c>/context</c>: "Context window: 131072 tokens (13% used)" and one bar line per category.</summary>
    public static ContextReport? ParseContext(string text)
    {
        var lines = Lines(text);
        if (lines.FirstOrDefault(l => l.StartsWith("Context usage is unavailable", StringComparison.Ordinal)) is { } unavailable)
            return new ContextReport(0, 0, 0, [], unavailable);
        var header = lines.Select(l => ContextHeader().Match(l)).FirstOrDefault(m => m.Success);
        if (header is null)
        {
            // The fallback when omp's breakdown fails: "Context", "Window: N", "Used: N"
            var window = Number(lines, "Window:");
            var used = Number(lines, "Used:");
            return window is > 0 && used is not null ? new ContextReport(window.Value, 100.0 * used.Value / window.Value, used.Value, []) : null;
        }
        var total = long.Parse(header.Groups["window"].Value, CultureInfo.InvariantCulture);
        var cats = new List<ContextCategory>();
        foreach (var l in lines)
        {
            if (ContextLine().Match(l) is not { Success: true } m) continue;
            var tokens = long.Parse(m.Groups["tokens"].Value, CultureInfo.InvariantCulture);
            var label = m.Groups["label"].Value.Trim();
            // omp pads labels to 16 characters: its own "Auto-compact buf" is the auto-compaction reserve
            if (label == "Auto-compact buf") label = "Auto-compact buffer";
            cats.Add(new ContextCategory(label, tokens, total > 0 ? (double)tokens / total : 0));
        }
        var usedTokens = cats.Where(c => c.Label is not ("Free" or "Auto-compact buffer")).Sum(c => c.Tokens);
        return new ContextReport(total, double.Parse(header.Groups["pct"].Value, CultureInfo.InvariantCulture), usedTokens, cats);
    }

    [GeneratedRegex(@"Tokens: (?<before>-?\d+) -> (?<after>-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactTokens();

    /// <summary>The line that ends a <c>/compact</c> (printed after the command returned, when it ran in the background).</summary>
    public static MaintenanceResult? ParseCompactEnd(string text)
    {
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("Compaction complete", StringComparison.Ordinal))
            {
                var m = CompactTokens().Match(line);
                return m.Success
                    ? new MaintenanceResult(true, line, long.Parse(m.Groups["before"].Value, CultureInfo.InvariantCulture), long.Parse(m.Groups["after"].Value, CultureInfo.InvariantCulture))
                    : new MaintenanceResult(true, line);
            }
            if (line.StartsWith("Compaction failed", StringComparison.Ordinal) || line.StartsWith("Compaction cancelled", StringComparison.Ordinal))
                return new MaintenanceResult(false, AfterColon(line) ?? line);
            // parseCompactArgs refusing its arguments ("snapcompact" takes no focus, …)
            if (line.StartsWith("Usage:", StringComparison.Ordinal) || line.Contains("does not take", StringComparison.Ordinal))
                return new MaintenanceResult(false, line);
        }
        return null;
    }

    /// <summary>The line that ends a <c>/handoff</c>.</summary>
    public static MaintenanceResult? ParseHandoffEnd(string text)
    {
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("Context handed off", StringComparison.Ordinal)) return new MaintenanceResult(true, line);
            if (line.StartsWith("Handoff failed", StringComparison.Ordinal)) return new MaintenanceResult(false, AfterColon(line) ?? line);
            if (line.StartsWith("Handoff cancelled", StringComparison.Ordinal) || line.StartsWith("Handoff generation is already", StringComparison.Ordinal)
                || line.StartsWith("Wait for the current response", StringComparison.Ordinal))
                return new MaintenanceResult(false, line);
        }
        return null;
    }

    /// <summary>
    /// <c>/dirs</c>, <c>/add-dir</c>, <c>/remove-dir</c>: "Workspace directories:", then "  path (working directory)"
    /// and one indented line per added directory; a note ("Added …", "Removed …") may come first.
    /// </summary>
    public static (string? WorkingDirectory, IReadOnlyList<string> Added, string? Note) ParseDirectories(string text)
    {
        var lines = Lines(text);
        var at = lines.FindIndex(l => l.StartsWith("Workspace directories:", StringComparison.Ordinal));
        if (at < 0) return (null, [], text.Trim() is { Length: > 0 } t ? t : null);
        string? cwd = null;
        var added = new List<string>();
        foreach (var l in lines.Skip(at + 1).Where(l => l.StartsWith("  ", StringComparison.Ordinal)).Select(l => l.Trim()))
        {
            if (l.EndsWith(" (working directory)", StringComparison.Ordinal)) cwd = l[..^" (working directory)".Length];
            else if (l.Length > 0) added.Add(l);
        }
        var note = string.Join(" ", lines.Take(at).Where(l => l.Trim().Length > 0 && !l.StartsWith("Usage:", StringComparison.Ordinal)));
        return (cwd, added, note.Length > 0 ? note : null);
    }

    /// <summary><c>/export</c>: "Session exported to: &lt;path&gt;" (relative to omp's working directory by default).</summary>
    public static string? ParseExportPath(string text) => ValueAfter(text, "Session exported to: ");

    /// <summary><c>/share</c>: "Share URL: …", optionally "Gist: …" and a note about trimmed content.</summary>
    public static (string? Url, string? Gist, string? Note) ParseShare(string text) =>
        (ValueAfter(text, "Share URL: "), ValueAfter(text, "Gist: "), Lines(text).FirstOrDefault(l => l.StartsWith("Note: ", StringComparison.Ordinal))?[6..]);

    /// <summary><c>/stats</c>: "Dashboard available at: URL" or "Dashboard already running at: URL (…)".</summary>
    public static string? ParseDashboardUrl(string text)
    {
        foreach (var l in Lines(text))
        {
            var i = l.IndexOf(" at: ", StringComparison.Ordinal);
            if (!l.StartsWith("Dashboard ", StringComparison.Ordinal) || i < 0) continue;
            var url = l[(i + 5)..].Trim();
            var space = url.IndexOf(' ');
            return space > 0 ? url[..space] : url;
        }
        return null;
    }

    /// <summary>"Fast mode is on." / "Extended context is off." and the like: true, false, or null when not said.</summary>
    public static bool? ParseOnOff(string text, string subject)
    {
        foreach (var l in Lines(text))
        {
            if (!l.StartsWith(subject, StringComparison.Ordinal)) continue;
            var rest = l[subject.Length..].Trim().TrimEnd('.');
            if (rest is "is on" or "enabled" or "on") return true;
            if (rest is "is off" or "disabled" or "off") return false;
        }
        return null;
    }

    /// <summary><c>/advisor status</c> (session-advisors.ts formatAdvisorStatus) and the <c>/advisor on|off</c> replies.</summary>
    public static AdvisorState ParseAdvisor(string text)
    {
        if (text.Contains("no model is assigned to the 'advisor' role", StringComparison.Ordinal)) return AdvisorState.NeedsModel;
        if (text.StartsWith("Advisor is enabled", StringComparison.Ordinal) || text.StartsWith("Advisors enabled", StringComparison.Ordinal)
            || text.StartsWith("Advisor enabled", StringComparison.Ordinal)) return AdvisorState.On;
        if (text.StartsWith("Advisor is disabled", StringComparison.Ordinal) || text.StartsWith("Advisor disabled", StringComparison.Ordinal)) return AdvisorState.Off;
        return text.StartsWith("Advisor \"", StringComparison.Ordinal) ? AdvisorState.On : AdvisorState.Unknown;
    }

    /// <summary>The model named in "Advisor is enabled (provider/id). …", if any.</summary>
    public static string? AdvisorModel(string text) =>
        text.StartsWith("Advisor is enabled (", StringComparison.Ordinal) && text.IndexOf(')') is var close and > 20 ? text[20..close] : null;

    /// <summary>A number with thousands separators ("131,072").</summary>
    public static string Tokens(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"131k", "1.2M" for tight places (the ring's tooltip keeps the exact number).</summary>
    public static string ShortTokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString(n >= 10_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1_000.0).ToString(n >= 10_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static List<string> Lines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    private static long? Number(List<string> lines, string prefix) =>
        lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) is { } l
        && long.TryParse(l[prefix.Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? ValueAfter(string text, string prefix) =>
        Lines(text).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) is { } l && l[prefix.Length..].Trim() is { Length: > 0 } v ? v : null;

    private static string? AfterColon(string line) => line.IndexOf(": ", StringComparison.Ordinal) is var i and > 0 ? line[(i + 2)..] : null;
}
