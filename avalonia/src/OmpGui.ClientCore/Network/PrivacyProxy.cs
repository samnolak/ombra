using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace OmpGui.ClientCore.Network;

/// <summary>A connection <see cref="PrivacyProxy"/> refused: where omp tried to go, never what it sent.</summary>
public sealed record BlockedConnection(string Host, int Port, DateTimeOffset At);

/// <summary>
/// The user's own network proxy (a corporate one). Strict network privacy puts <see cref="PrivacyProxy"/> in its place
/// in omp's environment, so the app's proxy goes through it instead, as omp would have. Read from the conventional
/// variables, https first (omp's traffic is HTTPS): HTTPS_PROXY, https_proxy, HTTP_PROXY, http_proxy, ALL_PROXY,
/// all_proxy. The first value that is an http:// or https:// address wins (a value without a scheme is http://, as HTTP
/// clients read it); one that is not (a socks:// proxy, a typo) is skipped rather than guessed at. NO_PROXY (else
/// no_proxy) lists the destinations reached directly.
/// </summary>
public sealed record UpstreamProxy(Uri Address, IReadOnlyList<string> Bypass)
{
    private static readonly string[] AddressVariables = ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"];
    private static readonly char[] BypassSeparators = [',', ' ', '\t', '\r', '\n'];

    /// <summary>The user's proxy as <paramref name="env"/> (a variable's value, null when unset) names it; null when none does.</summary>
    public static UpstreamProxy? FromEnvironment(Func<string, string?> env)
    {
        foreach (var variable in AddressVariables)
        {
            if (Parse(env(variable)) is not { } address) continue;
            var bypass = env("NO_PROXY");
            if (string.IsNullOrWhiteSpace(bypass)) bypass = env("no_proxy");
            return new UpstreamProxy(address, (bypass ?? "").Split(BypassSeparators, StringSplitOptions.RemoveEmptyEntries));
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="host"/> is reached directly, by the NO_PROXY conventions: <c>*</c> is every host,
    /// <c>example.com</c> and <c>.example.com</c> are that domain and its subdomains, an IP address is only itself.
    /// </summary>
    public bool Bypasses(string host)
    {
        var target = Bare(host);
        var address = IPAddress.TryParse(target, out var ip) ? ip : null;
        foreach (var raw in Bypass)
        {
            if (raw == "*") return true;
            var entry = Bare(raw);
            if (address is not null)
            {
                if (IPAddress.TryParse(entry, out var other) && other.Equals(address)) return true;
                continue;
            }
            var domain = entry.TrimStart('.');
            if (domain.Length > 0 && target.EndsWith(domain, StringComparison.Ordinal)
                && (target.Length == domain.Length || target[^(domain.Length + 1)] == '.'))
                return true;
        }
        return false;
    }

    /// <summary>The address without its credentials: a record prints its members, and this one may reach a log.</summary>
    public override string ToString() =>
        $"UpstreamProxy {{ Address = {new UriBuilder(Address) { UserName = "", Password = "" }.Uri}, Bypass = [{string.Join(", ", Bypass)}] }}";

    /// <summary>A host or NO_PROXY entry as compared: lower case, without a trailing dot or IPv6 brackets.</summary>
    private static string Bare(string host) => host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();

    /// <summary>A proxy address as HTTP clients read the variable; null when it is not an http:// or https:// proxy.</summary>
    private static Uri? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }
}

/// <summary>
/// Strict network privacy: the proxy every omp process is told to use (PI_PROXY and the HTTP(S)_PROXY family), so omp
/// reaches only the destinations the app allows. At every start omp fetches the public model lists of providers the user
/// never added, and the models.dev mirror; its settings can turn providers off, but only a proxy makes "nothing else is
/// contacted" hold for whatever omp does. It listens on 127.0.0.1 and speaks what Bun sends a proxy: CONNECT for HTTPS
/// (a byte tunnel: TLS stays end to end between omp and the server, nothing is decrypted) and absolute-form requests for
/// plain HTTP. Every request must carry the credentials in <see cref="Address"/>, a random token new for each proxy, so no
/// other program on the machine can use it. When the user has a proxy of their own (<see cref="UpstreamProxy"/>), allowed
/// connections go through it, as omp's would have.
/// </summary>
public sealed class PrivacyProxy : IAsyncDisposable
{
    private const string UserName = "ompgui";

    /// <summary>The largest request head read: a CONNECT from Bun is a few hundred bytes.</summary>
    private const int MaxHead = 16 * 1024;

    private const string Challenge = "Proxy-Authenticate: Basic realm=\"OMP GUI\"\r\n";

