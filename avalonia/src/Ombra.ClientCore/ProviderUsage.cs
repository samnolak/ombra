using System.Globalization;
using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>
/// One limit window or quota a provider reported (omp's <c>UsageLimit</c>, pi-ai usage.ts): "Claude 5 Hour" at 42 %,
/// resetting at a time.
/// </summary>
/// <param name="UsedFraction">0..1 (above 1 is overage); null when the provider gave no fraction or ratio.</param>
/// <param name="Used">The used amount in <paramref name="Unit"/>, when reported.</param>
/// <param name="Limit">The allowance in <paramref name="Unit"/>, when reported.</param>
/// <param name="Unit">percent, tokens, requests, credits, usd, minutes, bytes or unknown.</param>
/// <param name="ResetVerb">omp's word before the countdown ("resets", or "regen" for rolling windows).</param>
/// <param name="WindowId">omp's window id ("5h", "7d", "monthly"), when the limit has a window.</param>
public sealed record ProviderUsageLimit(string Id, string Label, double? UsedFraction, double? Used, double? Limit, string Unit,
    DateTimeOffset? ResetsAt, string ResetVerb, string Status, string? WindowId = null);

/// <summary>One account's report for a provider (omp's <c>UsageReport</c>).</summary>
/// <param name="Account">The account's e-mail or id, when omp knows it.</param>
/// <param name="Plan">The subscription plan ("plus", "max"), when the provider says.</param>
/// <param name="Resets">The account's saved rate-limit resets, when the provider reports them (ChatGPT, Claude).</param>
public sealed record ProviderUsageReport(string Provider, string? Account, string? Plan, DateTimeOffset? FetchedAt, IReadOnlyList<ProviderUsageLimit> Limits,
    ProviderUsageResets? Resets = null);

/// <summary>
/// Saved ("banked") rate-limit resets a provider granted the account, spent on demand to refresh a used-up window
/// (omp's <c>UsageResetCredits</c>, summarized as its <c>summarizeUsageResetCredits</c> does for <c>/usage</c>).
/// </summary>
/// <param name="Saved">Resets banked, including ones that cannot be spent now.</param>
/// <param name="Usable">The ones the provider says can be spent now.</param>
/// <param name="SoonestExpiry">When the first one still to spend expires (else when the last one expired).</param>
/// <param name="Unavailable">omp's reason none can be spent now ("cooldown until …", "blocked by Claude 5h").</param>
public sealed record ProviderUsageResets(int Saved, int Usable, DateTimeOffset? SoonestExpiry, string? Unavailable);

/// <summary>An account <c>/usage reset</c> lists: <c>/usage reset &lt;Provider&gt;/&lt;CredentialId&gt;</c> spends one of its resets.</summary>
/// <param name="Label">omp's name for it: the e-mail (or account id), then " · " and the organization when there is one.</param>
public sealed record SavedResetAccount(string Provider, string CredentialId, string Label, bool Active);

/// <summary><c>get_session_stats</c>: what this session spent (session-stats.ts).</summary>
public sealed record SessionTokenStats(long Input, long Output, long CacheRead, long CacheWrite, long Total, double Cost, long PremiumRequests);

/// <summary>
/// Reads <c>omp usage --json</c> (cli/usage-cli.ts, omp 18.8.0): every signed-in account's limits as omp's own usage
/// endpoints report them (cached by omp, so asking again is cheap). Providers without a usage endpoint are absent.
/// </summary>
public static class ProviderUsage
{
    /// <summary>The CLI arguments that print the reports.</summary>
    public static readonly IReadOnlyList<string> CliArgs = ["usage", "--json"];

