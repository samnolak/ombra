using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>A server refusal (§6.1 error list). <see cref="RemovalNotices"/>: admin-signed notices served with <c>403 removed</c>.</summary>
public sealed class TeamsServerException(int status, string reason, string? detail, IReadOnlyList<byte[]>? notices = null)
    : Exception($"{status} {reason}{(detail is null ? "" : ": " + detail)}")
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
    public string? Detail { get; } = detail;
    public IReadOnlyList<byte[]> RemovalNotices { get; } = notices ?? [];
}

/// <summary>
/// Authenticated session with one team server (§2.2, §6.1): challenge → hello signed by this device's identity key
/// inside ombra-core, access/refresh tokens, JSON and signed-Envelope requests. All traffic goes through
/// <see cref="ServerEndpoint"/> (pin or chain validation). Nothing here runs unless the user signed in.
/// </summary>
public sealed class TeamsServer : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _auth = new(1, 1);
    private TokenResponse? _tokens;

    public TeamsServer(ServerEndpoint endpoint, TimeProvider? clock = null)
    {
        Endpoint = endpoint;
        Clock = clock ?? TimeProvider.System;
        Http = endpoint.CreateHttpClient();
        endpoint.PinsRevoked += OnPinsRevoked;
    }

    /// <summary>
    /// A pinned SPKI was revoked by the roster: connections (and tokens obtained over them) authenticated with it are
    /// dropped; the next request re-handshakes against the current set.
    /// </summary>
    private void OnPinsRevoked()
    {
        var old = Http;
        Http = Endpoint.CreateHttpClient();
        _tokens = null;
        old.Dispose();
    }

    public ServerEndpoint Endpoint { get; }
    public TimeProvider Clock { get; }
    public HttpClient Http { get; private set; }
    public TeamsDevice? Device { get; private set; }
    public Guid? ServerId { get; private set; }
    public HelloResponse? LastHello { get; private set; }
    public string? AccessToken => _tokens?.AccessToken;
    public ulong Now => (ulong)Clock.GetUtcNow().ToUnixTimeSeconds();

    public void Attach(TeamsDevice device) => Device = device;

    private TeamsDevice RequireDevice() => Device ?? throw new InvalidOperationException("no device attached");

    public async Task<Guid> ServerIdAsync(CancellationToken ct = default)
    {
        if (ServerId is { } id) return id;
        var h = await SendAsync<HealthResponse>(HttpMethod.Get, "v1/health", null, anonymous: true, ct);
        ServerId = h.ServerId;
        return h.ServerId;
    }

    private async Task<(ChallengeResponse Challenge, ulong Time, byte[] Signature)> SignChallengeAsync(CancellationToken ct)
    {
        var d = RequireDevice();
        var id = d.Identity ?? throw new InvalidOperationException("device not initialised");
        var ch = await SendAsync<ChallengeResponse>(HttpMethod.Post, "v1/session/challenge", JsonContent.Create(new ChallengeRequest(id.DeviceId), options: Json), anonymous: true, ct);
        ServerId = ch.ServerId;
        var time = Now;
        var sig = TeamsHex.Bytes(d.Call("hello.sign", new() { ["serverId"] = TeamsHex.Of(ch.ServerId), ["nonce"] = TeamsHex.Of(ch.Nonce), ["time"] = time })["signature"]!);
        return (ch, time, sig);
    }

    /// <summary>§2.2 hello. <c>403 removed</c> carries the RemovalNotices (§5.3); <c>401</c> = device not registered (yet).</summary>
    public async Task<HelloResponse> HelloAsync(CancellationToken ct = default)
    {
        await _auth.WaitAsync(ct);
        try
        {
            var (ch, time, sig) = await SignChallengeAsync(ct);
            var hello = await SendAsync<HelloResponse>(HttpMethod.Post, "v1/session/hello",
                JsonContent.Create(new HelloRequest(Protocol.Current, RequireDevice().Identity!.DeviceId, ch.Nonce, time, sig), options: Json), anonymous: true, ct);
            _tokens = hello.Tokens;
            LastHello = hello;
            return hello;
        }
        finally { _auth.Release(); }
    }

    /// <summary>§2.4: the instance owner's first device, with the bootstrap code and the pin printed by install.sh.</summary>
    public async Task<HelloResponse> BootstrapAsync(string code, CancellationToken ct = default)
    {
        var d = RequireDevice();
        var id = d.Identity ?? throw new InvalidOperationException("device not initialised");
        var (ch, time, sig) = await SignChallengeAsync(ct);
        var pin = Endpoint.IpMode ? Endpoint.Pins.First() : null;
        var hello = await SendAsync<HelloResponse>(HttpMethod.Post, "v1/bootstrap", JsonContent.Create(new BootstrapRequest(code, pin, id.MemberId, id.DeviceId, "gui",
            id.IdentityKey, id.MlsSignatureKey, ch.Nonce, time, sig, Protocol.Current), options: Json), anonymous: true, ct);
        _tokens = hello.Tokens;
        LastHello = hello;
        return hello;
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_tokens is { } t && t.AccessExpiresAt > Clock.GetUtcNow().AddSeconds(30)) return;
        if (_tokens is { } r && r.RefreshExpiresAt > Clock.GetUtcNow())
        {
            try
            {
                _tokens = await SendAsync<TokenResponse>(HttpMethod.Post, "v1/session/refresh", JsonContent.Create(new RefreshRequest(r.RefreshToken), options: Json), anonymous: true, ct);
                return;
            }
            catch (TeamsServerException e) when (e.Status == 401) { }
        }
        await HelloAsync(ct);
    }

    public Task<T> GetAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, anonymous: false, ct);

    public Task<T> PostJsonAsync<T>(string path, object body, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Post, path, JsonContent.Create(body, body.GetType(), options: Json), anonymous: false, ct);

    public Task<T> PostAnonymousJsonAsync<T>(string path, object body, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Post, path, JsonContent.Create(body, body.GetType(), options: Json), anonymous: true, ct);

    public Task<T> GetAnonymousAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, anonymous: true, ct);

    public Task<T> PostBytesAsync<T>(string path, byte[] body, string contentType = "application/octet-stream", CancellationToken ct = default)
    {
        var c = new ByteArrayContent(body);
        c.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return SendAsync<T>(HttpMethod.Post, path, c, anonymous: false, ct);
    }

    /// <summary>A signed Envelope (§6.1): canonical CBOR, signature by this device inside ombra-core, header X-Ombra-Sig.</summary>
    public (byte[] Encoded, byte[] Signature) SignEnvelope(string kind, byte[] scope, CborMapWriter body, ulong baseRevision = 0, Guid? opId = null)
    {
        var d = RequireDevice();
        var env = new Envelope(Envelope.CurrentVersion, opId ?? Guid.CreateVersion7(), d.Identity!.DeviceId, scope, baseRevision, kind, body.Encode(),
            (ulong)Clock.GetUtcNow().ToUnixTimeMilliseconds());
        var encoded = env.Encode();
        return (encoded, TeamsHex.Bytes(d.Call("envelope.sign", new() { ["body"] = TeamsHex.Of(encoded) })["signature"]!));
    }

    public Task<T> PostEnvelopeAsync<T>(string path, string kind, byte[] scope, CborMapWriter body, ulong baseRevision = 0, bool anonymous = false,
        CancellationToken ct = default)
    {
        var (encoded, sig) = SignEnvelope(kind, scope, body, baseRevision);
        return SendEnvelopeAsync<T>(path, encoded, sig, anonymous, ct);
    }

    public Task<T> SendEnvelopeAsync<T>(string path, byte[] encoded, byte[] sig, bool anonymous, CancellationToken ct = default)
    {
        var c = new ByteArrayContent(encoded);
        c.Headers.ContentType = new MediaTypeHeaderValue(Headers.EnvelopeContentType);
        return SendAsync<T>(HttpMethod.Post, path, c, anonymous, ct,
            r => r.Headers.Add(Headers.Signature, Convert.ToBase64String(sig).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
    }

    /// <summary>C3: a streaming upload (blob): the signed envelope in <see cref="Headers.Envelope"/>, the ciphertext as the body.</summary>
    public Task<T> PutEnvelopedAsync<T>(string path, (byte[] Encoded, byte[] Signature) env, byte[] body, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Put, path, new ByteArrayContent(body), anonymous: false, ct, r =>
        {
            r.Headers.Add(Headers.Envelope, B64Url(env.Encoded));
            r.Headers.Add(Headers.Signature, B64Url(env.Signature));
        });

    /// <summary>C3: a signed DELETE (blob), the envelope as the body.</summary>
    public Task PostEnvelopeDeleteAsync(string path, string kind, byte[] scope, CborMapWriter body, CancellationToken ct = default)
    {
        var (encoded, sig) = SignEnvelope(kind, scope, body);
        var c = new ByteArrayContent(encoded);
        c.Headers.ContentType = new MediaTypeHeaderValue(Headers.EnvelopeContentType);
        return SendAsync<NoContent>(HttpMethod.Delete, path, c, anonymous: false, ct, r => r.Headers.Add(Headers.Signature, B64Url(sig)));
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, bool anonymous, CancellationToken ct,
        Action<HttpRequestMessage>? decorate = null)
    {
        byte[]? body = content is null ? null : await content.ReadAsByteArrayAsync(ct);
        var headers = content?.Headers.ContentType;
        for (var attempt = 0; ; attempt++)
        {
            if (!anonymous) await EnsureTokenAsync(ct);
            using var req = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                req.Content = new ByteArrayContent(body);
                req.Content.Headers.ContentType = headers;
            }
            if (!anonymous) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
            decorate?.Invoke(req);
            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && !anonymous && attempt == 0)
            {
                _tokens = null; // token revoked or expired early: one fresh hello, then retry
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                ErrorResponse? err = null;
                try { err = await resp.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct); }
                catch (JsonException) { }
                catch (NotSupportedException) { }
                throw new TeamsServerException((int)resp.StatusCode, err?.Reason ?? resp.StatusCode.ToString(), err?.Detail, err?.RemovalNotices);
            }
            if (typeof(T) == typeof(NoContent) || resp.StatusCode == HttpStatusCode.NoContent) return default!;
            if (typeof(T) == typeof(byte[])) return (T)(object)await resp.Content.ReadAsByteArrayAsync(ct); // C3: blob bytes
            return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    public void Dispose()
    {
        Endpoint.PinsRevoked -= OnPinsRevoked;
        Http.Dispose();
        _auth.Dispose();
    }
}

/// <summary>Marker for requests answered with 204.</summary>
public sealed record NoContent;