    /// <summary>A destination (or the user's proxy) not reached in this time gets a 504.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a refused client's remaining bytes are read before its connection closes.</summary>
    private static readonly TimeSpan LingerTimeout = TimeSpan.FromSeconds(2);

    private static readonly byte[] Established = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();

    /// <summary>Headers meant for this proxy or this one connection: never sent on.</summary>
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase) { "Proxy-Authorization", "Proxy-Connection", "Connection", "Keep-Alive" };

    /// <summary>What a host name in a request may hold (an IPv6 address comes in brackets and is parsed instead).</summary>
    private static readonly SearchValues<char> HostChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_");

    private readonly TcpListener _listener;
    private readonly Func<string, int, bool> _isAllowed;
    private readonly UpstreamProxy? _upstream;

    /// <summary><c>Basic …</c> for the user's proxy, from its address; null when it has no credentials.</summary>
    private readonly string? _upstreamAuthorization;

    private readonly TimeProvider _clock;

    /// <summary><c>ompgui:&lt;token&gt;</c>, what a client's Basic credentials must decode to.</summary>
    private readonly byte[] _credentials;

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();

    /// <summary>The client connections being served (tunnels included), so disposing can wait for them to close.</summary>
    private readonly HashSet<Task> _connections = [];

    private readonly Task _accepting;
    private int _disposed;

