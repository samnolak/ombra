using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// Address of a team server (§1, §6.2): IP mode = <c>https://&lt;ip&gt;[:port]</c> + SHA-256 SPKI pin (TLS 1.3 only, the chain
/// is not consulted, a mismatch is a refusal without "continue anyway"); domain mode = ordinary chain validation.
/// The pins of each team's verified roster head (current + next) replace the link pin with <see cref="TrustRosterPins"/>.
/// </summary>
public sealed class ServerEndpoint
{
    private readonly object _lock = new();
    private readonly HashSet<string> _initialPins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _rosterPins = new(StringComparer.Ordinal);
    private volatile HashSet<string> _pins = new(StringComparer.Ordinal);

    private ServerEndpoint(Uri baseUri, bool ipMode)
    {
        BaseUri = baseUri;
        IpMode = ipMode;
    }

    public Uri BaseUri { get; }
    public bool IpMode { get; }
    public IReadOnlyCollection<string> Pins => _pins.ToList();

    /// <summary>The trusted set shrank (a pin was revoked): pooled connections must be dropped.</summary>
    public event Action? PinsRevoked;
    /// <summary>SPKI hash of the last certificate presented (for diagnostics/settings), lowercase hex.</summary>
    public string? LastPresentedPin { get; private set; }

    /// <param name="address">IP or host, optionally with scheme and port (<c>203.0.113.7</c>, <c>https://203.0.113.7:8443</c>, <c>team.example.com</c>).</param>
    /// <param name="pin">SHA-256 of the server's SPKI, 64 hex characters; required for an IP address, not used with a domain.</param>
    public static ServerEndpoint Parse(string address, string? pin)
    {
        var a = address.Trim();
        if (a.Length == 0) throw new FormatException("server address is empty");
        if (!a.Contains("://", StringComparison.Ordinal)) a = "https://" + a;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/" || uri.Query.Length > 0)
            throw new FormatException("server address must be an https host or IP, optionally with a port");
        var ip = IPAddress.TryParse(uri.Host.Trim('[', ']'), out _);
        var e = new ServerEndpoint(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), ip);
        if (ip)
        {
            if (string.IsNullOrWhiteSpace(pin)) throw new FormatException("an IP address needs the server's pin (SPKI SHA-256)");
            e._initialPins.Add(NormalizePin(pin));
            e._pins = new HashSet<string>(e._initialPins, StringComparer.Ordinal);
        }
        else if (!string.IsNullOrWhiteSpace(pin))
        {
            throw new FormatException("a pin is used only with an IP address; a domain is checked by its certificate chain");
        }
        return e;
    }

    public static string NormalizePin(string pin)
    {
        var p = new string(pin.Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray()).ToLowerInvariant();
        if (p.Length != 64 || !p.All(Uri.IsHexDigit)) throw new FormatException("pin must be 64 hex characters (SHA-256 of the SPKI)");
        return p;
    }

    /// <summary>
    /// §6.2: the verified roster head of <paramref name="teamId"/> pins the current and the next SPKI. Its set replaces
    /// what that team trusted before (a SPKI removed by the admin is no longer accepted). With several teams on one
    /// server the trusted set is the union of their current heads; once any roster lists pins, the link pin alone no
    /// longer counts. Shrinking raises <see cref="PinsRevoked"/>.
    /// </summary>
    public void TrustRosterPins(string teamId, IEnumerable<string> pins)
    {
        if (!IpMode) return;
        var set = new HashSet<string>(pins.Select(NormalizePin), StringComparer.Ordinal);
        bool shrank;
        lock (_lock)
        {
            _rosterPins[teamId] = set;
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in _rosterPins.Values) next.UnionWith(s);
            if (next.Count == 0) next.UnionWith(_initialPins);
            shrank = _pins.Any(p => !next.Contains(p));
            _pins = next;
        }
        if (shrank) PinsRevoked?.Invoke();
    }

    public static string SpkiPin(X509Certificate2 cert) => Convert.ToHexStringLower(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    private bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        if (!IpMode) return errors == SslPolicyErrors.None;
        using var c = new X509Certificate2(certificate);
        var presented = SpkiPin(c);
        LastPresentedPin = presented;
        return _pins.Contains(presented);
    }

    private SslClientAuthenticationOptions Ssl() => new()
    {
        EnabledSslProtocols = IpMode ? SslProtocols.Tls13 : SslProtocols.None,
        RemoteCertificateValidationCallback = (_, cert, _, errors) => Validate(cert, errors),
    };

    /// <summary>An HttpClient bound to this endpoint and its pin set (no proxy: the pin must be checked end to end).</summary>
    public HttpClient CreateHttpClient(TimeSpan? timeout = null) =>
        new(new SocketsHttpHandler
        {
            SslOptions = Ssl(),
            UseProxy = !IpMode,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            BaseAddress = BaseUri,
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
        };

    public void ConfigureWebSocket(ClientWebSocketOptions o)
    {
        o.RemoteCertificateValidationCallback = (_, cert, _, errors) => Validate(cert, errors);
        if (IpMode) o.Proxy = null;
    }

    public Uri WebSocketUri(string path) => new(new UriBuilder(BaseUri) { Scheme = "wss" }.Uri, path);

    /// <summary>Address for display/logs (no secrets in it).</summary>
    public override string ToString() => BaseUri.GetLeftPart(UriPartial.Authority);
}
