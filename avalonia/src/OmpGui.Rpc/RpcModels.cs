using System.Diagnostics;
using System.Text.Json;

namespace OmpGui.Rpc;

/// <summary>One logical stdout frame (after v2 reassembly). <see cref="ReadTimestamp"/> is when its last physical line was read (T2).</summary>
public sealed record RpcFrame(string Type, string? Id, JsonElement Json, long ReadTimestamp, int PhysicalBytes)
{
    /// <summary>Synthetic frame types the transport emits for problems it tolerates.</summary>
    public const string Malformed = "_malformed";
    public const string ProtocolError = "_protocol_error";
    public const string UnmatchedResponse = "_unmatched_response";
}

public sealed record RpcReady(int ProtocolVersion, IReadOnlyList<int> SupportedProtocolVersions, int MaxFrameBytes, int MaxReassembledFrameBytes)
{
    public bool SupportsV2 => SupportedProtocolVersions.Contains(2);
}

public sealed record RpcResponse(string Command, bool Success, string? Error, JsonElement? Data, RpcTimings Timings);

/// <summary>Latency probes, <see cref="Stopwatch.GetTimestamp"/> values; 0 = not reached.</summary>
public sealed class RpcTimings
{
    /// <summary>T0: the caller asked the transport to send.</summary>
    public long Sent;
    /// <summary>T1: the command was written and flushed to the child's stdin.</summary>
    public long Flushed;
    /// <summary>T2: the response line was fully read from stdout.</summary>
    public long ResponseRead;
    /// <summary>T3: the awaiting caller resumed with the response.</summary>
    public long Delivered;

    public static double Ms(long from, long to) => from == 0 || to == 0 ? double.NaN : (to - from) * 1000.0 / Stopwatch.Frequency;
}

public sealed class RpcCommandException(string command, string error) : Exception($"{command} failed: {error}")
{
    public string Command { get; } = command;
    public string Error { get; } = error;
}

public sealed class RpcConnectionClosedException(string message, Exception? inner = null) : Exception(message, inner);
