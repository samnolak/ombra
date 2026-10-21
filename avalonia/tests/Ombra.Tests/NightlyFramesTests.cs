using System.Runtime.Versioning;
using Ombra.App.Nightly;

namespace Ombra.Tests;

/// <summary>
/// A Nightly pane's surface list (<see cref="NightlyFrames"/>) against Nightly's sent-surface FIFO: at most
/// <see cref="NightlyFrames.MaxSurfaces"/> held, a repeated id or a new size lets the old ones go, and every reference
/// it takes is given back exactly once. Counted references stand in for CoreFoundation's.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class NightlyFramesTests
{
    private sealed class Refs
    {
        public readonly Dictionary<nint, int> Counts = [];
        public nint Retain(nint s) { Counts[s] = Counts.GetValueOrDefault(s) + 1; return s; }
        public void Release(nint s) { Assert.True(Counts.GetValueOrDefault(s) > 0, $"over-release of {s}"); Counts[s]--; }
        public int Held => Counts.Values.Sum();
    }

    private static NightlyProtocol.SurfaceInfo Info(uint id, int width = 800, int height = 600) => new(1, id, width, height);

    [Fact]
    public void KeepsTheLatestSixteenAnnounced()
    {
        var refs = new Refs();
        var frames = new NightlyFrames(refs.Retain, refs.Release);
        for (uint id = 1; id <= 100; id++) frames.AddSurface(Info(id), (nint)id);

        Assert.Equal(NightlyFrames.MaxSurfaces, frames.Count);
        Assert.Equal(NightlyFrames.MaxSurfaces, refs.Held);
        Assert.Equal(0, frames.Surface(84));
        for (uint id = 85; id <= 100; id++) Assert.Equal((nint)id, frames.Surface(id));
    }

    [Fact]
    public void ARepeatedIdReplacesTheOldReferenceAndCountsAsNewest()
    {
        var refs = new Refs();
        var frames = new NightlyFrames(refs.Retain, refs.Release);
        for (uint id = 1; id <= 16; id++) frames.AddSurface(Info(id), (nint)id);
        frames.AddSurface(Info(1), 1001); // re-announced after Nightly dropped it

        Assert.Equal(16, frames.Count);
        Assert.Equal(0, refs.Counts[1]);
        Assert.Equal(1001, frames.Surface(1));

        frames.AddSurface(Info(17), 17); // the oldest now is 2, not 1
        Assert.Equal(0, frames.Surface(2));
        Assert.Equal(1001, frames.Surface(1));
        Assert.Equal(16, refs.Held);
    }

    [Fact]
    public void ANewSizeLetsTheOtherSizesGo()
    {
        var refs = new Refs();
        var frames = new NightlyFrames(refs.Retain, refs.Release);
        for (uint id = 1; id <= 5; id++) frames.AddSurface(Info(id), (nint)id);
        frames.AddSurface(Info(6, 1024, 768), 6);

        Assert.Equal(1, frames.Count);
        Assert.Equal(1, refs.Held);
        Assert.Equal(6, frames.Surface(6));
    }

    [Fact]
    public void ClearGivesEveryReferenceBackOnce()
    {
        var refs = new Refs();
        var frames = new NightlyFrames(refs.Retain, refs.Release);
        for (uint id = 1; id <= 40; id++) frames.AddSurface(Info(id % 20), (nint)(id + 100));
        frames.SetLatest(new NightlyProtocol.FrameInfo(1, 19, 1, 0, 800, 600));
        Assert.True(frames.HasFrame);

        frames.Clear();
        frames.Dispose();

        Assert.Equal(0, frames.Count);
        Assert.False(frames.HasFrame);
        Assert.All(refs.Counts.Values, n => Assert.Equal(0, n));
    }
}
