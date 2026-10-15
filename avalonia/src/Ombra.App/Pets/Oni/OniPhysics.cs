namespace Ombra.App.Pets.Oni;

/// <summary>The reference's motion helpers (interaction.cjs): an exact critically damped spring, easing, the drag orbit.</summary>
public static class OniMotion
{
    /// <summary>Exact critically damped update toward <paramref name="target"/>; keeps position and velocity when the target changes.</summary>
    public static (double X, double V) Spring(double x, double v, double target, double w, double dt)
    {
        var q = x - target;
        var c = v + w * q;
        var e = Math.Exp(-w * dt);
        return (target + (q + c * dt) * e, (v - w * c * dt) * e);
    }

    public static double Smooth(double u)
    {
        u = Math.Clamp(u, 0, 1);
        return u * u * (3 - 2 * u);
    }

    /// <summary>Two inclined ellipses projected around the torso (the dragged spirits); depth decides which side of the body.</summary>
    public static (double X, double Y, double Depth) Orbit(double t, int index, double size)
    {
        var phase = t * 4.4 + index * Math.PI;
        var tilt = index != 0 ? -.56 : .56;
        var u = Math.Cos(phase) * size * .58;
        var v = Math.Sin(phase) * size * .12;
        return (u * Math.Cos(tilt) - v * Math.Sin(tilt), u * Math.Sin(tilt) + v * Math.Cos(tilt), Math.Sin(phase));
    }
}

/// <summary>Click or drag (interaction.cjs <c>PointerGesture</c>): a press becomes a drag past <see cref="Threshold"/> logical px; a cancel is never a click.</summary>
public sealed class OniGesture(double threshold = 6)
{
    public double Threshold { get; } = threshold;
    private object? _id;
    private double _x, _y;

    public bool Dragging { get; private set; }
    public bool Pressed => _id is not null;

    public bool Down(object id, double x, double y)
    {
        if (_id is not null) return false;
        (_id, _x, _y, Dragging) = (id, x, y, false);
        return true;
    }

    /// <summary>none | pending | start | drag.</summary>
    public string Move(object id, double x, double y)
    {
        if (!Equals(id, _id)) return "none";
        if (Dragging) return "drag";
        if (Math.Sqrt((x - _x) * (x - _x) + (y - _y) * (y - _y)) < Threshold) return "pending";
        Dragging = true;
        return "start";
    }

    /// <summary>none | drop | cancel | click.</summary>
    public string Up(object id, bool cancel = false)
    {
        if (_id is null || !Equals(id, _id)) return "none";
        var result = Dragging ? "drop" : cancel ? "cancel" : "click";
        _id = null;
        Dragging = false;
        return result;
    }
}

/// <summary>
/// The carried body (interaction.cjs <c>DragBody</c>): held, it follows its target on a stiff spring and its lower body
/// lags behind accelerations (a pendulum from the grip, about 10° at most); released, it keeps its velocity and lean,
/// falls and lands. Units: logical px, seconds.
/// </summary>
public sealed class OniDragBody
{
    public double X, Y, Vx, Vy, Angle, Av, Phase, Pv, TargetX, TargetY, Ax, Ay, Length = 52, ImpactSpeed, Clock;
    public double Left, Right, Top, Floor;
    public bool Held, Air;
    public int Impacts;
    private double _lastImpact = -10, _offsetX, _offsetY;

    public OniDragBody(double x, double y, double left, double right, double top, double floor)
    {
        (X, Y, TargetX, TargetY) = (x, y, x, y);
        (Left, Right, Top, Floor) = (left, right, top, floor);
    }

    public void Grab(double px, double py)
    {
        Held = true;
        Air = true;
        _offsetX = X - px;
        _offsetY = Y - py;
        TargetX = X;
        TargetY = Y;
    }

    public void Move(double px, double py)
    {
        if (!Held) return;
        TargetX = Math.Clamp(px + _offsetX, Left, Right);
        TargetY = Math.Clamp(py + _offsetY, Top, Floor);
    }

    /// <summary>Let go: velocity, lean and lift are kept on purpose.</summary>
    public void Release() => Held = false;

    public void Step(double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0) return;
        dt = Math.Min(dt, .1);
        var n = (int)Math.Ceiling(dt / (1 / 120.0));
        var h = dt / n;
        for (var i = 0; i < n; i++) Substep(h);
    }

    private void Substep(double dt)
    {
        Clock += dt;
        var oldVx = Vx;
        var oldVy = Vy;
        if (Held)
        {
            (X, Vx) = OniMotion.Spring(X, Vx, TargetX, 48, dt);
            (Y, Vy) = OniMotion.Spring(Y, Vy, TargetY, 48, dt);
        }
        else
        {
            Vx *= Math.Exp(-(Air ? 2.3 : 10) * dt);
            X += Vx * dt;
            if (Air)
            {
                Vy += 1100 * dt;
                Y += Vy * dt;
            }
        }
        if (X < Left || X > Right)
        {
            X = Math.Clamp(X, Left, Right);
            Vx *= -.16;
        }
        if (Y < Top)
        {
            Y = Top;
            if (Vy < 0) Vy *= -.16;
        }
        if (!Held && Y >= Floor)
        {
            Y = Floor;
            if (Vy > 65 && Clock - _lastImpact > .2)
            {
                Impacts++;
                _lastImpact = Clock;
                ImpactSpeed = Vy;
            }
            Vy = 0;
            Air = false;
        }
        Ax = Math.Clamp((Vx - oldVx) / dt, -6500, 6500);
        Ay = Math.Clamp((Vy - oldVy) / dt, -4000, 4000);
        // A suspended mass below a moving nape: rightward acceleration leans the feet left (+angle). Constant velocity
        // keeps no lean; in free fall gravity and the pivot's acceleration cancel, the swing goes on until landing
        var suspended = Held || Air;
        var effectiveG = Held ? Math.Clamp(1100 - Ay, 250, 2400) : Air ? 0 : 1800;
        var force = Held ? Ax * .28 : 0;
        var angularA = (force * Math.Cos(Angle) - effectiveG * Math.Sin(Angle)) / Length - (suspended ? 14 : 18) * Av;
        Av += angularA * dt;
        Angle += Av * dt;
        if (Math.Abs(Angle) > .18)
        {
            Angle = Math.Clamp(Angle, -.18, .18);
            if (Av * Angle > 0) Av *= .15;
        }
        (Phase, Pv) = OniMotion.Spring(Phase, Pv, Held || Air ? 1 : 0, 13, dt);
        Phase = Math.Clamp(Phase, 0, 1);
    }
}

/// <summary>A spirit's visual pose, eased toward where its state wants it (interaction-preview.js <c>p.visual</c>).</summary>
public struct OniSpiritPose
{
    public double X, Y, Depth, Scale, Alpha, Tempo, Lean;
}
