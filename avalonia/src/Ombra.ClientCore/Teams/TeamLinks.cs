using System.Web;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// Invite link (§3.4): <c>ombra://join?s=&lt;endpoint&gt;&amp;pin=&lt;spki&gt;&amp;t=&lt;teamId&gt;&amp;h=&lt;version&gt;.&lt;hash&gt;&amp;a=&lt;inviter fingerprint&gt;&amp;i=&lt;inviteId&gt;#k=&lt;secret&gt;</c>.
/// The secret is only in the fragment; <see cref="ToString"/> never prints it (R2-06) — share <see cref="ToShareableString"/>.
/// </summary>
public sealed class InviteLink
{
    public InviteLink(string server, string? pin, string teamId, ulong headVersion, string headHash, string inviterFingerprint, Guid inviteId, byte[] secret)
    {
        if (secret.Length != 32) throw new ArgumentException("invite secret must be 256 bits", nameof(secret));
        Server = server;
        Pin = pin;
        TeamId = teamId;
        HeadVersion = headVersion;
        HeadHash = headHash;
        InviterFingerprint = inviterFingerprint;
        InviteId = inviteId;
        Secret = secret;
    }

    public string Server { get; }
    public string? Pin { get; }
    public string TeamId { get; }
    public ulong HeadVersion { get; }
    public string HeadHash { get; }
    public string InviterFingerprint { get; }
    public Guid InviteId { get; }
    public byte[] Secret { get; }

    public ServerEndpoint Endpoint() => ServerEndpoint.Parse(Server, Pin);

    private string Query() =>
        $"ombra://join?s={Uri.EscapeDataString(Server)}{(Pin is null ? "" : "&pin=" + Pin)}&t={TeamId}&h={HeadVersion}.{HeadHash}&a={InviterFingerprint}&i={InviteId:D}";

    /// <summary>The full link for the trusted channel to the invitee (contains the secret).</summary>
    public string ToShareableString() => Query() + "#k=" + Base64Url(Secret);

    /// <summary>Safe for logs and UI lists: without the secret.</summary>
    public override string ToString() => Query();

    public static InviteLink Parse(string link)
    {
        var (q, fragment) = Split(link, "join");
        var k = fragment.StartsWith("k=", StringComparison.Ordinal) ? FromBase64Url(fragment[2..]) : throw new FormatException("invite link has no secret (#k=)");
        var h = Required(q, "h").Split('.', 2);
        if (h.Length != 2 || !ulong.TryParse(h[0], out var hv)) throw new FormatException("invite link: bad h");
        return new InviteLink(Required(q, "s"), q["pin"], Hex32(Required(q, "t")), hv, Hex32(h[1]), Hex32(Required(q, "a")),
            Guid.TryParse(Required(q, "i"), out var i) ? i : throw new FormatException("invite link: bad i"), k);
    }

    internal static (System.Collections.Specialized.NameValueCollection Query, string Fragment) Split(string link, string action)
    {
        var s = link.Trim();
        var hash = s.IndexOf('#');
        var fragment = hash >= 0 ? s[(hash + 1)..] : "";
        var head = hash >= 0 ? s[..hash] : s;
        var prefix = $"ombra://{action}?";
        if (!head.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new FormatException($"not an ombra://{action} link");
        return (HttpUtility.ParseQueryString(head[prefix.Length..]), fragment);
    }

    internal static string Required(System.Collections.Specialized.NameValueCollection q, string key) =>
        q[key] is { Length: > 0 } v ? v : throw new FormatException($"link: missing {key}");

    private static string Hex32(string s) =>
        s.Length == 64 && s.All(Uri.IsHexDigit) ? s.ToLowerInvariant() : throw new FormatException("link: expected 64 hex characters");

    internal static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=')); }
        catch (FormatException) { throw new FormatException("link: bad base64url"); }
    }
}

/// <summary>
/// Pairing link shown by an existing device (§2.3, SAS path): <c>ombra://pair?s=&lt;endpoint&gt;&amp;pin=&lt;spki&gt;&amp;c=&lt;code&gt;</c>.
/// The code only locates the relay session; nothing in it is secret — the six words decide.
/// </summary>
public sealed record PairingLink(string Server, string? Pin, string Code)
{
    public ServerEndpoint Endpoint() => ServerEndpoint.Parse(Server, Pin);

    public override string ToString() => $"ombra://pair?s={Uri.EscapeDataString(Server)}{(Pin is null ? "" : "&pin=" + Pin)}&c={Code}";

    public static PairingLink Parse(string link)
    {
        var (q, _) = InviteLink.Split(link, "pair");
        return new PairingLink(InviteLink.Required(q, "s"), q["pin"], InviteLink.Required(q, "c"));
    }
}
