namespace OmpGui.App.Pets;

/// <summary>
/// What makes a pet alive: a pure animation scheduler. Given the time it answers with a <see cref="PetPose"/> — eased
/// breathing, blinks every 2–6 s (sometimes a double blink), glances around, a small idle action every 8–20 s (a twitch,
/// a hop, a stretch with a yawn, a look around, a wiggle), each mood's own motion (a thought cloud with typing dots, a
/// busy bounce and a turning gear, a "!" that pops, a happy hop with sparkles, a wobble and a sweat drop, Zzz drifting
/// up), eased moves between moods, and reactions to the pointer, a click and typing. Random but seeded; the time is
/// passed in, so tests step through it. <see cref="Plan"/> schedules what comes next (call it before <see cref="Sample"/>
/// whenever the time moves on); <see cref="NextChange"/> says when the picture changes next, so a view wakes only then.
/// </summary>
public sealed class PetLife
{
    /// <summary>A mood that lasts this long stops moving (asleep, a question nobody answers, omp stopped): no timer at all.</summary>
    public static TimeSpan RestAfter(PetMood mood) => mood switch
    {
        PetMood.Sleeping => TimeSpan.FromSeconds(40),
        PetMood.Error => TimeSpan.FromSeconds(20),
        PetMood.Waiting => TimeSpan.FromSeconds(60),
        _ => TimeSpan.MaxValue,
    };

    /// <summary>How long a move from one mood to the next is eased.</summary>
    public const double Blend = 0.32;

    public enum IdleAction { Twitch, Hop, Stretch, LookAround, Wiggle }

    private readonly Random _rng;
    private readonly DateTimeOffset _origin;

    private PetMood _mood = PetMood.Idle, _prev = PetMood.Idle;
    private double _moodAt = -100, _prevAt = -100;

    // Scheduled: the next blink, glance, idle action
    private double _blinkAt;
    private bool _doubleBlink;
    private double _glanceAt, _glanceEnd;
    private int _glanceX, _glanceY;
    private double _actionAt;
    private IdleAction _action;

    // Reactions
    private double _patAt = -100, _hoverAt = -100, _attendUntil = -100;
    private bool _hovering;
    private int _hoverX, _hoverY;

    public PetLife(DateTimeOffset now, int? seed = null)
    {
        _rng = seed is { } s ? new Random(s) : new Random();
        _origin = now;
        _blinkAt = Uniform(0.8, 3);
        _glanceAt = Uniform(3, 7);
        _glanceEnd = _glanceAt + 1;
        _actionAt = Uniform(5, 12);
        _action = PickAction();
    }

    public PetMood Mood => _mood;

    private double T(DateTimeOffset now) => (now - _origin).TotalSeconds;

    private double Uniform(double a, double b) => a + (b - a) * _rng.NextDouble();

    // For tests: what is scheduled, in seconds since the start
    internal double NextBlink => _blinkAt;
    internal bool NextBlinkIsDouble => _doubleBlink;
    internal double NextAction => _actionAt;
    internal IdleAction NextActionKind => _action;
    internal double NextGlance => _glanceAt;

    private IdleAction PickAction() => _rng.Next(10) switch
    {
        0 or 1 or 2 => IdleAction.Twitch,
        3 or 4 => IdleAction.Hop,
        5 or 6 => IdleAction.LookAround,
        7 => IdleAction.Stretch,
        _ => IdleAction.Wiggle,
    };

    private static double Duration(IdleAction a) => a switch
    {
        IdleAction.Twitch => 0.5,
        IdleAction.Hop => 0.6,
        IdleAction.Stretch => 1.6,
        IdleAction.LookAround => 1.7,
        _ => 0.7,
    };

    private double BlinkLength => _doubleBlink ? 0.42 : 0.17;

    // ───────────────────────────── Input ─────────────────────────────

    /// <summary>A new mood: eased in over <see cref="Blend"/> seconds, with a blink to hide the change of face.</summary>
    public void SetMood(PetMood mood, DateTimeOffset now)
    {
        if (mood == _mood) return;
        var t = T(now);
        (_prev, _prevAt) = (_mood, _moodAt);
        _mood = mood;
        _moodAt = t;
        if (mood == PetMood.Idle) _actionAt = t + Uniform(4, 9);
        if (mood is not PetMood.Sleeping) { _blinkAt = t + 0.02; _doubleBlink = false; }
    }

