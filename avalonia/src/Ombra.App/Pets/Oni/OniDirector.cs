namespace Ombra.App.Pets.Oni;

/// <summary>
/// What ONI does and how it moves between poses: a port of the reference <c>Director</c> (scenario-player.cjs) with the
/// same semantics and units — the director's clock in seconds, clip times in milliseconds. Events set what the agent
/// does (a base), short reactions (a flash: joy after a run, a wince for a failed tool) and whether it is carried; the
/// pose follows through bridges (the entry transitions, a critically damped phase), a settle back along the loop's
/// drawings before leaving, and loops. Pure: the time is passed in through <see cref="Step"/>.
/// </summary>
public sealed class OniDirector
{
    public static readonly string[] Ids = ["idle", "thinking", "working", "waiting", "joy", "error", "sleeping", "dragging"];

    /// <summary>How long each bridge takes (seconds); idle has none (0.8 is the reference's fallback).</summary>
    private static readonly Dictionary<string, double> Durations = new()
    {
        ["thinking"] = .7, ["working"] = 1.3, ["waiting"] = .75, ["joy"] = .65, ["error"] = .6, ["sleeping"] = 1.25, ["dragging"] = .65,
    };

    public const double SleepAfter = 180, ToolErrorFor = 2.4, JoyVisibleFor = 2.4, JoyDeadline = 8, VisualDeltaLimit = .1;

    private sealed class Flash(string id, double until, bool countsVisible)
    {
        public string Id = id;
        public double Until = until;
        /// <summary>Joy: counted from its visible loop (null until then); a failed tool's wince does not count this way.</summary>
        public readonly bool CountsVisible = countsVisible;
        public double? VisibleUntil;
    }

    private readonly IReadOnlyDictionary<string, OniClip> _clips;
    private Flash? _flash;
    private int _settleIndex;
    private double _settleAge;

    public OniDirector(IReadOnlyDictionary<string, OniClip> clips) => _clips = clips;

    public double Clock { get; private set; }
    public double LastActive { get; private set; }
    public string Base { get; private set; } = "idle";
    public bool Carried { get; private set; }
    public string State { get; private set; } = "idle";
    public string Wanted { get; private set; } = "idle";
    public double Phase { get; private set; }
    public double Velocity { get; private set; }
    /// <summary>loop | bridge | settle.</summary>
    public string Mode { get; private set; } = "loop";
    public double Age { get; private set; }
    /// <summary>The pose being asked for (effects follow it at once while the body finishes its transition).</summary>
    public string Cue { get; private set; } = "idle";
    public double CueAge { get; private set; }
    public double Settle { get; private set; }
    public double Attention { get; private set; }
    public bool Hover { get; set; }
    /// <summary>The flash showing or pending (joy, error), if any.</summary>
    public string? Flashing => _flash?.Id;

    /// <summary>When the flash ends at the latest (director clock), if there is one.</summary>
    public double? FlashUntil => _flash?.Until;

    /// <summary>The pointer moved over the pet: it notices (the reference's hover also counts as activity).</summary>
    public void Notice() => LastActive = Clock;

    public void Event(string kind)
    {
        switch (kind)
        {
            case "drag-start": Carried = true; LastActive = Clock; break;
            case "land": Carried = false; LastActive = Clock; break;
            case "done":
                if (Base is not ("waiting" or "error"))
                {
                    Base = "idle";
                    _flash = new Flash("joy", Clock + JoyDeadline, countsVisible: true);
                }
                LastActive = Clock;
                break;
            case "tool-error": _flash = new Flash("error", Clock + ToolErrorFor, countsVisible: false); LastActive = Clock; break;
            case "abort": Base = "idle"; _flash = null; LastActive = Clock; break;
            case "click" or "type": LastActive = Clock; Attention = 1; break;
            case "sleep": Base = "idle"; _flash = null; LastActive = Clock - SleepAfter; break;
            case "idle" or "thinking" or "working" or "waiting" or "error":
                Base = kind;
                LastActive = Clock;
                if (kind is "waiting" or "error" || (kind != "idle" && _flash?.Id == "joy")) _flash = null;
                break;
            default: throw new ArgumentException("Unknown event " + kind, nameof(kind));
        }
        Resolve();
    }

    /// <summary>What the pet should be doing now (scenario-player.cjs <c>target</c>).</summary>
    public string Target(bool ignoreCarry = false)
    {
        if (Carried && !ignoreCarry) return "dragging";
        if (Base is "waiting" or "error") return Base;
        if (_flash is { } f && Clock < f.Until) return f.Id;
        if (Base != "idle") return Base;
        return Clock - LastActive >= SleepAfter ? "sleeping" : "idle";
    }

