using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OmpGui.ClientCore.Network;

namespace OmpGui.Tests;

/// <summary>
/// Strict network privacy: the proxy omp is given lets through only what the app allows (HTTPS tunnels and plain HTTP),
/// refuses the rest with 403 and reports it, serves nobody without its credentials, goes through the user's own proxy
/// when there is one (NO_PROXY honoured), and closes its tunnels when disposed. Every server here is a listener on the
/// loopback interface: nothing reaches the internet. Also the allowlist's matching and entry rules, and how the user's
/// proxy is read from the environment.
/// </summary>
public sealed class PrivacyProxyTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly Func<string, int, bool> Everything = (_, _) => true;

    // ── The proxy ──

    [Fact]
    public async Task An_allowed_connect_tunnels_bytes_both_ways_until_each_side_ends()
    {
        await using var echo = new Server(EchoAsync);
        var asked = new ConcurrentQueue<(string, int)>();
        await using var proxy = PrivacyProxy.Start((host, port) =>
        {
            asked.Enqueue((host, port));
            return true;
        });
        var blocked = 0;
        proxy.Blocked += _ => Interlocked.Increment(ref blocked);

        // Bytes sent right behind the head (an eager TLS hello) are not lost
        using var client = await Client.SendAsync(proxy, Connect(proxy, $"127.0.0.1:{echo.Port}") + "early ");
        Assert.Equal("HTTP/1.1 200 Connection Established\r\n\r\n", client.Answer);
        Assert.Equal("early ", await client.ReadAsync(6));
        await client.WriteAsync("ping");
        Assert.Equal("ping", await client.ReadAsync(4));

        // The client is done sending: the server sees the end, ends its own side, and the client sees that
        client.Socket.Shutdown(SocketShutdown.Send);
        Assert.Equal("", await client.ReadToEndAsync());

        Assert.Equal(("127.0.0.1", echo.Port), Assert.Single(asked));
        Assert.Equal(0, blocked);
        Assert.Equal(1, echo.Accepted);
    }

    [Fact]
    public async Task A_destination_not_allowed_gets_403_and_is_reported_without_a_connection()
    {
        await using var server = new Server(EchoAsync);
        var at = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        await using var proxy = PrivacyProxy.Start((_, _) => false, clock: new FixedClock(at));
        var blocked = new ConcurrentQueue<BlockedConnection>();
        proxy.Blocked += blocked.Enqueue;

        using (var client = await Client.SendAsync(proxy, Connect(proxy, $"127.0.0.1:{server.Port}")))
        {
            Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", client.Answer);
            Assert.Equal("Blocked by OMP GUI strict network privacy: 127.0.0.1", await client.ReadToEndAsync());
        }
        // Plain HTTP too (port 80 when the URL has none), and an IPv6 destination in brackets
        using (var client = await Client.SendAsync(proxy, $"GET http://catalog.stencil.so/models.json.zstd HTTP/1.1\r\nHost: catalog.stencil.so\r\n{Credentials(proxy)}\r\n"))
            Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", client.Answer);
        using (var client = await Client.SendAsync(proxy, Connect(proxy, "[2001:db8::1]:8443")))
            Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", client.Answer);

        Assert.Equal(
            [new BlockedConnection("127.0.0.1", server.Port, at), new BlockedConnection("catalog.stencil.so", 80, at), new BlockedConnection("2001:db8::1", 8443, at)],
            blocked);
        Assert.Equal(0, server.Accepted);
    }

    [Fact]
    public async Task Without_its_credentials_the_proxy_serves_nobody()
    {
        await using var server = new Server(EchoAsync);
        var asked = 0;
        await using var proxy = PrivacyProxy.Start((_, _) =>
        {
            Interlocked.Increment(ref asked);
            return true;
        });
        var blocked = 0;
        proxy.Blocked += _ => Interlocked.Increment(ref blocked);
        var token = Token(proxy);
        Assert.Equal("127.0.0.1", proxy.Address.Host);
        Assert.Equal("ompgui:" + token, proxy.Address.UserInfo);
        Assert.Matches("^[0-9a-f]{32}$", token);

        var authority = $"127.0.0.1:{server.Port}";
        foreach (var credentials in new[]
                 {
                     "",
                     Basic("ompgui:" + new string('0', 32)),
                     Basic("someone:" + token),
                     Basic("ompgui:" + token + "0"),
                     "Proxy-Authorization: Bearer " + token + "\r\n",
                 })
        {
            foreach (var request in new[]
                     {
                         $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{credentials}\r\n",
                         $"GET http://{authority}/ HTTP/1.1\r\nHost: {authority}\r\n{credentials}\r\n",
                     })
            {
                using var client = await Client.SendAsync(proxy, request);
                Assert.StartsWith("HTTP/1.1 407 Proxy Authentication Required\r\n", client.Answer);
                Assert.Contains("\r\nProxy-Authenticate: Basic realm=\"OMP GUI\"\r\n", client.Answer);
                Assert.DoesNotContain(token, client.Answer + await client.ReadToEndAsync());
            }
        }
        Assert.Equal(0, asked);
        Assert.Equal(0, blocked);
        Assert.Equal(0, server.Accepted);
    }

    [Fact]
    public async Task A_new_allowlist_applies_from_the_next_connection()
    {
        await using var echo = new Server(EchoAsync);
        var allowlist = new HostAllowlist(["127.0.0.1"]);
        await using var proxy = PrivacyProxy.Start((host, _) => allowlist.Matches(host));
        var authority = $"127.0.0.1:{echo.Port}";

        using (var client = await Client.SendAsync(proxy, Connect(proxy, authority)))
            Assert.StartsWith("HTTP/1.1 200 ", client.Answer);

        allowlist = new HostAllowlist(["api.anthropic.com"]);
        using (var client = await Client.SendAsync(proxy, Connect(proxy, authority)))
            Assert.StartsWith("HTTP/1.1 403 ", client.Answer);

        allowlist = new HostAllowlist(["api.anthropic.com", "127.0.0.1"]);
        using (var client = await Client.SendAsync(proxy, Connect(proxy, authority)))
            Assert.StartsWith("HTTP/1.1 200 ", client.Answer);
    }

    [Fact]
    public async Task Allowed_connections_go_through_the_users_proxy_with_its_credentials_except_no_proxy_hosts()
    {
        var heads = new ConcurrentQueue<string>();
        await using var corporate = new Server(async (socket, stream, ct) =>
        {
            var head = await ReadHeadAsync(stream, ct);
            heads.Enqueue(head);
            if (head.StartsWith("CONNECT denied.example.com:", StringComparison.Ordinal))
                await WriteAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n", ct);
            else if (head.StartsWith("CONNECT ", StringComparison.Ordinal))
            {
                await WriteAsync(stream, "HTTP/1.1 200 Connection established\r\n\r\n", ct);
                await EchoAsync(socket, stream, ct);
            }
            else
                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", ct);
        });
        await using var echo = new Server(EchoAsync);
        var env = new Dictionary<string, string>
        {
            ["HTTPS_PROXY"] = $"http://corp%40user:p%3Ass@127.0.0.1:{corporate.Port}",
            ["NO_PROXY"] = "localhost, .internal.example 127.0.0.1",
        };
        var upstream = UpstreamProxy.FromEnvironment(name => env.GetValueOrDefault(name));
        Assert.NotNull(upstream);
        await using var proxy = PrivacyProxy.Start(Everything, upstream);
        var theirs = Basic("corp@user:p:ss");

        // HTTPS: their proxy is asked for the tunnel, with their credentials, never the app's token
        using (var client = await Client.SendAsync(proxy, Connect(proxy, "api.example.com:443")))
        {
            Assert.StartsWith("HTTP/1.1 200 ", client.Answer);
            await client.WriteAsync("hello");
            Assert.Equal("hello", await client.ReadAsync(5));
        }
        var connect = Assert.Single(heads);
        Assert.StartsWith("CONNECT api.example.com:443 HTTP/1.1\r\n", connect);
        Assert.Contains("\r\n" + theirs, connect);
        Assert.DoesNotContain(Token(proxy), connect);

        // Plain HTTP: the request goes to their proxy as it is (absolute form), with their credentials
        using (var client = await Client.SendAsync(proxy, $"GET http://plain.example.com/models HTTP/1.1\r\nHost: plain.example.com\r\n{Credentials(proxy)}\r\n"))
        {
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", client.Answer);
            Assert.Equal("ok", await client.ReadToEndAsync());
        }
        var plain = heads.ToArray()[1];
        Assert.StartsWith("GET http://plain.example.com/models HTTP/1.1\r\n", plain);
        Assert.Contains("\r\n" + theirs, plain);
        Assert.DoesNotContain(Token(proxy), plain);

        // Their proxy refusing is a 502 to omp
        using (var client = await Client.SendAsync(proxy, Connect(proxy, "denied.example.com:443")))
            Assert.StartsWith("HTTP/1.1 502 Bad Gateway\r\n", client.Answer);

        // NO_PROXY: straight to the server, their proxy not asked
        using (var client = await Client.SendAsync(proxy, Connect(proxy, $"127.0.0.1:{echo.Port}")))
        {
            Assert.StartsWith("HTTP/1.1 200 ", client.Answer);
            await client.WriteAsync("direct");
            Assert.Equal("direct", await client.ReadAsync(6));
        }
        Assert.Equal(3, heads.Count);
        Assert.Equal(1, echo.Accepted);
    }

    [Fact]
    public async Task Plain_http_goes_to_the_server_in_origin_form_without_the_proxy_headers()
    {
        var heads = new ConcurrentQueue<string>();
        await using var server = new Server(async (_, stream, ct) =>
        {
            var head = await ReadHeadAsync(stream, ct);
            var body = new byte[4];
            await stream.ReadExactlyAsync(body, ct);
            heads.Enqueue(head + Encoding.Latin1.GetString(body));
            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nhi", ct);
        });
        await using var proxy = PrivacyProxy.Start(Everything);
        var authority = $"127.0.0.1:{server.Port}";

        using var client = await Client.SendAsync(proxy,
            $"POST http://{authority}/v1/models?all=1 HTTP/1.1\r\nHost: {authority}\r\n{Credentials(proxy)}Proxy-Connection: keep-alive\r\n"
            + "Connection: keep-alive\r\nUser-Agent: Bun/1.4.2\r\nContent-Length: 4\r\n\r\nbody");
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", client.Answer);
        Assert.Equal("hi", await client.ReadToEndAsync());
        Assert.Equal(
            $"POST /v1/models?all=1 HTTP/1.1\r\nHost: {authority}\r\nUser-Agent: Bun/1.4.2\r\nContent-Length: 4\r\nConnection: close\r\n\r\nbody",
            Assert.Single(heads));
    }

    [Fact]
    public async Task Oversized_and_unusable_requests_are_refused_before_any_check()
    {
        var asked = 0;
        await using var proxy = PrivacyProxy.Start((_, _) =>
        {
            Interlocked.Increment(ref asked);
            return true;
        });

        using (var client = await Client.SendAsync(proxy, $"CONNECT 127.0.0.1:443 HTTP/1.1\r\n{Credentials(proxy)}X-Filler: {new string('a', 20 * 1024)}"))
            Assert.StartsWith("HTTP/1.1 431 Request Header Fields Too Large\r\n", client.Answer);

        foreach (var request in new[]
                 {
                     $"GET /models HTTP/1.1\r\nHost: api.venice.ai\r\n{Credentials(proxy)}\r\n", // origin-form: not for a proxy
                     $"GET https://api.venice.ai/models HTTP/1.1\r\n{Credentials(proxy)}\r\n", // HTTPS comes by CONNECT
                     $"CONNECT api.venice.ai HTTP/1.1\r\n{Credentials(proxy)}\r\n", // no port
                     $"CONNECT 2001:db8::1:443 HTTP/1.1\r\n{Credentials(proxy)}\r\n", // IPv6 without brackets
                     $"CONNECT api.venice.ai:443 HTTP/1.1\r\n{Credentials(proxy)}X-Folded: a\r\n b\r\n\r\n", // a folded header
                     "HELLO\r\n\r\n",
                 })
        {
            using var client = await Client.SendAsync(proxy, request);
            Assert.StartsWith("HTTP/1.1 400 Bad Request\r\n", client.Answer);
        }
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Disposing_closes_every_open_tunnel_and_the_port()
    {
        await using var echo = new Server(EchoAsync);
        await using var proxy = PrivacyProxy.Start(Everything);
        var port = proxy.Address.Port;
        using var client = await Client.SendAsync(proxy, Connect(proxy, $"127.0.0.1:{echo.Port}"));
        Assert.StartsWith("HTTP/1.1 200 ", client.Answer);

        await proxy.DisposeAsync().AsTask().WaitAsync(Wait);
        await proxy.DisposeAsync().AsTask().WaitAsync(Wait); // again: nothing left to do

        try { Assert.Equal("", await client.ReadToEndAsync()); }
        catch (IOException) { } // closed with a reset: ended all the same
        using var late = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => late.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Wait));
    }

    // ── The allowlist ──

    [Theory]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "API.Example.COM", true)]
    [InlineData("api.example.com", "api.example.com.", true)]
    [InlineData("API.example.com.", "api.example.com", true)]
    [InlineData("api.example.com", "eu.api.example.com", false)]
    [InlineData("api.example.com", "example.com", false)]
    [InlineData("api.example.com", "api.example.com:443", false)]
    [InlineData("api.example.com", "", false)]
    [InlineData("*.example.com", "api.example.com", true)]
    [InlineData("*.example.com", "eu.api.example.com", true)]
    [InlineData("*.Example.com", "API.EXAMPLE.COM.", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.example.com", "badexample.com", false)]
    [InlineData("*.example.com", "example.com.evil.test", false)]
    [InlineData("1.2.3.4", "1.2.3.4", true)]
    [InlineData("1.2.3.4", "1.2.3.40", false)]
    [InlineData("1.2.3.4", "5.1.2.3.4", false)]
    [InlineData("::1", "::1", true)]
    [InlineData("[::1]", "0:0:0:0:0:0:0:1", true)]
    [InlineData("2001:db8::1", "[2001:DB8:0::1]", true)]
    [InlineData("2001:db8::1", "2001:db8::2", false)]
    public void The_allowlist_matches_hosts_by_name_wildcard_or_address(string entry, string host, bool matches) =>
        Assert.Equal(matches, new HostAllowlist([entry]).Matches(host));

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("API.Example.com.", true)]
    [InlineData("*.example.com", true)]
    [InlineData("localhost", true)]
    [InlineData("xn--bcher-kva.example", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("::1", true)]
    [InlineData("[2001:db8::1]", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(" api.example.com", false)]
    [InlineData("api example.com", false)]
    [InlineData("https://api.example.com", false)]
    [InlineData("api.example.com/v1", false)]
    [InlineData("api.example.com:443", false)]
    [InlineData("1.2.3.4:443", false)]
    [InlineData("[::1]:443", false)]
    [InlineData("*", false)]
    [InlineData("*.", false)]
    [InlineData("*.*.example.com", false)]
    [InlineData("api.*.example.com", false)]
    [InlineData("*.1.2.3.4", false)]
    [InlineData(".example.com", false)]
    [InlineData("api..example.com", false)]
    [InlineData("-api.example.com", false)]
    [InlineData("api-.example.com", false)]
    [InlineData("under_score.example.com", false)]
    [InlineData("bücher.example", false)]
    public void Allowlist_entries_are_host_names_wildcards_or_addresses(string entry, bool valid) =>
        Assert.Equal(valid, HostAllowlist.IsValidEntry(entry));

    [Fact]
    public void The_allowlist_keeps_its_valid_entries_once_in_canonical_spelling()
    {
        var list = new HostAllowlist(["API.example.com.", "api.example.com", "https://api.example.com", "*.Example.com", "*.example.com", "[::1]", "", "*"]);
        Assert.Equal(["api.example.com", "*.example.com", "::1"], list.Entries);
        Assert.False(list.Matches("evil.test"));
        Assert.Empty(new HostAllowlist([]).Entries);
        Assert.False(new HostAllowlist([]).Matches("api.example.com"));
        Assert.True(HostAllowlist.IsValidEntry(new string('a', 63) + ".example"));
        Assert.False(HostAllowlist.IsValidEntry(new string('a', 64) + ".example"));
    }

    // ── The user's own proxy ──

    [Fact]
    public void The_users_proxy_comes_from_the_environment_https_first()
    {
        static UpstreamProxy? Read(Dictionary<string, string> env) => UpstreamProxy.FromEnvironment(name => env.GetValueOrDefault(name));

        Assert.Null(Read(new()));
        Assert.Null(Read(new() { ["HTTPS_PROXY"] = " ", ["ALL_PROXY"] = "socks5://socks.corp:1080" }));

        var all = Read(new()
        {
            ["HTTPS_PROXY"] = "http://upper.corp:3128", ["https_proxy"] = "http://lower.corp:3128",
            ["HTTP_PROXY"] = "http://plain.corp:8080", ["ALL_PROXY"] = "http://all.corp:1",
        })!;
        Assert.Equal(new Uri("http://upper.corp:3128"), all.Address);
        Assert.Empty(all.Bypass);
        Assert.Equal(new Uri("http://lower.corp:3128"), Read(new() { ["https_proxy"] = "http://lower.corp:3128", ["HTTP_PROXY"] = "http://plain.corp:8080" })!.Address);
        Assert.Equal(new Uri("http://plain.corp:8080"), Read(new() { ["HTTP_PROXY"] = "http://plain.corp:8080", ["ALL_PROXY"] = "http://all.corp:1" })!.Address);
        Assert.Equal(new Uri("http://lower.corp:8080"), Read(new() { ["http_proxy"] = "http://lower.corp:8080", ["ALL_PROXY"] = "http://all.corp:1" })!.Address);
        Assert.Equal(new Uri("http://all.corp:1"), Read(new() { ["all_proxy"] = "http://all.corp:1" })!.Address);

        // A value it cannot use is skipped, not guessed at; one without a scheme is http://
        Assert.Equal(new Uri("http://plain.corp:8080"), Read(new() { ["HTTPS_PROXY"] = "socks5://socks.corp:1080", ["https_proxy"] = "http://", ["HTTP_PROXY"] = "plain.corp:8080" })!.Address);
        Assert.Equal(new Uri("https://tls.corp/"), Read(new() { ["HTTPS_PROXY"] = "https://tls.corp" })!.Address);

        // NO_PROXY first, split on commas and blanks
        Assert.Equal(["localhost", ".corp.example", "10.0.0.1"],
            Read(new() { ["HTTPS_PROXY"] = "http://p.corp:1", ["NO_PROXY"] = " localhost,.corp.example  10.0.0.1, ", ["no_proxy"] = "ignored.example" })!.Bypass);
        Assert.Equal(["lower.example"], Read(new() { ["HTTPS_PROXY"] = "http://p.corp:1", ["no_proxy"] = "lower.example" })!.Bypass);

        // A record prints its members: never the user's proxy password
        Assert.DoesNotContain("s3cret", Read(new() { ["HTTPS_PROXY"] = "http://me:s3cret@p.corp:1" })!.ToString());
    }

    [Theory]
    [InlineData("*", "api.example.com", true)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "API.Example.com.", true)]
    [InlineData(".example.com", "example.com", true)]
    [InlineData(".example.com", "eu.api.example.com", true)]
    [InlineData("example.com", "badexample.com", false)]
    [InlineData("example.com", "example.com.evil.test", false)]
    [InlineData("10.0.0.1", "10.0.0.1", true)]
    [InlineData("10.0.0.1", "10.0.0.10", false)]
    [InlineData("0.0.1", "10.0.0.1", false)]
    [InlineData("[::1]", "::1", true)]
    [InlineData("localhost", "127.0.0.1", false)]
    public void No_proxy_entries_bypass_the_users_proxy(string entry, string host, bool bypasses) =>
        Assert.Equal(bypasses, new UpstreamProxy(new Uri("http://proxy.corp:3128"), [entry]).Bypasses(host));

    // ── Helpers ──

    private static string Basic(string userInfo) => "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo)) + "\r\n";

    /// <summary>The proxy's own credentials, as Bun sends them from the proxy address.</summary>
    private static string Credentials(PrivacyProxy proxy) => Basic(Uri.UnescapeDataString(proxy.Address.UserInfo));

    private static string Token(PrivacyProxy proxy) => proxy.Address.UserInfo.Split(':')[1];

    private static string Connect(PrivacyProxy proxy, string authority) => $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{Credentials(proxy)}\r\n";

    private static Task WriteAsync(Stream stream, string text, CancellationToken ct) => stream.WriteAsync(Encoding.Latin1.GetBytes(text), ct).AsTask();

    /// <summary>Reads up to and including the blank line that ends a head, byte by byte: nothing past it is consumed.</summary>
    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct = default)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, ct) == 1)
            head.Append((char)one[0]);
        return head.ToString();
    }

    /// <summary>Sends back what arrives; when the client is done sending, ends its own side.</summary>
    private static async Task EchoAsync(Socket socket, NetworkStream stream, CancellationToken ct)
    {
        await stream.CopyToAsync(stream, ct);
        socket.Shutdown(SocketShutdown.Send);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A program using the proxy as Bun does: one connection, a request head, the proxy's answer head, then bytes.</summary>
    private sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp = new();

        private Client() { }

        public string Answer { get; private set; } = "";
        public Socket Socket => _tcp.Client;
        // Taken once: after Shutdown(Send) macOS reports the socket as not connected and GetStream refuses
        private NetworkStream Stream { get; set; } = null!;

        public static async Task<Client> SendAsync(PrivacyProxy proxy, string request)
        {
            var client = new Client();
            await client._tcp.ConnectAsync(IPAddress.Loopback, proxy.Address.Port).WaitAsync(Wait);
            client.Stream = client._tcp.GetStream();
            await client.WriteAsync(request);
            client.Answer = await ReadHeadAsync(client.Stream).WaitAsync(Wait);
            return client;
        }

        public Task WriteAsync(string text) => Stream.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask().WaitAsync(Wait);

        public async Task<string> ReadAsync(int count)
        {
            var bytes = new byte[count];
            await Stream.ReadExactlyAsync(bytes).AsTask().WaitAsync(Wait);
            return Encoding.Latin1.GetString(bytes);
        }

        public async Task<string> ReadToEndAsync()
        {
            using var all = new MemoryStream();
            await Stream.CopyToAsync(all).WaitAsync(Wait);
            return Encoding.Latin1.GetString(all.ToArray());
        }

        public void Dispose() => _tcp.Dispose();
    }

    /// <summary>A server on the loopback interface: <c>talk</c> runs for each connection, which then closes.</summary>
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _accepted;

        public Server(Func<Socket, NetworkStream, CancellationToken, Task> talk)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                while (true)
                {
                    Socket socket;
                    try { socket = await _listener.AcceptSocketAsync(_stop.Token); }
                    catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or SocketException) { return; }
                    Interlocked.Increment(ref _accepted);
                    _ = Task.Run(async () =>
                    {
                        await using var stream = new NetworkStream(socket, ownsSocket: true);
                        try { await talk(socket, stream, _stop.Token); }
                        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    });
                }
            });
        }

        public int Port { get; }

        /// <summary>Connections accepted so far.</summary>
        public int Accepted => Volatile.Read(ref _accepted);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
        }
    }
}
