using System.Text.RegularExpressions;

namespace Ombra.ClientCore;

/// <summary>
/// <c>/skill:&lt;name&gt;</c> in a message, as omp 18.12 reads it (pi-tui <c>prompt/skill-tokens.ts</c>): a token
/// between whitespace or line edges, at the start or anywhere in the text unless the message is another slash command
/// or a <c>!</c>/<c>$</c> shell line. omp runs the first such token as the skill (its body becomes the message, the rest
/// of the text its arguments, images kept) and only over <c>prompt</c>; <c>steer</c>/<c>follow_up</c> pass the text on
/// as it is, and a second token would reach the model as plain text.
/// </summary>
public static partial class SkillTokens
{
    [GeneratedRegex(@"(^|\s)/skill:([^\s/]+(?:/[^\s/]+)?)(?=\s|$)")]
    private static partial Regex Token();

    /// <summary>omp runs a skill for this message (sent as a prompt).</summary>
    public static bool HasInvocation(string text) => Allowed(text) && Token().IsMatch(text);

    /// <summary>
    /// The message for omp: the first skill stays a skill; each further one becomes a <c>skill://name</c> reference
    /// the model reads (omp runs one skill per message), so several skills picked for one message all reach it.
    /// </summary>
    public static string ForOmp(string text)
    {
        if (!Allowed(text)) return text;
        var seen = 0;
        return Token().Replace(text, m => ++seen == 1 ? m.Value : m.Groups[1].Value + "skill://" + m.Groups[2].Value);
    }

    private static bool Allowed(string text)
    {
        var t = text.TrimStart();
        if (t.StartsWith("/skill:", StringComparison.Ordinal)) return true;
        if (t.StartsWith('/')) return false;
        return !LocalExecution(t);
    }

    // omp's startsWithLocalExecutionPrefix: "!cmd", "$ cmd" / "$$ cmd" (not "${…}")
    private static bool LocalExecution(string t)
    {
        if (t.StartsWith('!')) return true;
        if (t.Length == 0 || t[0] != '$') return false;
        if (t.Length > 1 && t[1] == '{') return false;
        var sigil = t.Length > 1 && t[1] == '$' ? 2 : 1;
        return sigil >= t.Length || t[sigil] is ' ' or '\t' or '\n' or '\r';
    }
}