    /// <summary>Starts in a mood without easing into it (a view shown for the first time).</summary>
    public void Begin(PetMood mood, DateTimeOffset now)
    {
        _mood = _prev = mood;
        _moodAt = _prevAt = T(now) - 60;
        if (mood == PetMood.Idle && _actionAt < T(now)) _actionAt = T(now) + Uniform(4, 9);
    }

    /// <summary>The pointer is over the pet (<paramref name="x"/>, <paramref name="y"/>: where, in pet pixels from its face), or left it.</summary>
    public void Hover(bool over, double x, double y, DateTimeOffset now)
    {
        var t = T(now);
        if (over && !_hovering) _hoverAt = t;
        _hovering = over;
        _hoverX = x < -3 ? -1 : x > 3 ? 1 : 0;
        _hoverY = y < -5 ? -1 : 0;
    }

    /// <summary>Starts an idle action now (previews, tests).</summary>
    internal void Act(IdleAction action, DateTimeOffset now)
    {
        _action = action;
        _actionAt = T(now);
    }

    /// <summary>A click: a happy hop and a heart.</summary>
    public void Pat(DateTimeOffset now) => _patAt = T(now);

    /// <summary>The user types: the pet looks down at the message box for a moment.</summary>
    public void Attend(DateTimeOffset now) => _attendUntil = T(now) + 1.4;

    /// <summary>True once a long mood came to rest: the still is shown and nothing needs a timer.</summary>
    public bool IsResting(DateTimeOffset now) =>
        RestAfter(_mood) != TimeSpan.MaxValue && T(now) - _moodAt >= RestAfter(_mood).TotalSeconds && T(now) - _patAt > 1;

    /// <summary>Schedules the next blink, glance and idle action once the current ones are over.</summary>
    public void Plan(DateTimeOffset now)
    {
        var t = T(now);
        if (t >= _blinkAt + BlinkLength)
        {
            _blinkAt = t + Uniform(2, 6);
            _doubleBlink = _rng.NextDouble() < 0.2;
        }
        if (t >= _glanceEnd)
        {
            _glanceAt = t + (_mood == PetMood.Thinking ? Uniform(0.8, 2) : Uniform(4, 9));
            _glanceEnd = _glanceAt + Uniform(0.7, 1.5);
            (_glanceX, _glanceY) = _mood == PetMood.Thinking
                ? (_rng.Next(2) == 0 ? -1 : 1, 0)
                : _rng.Next(5) switch { 0 or 1 => (-1, 0), 2 or 3 => (1, 0), _ => (0, -1) };
        }
        if (t >= _actionAt + Duration(_action))
        {
            _actionAt = t + Uniform(8, 20);
            _action = PickAction();
        }
    }

