using System.Buffers;
using System.Net;

namespace OmpGui.ClientCore.Network;

/// <summary>
/// The destinations strict network privacy lets omp reach (<see cref="PrivacyProxy"/> asks for every connection): exact
/// host names, or <c>*.example.com</c> for any subdomain of example.com but not example.com itself (a wildcard for a
/// provider's regional hosts must not also open its apex, often another service). Matching ignores case and a trailing
/// dot (both name the same DNS host). An IP literal matches only the same address however it is spelled (<c>[::1]</c>,
/// <c>0:0:0:0:0:0:0:1</c>); a wildcard never matches an address. Immutable: the app swaps in a new list when the user's
/// providers change.
/// </summary>
public sealed class HostAllowlist
{
    private static readonly SearchValues<char> LabelChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-");

    /// <summary>What an IPv6 literal can hold: hex digits, separators, an embedded IPv4 address, a zone such as <c>%en0</c>.</summary>
    private static readonly SearchValues<char> Ipv6Chars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:.%");

    /// <summary>The exact entries: host names and addresses, canonical.</summary>
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);

    /// <summary>The wildcards as the suffix a match ends with: <c>*.example.com</c> is <c>.example.com</c>.</summary>
    private readonly List<string> _suffixes = [];

    /// <summary>
    /// The list of <paramref name="entries"/>. An invalid entry (<see cref="IsValidEntry"/>) is left out: a hand-edited
    /// settings file can make the list allow less, never more.
    /// </summary>
    public HostAllowlist(IEnumerable<string> entries)
    {
        var kept = new List<string>();
        foreach (var entry in entries)
        {
            if (Parse(entry) is not { } e) continue;
            if (e.Wildcard)
            {
                var suffix = "." + e.Host;
                if (_suffixes.Contains(suffix)) continue;
                _suffixes.Add(suffix);
            }
            else if (!_exact.Add(e.Host)) continue;
            kept.Add(e.Wildcard ? "*." + e.Host : e.Host);
        }
        Entries = kept.AsReadOnly();
    }

    /// <summary>
    /// The entries kept, once each, in the order given and in canonical spelling: lower case without the trailing dot, an
    /// address as <see cref="IPAddress.ToString"/> writes it (<c>[::1]</c> is <c>::1</c>).
    /// </summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>Whether omp may connect to <paramref name="host"/>, as a proxy request names it (an IPv6 address with or without brackets).</summary>
    public bool Matches(string host)
    {
        if (Canonicalize(host) is not { } h) return false;
        if (_exact.Contains(h.Value)) return true;
        if (h.IsAddress) return false;
        foreach (var suffix in _suffixes)
            if (h.Value.EndsWith(suffix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> can be in a list: a DNS host name (labels of letters, digits and inner hyphens),
    /// <c>*.</c> before one, or an IPv4 or IPv6 address (IPv6 with or without brackets). Never a URL, a path, a port,
    /// blanks, a bare <c>*</c>, or a wildcard anywhere but the whole first label.
    /// </summary>
    public static bool IsValidEntry(string entry) => Parse(entry) is not null;

    /// <summary>A host as compared: its canonical spelling, and whether it is an IP address.</summary>
    private readonly record struct Canonical(string Value, bool IsAddress);

    /// <summary>An entry as matched: its canonical host and whether it is a wildcard; null when it is not valid.</summary>
    private static (string Host, bool Wildcard)? Parse(string entry)
    {
        var wildcard = entry.StartsWith("*.", StringComparison.Ordinal);
        return Canonicalize(wildcard ? entry[2..] : entry) is { } host && !(wildcard && host.IsAddress) ? (host.Value, wildcard) : null;
    }

    /// <summary>
    /// <paramref name="host"/> in canonical spelling; null when it is neither a host name nor an IP address. A name that
    /// parses as an IPv4 address (<c>2130706433</c>, <c>0x7f.0.0.1</c>) is that address: it is where a connection to it goes.
    /// </summary>
    private static Canonical? Canonicalize(string host)
    {
        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (bare.Contains(':'))
            return !bare.AsSpan().ContainsAnyExcept(Ipv6Chars) && IPAddress.TryParse(bare, out var v6) ? new Canonical(v6.ToString(), true) : null;
        var name = host.EndsWith('.') ? host[..^1] : host;
        if (!IsHostName(name)) return null;
        return IPAddress.TryParse(name, out var v4) ? new Canonical(v4.ToString(), true) : new Canonical(name.ToLowerInvariant(), false);
    }

    /// <summary>DNS host name syntax: dot-separated labels of 1-63 letters, digits and inner hyphens, at most 253 characters in all.</summary>
    private static bool IsHostName(string name)
    {
        if (name.Length is 0 or > 253) return false;
        var rest = name.AsSpan();
        while (true)
        {
            var dot = rest.IndexOf('.');
            var label = dot < 0 ? rest : rest[..dot];
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || label.ContainsAnyExcept(LabelChars)) return false;
            if (dot < 0) return true;
            rest = rest[(dot + 1)..];
        }
    }
}