    private PrivacyProxy(TcpListener listener, Func<string, int, bool> isAllowed, UpstreamProxy? upstream, TimeProvider clock)
    {
        _listener = listener;
        _isAllowed = isAllowed;
        _upstream = upstream;
        _upstreamAuthorization = upstream is null ? null : BasicCredentials(upstream.Address);
        _clock = clock;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _credentials = Encoding.ASCII.GetBytes(UserName + ":" + token);
        Address = new Uri($"http://{UserName}:{token}@127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        _accepting = AcceptAsync();
    }

    /// <summary>
    /// Starts listening on 127.0.0.1 (a port the OS picks). <paramref name="isAllowed"/> decides each connection by the
    /// host as the client named it (an IPv6 address without brackets) and the port; it is asked anew every time, so the
    /// app can swap the rules it reads while the proxy runs. <paramref name="upstream"/> is the user's own proxy, if any.
    /// </summary>
    public static PrivacyProxy Start(Func<string, int, bool> isAllowed, UpstreamProxy? upstream = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(isAllowed);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new PrivacyProxy(listener, isAllowed, upstream, clock ?? TimeProvider.System);
    }

    /// <summary>
    /// <c>http://ompgui:&lt;token&gt;@127.0.0.1:&lt;port&gt;/</c>, the value of omp's proxy variables. It holds the token:
    /// it goes into omp's environment only, never into a log or on screen.
    /// </summary>
    public Uri Address { get; }

    /// <summary>
    /// A destination was refused (raised on a pool thread, before the client gets its 403). Not raised for a client
    /// without the credentials, which never got to name one.
    /// </summary>
    public event Action<BlockedConnection>? Blocked;

    /// <summary>Stops listening and closes every open tunnel; done when all are closed. Calling it again does nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Task[] open;
        lock (_gate) open = [.. _connections];
        await Task.WhenAll(open).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false); }
            // Stopped (a stopped listener throws InvalidOperationException, ObjectDisposedException among them)
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException) { return; }
            // A client that went away before it was accepted; when stopping, the loop's condition ends it
            catch (SocketException) { continue; }
            var connection = Task.Run(() => ServeAsync(socket));
            lock (_gate) _connections.Add(connection);
            _ = connection.ContinueWith(done => { lock (_gate) _connections.Remove(done); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>One client connection: its request head, the credentials, the destination's check, then a tunnel or a refusal.</summary>
    private async Task ServeAsync(Socket socket)
    {
        var ct = _stop.Token;
        socket.NoDelay = true;
        await using var client = new Leg(socket, new NetworkStream(socket, ownsSocket: true));
        byte[]? buffer = ArrayPool<byte>.Shared.Rent(MaxHead);
        Leg? target = null;
        try
        {
            var (end, filled) = await ReadHeadAsync(client.Stream, buffer, ct).ConfigureAwait(false);
            if (end < 0)
            {
                if (filled == MaxHead)
                    await RefuseAsync(client, "431 Request Header Fields Too Large", "The request head is over 16 KiB.", ct).ConfigureAwait(false);
                return;
            }
            var request = Request.Parse(Encoding.Latin1.GetString(buffer, 0, end - 4));
            if (request is null)
            {
                await RefuseAsync(client, "400 Bad Request", "Not an HTTP request.", ct).ConfigureAwait(false);
                return;
            }
            if (!Authorized(request.Header("Proxy-Authorization")))
            {
                await RefuseAsync(client, "407 Proxy Authentication Required", "This proxy needs its credentials.", ct, Challenge).ConfigureAwait(false);
                return;
            }
            var tunnel = request.Method == "CONNECT";
            var destination = tunnel ? Destination.FromAuthority(request.Target, null, "") : Destination.FromUrl(request.Target);
            if (destination is null)
            {
                await RefuseAsync(client, "400 Bad Request", "Only CONNECT host:port and http:// absolute-form requests are served.", ct).ConfigureAwait(false);
                return;
            }
            if (!_isAllowed(destination.Host, destination.Port))
            {
                Blocked?.Invoke(new BlockedConnection(destination.Host, destination.Port, _clock.GetUtcNow()));
                await RefuseAsync(client, "403 Forbidden", "Blocked by OMP GUI strict network privacy: " + destination.Host, ct).ConfigureAwait(false);
                return;
            }
            var via = _upstream is { } upstream && !upstream.Bypasses(destination.Host) ? upstream : null;
            (Leg Target, byte[]? Early) opened;
            try { opened = await OpenAsync(destination, via, tunnel, ct).ConfigureAwait(false); }
            catch (GatewayException e)
            {
                await RefuseAsync(client, e.Status, e.Message, ct).ConfigureAwait(false);
                return;
            }
            target = opened.Target;
            if (tunnel)
            {
                await client.Stream.WriteAsync(Established, ct).ConfigureAwait(false);
                if (opened.Early is { } early) await client.Stream.WriteAsync(early, ct).ConfigureAwait(false);
            }
            else
                await target.Stream.WriteAsync(Encoding.Latin1.GetBytes(Forward(request, destination, via)), ct).ConfigureAwait(false);
            // What the client sent past its head (a request body, an eager TLS hello) goes first
            if (filled > end) await target.Stream.WriteAsync(buffer.AsMemory(end, filled - end), ct).ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = null;
            await PipeAsync(client, target, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client or the destination went away, or the proxy is closing
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            if (target is not null) await target.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Whether <paramref name="header"/> is Basic with this proxy's user and token (compared in constant time).</summary>
    private bool Authorized(string? header)
    {
        const string scheme = "Basic ";
        Span<byte> given = stackalloc byte[64];
        return header is not null && header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            && Convert.TryFromBase64Chars(header.AsSpan(scheme.Length).Trim(), given, out var length)
            && CryptographicOperations.FixedTimeEquals(given[..length], _credentials);
    }

    /// <summary>
    /// A connection for <paramref name="destination"/>: to it directly, or to the user's proxy <paramref name="via"/>
    /// (asked for a tunnel of its own when <paramref name="tunnel"/>; a plain request goes to it as it is), with the bytes
    /// that proxy sent past its answer. Throws <see cref="GatewayException"/> when it cannot be made in <see cref="ConnectTimeout"/>.
    /// </summary>
    private async Task<(Leg Target, byte[]? Early)> OpenAsync(Destination destination, UpstreamProxy? via, bool tunnel, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConnectTimeout);
        var (host, port) = via is null ? (destination.Host, destination.Port) : (via.Address.IdnHost, via.Address.Port);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Stream? stream = null;
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(host, port), deadline.Token).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
            if (via?.Address.Scheme == Uri.UriSchemeHttps)
            {
                var tls = new SslStream(stream);
                stream = tls;
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, deadline.Token).ConfigureAwait(false);
            }
            var leg = new Leg(socket, stream);
            return (leg, via is not null && tunnel ? await ConnectThroughAsync(leg, destination, deadline.Token).ConfigureAwait(false) : null);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or AuthenticationException or GatewayException)
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            else socket.Dispose();
            if (e is GatewayException || ct.IsCancellationRequested) throw;
            var what = via is null ? destination.Host : "the network proxy";
            throw e is OperationCanceledException
                ? new GatewayException("504 Gateway Timeout", $"No connection to {what} within {ConnectTimeout.TotalSeconds:0} s.")
                : new GatewayException("502 Bad Gateway", $"Could not connect to {what}.");
        }
    }

    /// <summary>
    /// Asks the user's proxy for a tunnel to <paramref name="destination"/>, with their credentials: the bytes it sent past
    /// its answer (null when none). Throws <see cref="GatewayException"/> when it refused.
    /// </summary>
    private async Task<byte[]?> ConnectThroughAsync(Leg upstream, Destination destination, CancellationToken ct)
    {
        var authority = destination.Authority;
        var credentials = _upstreamAuthorization is null ? "" : $"Proxy-Authorization: {_upstreamAuthorization}\r\n";
        await upstream.Stream.WriteAsync(Encoding.Latin1.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{credentials}\r\n"), ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxHead);
        try
        {
            var (end, filled) = await ReadHeadAsync(upstream.Stream, buffer, ct).ConfigureAwait(false);
            if (end < 0) throw new GatewayException("502 Bad Gateway", "The network proxy gave no answer.");
            var status = StatusOf(Encoding.Latin1.GetString(buffer, 0, end));
            if (status is < 200 or > 299) throw new GatewayException("502 Bad Gateway", $"The network proxy refused the connection ({status}).");
            return filled > end ? buffer[end..filled] : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The head of a plain-HTTP request as sent on: origin-form to the server, or absolute-form with the user's
    /// credentials to their proxy; without this proxy's credentials or the client's connection headers. One request per
    /// connection: only the first head is checked, so the server must end the connection after answering it.
    /// </summary>
    private string Forward(Request request, Destination destination, UpstreamProxy? via)
    {
        var head = new StringBuilder(512);
        head.Append(request.Method).Append(' ').Append(via is null ? destination.Path : request.Target).Append(' ').Append(request.Version).Append("\r\n");
        var hasHost = false;
        foreach (var (name, value) in request.Headers)
        {
            if (HopHeaders.Contains(name)) continue;
            hasHost |= name.Equals("Host", StringComparison.OrdinalIgnoreCase);
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        if (!hasHost) head.Append("Host: ").Append(destination.Authority).Append("\r\n");
        if (via is not null && _upstreamAuthorization is not null) head.Append("Proxy-Authorization: ").Append(_upstreamAuthorization).Append("\r\n");
        return head.Append("Connection: close\r\n\r\n").ToString();
    }

    /// <summary><c>Basic …</c> from the user info of the user's proxy address (percent-decoded, as typed into HTTPS_PROXY); null without one.</summary>
    private static string? BasicCredentials(Uri address)
    {
        var info = address.UserInfo;
        if (info.Length == 0) return null;
        var colon = info.IndexOf(':');
        var user = Uri.UnescapeDataString(colon < 0 ? info : info[..colon]);
        var password = colon < 0 ? "" : Uri.UnescapeDataString(info[(colon + 1)..]);
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
    }

    /// <summary>The status code of a response head (<c>HTTP/1.1 200 Connection established</c>); 0 when it has none.</summary>
    private static int StatusOf(string head)
    {
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        var parts = (lineEnd < 0 ? head : head[..lineEnd]).Split(' ', 3);
        return parts.Length >= 2 && parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) ? status : 0;
    }

    /// <summary>
    /// Reads into <paramref name="buffer"/>, at most <see cref="MaxHead"/> bytes, until the blank line that ends a head:
    /// where the bytes after the head start (-1 when the stream ended or the limit was reached first) and how many were read.
    /// </summary>
    private static async Task<(int End, int Filled)> ReadHeadAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < MaxHead)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled, MaxHead - filled), ct).ConfigureAwait(false);
            if (read == 0) return (-1, filled);
            var from = Math.Max(0, filled - 3);
            filled += read;
            var at = buffer.AsSpan(from, filled - from).IndexOf("\r\n\r\n"u8);
            if (at >= 0) return (from + at + 4, filled);
        }
        return (-1, filled);
    }

    /// <summary>
    /// Answers <paramref name="status"/> with a line of text, then closes the way servers do: the sending side first, then
    /// what the client still sends is read and dropped for a moment, so the close does not reset the connection and lose
    /// the answer (a client still sending an oversized head would otherwise see a reset instead of its 431).
    /// </summary>
    private static async Task RefuseAsync(Leg client, string status, string text, CancellationToken ct, string header = "")
    {
        var answer = $"HTTP/1.1 {status}\r\n{header}Content-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(text)}\r\nConnection: close\r\n\r\n{text}";
        await client.Stream.WriteAsync(Encoding.UTF8.GetBytes(answer), ct).ConfigureAwait(false);
        await client.CloseSendAsync().ConfigureAwait(false);
        using var linger = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linger.CancelAfter(LingerTimeout);
        var sink = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (await client.Stream.ReadAsync(sink, linger.Token).ConfigureAwait(false) > 0) { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client kept the connection open: closed now
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sink);
        }
    }

    /// <summary>
    /// Copies bytes both ways until both directions have ended. A side done sending is half-closed on the other side while
    /// the other direction goes on (HTTP relies on it: a server reading a request to its end, a client a response); a
    /// broken connection, or the proxy closing, ends both.
    /// </summary>
    private static async Task PipeAsync(Leg a, Leg b, CancellationToken ct)
    {
        using var broken = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAll(PumpAsync(a, b, broken), PumpAsync(b, a, broken)).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Leg from, Leg to, CancellationTokenSource broken)
    {
        try
        {
            await from.Stream.CopyToAsync(to.Stream, broken.Token).ConfigureAwait(false);
            await to.CloseSendAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            await broken.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A request head: the request line's three parts and the header fields in order.</summary>
    private sealed record Request(string Method, string Target, string Version, List<(string Name, string Value)> Headers)
    {
        /// <summary>The head without its blank line; null when it is not HTTP/1.x.</summary>
        public static Request? Parse(string head)
        {
            var lines = head.Split("\r\n");
            var start = lines[0].Split(' ');
            if (start.Length != 3 || start[0].Length == 0 || start[1].Length == 0 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal)
                || lines[0].AsSpan().ContainsAny('\r', '\n'))
                return null;
            var headers = new List<(string, string)>(lines.Length - 1);
            foreach (var line in lines.AsSpan(1))
            {
                var colon = line.IndexOf(':');
                // A blank in a name, a folded line or a stray CR/LF is how requests are smuggled past a proxy
                if (colon <= 0 || line.AsSpan(0, colon).ContainsAny(' ', '\t') || line.AsSpan().ContainsAny('\r', '\n')) return null;
                headers.Add((line[..colon], line[(colon + 1)..].Trim(' ', '\t')));
            }
            return new Request(start[0], start[1], start[2], headers);
        }

        /// <summary>The first field named <paramref name="name"/> (any case); null when there is none.</summary>
        public string? Header(string name)
        {
            foreach (var (n, value) in Headers)
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;
            return null;
        }
    }

    /// <summary>
    /// Where a request goes: the host as the client named it (an IPv6 address without brackets), the port, and for plain
    /// HTTP the path and query to ask the server for.
    /// </summary>
    private sealed record Destination(string Host, int Port, string Path)
    {
        /// <summary><c>host:port</c>, an IPv6 address in brackets.</summary>
        public string Authority => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

        /// <summary><c>host:port</c> or <c>[v6]:port</c>; the port may be left out when there is a <paramref name="defaultPort"/>.</summary>
        public static Destination? FromAuthority(ReadOnlySpan<char> authority, int? defaultPort, string path)
        {
            ReadOnlySpan<char> host, port;
            if (authority.Length > 0 && authority[0] == '[')
            {
                var close = authority.IndexOf(']');
                if (close < 0) return null;
                host = authority[1..close];
                port = authority[(close + 1)..];
                if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return null;
            }
            else
            {
                var colon = authority.LastIndexOf(':');
                host = colon < 0 ? authority : authority[..colon];
                port = colon < 0 ? ReadOnlySpan<char>.Empty : authority[colon..];
                if (host.Length is 0 or > 253 || host.ContainsAnyExcept(HostChars)) return null;
            }
            int number;
            if (port.IsEmpty)
            {
                if (defaultPort is not { } fallback) return null;
                number = fallback;
            }
            else if (port[0] != ':' || !int.TryParse(port[1..], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number is < 1 or > 65535)
                return null;
            return new Destination(host.ToString(), number, path);
        }

        /// <summary>A plain-HTTP absolute-form target, <c>http://host[:port]/path?query</c>; null for anything else.</summary>
        public static Destination? FromUrl(string target)
        {
            const string scheme = "http://";
            if (!target.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
            var rest = target.AsSpan(scheme.Length);
            var end = rest.IndexOfAny('/', '?');
            var path = end < 0 ? "/" : rest[end] == '?' ? string.Concat("/", rest[end..]) : rest[end..].ToString();
            return FromAuthority(end < 0 ? rest : rest[..end], 80, path);
        }
    }

    /// <summary>One side of a connection: its socket (for the half-close) and the stream its bytes go through (TLS to an https:// proxy).</summary>
    private sealed class Leg(Socket socket, Stream stream) : IAsyncDisposable
    {
        public Stream Stream => stream;

        /// <summary>Tells the peer nothing more comes (TLS close_notify first, then TCP FIN), while its bytes still arrive.</summary>
        public async Task CloseSendAsync()
        {
            if (stream is SslStream tls) await tls.ShutdownAsync().ConfigureAwait(false);
            socket.Shutdown(SocketShutdown.Send);
        }

        /// <summary>Closes the stream, and with it the socket.</summary>
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    /// <summary>A destination that could not be reached: the status line and text the client gets.</summary>
    private sealed class GatewayException(string status, string message) : Exception(message)
    {
        public string Status { get; } = status;
    }
}