    // ───────────────────────────── Easing ─────────────────────────────

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - Clamp01(p), 3);
    private static double EaseInOut(double p) { p = Clamp01(p); return p * p * (3 - 2 * p); }
    private static double EaseOutBack(double p) { p = Clamp01(p); const double c = 1.9; return 1 + (c + 1) * Math.Pow(p - 1, 3) + c * Math.Pow(p - 1, 2); }
    private static double Frac(double v) => v - Math.Floor(v);
    private static double Breath(double t, double period) => (1 - Math.Cos(2 * Math.PI * t / period)) / 2;

    /// <summary>A hop of <paramref name="height"/> pixels over <paramref name="length"/> seconds, <paramref name="p"/> in 0…1:
    /// crouch, jump (stretched), land (squashed). Returns dy, sx, sy and the shadow.</summary>
    private static (double Dy, double Sx, double Sy, double Shadow) Hop(double p, double height)
    {
        if (p <= 0 || p >= 1) return (0, 1, 1, 1);
        if (p < 0.18) { var q = Math.Sin(Math.PI * p / 0.18); return (0, 1 + 0.06 * q, 1 - 0.1 * q, 1); }
        if (p < 0.8)
        {
            var q = (p - 0.18) / 0.62;
            var up = Math.Sin(Math.PI * q);
            return (-height * up, 1 - 0.04 * (1 - q), 1 + 0.07 * (1 - q), 1 - 0.5 * up);
        }
        var l = Math.Sin(Math.PI * (p - 0.8) / 0.2);
        return (0, 1 + 0.07 * l, 1 - 0.09 * l, 1);
    }

    // ───────────────────────────── Poses ─────────────────────────────

    private readonly record struct Motion(PetFace Face, double Dx, double Dy, double Sx, double Sy, double Shadow, List<PetParticle> Fx);

    /// <summary>The picture at a still moment of a mood: shown in a background window, on previews that do not move, and at rest.</summary>
    public static PetPose Still(PetBody body, PetMood mood)
    {
        var fx = new List<PetParticle>();
        var face = mood switch
        {
            PetMood.Sleeping => new PetFace(PetArt.Sleep, PetArt.SmallMouth),
            PetMood.Thinking => new PetFace(PetArt.LookUp, PetArt.SmallMouth),
            PetMood.Working => new PetFace(PetArt.Open, PetArt.SmallMouth, LookX: -1),
            PetMood.Waiting => new PetFace(PetArt.Wide, PetArt.Oh),
            PetMood.Done => new PetFace(PetArt.Happy, PetArt.Grin),
            PetMood.Error => new PetFace(PetArt.Squint, PetArt.Wavy),
            _ => new PetFace(PetArt.Open),
        };
        switch (mood)
        {
            case PetMood.Sleeping: fx.Add(new(PetArt.SmallZ, 7, 9)); fx.Add(new(PetArt.BigZ, 3, 3)); break;
            case PetMood.Thinking: Think(fx, 0.3, 1); break;
            case PetMood.Working: fx.Add(new(PetArt.GearA, 1, 5)); break;
            case PetMood.Waiting: fx.Add(new(PetArt.Bang, 6, 2)); break;
            case PetMood.Done: fx.Add(new(PetArt.SparkBig, 2, 3)); fx.Add(new(PetArt.SparkSmall, 7, 10)); break;
            case PetMood.Error: fx.Add(new(PetArt.Sweat, 8, 5)); break;
        }
        return new PetPose(face, Dy: body.Floats ? -1 : 0, Shadow: body.Floats ? 0.6 : 1, Particles: fx);
    }

    /// <summary>A thought cloud whose three dots hop in turn (also the "typing" dots while omp writes).</summary>
    private static void Think(List<PetParticle> fx, double tau, double pop)
    {
        var s = pop;
        fx.Add(new(PetArt.ThoughtTrail, 9, 13, 1, Math.Min(1, s * 1.4)));
        fx.Add(new(PetArt.ThoughtTrail, 7, 9.5, 1, s));
        fx.Add(new(PetArt.ThoughtCloud, 0, 1, 1, s));
        if (pop < 0.9) return;
        for (var i = 0; i < 3; i++)
        {
            var q = Frac((tau - i * 0.16) / 1.2);
            var lift = q < 0.25 ? Math.Sin(Math.PI * q / 0.25) : 0;
            fx.Add(new(PetArt.ThoughtDot, 2.5 + i * 2.5, 4 - Math.Round(lift)));
        }
    }

    /// <summary>A mood's own motion, <paramref name="tau"/> seconds after it began, at time <paramref name="t"/>.</summary>
    private Motion MoodMotion(PetBody body, PetMood mood, double tau, double t, double pop)
    {
        var fx = new List<PetParticle>();
        double dx = 0, dy = 0, sx = 1, sy = 1, shadow = 1;
        PetFace face;
        void Breathe(double period, double amount)
        {
            var b = Breath(t, period);
            if (body.Floats) { dy += -1 - 0.9 * b; shadow = 0.75 - 0.2 * b; }
            else { sy *= 1 + amount * b; sx *= 1 - amount * 0.4 * b; }
        }
        switch (mood)
        {
            case PetMood.Sleeping:
            {
                face = new PetFace(PetArt.Sleep, PetArt.SmallMouth);
                Breathe(4.8, 0.055);
                // A z every 1.7 s drifts up and away, growing and fading
                for (var k = Math.Max(0, (int)Math.Floor(tau / 1.7) - 2); k <= Math.Floor(tau / 1.7); k++)
                {
                    var q = (tau - k * 1.7) / 3.4;
                    if (q is < 0 or > 1) continue;
                    var op = q < 0.15 ? q / 0.15 : q > 0.7 ? (1 - q) / 0.3 : 1;
                    fx.Add(new(q < 0.45 ? PetArt.SmallZ : PetArt.BigZ, 8 - 5 * q, 10 - 9 * q, op));
                }
                break;
            }
            case PetMood.Thinking:
                face = new PetFace(PetArt.LookUp, PetArt.SmallMouth);
                Breathe(3.0, 0.03);
                Think(fx, tau, pop);
                break;
            case PetMood.Working:
            {
                // Eyes darting over the work, a busy little bounce squashed where it lands
                var read = (int)Math.Floor(tau / 0.55) % 4;
                face = new PetFace(PetArt.Open, PetArt.SmallMouth, LookX: read switch { 0 => -1, 2 => 1, _ => 0 });
                var q = Frac(tau / 0.44);
                var up = Math.Sin(Math.PI * q);
                dy = -1.1 * up;
                sy = 1 + 0.03 * up - 0.05 * Math.Max(0, 1 - up * 3);
                sx = 1 + 0.03 * Math.Max(0, 1 - up * 3);
                if (body.Floats) dy -= 1;
                fx.Add(new(Frac(tau / 0.36) < 0.5 ? PetArt.GearA : PetArt.GearB, 1, 5 - Math.Round(up * 0.6), 1, pop));
                break;
            }
            case PetMood.Waiting:
            {
                face = new PetFace(PetArt.Wide, PetArt.Oh);
                Breathe(2.6, 0.03);
                // Every 2.8 s (for the first half minute) a little hop to get attention, the "!" bouncing with it
                var cycle = Frac(tau / 2.8);
                var hop = tau < 30 && tau > 1 ? Hop(cycle / 0.3, 1.5) : (0, 1, 1, 1);
                dy += hop.Dy; sx *= hop.Sx; sy *= hop.Sy; shadow *= hop.Shadow;
                fx.Add(new(PetArt.Bang, 6, 2 + Math.Round(hop.Dy * 0.8), 1, pop < 1 ? EaseOutBack(pop) : 1));
                break;
            }
            case PetMood.Done:
            {
                face = new PetFace(PetArt.Happy, PetArt.Grin);
                var hop = Hop(Frac(tau / 0.8) / 0.75, 2.6);
                (dy, sx, sy, shadow) = hop;
                if (body.Floats) dy -= 1;
                (double X, double Y, bool Big)[] spots = [(1, 2, true), (8, 1, false), (3, 10, false), (8, 7, true), (0, 15, false)];
                for (var i = 0; i < spots.Length; i++)
                {
                    var q = Frac((tau + i * 0.23) / 0.9);
                    var s = Math.Sin(Math.PI * q);
                    fx.Add(new(spots[i].Big ? PetArt.SparkBig : PetArt.SparkSmall, spots[i].X, spots[i].Y, 1, Math.Round(s * 3) / 3 * pop));
                }
                break;
            }
            case PetMood.Error:
            {
                face = new PetFace(PetArt.Squint, PetArt.Wavy);
                Breathe(2.4, 0.03);
                // A wobble at the start of each 2.4 s, dying out; a sweat drop slides down the side of the head
                var c = Frac(tau / 2.4) * 2.4;
                if (tau < 8 && c < 0.7) dx = Math.Sin(2 * Math.PI * c / 0.16) * (1 - c / 0.7) * 1.2;
                var q = Clamp01(c / 1.5);
                fx.Add(new(PetArt.Sweat, 8, 4 + 4 * EaseInOut(q), q > 0.7 ? (1 - q) / 0.3 : 1));
                break;
            }
            default:
                face = new PetFace(PetArt.Open);
                Breathe(3.4, 0.04);
                break;
        }
        return new Motion(face, dx, dy, sx, sy, shadow, fx);
    }

    /// <summary>The pose at <paramref name="now"/>. Pure: the same time gives the same pose until <see cref="Plan"/> or an input changes the schedule.</summary>
    public PetPose Sample(PetBody body, DateTimeOffset now)
    {
        var t = T(now);
        var tau = t - _moodAt;
        var blend = EaseOutCubic(tau / Blend);
        var m = MoodMotion(body, _mood, tau, t, blend);
        var (face, dx, dy, sx, sy, shadow, fx) = (m.Face, m.Dx, m.Dy, m.Sx, m.Sy, m.Shadow, m.Fx);
        if (blend < 1)
        {
            // Eased from the last mood: its motion fades into this one, its effects fade out
            var o = MoodMotion(body, _prev, t - _prevAt, t, 1);
            dx = o.Dx + (dx - o.Dx) * blend;
            dy = o.Dy + (dy - o.Dy) * blend;
            sx = o.Sx + (sx - o.Sx) * blend;
            sy = o.Sy + (sy - o.Sy) * blend;
            shadow = o.Shadow + (shadow - o.Shadow) * blend;
            fx.InsertRange(0, o.Fx.Select(p => p with { Scale = p.Scale * (1 - blend) }));
            // A small start: a dip, then up
            sy *= 1 - 0.06 * Math.Sin(Math.PI * Clamp01(tau / Blend));
        }

        // Idle life: actions
        if (_mood == PetMood.Idle && blend >= 1 && t >= _actionAt && t < _actionAt + Duration(_action))
        {
            var p = (t - _actionAt) / Duration(_action);
            switch (_action)
            {
                case IdleAction.Twitch:
                    face = face with { Twitch = p < 0.25 || p is > 0.5 and < 0.75 };
                    break;
                case IdleAction.Hop:
                    var h = Hop(p, 2);
                    dy += h.Dy; sx *= h.Sx; sy *= h.Sy; shadow *= h.Shadow;
                    break;
                case IdleAction.Stretch:
                    // Up tall with a yawn, then settle
                    var s = p < 0.35 ? EaseInOut(p / 0.35) : p < 0.7 ? 1 : 1 - EaseInOut((p - 0.7) / 0.3);
                    sy *= 1 + 0.08 * s;
                    sx *= 1 - 0.04 * s;
                    if (p is > 0.2 and < 0.8) face = new PetFace(PetArt.Closed, PetArt.Oh);
                    break;
                case IdleAction.LookAround:
                    if (p < 0.45) face = face with { LookX = -1 };
                    else if (p > 0.52 && p < 0.95) face = face with { LookX = 1 };
                    dx += p < 0.45 ? -0.5 : p > 0.52 && p < 0.95 ? 0.5 : 0;
                    break;
                case IdleAction.Wiggle:
                    dx += Math.Sin(2 * Math.PI * p * 3) * 0.8 * (1 - p);
                    face = face with { Eyes = PetArt.Happy };
                    break;
            }
        }

        // Glances (idle, thinking)
        if (_mood is PetMood.Idle or PetMood.Thinking && face.Eyes.CanLook && face.LookX == 0 && t >= _glanceAt && t < _glanceEnd)
            face = face with { LookX = _glanceX, LookY = _mood == PetMood.Thinking ? 0 : _glanceY };

        // Typing: a look down at the message box
        if (t < _attendUntil && _mood is not PetMood.Sleeping && face.Eyes.CanLook)
            face = face with { LookX = -1, LookY = face.Eyes == PetArt.LookUp ? 0 : 1 };

        // The pointer: a wiggle as it arrives, then eyes on it
        if (_mood is not PetMood.Sleeping)
        {
            var hp = (t - _hoverAt) / 0.45;
            if (hp is >= 0 and < 1)
            {
                sy *= 1 - 0.05 * Math.Sin(Math.PI * hp);
                sx *= 1 + 0.04 * Math.Sin(Math.PI * hp);
            }
            if (_hovering && face.Eyes.CanLook) face = face with { LookX = _hoverX, LookY = _hoverY };
        }

        // A click: a happy hop and a heart
        var pp = (t - _patAt) / 0.7;
        if (pp is >= 0 and < 1)
        {
            face = new PetFace(PetArt.Happy, PetArt.Grin);
            var h = Hop(pp / 0.85, 2.2);
            dy += h.Dy; sx *= h.Sx; sy *= h.Sy; shadow *= h.Shadow;
        }
        var hq = (t - _patAt) / 1.1;
        if (hq is >= 0 and < 1)
            fx.Add(new(PetArt.Heart, 5 - 2 * hq, 9 - 8 * EaseOutCubic(hq), hq > 0.65 ? (1 - hq) / 0.35 : 1, EaseOutBack(hq / 0.25)));

        // Blinks, on eyes that are open
        var bt = t - _blinkAt;
        if (bt >= 0 && bt < BlinkLength && face.Eyes.CanLook)
        {
            var b = bt >= 0.25 ? bt - 0.25 : bt;
            var eyes = b < 0.05 ? PetArt.Half : b < 0.11 ? PetArt.Closed : b < 0.17 ? PetArt.Half : face.Eyes;
            face = face with { Eyes = eyes, LookY = eyes == face.Eyes ? face.LookY : 0 };
        }
        return new PetPose(face, dx, dy, sx, sy, shadow, fx);
    }

    /// <summary>
    /// When the picture changes next after <paramref name="now"/> at <paramref name="scale"/> (looked ahead in steps of
    /// 1/30 s, at most <paramref name="horizon"/>): the moment a view needs to redraw, and only then.
    /// </summary>
    public DateTimeOffset NextChange(PetBody body, DateTimeOffset now, double scale, TimeSpan? horizon = null)
    {
        var key = PetFrames.Key(Sample(body, now), scale);
        var limit = horizon ?? TimeSpan.FromSeconds(3);
        var step = TimeSpan.FromSeconds(1 / 30.0);
        for (var at = now + step; at - now < limit; at += step)
        {
            if (IsResting(at)) return at;
            if (PetFrames.Key(Sample(body, at), scale) != key) return at;
        }
        return now + limit;
    }
}
