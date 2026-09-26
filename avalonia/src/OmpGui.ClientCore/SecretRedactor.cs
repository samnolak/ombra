using System.Text.RegularExpressions;

namespace OmpGui.ClientCore;

/// <summary>
/// Removes secrets from text that leaves the machine (diagnostics). Two layers: exact values known to be secret
/// (e.g. every value in the client settings' <c>environment</c>) are replaced wherever they appear, and common
/// credential shapes (API keys, tokens, bearer headers, <c>KEY=value</c> / <c>"apiKey": "…"</c> pairs, URLs with
/// passwords) are masked even when nothing told us about them. The user's home folder becomes <c>~</c>.
/// </summary>
public sealed partial class SecretRedactor
{
    public const string Mask = "[redacted]";
    private readonly List<string> _known;
    private readonly string? _home;

    public SecretRedactor(IEnumerable<string?>? knownSecrets = null, string? home = null)
    {
        // Longest first, so a secret that contains another is replaced whole. Very short values would mask ordinary text.
        _known = (knownSecrets ?? []).Where(s => s is { Length: >= 6 }).Select(s => s!).Distinct().OrderByDescending(s => s.Length).ToList();
        _home = home is { Length: > 1 } h ? h.TrimEnd('/', '\\') : null;
    }

    public string Redact(string text)
    {
        foreach (var secret in _known) text = text.Replace(secret, Mask, StringComparison.Ordinal);
        text = KnownTokens().Replace(text, Mask);
        text = Bearer().Replace(text, m => m.Groups[1].Value + Mask);
        text = SecretAssignment().Replace(text, m => m.Groups[1].Value + Mask);
        text = SecretJson().Replace(text, m => m.Groups[1].Value + Mask + "\"");
        text = UrlCredentials().Replace(text, m => m.Groups[1].Value + Mask + "@");
        text = ApiKeyArgument().Replace(text, m => m.Groups[1].Value + Mask);
        if (_home is not null) text = text.Replace(_home, "~", StringComparison.Ordinal);
        return text;
    }

    /// <summary>Provider key formats: OpenAI / Anthropic (sk-…), GitHub, Slack, AWS access key ids, Google API keys, JWTs.</summary>
    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|(?:AKIA|ASIA)[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})")]
    private static partial Regex KnownTokens();

    [GeneratedRegex(@"(?i)\b((?:bearer|basic|token)\s+)[A-Za-z0-9_\-\.=+/]{8,}")]
    private static partial Regex Bearer();

    /// <summary><c>ANTHROPIC_API_KEY=…</c>, <c>password: …</c>, <c>x-api-key: …</c>.</summary>
    [GeneratedRegex(@"(?i)\b([A-Z0-9_\-]*(?:API[_\-]?KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIALS?|AUTHORIZATION)[A-Z0-9_\-]*\s*[=:]\s*)(?!\[redacted\])[^\s""',;]{8,}")]
    private static partial Regex SecretAssignment();

    /// <summary><c>"apiKey": "…"</c> and similar JSON / YAML-in-JSON pairs.</summary>
    [GeneratedRegex(@"(?i)(""[A-Za-z0-9_\-]*(?:api[_\-]?key|token|secret|password|credential|authorization)[A-Za-z0-9_\-]*""\s*:\s*"")(?!\[redacted\])[^""]{3,}""")]
    private static partial Regex SecretJson();

    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.\-]*://[^\s/:@]+:)[^\s/@]+@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)(--api-key(?:=|\s+))(?!\[redacted\])\S+")]
    private static partial Regex ApiKeyArgument();
}
