using System.Text.Json;
using Avalonia.Platform;

namespace Ombra.App.Pets.Oni;

/// <summary>One drawing in a sheet: its cell, the ink inside it (cell-relative, inclusive) and the point that is put on the ground (or the head, for dragging).</summary>
public sealed record OniFrame(int Sx, int Sy, int Cw, int Ch, int Left, int Top, int Right, int Bottom, double AnchorX, double AnchorY);

/// <summary>A clip key: which drawing, from when, for how long (milliseconds, as the manifest has them).</summary>
public readonly record struct OniKey(int Frame, double Start, double Duration);

/// <summary>A state's loop (states-player.cjs): keys in milliseconds; a full cycle (joy) leaves through its neutral drawings.</summary>
public sealed record OniClip(IReadOnlyList<OniKey> Keys, double Duration, bool FullCycle, IReadOnlyList<int> NeutralFrames)
{
    /// <summary>The key at <paramref name="time"/> milliseconds (states-player.cjs <c>sample</c>).</summary>
    public OniKey Sample(double time, bool loop = true)
    {
        var t = loop ? ((time % Duration) + Duration) % Duration : Math.Max(0, Math.Min(time, Duration - 0.001));
        for (var i = Keys.Count - 1; i >= 0; i--)
            if (t >= Keys[i].Start) return Keys[i];
        return Keys[0];
    }

    /// <summary>The ping-pong timeline of a sheet (states-player.cjs <c>timeline</c>): hold on the first, a pause at the apex, steps between.</summary>
    public static OniClip Timeline(IReadOnlyList<int> frames, double step, double hold, double apex)
    {
        if (frames.Count < 2 || new[] { step, hold, apex }.Any(n => !double.IsFinite(n) || n <= 0)) throw new ArgumentException("Invalid clip");
        var sequence = frames.Concat(frames.Skip(1).Take(frames.Count - 2).Reverse()).ToList();
        var keys = new List<OniKey>(sequence.Count);
        double total = 0;
        for (var i = 0; i < sequence.Count; i++)
        {
            var duration = i == 0 ? hold : i == frames.Count - 1 ? apex : step;
            keys.Add(new OniKey(sequence[i], total, duration));
            total += duration;
        }
        return new OniClip(keys, total, false, []);
    }
}

/// <summary>A pose the pet holds (frames/approved-v1/manifest.json): its sheet, drawings, loop and how it is placed.</summary>
public sealed record OniState(string Id, string Source, IReadOnlyList<OniFrame> Frames, OniClip Clip, double Scale, bool HeadAnchored, int StaticFrame, string? Entry);

/// <summary>A sheet of transition or effect drawings (transitions/manifest.json); drawn at <c>height / ReferenceHeight</c>.</summary>
public sealed record OniSheet(string Id, string Source, IReadOnlyList<OniFrame> Frames, double ReferenceHeight, IReadOnlyList<int>? Sequence)
{
    /// <summary>The drawing for a transition at <paramref name="phase"/> (interaction.cjs <c>transitionFrame</c>).</summary>
    public int FrameAt(double phase)
    {
        var count = Sequence?.Count ?? Frames.Count;
        var i = (int)Math.Floor(Math.Clamp(phase, 0, 1) * (count - 1) + 0.5); // JS Math.round
        return Sequence is { } s ? s[i] : i;
    }
}

/// <summary>
/// ONI-01 as delivered (tools/pets/oni-01/runtime, copied to Assets/Pets/Oni01): the eight poses, the seven
/// transitions, the effect sheets. Geometry in source pixels, clip times in milliseconds.
/// </summary>
public sealed class OniSpec
{
    public const string Root = "avares://Ombra/Assets/Pets/Oni01/";

    /// <summary>The transition that leads into each pose from idle (scenario-preview.js <c>bridgeIds</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> Bridges = new Dictionary<string, string>
    {
        ["working"] = "workEntry", ["sleeping"] = "sleepEntry", ["thinking"] = "thinkEntry", ["error"] = "errorEntry",
        ["joy"] = "joyEntry", ["waiting"] = "question", ["dragging"] = "pickup",
    };

