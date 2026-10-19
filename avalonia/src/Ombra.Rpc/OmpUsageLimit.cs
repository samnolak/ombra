using System.Text.Json;

namespace Ombra.Rpc;

/// <summary>The active account's exceptional usage stage, not its full subscription quotas.</summary>
public sealed record OmpUsageLimit(string Stage, DateTimeOffset? ResetsAt, bool ExtraUsage, int? AllowanceLeftPercent)
{
    internal static OmpUsageLimit? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("stage", out var stage)
            || stage.ValueKind != JsonValueKind.String) return null;
        DateTimeOffset? resetsAt = null;
        if (value.TryGetProperty("resetsAtSec", out var reset))
        {
            if (reset.ValueKind != JsonValueKind.Number || !reset.TryGetDouble(out var seconds)
                || !double.IsFinite(seconds) || seconds < -62135596800 || seconds > 253402300799) return null;
            resetsAt = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        }
        if (stage.ValueEquals("wrap_up"))
        {
            if (!value.TryGetProperty("extraUsage", out var extra) || extra.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            return new("wrap_up", resetsAt, extra.GetBoolean(), null);
        }
        if (!stage.ValueEquals("low_priority") || resetsAt is null) return null;
        int? allowance = null;
        if (value.TryGetProperty("allowanceLeftPercent", out var left))
        {
            if (left.ValueKind != JsonValueKind.Number || !left.TryGetInt32(out var percent) || percent is < 0 or > 100) return null;
            allowance = percent;
        }
        return new("low_priority", resetsAt, false, allowance);
    }
}