    private void Resolve()
    {
        Wanted = Target();
        if (Cue != Wanted)
        {
            Cue = Wanted;
            CueAge = 0;
        }
    }

    private int LoopFrame()
    {
        if (!_clips.TryGetValue(State, out var clip)) return 0;
        var t = Age * 1000 % clip.Duration;
        for (var i = clip.Keys.Count - 1; i >= 0; i--)
            if (t >= clip.Keys[i].Start) return clip.Keys[i].Frame;
        return 0;
    }

    /// <summary>Advances by <paramref name="dt"/> seconds: the semantic clock fully, the animation at most <see cref="VisualDeltaLimit"/>.</summary>
    public void Step(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt), "Invalid delta");
        Clock += dt;
        if (Base != "idle") LastActive = Clock;
        // Success must survive putting away the laptop: the short celebration counts from its visible loop, with an
        // absolute deadline against stale reactions
        if (_flash is { Id: "joy", CountsVisible: true, VisibleUntil: null } joy && State == "joy" && Mode == "loop")
        {
            joy.VisibleUntil = Clock + JoyVisibleFor;
            joy.Until = Math.Min(joy.Until, joy.VisibleUntil.Value);
        }
        if (_flash is { } f && Clock >= f.Until) _flash = null;
        Resolve();
        CueAge += dt;
        Attention *= Math.Exp(-dt * 3);
        // Bound animation work after a stall; never skip across drawings
        var remaining = Math.Min(dt, VisualDeltaLimit);
        while (remaining > 0)
        {
            var h = Math.Min(remaining, 1 / 120.0);
            Animate(h);
            remaining -= h;
        }
    }

    private void Animate(double dt)
    {
        if (Mode == "loop")
        {
            if (State == Wanted)
            {
                Age += dt;
                return;
            }
            if (State == "idle")
            {
                State = Wanted;
                Phase = 0;
                Velocity = 0;
                Mode = "bridge";
                return;
            }
            // Back along adjacent loop drawings to the entry pose before leaving
            var clip = _clips[State];
            var t = Age * 1000 % clip.Duration;
            _settleIndex = LastIndex(clip.Keys, t);
            _settleAge = 0;
            Settle = clip.Keys[_settleIndex].Frame;
            Mode = "settle";
        }
        if (Mode == "settle")
        {
            if (Wanted == State)
            {
                var frame = (int)Math.Floor(Settle + 0.5);
                var start = _clips.TryGetValue(State, out var c) && c.Keys.FirstOrDefault(k => k.Frame == frame) is { Duration: > 0 } key ? key.Start : 0;
                Age = start / 1000;
                Mode = "loop";
                return;
            }
            _settleAge += dt;
            var clip = _clips[State];
            var keys = clip.Keys;
            var neutral = clip.FullCycle ? clip.NeutralFrames.Contains((int)Settle) : _settleIndex == 0;
            var interval = clip.FullCycle ? keys[_settleIndex].Duration / 1000 : .08;
            if (_settleAge >= interval && !neutral)
            {
                _settleAge -= interval;
                _settleIndex = clip.FullCycle ? (_settleIndex + 1) % keys.Count : Math.Max(0, _settleIndex - 1);
                Settle = keys[_settleIndex].Frame;
            }
            if (clip.FullCycle ? clip.NeutralFrames.Contains((int)Settle) : _settleIndex == 0)
            {
                Phase = 1;
                Velocity = 0;
                Mode = "bridge";
            }
            return;
        }
        var target = Wanted == State ? 1.0 : 0.0;
        var w = 8 / Durations.GetValueOrDefault(State, .8);
        var q = Phase - target;
        var cc = Velocity + w * q;
        var e = Math.Exp(-w * dt);
        Phase = Math.Clamp(target + (q + cc * dt) * e, 0, 1);
        Velocity = (Velocity - w * cc * dt) * e;
        if (Math.Abs(Phase - target) < .004 && Math.Abs(Velocity) < .025)
        {
            Phase = target;
            Velocity = 0;
            if (target == 1)
            {
                Mode = "loop";
                Age = 0;
            }
            else
            {
                State = "idle";
                Mode = "loop";
                Age = 0;
            }
        }
    }

    private static int LastIndex(IReadOnlyList<OniKey> keys, double t)
    {
        for (var i = keys.Count - 1; i >= 0; i--)
            if (t >= keys[i].Start) return i;
        return -1;
    }

    /// <summary>The drawing to show (scenario-player.cjs <c>sample().frame</c>).</summary>
    public int Frame => Mode == "settle" ? (int)Math.Floor(Settle + 0.5) : LoopFrame();
}
