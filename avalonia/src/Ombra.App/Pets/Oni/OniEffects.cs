namespace Ombra.App.Pets.Oni;

/// <summary>The effects' choreography (effects-player.cjs): where the two spirits are in each pose, and the sign over the pet.</summary>
public static class OniEffects
{
    /// <summary>The sign by pose: bouncing dots while thinking, ? waiting, !! error, rising z/Z asleep.</summary>
    public static string Indicator(string id) => id switch
    {
        "thinking" => "dots",
        "waiting" => "?",
        "sleeping" => "z",
        "error" => "!!",
        _ => "",
    };

    /// <summary>Spirit <paramref name="i"/> (0 red, 1 violet) in pose <paramref name="id"/> at <paramref name="t"/> seconds, in units of the pet's height from its base.</summary>
    public static OniSpiritPose SpiritPose(string id, double t, int i)
    {
        var side = i != 0 ? 1 : -1;
        var p = t * (i != 0 ? 1.47 : 1.8) + i * 2.2;
        var pose = new OniSpiritPose
        {
            X = side * .43 + Math.Sin(p * .63) * .025,
            Y = -.43 + (i != 0 ? .06 : -.06) + Math.Sin(p) * .025,
            Depth = -1, Scale = .21, Alpha = i != 0 ? .72 : .85, Tempo = 1, Lean = Math.Sin(p * .8) * .055,
        };
        switch (id)
        {
            case "joy":
            {
                var u = (t * .7 + i * .36) % 1;
                var hop = u < .72 ? 4 * (u / .72) * (1 - u / .72) : 0;
                pose.X = side * .48 + Math.Sin(p) * .015;
                pose.Y = -.38 - hop * .08;
                pose.Scale = .22 * (1 + hop * .08 - (u > .8 ? .08 * Math.Sin((u - .8) / .2 * Math.PI) : 0));
                pose.Alpha = .95;
                pose.Tempo = 1.5;
                break;
            }
            case "working":
            {
                var a = t * 1.1 + i * Math.PI;
                pose.X = Math.Cos(a) * .49;
                pose.Y = -.43 + Math.Sin(a) * .43;
                pose.Scale = .18;
                pose.Tempo = 1.2;
                pose.Alpha = .85;
                break;
            }
            case "thinking":
            {
                var a = t * .55 + i * Math.PI;
                pose.X = Math.Cos(a) * .49;
                pose.Y = -.43 + Math.Sin(a) * .1;
                pose.Depth = Math.Sin(a);
                pose.Scale = .18 * (1 + pose.Depth * .1);
                pose.Tempo = .65;
                break;
            }
            case "waiting":
            {
                var pulse = (1 + Math.Sin(t * 3 + i * Math.PI)) / 2;
                pose.X = side * .49;
                pose.Y = -.42 - pulse * .035;
                pose.Scale = .20 * (1 + pulse * .25);
                pose.Alpha = .72 + pulse * .23;
                pose.Tempo = .9;
                break;
            }
            case "sleeping":
            {
                var u = Math.Min(1, t / 1.8);
                var ease = u * u * (3 - 2 * u);
                pose.X = side * (.43 - .32 * ease);
                pose.Y = -.3 - ease * .03;
                pose.Scale = .17;
                pose.Alpha = .6 * (1 - ease);
                pose.Tempo = .35;
                break;
            }
            case "error":
            {
                var a = t * 3.4 + i * Math.PI;
                pose.X = Math.Sin(a) * .51 + Math.Sin(t * 7.1 + i) * .05;
                pose.Y = -.46 + Math.Sin(t * 4.9 + i * 2.5) * .17;
                pose.Depth = pose.Y > -.37 ? 1 : -1;
                pose.Scale = .20;
                pose.Alpha = .95;
                pose.Tempo = 1.8;
                pose.Lean = Math.Cos(a) * .16;
                break;
            }
            case "dragging":
            {
                var o = OniMotion.Orbit(t, i, 1);
                pose.X = o.X;
                pose.Y = -.47 + o.Y;
                pose.Depth = o.Depth;
                pose.Scale = .2 * (1 + o.Depth * .13);
                pose.Alpha = .82 + o.Depth * .1;
                pose.Tempo = 1.7;
                break;
            }
        }
        return pose;
    }

    /// <summary>Which of a spirit's 8 flame drawings shows at <paramref name="t"/> (its own fire time), with a blink now and then.</summary>
    public static int SpiritFrame(int color, double t, bool still)
    {
        var phrase = (t + color * 2.1) % 6.7;
        var blink = !still && phrase > 4.3 && phrase < 4.53;
        ReadOnlySpan<int> open = color != 0 ? [0, 3, 4, 5, 6, 7, 6, 5, 4, 3] : [0, 1, 2, 3, 6, 7, 6, 3, 2, 1];
        var index = still ? 0 : blink ? (color != 0 ? 1 : 4) + (phrase > 4.42 ? 1 : 0) : open[(int)Math.Floor(t * (color != 0 ? 7 : 8)) % open.Length];
        return color * 8 + index;
    }
}
