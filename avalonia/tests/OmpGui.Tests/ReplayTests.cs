using System.IO.Pipelines;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// Feeds a recorded omp stdout (e.g. a 1.5 MB reply that omp sends as hundreds of MB of v2 chunks) through the
/// transport. Recordings are too large for the repository: set OMPGUI_REPLAY to one made with
/// `... --mode rpc-ui > out.jsonl` whose first command was {"id":"n","type":"negotiate_protocol","protocolVersion":2}.
/// </summary>
public sealed class ReplayTests
{
    [Fact]
    public async Task Replays_a_recorded_stdout_completely()
    {
        var path = Environment.GetEnvironmentVariable("OMPGUI_REPLAY");
        if (path is null) { Assert.Skip("OMPGUI_REPLAY not set"); return; }
        var pipe = new Pipe();
        var sink = new MemoryStream();
        await using var conn = new RpcConnection(pipe.Reader.AsStream(), sink);
        var text = await File.ReadAllTextAsync(path);
        // The recording answered id "n"; the client's first request id is "g1".
        var bytes = System.Text.Encoding.UTF8.GetBytes(text.Replace("{\"id\":\"n\",\"type\":\"response\"", "{\"id\":\"g1\",\"type\":\"response\""));
        var negotiated = conn.NegotiateAsync();
        var firstLine = Array.IndexOf(bytes, (byte)'\n') + 1; // the ready frame
        var feed = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(bytes.AsMemory(0, firstLine));
            // A real omp answers only after the request: wait until negotiate_protocol is pending.
            while (conn.PendingRequestCount == 0) await Task.Delay(5);
            for (var i = firstLine; i < bytes.Length; i += 65536)
            {
                await pipe.Writer.WriteAsync(bytes.AsMemory(i, Math.Min(65536, bytes.Length - i)));
            }
            await pipe.Writer.CompleteAsync();
        });
        var types = new List<string>();
        await foreach (var f in conn.Events.ReadAllAsync().WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token))
            types.Add(f.Type);
        await feed;
        Assert.Equal(2, await negotiated);
        Assert.Contains("agent_end", types);
        Assert.DoesNotContain(RpcFrame.ProtocolError, types);
        Assert.DoesNotContain(RpcFrame.Malformed, types);
        Assert.Contains("end of stream", conn.CloseReason!.Message);
    }
}
