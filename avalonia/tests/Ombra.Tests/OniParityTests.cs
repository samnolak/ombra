using System.Text.Json;
using Ombra.App.Pets.Oni;
using Xunit;

namespace Ombra.Tests;

/// <summary>
/// ONI-01's native players against the reference ones (tools/pets/oni-01/runtime/*.cjs), step for step: the traces in
/// Fixtures/oni-golden.json come from the JavaScript players (Fixtures/oni-golden.cjs regenerates them).
/// </summary>
public sealed class OniParityTests
{
    private const double Tolerance = 2e-6;

    private static readonly Lazy<JsonDocument> Golden = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "oni-golden.json"))));

    internal static OniSpec Spec { get; } = OniSpec.Load(path =>
        File.OpenRead(RuntimeInstallerTests.RepoFile(Path.Combine("avalonia", "src", "Ombra.App", "Assets", "Pets", "Oni01", path))));

    private static OniDirector NewDirector() => new(Spec.States.ToDictionary(s => s.Key, s => s.Value.Clip));

    private static JsonElement.ArrayEnumerator Rows(string name) => Golden.Value.RootElement.GetProperty(name).EnumerateArray();

    private static void Near(double expected, double actual, string what)
    {
        if (Math.Abs(expected - actual) > Tolerance) Assert.Fail($"{what}: expected {expected}, got {actual}");
    }

    private static void SameStep(JsonElement row, OniDirector d, string where)
    {
        var what = $"{where}: {row}";
        Assert.True(row[0].GetString() == d.State, what + " state " + d.State);
        Assert.True(row[1].GetString() == d.Mode, what + " mode " + d.Mode);
        Assert.True(row[2].GetInt32() == d.Frame, what + " frame " + d.Frame);
        Near(row[3].GetDouble(), Math.Round(d.Phase, 6), what + " phase");
        Assert.True(row[4].GetString() == d.Cue, what + " cue " + d.Cue);
        Near(row[5].GetDouble(), Math.Round(d.Velocity, 6), what + " velocity");
    }

    private static readonly (string Event, double Seconds)[] Script =
    [
        ("idle", 2), ("thinking", 4), ("working", 5), ("waiting", 4), ("working", 4), ("done", 4), ("idle", 2), ("sleep", 4),
        ("type", 3), ("working", 3), ("tool-error", 4), ("working", 2), ("abort", 3), ("error", 4), ("idle", 3),
    ];

    private static readonly (string Event, double Seconds)[] Interruptions =
    [
        ("idle", .5), ("working", .35), ("waiting", .4), ("error", .35), ("thinking", .4), ("waiting", 1.8), ("working", 2),
        ("drag-left", .4), ("error", 2.5), ("sleep", 2.2), ("drag-right", .4), ("waiting", 2.6), ("working", 2.2), ("done", 4.5), ("idle", 1.5),
    ];

    [Theory]
    [InlineData("script")]
    [InlineData("interruptions")]
    public void The_director_plays_the_reference_scenarios_frame_for_frame(string name)
    {
        var d = NewDirector();
        using var rows = Rows(name);
        var n = 0;
        foreach (var (e, seconds) in name == "script" ? Script : Interruptions)
        {
            var drag = e is "drag-left" or "drag-right";
            d.Event(drag ? "drag-start" : e);
            var steps = (int)Math.Round(seconds * 60);
            for (var i = 0; i < steps; i++)
            {
                d.Step(1 / 60.0);
                Assert.True(rows.MoveNext(), "trace ended early");
                SameStep(rows.Current, d, $"{name} #{n++} after {e}");
            }
            if (drag) d.Event("land");
        }
        Assert.False(rows.MoveNext(), "trace has more steps");
    }

    [Fact]
    public void Uneven_frames_and_stalls_match_the_reference()
    {
        var d = NewDirector();
        double[] dts = [1 / 144.0, 1 / 30.0, 1 / 60.0, .25, 1 / 90.0, 7, 1 / 60.0];
        string[] events = ["working", "done", "tool-error", "waiting", "idle", "sleep", "type", "thinking", "abort", "error", "idle"];
        using var rows = Rows("stalls");
        for (var k = 0; k < events.Length; k++)
        {
            d.Event(events[k]);
            for (var i = 0; i < 40; i++)
            {
                d.Step(dts[(i + k) % dts.Length]);
                Assert.True(rows.MoveNext());
                SameStep(rows.Current, d, $"{events[k]} #{i}");
            }
        }
    }

    [Fact]
    public void The_carried_body_swings_falls_and_lands_as_the_reference()
    {
        var b = new OniDragBody(360, 284, 90, 630, 155, 284) { Length = 128 * .4 };
        static (double X, double Y)? Path(double t) =>
            t < .5 ? (360 + 400 * t, 284 - 240 * t) : t < 1 ? (560 - 500 * (t - .5), 164 + 60 * (t - .5)) : null;
        b.Grab(360, 284);
        double[] dts = [1 / 60.0, 1 / 60.0, 1 / 120.0, 1 / 30.0, 1 / 60.0];
        var t = 0.0;
        using var rows = Rows("drag");
        for (var i = 0; i < 200; i++)
        {
            var dt = dts[i % dts.Length];
            t += dt;
            if (Path(t) is { } p) b.Move(p.X, p.Y);
            else if (b.Held) b.Release();
            b.Step(dt);
            Assert.True(rows.MoveNext());
            var r = rows.Current;
            var at = $"step {i}: {r}";
            Near(r[0].GetDouble(), Math.Round(b.X, 6), at + " x");
            Near(r[1].GetDouble(), Math.Round(b.Y, 6), at + " y");
            Near(r[2].GetDouble(), Math.Round(b.Vx, 6), at + " vx");
            Near(r[3].GetDouble(), Math.Round(b.Vy, 6), at + " vy");
            Near(r[4].GetDouble(), Math.Round(b.Angle, 6), at + " angle");
            Near(r[5].GetDouble(), Math.Round(b.Phase, 6), at + " phase");
            Assert.Equal(r[6].GetBoolean(), b.Held);
            Assert.Equal(r[7].GetBoolean(), b.Air);
            Assert.Equal(r[8].GetInt32(), b.Impacts);
            Near(r[9].GetDouble(), Math.Round(b.ImpactSpeed, 6), at + " impact");
        }
        // It came down: on the floor, with one landing hard enough for dust
        Assert.False(b.Air);
        Assert.True(b.Impacts >= 1);
    }

    [Fact]
    public void Spirits_take_the_reference_poses()
    {
        foreach (var r in Rows("spirits"))
        {
            var p = OniEffects.SpiritPose(r[0].GetString()!, r[1].GetDouble(), r[2].GetInt32());
            var at = r.ToString();
            Near(r[3].GetDouble(), Math.Round(p.X, 6), at + " x");
            Near(r[4].GetDouble(), Math.Round(p.Y, 6), at + " y");
            Near(r[5].GetDouble(), Math.Round(p.Depth, 6), at + " depth");
            Near(r[6].GetDouble(), Math.Round(p.Scale, 6), at + " scale");
            Near(r[7].GetDouble(), Math.Round(p.Alpha, 6), at + " alpha");
            Near(r[8].GetDouble(), Math.Round(p.Tempo, 6), at + " tempo");
            Near(r[9].GetDouble(), Math.Round(p.Lean, 6), at + " lean");
        }
    }

    [Fact]
    public void Clips_sample_the_reference_drawings()
    {
        foreach (var r in Rows("samples"))
        {
            var clip = Spec.States[r[0].GetString()!].Clip;
            var t = r[1].GetDouble();
            Assert.True(r[2].GetInt32() == clip.Sample(t).Frame, $"{r} loop");
            Assert.True(r[3].GetInt32() == clip.Sample(t, loop: false).Frame, $"{r} once");
        }
    }
}
