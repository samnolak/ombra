using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace OmpGui.Rpc;

/// <summary>
/// Protocol v2 <c>rpc_chunk</c> reassembly. A line-for-line port of the official decoder
/// (oh-my-pi packages/coding-agent/src/modes/rpc/rpc-frame.ts, <c>RpcFrameDecoder</c>, tag v18.2.0):
/// same validation, same limits, same error conditions. Any violation is fatal for the stream.
/// </summary>
public sealed class RpcFrameDecoder
{
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxReassembledBytes = 64 * 1024 * 1024;
    private const int ChunkPayloadBytes = 256 * 1024;
    private const int MaxChunkCount = (MaxReassembledBytes + ChunkPayloadBytes - 1) / ChunkPayloadBytes;

    private Pending? _pending;

    private sealed class Pending(string chunkId, int count, int byteLength)
    {
        public readonly string ChunkId = chunkId;
        public readonly int Count = count;
        public readonly int ByteLength = byteLength;
        public readonly byte[] Buffer = new byte[byteLength];
        public int NextIndex;
        public int ReceivedBytes;
    }

    public bool HasPendingChunks => _pending is not null;

    /// <summary>
    /// Feeds one parsed JSONL frame. Returns the logical frame (the input itself when it is not a chunk,
    /// or the reassembled object after the last chunk), or null while a chunk sequence is incomplete.
    /// </summary>
    /// <exception cref="RpcProtocolException">The sequence violates the v2 framing rules.</exception>
    public JsonElement? Push(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object) throw new RpcProtocolException("rpc frame must be an object");
        if (!IsChunk(frame))
        {
            if (_pending is not null) throw new RpcProtocolException("rpc chunk sequence interrupted");
            return frame;
        }

        if (!TryGetString(frame, "chunkId", out var chunkId) || chunkId.Length == 0 || chunkId.Length > 128 ||
            !TryGetInt(frame, "index", out var index) || !TryGetInt(frame, "count", out var count) ||
            !TryGetInt(frame, "byteLength", out var byteLength) ||
            index < 0 || count < 2 || count > MaxChunkCount || index >= count ||
            byteLength < MaxFrameBytes || byteLength > MaxReassembledBytes)
            throw new RpcProtocolException("invalid rpc chunk metadata");

        var bytes = DecodeBase64(frame);
        if (bytes.Length > ChunkPayloadBytes) throw new RpcProtocolException("rpc chunk payload exceeds the transport limit");

        if (_pending is null)
        {
            if (index != 0) throw new RpcProtocolException("rpc chunk sequence must start at index 0");
            _pending = new Pending(chunkId, count, byteLength);
        }
        var p = _pending;
        if (p.ChunkId != chunkId || p.Count != count || p.ByteLength != byteLength || p.NextIndex != index)
            throw new RpcProtocolException("rpc chunk sequence mismatch");
        if (p.ReceivedBytes + bytes.Length > p.ByteLength)
            throw new RpcProtocolException("rpc chunk sequence exceeds declared length");
        bytes.CopyTo(p.Buffer, p.ReceivedBytes);
        p.ReceivedBytes += bytes.Length;
        p.NextIndex++;
        if (p.NextIndex < p.Count) return null;
        _pending = null;
        if (p.ReceivedBytes != p.ByteLength) throw new RpcProtocolException("rpc chunk sequence length mismatch");

        string json;
        try
        {
            json = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(p.Buffer);
        }
        catch (DecoderFallbackException)
        {
            throw new RpcProtocolException("rpc chunk payload is not valid UTF-8");
        }
        JsonElement result;
        try
        {
            using var doc = JsonDocument.Parse(json, RpcConnection.ParseOptions);
            result = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new RpcProtocolException("rpc chunk payload is not valid JSON: " + e.Message);
        }
        if (result.ValueKind != JsonValueKind.Object) throw new RpcProtocolException("rpc frame must be an object");
        if (IsChunk(result)) throw new RpcProtocolException("rpc chunk payload cannot be another chunk");
        return result;
    }

    public void Reset() => _pending = null;

    private static bool IsChunk(JsonElement e) =>
        e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.ValueEquals("rpc_chunk");

    private static bool TryGetString(JsonElement e, string name, out string value)
    {
        value = "";
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString()!;
        return true;
    }

    private static bool TryGetInt(JsonElement e, string name, out int value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value);
    }

    /// <summary>Strict canonical base64, as the official decoder demands (re-encoding must round-trip).</summary>
    private static byte[] DecodeBase64(JsonElement frame)
    {
        if (!TryGetString(frame, "data", out var data) || data.Length == 0 || data.Length % 4 != 0)
            throw new RpcProtocolException("invalid rpc chunk data");
        var bytes = new byte[Base64.GetMaxDecodedFromUtf8Length(data.Length)];
        if (!Convert.TryFromBase64String(data, bytes, out var written) || data.Contains('\n') || data.Contains(' '))
            throw new RpcProtocolException("invalid rpc chunk data");
        Array.Resize(ref bytes, written);
        if (Convert.ToBase64String(bytes) != data) throw new RpcProtocolException("invalid rpc chunk data");
        return bytes;
    }
}

public sealed class RpcProtocolException(string message) : Exception(message);
