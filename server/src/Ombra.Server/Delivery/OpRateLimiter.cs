using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Ombra.Server.Delivery;

/// <summary>Per-device token bucket for /ops (§3.0: 20 ops/s, burst 100). In-memory: the server is a single instance.</summary>
public sealed class OpRateLimiter(TimeProvider clock, IOptions<OmbraOptions> options)
{
    private readonly LimitsOptions _limits = options.Value.Limits;
    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    private sealed class Bucket(double tokens, long stamp)
    {
        public double Tokens = tokens;
        public long Stamp = stamp;
    }

    /// <summary>Takes one token; returns the wait until the next token when empty.</summary>
    public bool TryTake(Guid deviceId, out TimeSpan retryAfter)
    {
        var now = clock.GetTimestamp();
        var bucket = _buckets.GetOrAdd(deviceId, _ => new Bucket(_limits.DeviceOpsBurst, now));
        lock (bucket)
        {
            var elapsed = clock.GetElapsedTime(bucket.Stamp, now).TotalSeconds;
            bucket.Tokens = Math.Min(_limits.DeviceOpsBurst, bucket.Tokens + elapsed * _limits.DeviceOpsPerSecond);
            bucket.Stamp = now;
            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                retryAfter = TimeSpan.Zero;
                return true;
            }
            retryAfter = TimeSpan.FromSeconds((1 - bucket.Tokens) / _limits.DeviceOpsPerSecond);
            return false;
        }
    }

    public void Forget(IEnumerable<Guid> devices)
    {
        foreach (var d in devices) _buckets.TryRemove(d, out _);
    }
}
