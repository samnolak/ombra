using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;

namespace Ombra.Server;

/// <summary>
/// Pre-auth DoS limits (implementation review): per-route request body limits applied before anything is buffered,
/// and a per-client-address rate limit on the endpoints reachable without a token.
/// </summary>
public static class RequestGuards
{
    public const string PreAuthPolicy = "preauth";

    public static IServiceCollection AddRequestGuards(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                await ctx.HttpContext.Response.WriteAsJsonAsync(
                    new ErrorResponse(Reasons.RateLimited, "too many unauthenticated requests from this address"), JsonDefaults.Options, ct);
            };
            o.AddPolicy(PreAuthPolicy, ctx =>
            {
                var limit = ctx.RequestServices.GetRequiredService<IOptions<OmbraOptions>>().Value.Limits.PreAuthRequestsPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "-",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });

    /// <summary>Body limit of a request by route: streaming uploads keep their own maxima, everything else is small.</summary>
    public static long BodyLimit(HttpRequest req, LimitsOptions l)
    {
        var p = req.Path.Value ?? "";
        if (HttpMethods.IsPut(req.Method))
        {
            if (p.StartsWith("/v1/blobs/", StringComparison.Ordinal)) return l.BlobMaxBytes + (1 << 20);
            if (p.StartsWith("/v1/transfers/", StringComparison.Ordinal) && p.Contains("/chunks/", StringComparison.Ordinal))
                return l.TransferChunkPlainBytes + l.ChunkOverheadBytes + (64 << 10);
        }
        if (HttpMethods.IsPost(req.Method))
        {
            if (p.StartsWith("/v1/session/", StringComparison.Ordinal) || p == "/v1/bootstrap"
                || (p.StartsWith("/v1/devices/pair/", StringComparison.Ordinal) && p.EndsWith("/join", StringComparison.Ordinal))) return l.PreAuthBodyBytes;
            if (p == "/v1/keypackages" || p == "/v1/teams" || (p.StartsWith("/v1/groups/", StringComparison.Ordinal) && p.EndsWith("/commit", StringComparison.Ordinal))
                || (p.StartsWith("/v1/teams/", StringComparison.Ordinal) && (p.EndsWith("/roster", StringComparison.Ordinal) || p.EndsWith("/groups", StringComparison.Ordinal)
                    || p.EndsWith("/ownership/accept", StringComparison.Ordinal))))
                return l.HandshakeBodyBytes;
        }
        return l.RequestBodyBytes;
    }

    /// <summary>
    /// Sets the server's per-request body limit (Kestrel enforces it while reading) and rejects a declared oversize
    /// body up front with 413; a body without Content-Length on a host without the feature is wrapped in a counting stream.
    /// </summary>
    public static void UseBodyLimits(this WebApplication app) =>
        app.Use(async (ctx, next) =>
        {
            var limit = BodyLimit(ctx.Request, ctx.RequestServices.GetRequiredService<IOptions<OmbraOptions>>().Value.Limits);
            var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            var enforced = feature is { IsReadOnly: false };
            if (enforced) feature!.MaxRequestBodySize = limit;
            if (ctx.Request.ContentLength > limit) throw new ApiException(413, Reasons.TooLarge, $"request body over {limit} bytes");
            if (ctx.Request.ContentLength is null && !enforced) ctx.Request.Body = new LimitedStream(ctx.Request.Body, limit);
            await next(ctx);
        });

    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Count(int n)
        {
            _read += n;
            if (_read > limit) throw new BadHttpRequestException($"request body over {limit} bytes", StatusCodes.Status413PayloadTooLarge);
            return n;
        }
    }
}