    public required IReadOnlyDictionary<string, OniState> States { get; init; }
    public required IReadOnlyDictionary<string, OniSheet> Sheets { get; init; }

    public OniSheet Effects => Sheets["effects"];
    public OniSheet Flames => Sheets["flames"];
    public OniSheet Vfx => Sheets["vfx"];

    private static OniSpec? _shared;

    /// <summary>The app's copy (parsed once; small: geometry and timing only).</summary>
    public static OniSpec Shared => _shared ??= Load(path => AssetLoader.Open(new Uri(Root + path)));

    /// <summary>The asset path of a manifest's <c>source</c> ("frames/approved-v1/idle.png" → "frames/idle.png").</summary>
    public static string AssetPath(string source) =>
        source.StartsWith("frames/", StringComparison.Ordinal) ? "frames/" + Path.GetFileName(source) : source;

    public static OniSpec Load(Func<string, Stream> open)
    {
        using var framesDoc = JsonDocument.Parse(open("frames/manifest.json"));
        using var sheetsDoc = JsonDocument.Parse(open("transitions/manifest.json"));
        var states = new Dictionary<string, OniState>();
        foreach (var s in framesDoc.RootElement.GetProperty("states").EnumerateArray())
        {
            var id = s.GetProperty("id").GetString()!;
            var frames = s.GetProperty("frames").EnumerateArray().Select(f => new OniFrame(
                Int(f, "sx"), Int(f, "sy"), Int(f, "cw"), Int(f, "ch"), Int(f, "left"), Int(f, "top"), Int(f, "right"), Int(f, "bottom"),
                f.GetProperty("anchorX").GetDouble(), f.GetProperty("anchorY").GetDouble())).ToList();
            var c = s.GetProperty("clip");
            var keys = c.GetProperty("keys").EnumerateArray()
                .Select(k => new OniKey(Int(k, "frame"), k.GetProperty("start").GetDouble(), k.GetProperty("duration").GetDouble())).ToList();
            var clip = new OniClip(keys, c.GetProperty("duration").GetDouble(),
                c.TryGetProperty("fullCycle", out var fc) && fc.GetBoolean(),
                c.TryGetProperty("neutralFrames", out var nf) ? nf.EnumerateArray().Select(n => n.GetInt32()).ToList() : []);
            states[id] = new OniState(id, AssetPath(s.GetProperty("source").GetString()!), frames, clip, s.GetProperty("scale").GetDouble(),
                s.GetProperty("anchor").GetString() == "head",
                s.TryGetProperty("staticFrame", out var sf) ? sf.GetInt32() : keys[0].Frame,
                s.TryGetProperty("entry", out var e) ? e.GetString() : null);
        }
        var sheets = new Dictionary<string, OniSheet>();
        foreach (var p in sheetsDoc.RootElement.EnumerateObject())
        {
            var v = p.Value;
            var frames = v.GetProperty("frames").EnumerateArray().Select(f => new OniFrame(
                Int(f, "sx"), Int(f, "sy"), Int(f, "cw"), Int(f, "ch"), Int(f, "l"), Int(f, "t"), Int(f, "r"), Int(f, "b"),
                f.GetProperty("ax").GetDouble(), f.GetProperty("ay").GetDouble())).ToList();
            sheets[p.Name] = new OniSheet(p.Name, AssetPath(v.GetProperty("source").GetString()!), frames, v.GetProperty("referenceHeight").GetDouble(),
                v.TryGetProperty("sequence", out var q) ? q.EnumerateArray().Select(n => n.GetInt32()).ToList() : null);
        }
        return new OniSpec { States = states, Sheets = sheets };
    }

    private static int Int(JsonElement e, string name) => (int)Math.Round(e.GetProperty(name).GetDouble());
}
