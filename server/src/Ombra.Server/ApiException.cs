using Ombra.Server.Contracts;

namespace Ombra.Server;

/// <summary>Maps to <c>{status, ErrorResponse{reason, detail, removalNotices}}</c> (§6.1 error list).</summary>
public sealed class ApiException(int status, string reason, string? detail = null) : Exception(detail ?? reason)
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
    public string? Detail { get; } = detail;
    public TimeSpan? RetryAfter { get; init; }
    /// <summary>§5.1 п.6 / §5.3: signed RemovalNotices served to a removed device with its <c>403 removed</c>.</summary>
    public IReadOnlyList<byte[]>? RemovalNotices { get; init; }

    public static ApiException Forbidden(string reason, string? detail = null) => new(403, reason, detail);
    public static ApiException Conflict(string reason, string? detail = null) => new(409, reason, detail);
    public static ApiException NotFound(string? detail = null) => new(404, Reasons.NotFound, detail);
    public static ApiException Bad(string detail) => new(400, Reasons.BadRequest, detail);
}