    /// <summary>The reports in the JSON, or null when it is not omp's usage payload.</summary>
    public static IReadOnlyList<ProviderUsageReport>? Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (doc.RootElement is not { ValueKind: JsonValueKind.Object } root
                || !root.TryGetProperty("reports", out var reports) || reports.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<ProviderUsageReport>();
            foreach (var r in reports.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object || Str(r, "provider") is not { Length: > 0 } provider) continue;
                var meta = r.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
                var hasMeta = meta.ValueKind == JsonValueKind.Object;
                var account = hasMeta ? Str(meta, "email") ?? Str(meta, "accountId") : null;
                var plan = hasMeta ? Str(meta, "planType") : null;
                var limits = new List<ProviderUsageLimit>();
                if (r.TryGetProperty("limits", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var l in ls.EnumerateArray())
                        if (ParseLimit(l) is { } limit) limits.Add(limit);
                if (limits.Count == 0) continue;
                var resets = r.TryGetProperty("resetCredits", out var rc) && rc.ValueKind == JsonValueKind.Object ? ParseResets(rc) : null;
                list.Add(new ProviderUsageReport(provider, account, plan, Time(Num(r, "fetchedAt")), limits, resets));
            }
            return list;
        }
    }

    /// <summary>omp's <c>summarizeUsageResetCredits</c> (pi-tui usage-display.ts, omp 18.8.0).</summary>
    private static ProviderUsageResets? ParseResets(JsonElement rc)
    {
        if (Num(rc, "availableCount") is not { } available) return null;
        var saved = Math.Max(0, (int)Math.Truncate(available));
        var usable = Math.Max(0, (int)Math.Truncate(Num(rc, "redeemableCount") ?? available));
        var credits = rc.TryGetProperty("credits", out var cs) && cs.ValueKind == JsonValueKind.Array
            ? cs.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).ToList() : [];
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? soonest = null, latestExpired = null;
        foreach (var c in credits)
        {
            if (Num(c, "remainingCount") == 0 || Str(c, "status") == "redeemed" || IsoTime(Str(c, "expiresAt")) is not { } at) continue;
            if (at > now) { if (soonest is null || at < soonest) soonest = at; }
            else if (latestExpired is null || at > latestExpired) latestExpired = at;
        }
        // The credit the next spend would use: the one the provider picked, else the first usable, else the first
        var next = Str(rc, "nextCreditId");
        var selected = next is not null ? credits.FirstOrDefault(c => Str(c, "id") == next)
            : credits.FirstOrDefault(c => !(c.TryGetProperty("usable", out var u) && u.ValueKind == JsonValueKind.False));
        if (next is null && selected.ValueKind != JsonValueKind.Object) selected = credits.FirstOrDefault();
        var hasSelected = selected.ValueKind == JsonValueKind.Object;
        var blocking = hasSelected && selected.TryGetProperty("blocking", out var b) && b.ValueKind == JsonValueKind.Array
            ? b.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => ResetWindow(x.GetString()!)).ToList() : [];
        var status = hasSelected ? Str(selected, "status") : null;
        var reason = Str(rc, "reason")
            ?? (Str(rc, "cooldownUntil") is { } cooldown ? "cooldown until " + cooldown : null)
            ?? (blocking.Count > 0 ? "blocked by " + string.Join(", ", blocking) : null)
            ?? (status is not null && status != "available" ? status : null)
            ?? (rc.TryGetProperty("eligible", out var el) && el.ValueKind == JsonValueKind.False ? "not eligible" : null)
            ?? (saved > 0 && usable == 0 ? "not usable right now" : null);
        return new ProviderUsageResets(saved, usable, soonest ?? latestExpired, reason);
    }

    /// <summary>omp's <c>formatUsageResetWindow</c>: a window a saved reset covers, by name.</summary>
    private static string ResetWindow(string id) => id switch
    {
        "anthropic:5h" => "Claude 5h",
        "anthropic:7d" => "Claude weekly",
        "anthropic:7d:opus" => "Claude Opus weekly",
        "anthropic:7d:sonnet" => "Claude Sonnet weekly",
        _ => id,
    };

    private static DateTimeOffset? IsoTime(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>The command that lists the accounts with saved resets and their ids (omp's builtin, over RPC).</summary>
    public const string SavedResetsCommand = "/usage reset";

    /// <summary>The command that spends one of <paramref name="account"/>'s saved resets.</summary>
    public static string SpendResetCommand(SavedResetAccount account) => $"/usage reset {account.Provider}/{account.CredentialId}";

    /// <summary>
    /// The accounts in <c>/usage reset</c>'s list (slash-commands/builtin-session.ts, omp 18.8.0):
    /// <c>- dev@example.com [Codex · openai-codex/3]: 2 saved, 1 usable now (active)</c>.
    /// </summary>
    public static IReadOnlyList<SavedResetAccount> ParseSavedResetAccounts(string output) =>
        [.. System.Text.RegularExpressions.Regex.Matches(output,
                @"^- (?<label>.+?) \[[^\]\n]* · (?<provider>[a-z0-9-]+)/(?<id>\d+)\]: [^\n]*?(?<active> \(active\))?\r?$",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => new SavedResetAccount(m.Groups["provider"].Value, m.Groups["id"].Value, m.Groups["label"].Value, m.Groups["active"].Success))];

    /// <summary>
    /// The listed account a usage report is about: same provider, and omp's label is the report's account (with its
    /// organization after " · "); a report without an account matches when its provider has only one.
    /// </summary>
    public static SavedResetAccount? ResetAccountFor(ProviderUsageReport report, IReadOnlyList<SavedResetAccount> accounts)
    {
        var mine = accounts.Where(a => a.Provider == report.Provider).ToList();
        if (report.Account is not { } account) return mine.Count == 1 ? mine[0] : null;
        var matching = mine.Where(a => a.Label == account || a.Label.StartsWith(account + " · ", StringComparison.Ordinal)).ToList();
        return matching.Count == 1 ? matching[0] : null;
    }

    /// <summary>True when omp's answer to <see cref="SpendResetCommand"/> says the reset was applied.</summary>
    public static bool ResetApplied(string output) => output.StartsWith("Reset applied for ", StringComparison.Ordinal);

    private static ProviderUsageLimit? ParseLimit(JsonElement l)
    {
        if (l.ValueKind != JsonValueKind.Object || Str(l, "label") is not { Length: > 0 } label) return null;
        var amount = l.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var hasAmount = amount.ValueKind == JsonValueKind.Object;
        var unit = (hasAmount ? Str(amount, "unit") : null) ?? "unknown";
        double? used = hasAmount ? Num(amount, "used") : null, max = hasAmount ? Num(amount, "limit") : null;
        // The precedence of pi-ai's resolveUsedFraction: explicit fraction, used/limit, percent, inverted remaining
        double? fraction = hasAmount ? Num(amount, "usedFraction") : null;
        fraction ??= used is { } u && max is > 0 ? u / max.Value : null;
        fraction ??= unit == "percent" && used is { } p ? p / 100 : null;
        fraction ??= hasAmount && Num(amount, "remainingFraction") is { } rf ? Math.Max(0, 1 - rf) : null;
        var window = l.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object ? w : default;
        var hasWindow = window.ValueKind == JsonValueKind.Object;
        return new ProviderUsageLimit(Str(l, "id") ?? label, label, fraction, used, max, unit,
            hasWindow ? Time(Num(window, "resetsAt")) : null, (hasWindow ? Str(window, "resetLabel") : null) ?? "resets",
            Str(l, "status") ?? "unknown", hasWindow ? Str(window, "id") : null);
    }

    /// <summary>
    /// The meter's name in sentence case: well-known windows by what they are ("5-hour limit", "Weekly limit"), keeping
    /// a model tier omp names in parentheses ("Weekly limit (Fable)"); otherwise omp's label without the provider's
    /// name in front ("Claude 5 Hour" under Claude).
    /// </summary>
    public static string DisplayLabel(ProviderUsageLimit l, string providerName)
    {
        var tier = l.Label.LastIndexOf(" (", StringComparison.Ordinal) is var p and > 0 && l.Label.EndsWith(')') ? l.Label[p..] : "";
        var window = l.WindowId switch
        {
            "5h" => "5-hour limit",
            "1d" or "24h" or "daily" => "Daily limit",
            "7d" or "weekly" => "Weekly limit",
            "30d" or "monthly" => "Monthly limit",
            _ => null,
        };
        if (window is not null) return window + tier;
        var label = l.Label.StartsWith(providerName + " ", StringComparison.OrdinalIgnoreCase) && l.Label.Length > providerName.Length + 1
            ? l.Label[(providerName.Length + 1)..] : l.Label;
        return label.Length > 0 ? char.ToUpperInvariant(label[0]) + label[1..] : label;
    }

    /// <summary>"resets in 2h 10m", "resets in 3d 4h", "resets in 5m"; "resets soon" under a minute.</summary>
    public static string ResetsIn(DateTimeOffset at, DateTimeOffset now, string verb = "resets")
    {
        var left = at - now;
        return left < TimeSpan.FromMinutes(1) ? verb + " soon" : $"{verb} in {Span(left)}";
    }

    /// <summary>A time left in its two largest units: "3d 4h", "2h 10m", "45m".</summary>
    public static string Span(TimeSpan left) =>
        left.TotalDays >= 1 ? $"{(int)left.TotalDays}d" + (left.Hours > 0 ? $" {left.Hours}h" : "")
        : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h" + (left.Minutes > 0 ? $" {left.Minutes}m" : "")
        : $"{(int)left.TotalMinutes}m";

    /// <summary>The amount when there is no fraction to draw: "$12.40 used", "1,200 of 5,000 requests".</summary>
    public static string Amount(ProviderUsageLimit l)
    {
        if (l.UsedFraction is { } f) return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(f * 100):0}% used");
        if (l.Used is not { } used) return "";
        string N(double v) => l.Unit == "usd" ? v.ToString("$#,0.00", CultureInfo.InvariantCulture) : v.ToString("#,0.##", CultureInfo.InvariantCulture);
        var unit = l.Unit is "usd" or "unknown" or "percent" ? "" : " " + l.Unit;
        return l.Limit is { } max ? $"{N(used)} of {N(max)}{unit}" : $"{N(used)}{unit} used";
    }

    /// <summary>A provider id as people know it ("openai-codex" → "ChatGPT").</summary>
    public static string ProviderName(string id) => id switch
    {
        "anthropic" => "Claude",
        "openai-codex" => "ChatGPT",
        "github-copilot" => "GitHub Copilot",
        "google-gemini-cli" or "gemini" => "Gemini",
        "google-antigravity" => "Antigravity",
        "cursor" => "Cursor",
        "kimi" or "kimi-code" => "Kimi",
        "zai" => "Z.ai",
        "minimax-code" => "MiniMax",
        "xai-oauth" or "xai" => "xAI",
        "ollama" => "Ollama",
        _ => id.Length > 0 ? char.ToUpperInvariant(id[0]) + id[1..].Replace('-', ' ') : id,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    private static DateTimeOffset? Time(double? ms) => ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms.Value) : null;
}
