using System.Text;
using System.Text.Json;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>Mirrors the validation of the official decoder (rpc-frame.ts, tag v18.2.0).</summary>
public sealed class RpcFrameDecoderTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<JsonElement> Chunks(string payloadJson, string chunkId = "rpc-1")
    {
        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        var count = (bytes.Length + 262143) / 262144;
        var list = new List<JsonElement>();
        for (var i = 0; i < count; i++)
        {
            var slice = bytes.AsSpan(i * 262144, Math.Min(262144, bytes.Length - i * 262144)).ToArray();
            list.Add(J(JsonSerializer.Serialize(new { type = "rpc_chunk", chunkId, index = i, count, byteLength = bytes.Length, data = Convert.ToBase64String(slice) })));
        }
        return list;
    }

    private static string BigPayload(int chars = 1_300_000) => JsonSerializer.Serialize(new { type = "response", id = "x", data = new string('z', chars) + "ü漢" });

    [Fact]
    public void Passes_plain_frames_through()
    {
        var d = new RpcFrameDecoder();
        Assert.Equal("agent_start", d.Push(J("""{"type":"agent_start"}"""))!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void Reassembles_a_chunk_sequence_including_multibyte_utf8()
    {
        var payload = BigPayload();
        var chunks = Chunks(payload);
        Assert.True(chunks.Count >= 5);
        var d = new RpcFrameDecoder();
        for (var i = 0; i < chunks.Count - 1; i++) Assert.Null(d.Push(chunks[i]));
        var result = d.Push(chunks[^1])!.Value;
        Assert.Equal(payload, result.GetRawText());
        Assert.False(d.HasPendingChunks);
    }

    [Fact]
    public void Rejects_a_frame_interleaved_into_a_sequence()
    {
        var chunks = Chunks(BigPayload());
        var d = new RpcFrameDecoder();
        d.Push(chunks[0]);
        var e = Assert.Throws<RpcProtocolException>(() => d.Push(J("""{"type":"agent_start"}""")));
        Assert.Contains("interrupted", e.Message);
    }

    [Fact]
    public void Rejects_out_of_order_and_mismatched_chunks()
    {
        var chunks = Chunks(BigPayload());
        Assert.Throws<RpcProtocolException>(() => new RpcFrameDecoder().Push(chunks[1]));
        var d = new RpcFrameDecoder();
        d.Push(chunks[0]);
        Assert.Throws<RpcProtocolException>(() => d.Push(chunks[2]));
        var other = Chunks(BigPayload(), chunkId: "rpc-2");
        var d2 = new RpcFrameDecoder();
        d2.Push(chunks[0]);
        Assert.Throws<RpcProtocolException>(() => d2.Push(other[1]));
    }

    [Theory]
    [InlineData("""{"type":"rpc_chunk","chunkId":"a","index":0,"count":1,"byteLength":2000000,"data":"AAAA"}""")] // count < 2
    [InlineData("""{"type":"rpc_chunk","chunkId":"a","index":0,"count":2,"byteLength":100,"data":"AAAA"}""")] // below the physical limit
    [InlineData("""{"type":"rpc_chunk","chunkId":"a","index":0,"count":2,"byteLength":70000000,"data":"AAAA"}""")] // above 64 MiB
    [InlineData("""{"type":"rpc_chunk","chunkId":"","index":0,"count":2,"byteLength":2000000,"data":"AAAA"}""")] // empty id
    [InlineData("""{"type":"rpc_chunk","chunkId":"a","index":2,"count":2,"byteLength":2000000,"data":"AAAA"}""")] // index >= count
    [InlineData("""{"type":"rpc_chunk","chunkId":"a","index":0,"count":2,"byteLength":2000000,"data":"AA A"}""")] // not base64
    [InlineData("""{"type":"rpc_chunk","chunkId":"a","index":0,"count":2,"byteLength":2000000,"data":"AAB="}""")] // non-canonical base64
    [InlineData("""{"type":"rpc_chunk","chunkId":"a","index":0,"count":2,"byteLength":2000000,"data":""}""")] // empty data
    public void Rejects_invalid_metadata_and_data(string frame) =>
        Assert.Throws<RpcProtocolException>(() => new RpcFrameDecoder().Push(J(frame)));

    [Fact]
    public void Rejects_declared_length_mismatch()
    {
        var chunks = Chunks(BigPayload());
        var last = chunks[^1];
        // Same chunks, but claim one byte more than was sent.
        var d = new RpcFrameDecoder();
        var declared = chunks[0].GetProperty("byteLength").GetInt32() + 1;
        JsonElement Rewrite(JsonElement c) => J(JsonSerializer.Serialize(new
        {
            type = "rpc_chunk", chunkId = "rpc-1", index = c.GetProperty("index").GetInt32(), count = c.GetProperty("count").GetInt32(),
            byteLength = declared, data = c.GetProperty("data").GetString(),
        }));
        for (var i = 0; i < chunks.Count - 1; i++) d.Push(Rewrite(chunks[i]));
        Assert.Throws<RpcProtocolException>(() => d.Push(Rewrite(last)));
    }

    [Fact]
    public void Rejects_invalid_utf8_payload()
    {
        var bytes = new byte[1_100_000];
        Array.Fill(bytes, (byte)'a');
        bytes[0] = (byte)'"';
        bytes[500] = 0xFF;
        bytes[^1] = (byte)'"';
        var count = (bytes.Length + 262143) / 262144;
        var d = new RpcFrameDecoder();
        var thrown = Assert.Throws<RpcProtocolException>(() =>
        {
            for (var i = 0; i < count; i++)
            {
                var slice = bytes.AsSpan(i * 262144, Math.Min(262144, bytes.Length - i * 262144)).ToArray();
                d.Push(J(JsonSerializer.Serialize(new { type = "rpc_chunk", chunkId = "u", index = i, count, byteLength = bytes.Length, data = Convert.ToBase64String(slice) })));
            }
        });
        Assert.Contains("UTF-8", thrown.Message);
    }
}
