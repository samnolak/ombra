using System.Security.Cryptography;

namespace Ombra.ClientCore.Browser;

/// <summary>
/// A navigation about to start (Augur v1): the embedded Nightly opens the connection to <see cref="Url"/>'s origin
/// (DNS, TCP, TLS; never an HTTP request) while the action that loads it is still on its way. Sent as Nightly's
/// Speculate command (Ombra.App's <c>NightlyProtocol.SpeculateJson</c>, which enforces the bounds below); its
/// <see cref="Id"/> cancels it. Only for an action already committed to that address: an agent's open, goto or anchor
/// click, or an address the user entered; never for forms, buttons without a resolved link, or downloads.
/// </summary>
/// <param name="Id">16 lowercase hex digits (<see cref="NewId"/>), one per intent.</param>
/// <param name="Url">An absolute http or https address without user info (<see cref="Eligible"/>).</param>
/// <param name="Source"><see cref="AgentOpen"/>, <see cref="AgentClick"/>, <see cref="AgentGoto"/> or <see cref="AddressCommit"/>.</param>
/// <param name="P">The raw probability (0–1) that the navigation follows; Nightly calibrates it per source.</param>
/// <param name="LeadMs">How long until the navigation starts (0–<see cref="MaxLeadMs"/> ms).</param>
/// <param name="TtlMs">How long the hint holds (<see cref="MinTtlMs"/>–<see cref="MaxTtlMs"/> ms).</param>
public sealed record SpeculationHint(string Id, Uri Url, string Source, double P, int LeadMs, int TtlMs)
{
    public const string AgentOpen = "agent_open", AgentClick = "agent_click", AgentGoto = "agent_goto", AddressCommit = "address_commit";
    public const int MaxLeadMs = 5000, MinTtlMs = 100, MaxTtlMs = 30000;
    /// <summary>How long a hint made by <see cref="For"/> holds.</summary>
    public const int DefaultTtlMs = 10000;

    /// <summary>
    /// A hint for <paramref name="url"/> with the source's prior (agent_open 0.98, 250 ms ahead; agent_click 0.90,
    /// 120 ms; agent_goto 0.98, 10 ms; address_commit 0.995, 20 ms); <paramref name="leadMs"/> (clamped to
    /// 0–<see cref="MaxLeadMs"/>) replaces the lead when the caller measured it (the pointer's remaining travel). Null
    /// when <paramref name="url"/> is not <see cref="Eligible"/>.
    /// </summary>
    public static SpeculationHint? For(string source, Uri url, int? leadMs = null)
    {
        var (p, lead) = source switch
        {
            AgentOpen => (0.98, 250),
            AgentClick => (0.90, 120),
            AgentGoto => (0.98, 10),
            AddressCommit => (0.995, 20),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a speculation source."),
        };
        return Eligible(url) ? new SpeculationHint(NewId(), url, source, p, Math.Clamp(leadMs ?? lead, 0, MaxLeadMs), DefaultTtlMs) : null;
    }

    /// <summary>Whether a connection may be opened for <paramref name="url"/>: absolute http or https with a host, no user info.</summary>
    public static bool Eligible(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp) && url.Host.Length > 0 && url.UserInfo.Length == 0;

    /// <summary>Whether <paramref name="id"/> is a hint id: 16 lowercase hex digits.</summary>
    public static bool IsId(string id) => id.Length == 16 && id.All(char.IsAsciiHexDigitLower);

    public static string NewId() => RandomNumberGenerator.GetHexString(16, lowercase: true);
}
