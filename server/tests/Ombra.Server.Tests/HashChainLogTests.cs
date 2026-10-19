using System.Text.Json.Nodes;
using Ombra.Server.Logs;

namespace Ombra.Server.Tests;

public sealed class HashChainLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ombra-chain-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Appends_chain_to_local_and_sink_and_detects_edits_and_divergence()
    {
        var local = Path.Combine(_dir, "a.log");
        var sink = Path.Combine(_dir, "remote", "a.log");
        var log = new HashChainLog(local, sink);
        var t = DateTimeOffset.Parse("2026-10-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 3; i++) log.Append(new JsonObject { ["blobId"] = i.ToString(), ["by"] = "ttl" }, t.AddSeconds(i));

        var chain = HashChainLog.ReadVerified(local);
        Assert.Equal([1L, 2L, 3L], chain.Select(e => e.Seq));
        Assert.Equal(HashChainLog.ZeroHash, chain[0].PrevHash);
        Assert.Equal(chain.Select(e => e.Hash), HashChainLog.ReadVerified(sink).Select(e => e.Hash));

        // a shorter copy is a prefix: reconcile picks the longer
        var prefix = Path.Combine(_dir, "prefix.log");
        File.WriteAllLines(prefix, File.ReadAllLines(local).Take(2));
        Assert.Equal(3, HashChainLog.Reconcile(HashChainLog.ReadVerified(prefix), chain).Count);

        // removing a middle line breaks the chain
        var cut = Path.Combine(_dir, "cut.log");
        var lines = File.ReadAllLines(local);
        File.WriteAllLines(cut, [lines[0], lines[2]]);
        Assert.Throws<ChainBrokenException>(() => HashChainLog.ReadVerified(cut));

        // a different valid chain of the same length diverges
        var other = Path.Combine(_dir, "other.log");
        var otherLog = new HashChainLog(other);
        for (var i = 0; i < 3; i++) otherLog.Append(new JsonObject { ["blobId"] = (i + 10).ToString(), ["by"] = "ttl" }, t);
        Assert.Throws<ChainBrokenException>(() => HashChainLog.Reconcile(chain, HashChainLog.ReadVerified(other)));
    }
}
