using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace OmpGui.App.Pets;

/// <summary>What the pet shows: what the agent is doing, as a pet would feel about it.</summary>
public enum PetMood { Idle, Sleeping, Thinking, Working, Waiting, Done, Error }

/// <summary>A pet as drawn: its body, an optional coat colour (null: the body's own) and what it wears.</summary>
public sealed record PetLook(PetBody Body, PetCoat? Coat, PetAccessory Accessory)
{
    public PetLook(PetBody body) : this(body, null, PetArt.None) { }

    public string Key => $"{Body.Id}/{Coat?.Id ?? "-"}/{Accessory.Id}";
}

/// <summary>The face of a moment: eyes, mouth (null: the body's own at rest), a twitch, and where the eyes look (−1…1 each way).</summary>
public readonly record struct PetFace(EyeStyle Eyes, MouthStyle? Mouth = null, bool Twitch = false, int LookX = 0, int LookY = 0)
{
    public string Key => $"{Eyes.Id}.{Mouth?.Id ?? "-"}.{(Twitch ? 1 : 0)}.{LookX}.{LookY}";
}

/// <summary>A little sprite drawn over the canvas (Zzz, a thought cloud, sparkles): top-left in pet pixels, faded, scaled about its centre.</summary>
public readonly record struct PetParticle(PetArt.Sprite Sprite, double X, double Y, double Opacity = 1, double Scale = 1);

/// <summary>
/// A moment of a pet's animation (<see cref="PetLife"/>): the face, the body moved by (<see cref="Dx"/>, <see cref="Dy"/>)
/// pet pixels and scaled by (<see cref="Sx"/>, <see cref="Sy"/>) about the middle of its feet (squash and stretch), the
/// contact shadow's size (1: standing, less in the air), and the effects.
/// </summary>
public sealed record PetPose(PetFace Face, double Dx = 0, double Dy = 0, double Sx = 1, double Sy = 1, double Shadow = 1, IReadOnlyList<PetParticle>? Particles = null)
{
    public IReadOnlyList<PetParticle> Effects => Particles ?? [];
}

/// <summary>A rendered frame: <see cref="Width"/>×<see cref="Height"/> straight-alpha ARGB pixels (0 = transparent).</summary>
public sealed record PetFrame(uint[] Pixels, int Width, int Height);

/// <summary>
/// Composes the pixel data of <see cref="PetArt"/>: the body (recoloured by its coat) with the face of the moment and
/// the accessory, then renders a <see cref="PetPose"/> at any scale: the contact shadow, the body moved and squashed,
/// the effects in the room left of the body. Nearest-neighbour, so pet pixels stay crisp squares at whole scales.
/// </summary>
public static class PetFrames
{
    /// <summary>Canvas in pet pixels: the 24×24 body on the right, room for effects on its left.</summary>
    public const int Width = 36, Height = 24, BodyX = 12;

    /// <summary>The ground: the bottom edge of the feet row, which squash and stretch keep in place.</summary>
    public const double Ground = 23;

    private static readonly ConcurrentDictionary<(string, string), uint[,]> Bodies = new();
    private static readonly ConcurrentDictionary<string, (double Cx, double Rx, int Top)> Shapes = new();

    // ───────────────────────────── The body ─────────────────────────────

    /// <summary>The body with its coat, face and accessory, as a 24×24 grid of colours (0 = transparent). Cached.</summary>
    public static uint[,] Body(PetLook look, PetFace face) => Bodies.GetOrAdd((look.Key, face.Key), _ => Compose(look, face));

    private static uint[,] Compose(PetLook look, PetFace face)
    {
        var body = look.Body;
        var n = PetArt.BodySize;
        var rows = body.Rows.Select(r => r.ToCharArray()).ToArray();
        if (face.Twitch)
            foreach (var p in body.Twitch)
                for (var i = 0; i < p.Pixels.Length; i++)
                    rows[p.Y][p.X + i] = p.Pixels[i];
        var g = new uint[n, n];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                g[x, y] = Colour(look, rows[y][x]);
        // Eyes
        var eyes = face.Eyes;
        for (var i = 0; i < body.Eyes.Length; i++)
        {
            var (ex, ey) = body.Eyes[i];
            var (pattern, dx) = i == 0 ? (eyes.Left, eyes.LeftDx) : (eyes.Right, eyes.RightDx);
            for (var py = 0; py < pattern.Length; py++)
                for (var px = 0; px < pattern[py].Length; px++)
                {
                    var c = pattern[py][px] switch { 'e' => Colour(look, 'e'), 'w' => Colour(look, 'w'), _ => 0u };
                    if (c != 0) Set(g, ex + px + dx + face.LookX, ey + py + eyes.Dy + face.LookY, c);
                }
        }
        // Mouth
        if (body.Mouth is { } m)
        {
            var mouth = face.Mouth ?? body.IdleMouth;
            var w = mouth.Rows.Max(r => r.Length);
            for (var py = 0; py < mouth.Rows.Length; py++)
                for (var px = 0; px < mouth.Rows[py].Length; px++)
                {
                    var ch = mouth.Rows[py][px];
                    if (ch is 'm' or 'n' or 't') Set(g, m.X - w / 2 + px, m.Y + py, Colour(look, ch));
                }
        }
        Wear(g, look);
        return g;
    }

    private static uint Mix(uint a, uint b, double t)
    {
        uint Ch(int s) => (uint)Math.Round(((a >> s) & 0xFF) + ((double)((b >> s) & 0xFF) - ((a >> s) & 0xFF)) * t);
        return 0xFF000000u | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    /// <summary>The colour of a role: the coat's ramp over the body's own, the highlight and deep shadow mixed from it, mouth defaults.</summary>
    public static uint Colour(PetLook look, char role)
    {
        if (role == '.') return 0;
        var pal = look.Body.Palette;
        uint Role(char r) => look.Coat is { } coat && r is '1' or '2' or '3' or 'o'
            ? r switch { '1' => coat.Light, '2' => coat.Mid, '3' => coat.Shadow, _ => coat.Outline }
            : pal.TryGetValue(r, out var c) ? c : 0;
        return role switch
        {
            'h' => Mix(Role('1'), 0xFFFFFFFF, 0.55),
            'd' => Mix(Role('3'), Role('o'), 0.45),
            'm' => pal.ContainsKey('m') ? pal['m'] : Role('e'),
            'n' => pal.TryGetValue('n', out var n) ? n : 0xFF9C2F45,
            't' => pal.TryGetValue('t', out var t) ? t : 0xFFFF8FA3,
            _ => Role(role),
        };
    }

    private static void Set(uint[,] g, int x, int y, uint c)
    {
        if (x >= 0 && y >= 0 && x < g.GetLength(0) && y < g.GetLength(1)) g[x, y] = c;
    }

    private static void Stamp(uint[,] g, PetArt.Sprite s, int left, int top)
    {
        for (var y = 0; y < s.Rows.Length; y++)
            for (var x = 0; x < s.Rows[y].Length; x++)
                if (s.Palette.TryGetValue(s.Rows[y][x], out var c)) Set(g, left + x, top + y, c);
    }

    /// <summary>Hats sit on the head, one row into it (moved down when they would leave the canvas); glasses ring the
    /// eyes; a scarf recolours the body's two neck rows inside its outline and hangs a tail on the right.</summary>
    private static void Wear(uint[,] g, PetLook look)
    {
        var body = look.Body;
        var (hx, hy) = body.Hat;
        switch (look.Accessory.Id)
        {
            case "party-hat" or "crown" or "top-hat":
                var hat = look.Accessory.Id switch { "party-hat" => PetArt.PartyHat, "crown" => PetArt.Crown, _ => PetArt.TopHat };
                Stamp(g, hat, hx - hat.Width / 2, Math.Max(0, hy - hat.Height + 2));
                break;
            case "bow":
                Stamp(g, PetArt.Bow, hx + 3, Math.Max(0, hy - 2));
                break;
            case "flower":
                Stamp(g, PetArt.Flower, hx - 8, Math.Max(0, hy - 1));
                break;
            case "glasses" or "shades":
                var shades = look.Accessory.Id == "shades";
                var frame = shades ? PetArt.ShadesLens : PetArt.GlassesFrame;
                foreach (var (ex, ey) in body.Eyes)
                {
                    for (var y = ey - 1; y <= ey + 3; y++)
                        for (var x = ex - 1; x <= ex + 2; x++)
                        {
                            var ring = y == ey - 1 || y == ey + 3 || x == ex - 1 || x == ex + 2;
                            if (ring && (x == ex - 1 || x == ex + 2) && (y == ey - 1 || y == ey + 3)) continue; // round corners
                            if (ring) Set(g, x, y, frame);
                            else if (shades) Set(g, x, y, PetArt.ShadesLens);
                        }
                    if (shades)
                    {
                        Set(g, ex, ey, PetArt.ShadesGlint);
                        Set(g, ex + 1, ey + 1, Mix(PetArt.ShadesLens, PetArt.ShadesGlint, 0.35));
                    }
                }
                if (body.Eyes.Length == 2)
                {
                    var (lx, ly) = body.Eyes[0];
                    var (rx, _) = body.Eyes[1];
                    for (var x = lx + 3; x < rx - 1; x++) Set(g, x, ly, frame);
                }
                break;
            case "scarf":
                var outline = Colour(look, 'o');
                var right = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    var y = body.Neck + dy;
                    for (var x = 0; x < PetArt.BodySize; x++)
                    {
                        if (g[x, y] == 0) continue;
                        right = Math.Max(right, x);
                        if (g[x, y] != outline) g[x, y] = dy == 0 ? PetArt.ScarfLight : PetArt.ScarfDark;
                    }
                }
                // The tail: two pixels wide, hanging from the right end of the scarf, outlined on its free side
                right = Math.Min(right, PetArt.BodySize - 2);
                Set(g, right - 4, body.Neck + 2, PetArt.ScarfLight);
                Set(g, right - 3, body.Neck + 2, PetArt.ScarfDark);
                Set(g, right - 4, body.Neck + 3, PetArt.ScarfDark);
                Set(g, right - 3, body.Neck + 3, outline);
                break;
        }
    }

    /// <summary>Where the feet are (centre and half width of the contact shadow) and the look's top row, hat included (room to hop).</summary>
    private static (double Cx, double Rx, int Top) Shape(PetLook look) => Shapes.GetOrAdd(look.Key, _ =>
    {
        var body = look.Body;
        int min = PetArt.BodySize, max = -1, top = PetArt.BodySize;
        var g = Body(look, new PetFace(PetArt.Open));
        for (var y = 0; y < PetArt.BodySize; y++)
            for (var x = 0; x < PetArt.BodySize; x++)
            {
                if (g[x, y] == 0) continue;
                top = Math.Min(top, y);
                if (y < 20 || body.Rows[y][x] == '.') continue;
                min = Math.Min(min, x);
                max = Math.Max(max, x);
            }
        return ((min + max + 1) / 2.0, (max - min + 1) / 2.0 + 1, top);
    });

    // ───────────────────────────── Rendering ─────────────────────────────

    /// <summary>The pose as a still at 1:1 (tests, sheets): see <see cref="PetLife.Still"/>.</summary>
    public static PetFrame Still(PetLook look, PetMood mood, double scale = 1, bool bodyOnly = false) =>
        Render(look, PetLife.Still(look.Body, mood), scale, bodyOnly);

    /// <summary>
    /// Renders a pose at <paramref name="scale"/> output pixels per pet pixel (nearest neighbour). Moves are rounded to
    /// whole output pixels; a hop never lifts the pet out of the canvas by more than a pixel (a pet in a tall hat hops low).
    /// <paramref name="bodyOnly"/>: only the 24 columns of the body.
    /// </summary>
    public static PetFrame Render(PetLook look, PetPose pose, double scale = 1, bool bodyOnly = false)
    {
        var cw = bodyOnly ? PetArt.BodySize : Width;
        var ox = bodyOnly ? BodyX : 0;
        int w = Math.Max(1, (int)Math.Round(cw * scale)), h = Math.Max(1, (int)Math.Round(Height * scale));
        var px = new uint[w * h];
        var grid = Body(look, pose.Face);
        var (cx, rx, top) = Shape(look);
        var sx = pose.Sx <= 0 ? 1 : pose.Sx;
        var sy = pose.Sy <= 0 ? 1 : pose.Sy;
        var ax = BodyX + PetArt.BodySize / 2.0;
        var dx = Math.Round(pose.Dx * scale) / scale;
        var dyMin = -Math.Max(Ground + (top - Ground) * sy, 1);
        var dy = Math.Round(Math.Max(pose.Dy, dyMin) * scale) / scale;
        // Contact shadow: an ellipse on the bottom two rows, drawn in whole pet pixels
        var shadow = Math.Clamp(pose.Shadow, 0, 1);
        var srx = rx * (0.55 + 0.45 * shadow) * sx;
        var alpha = (uint)Math.Round(255 * PetArt.ShadowOpacity * (0.35 + 0.65 * shadow) * (look.Body.Floats ? 0.7 : 1));
        var shadowColour = alpha << 24;
        for (var y = 0; y < h; y++)
        {
            var v = (y + 0.5) / scale;
            var bv = (v - Ground - dy) / sy + Ground;
            var gy = (int)Math.Floor(bv);
            var pvy = (int)Math.Floor(v);
            for (var x = 0; x < w; x++)
            {
                var u = (x + 0.5) / scale + ox;
                uint c = 0;
                var bu = (u - ax - dx) / sx + ax - BodyX;
                var gx = (int)Math.Floor(bu);
                if (gx >= 0 && gy >= 0 && gx < PetArt.BodySize && gy < PetArt.BodySize) c = grid[gx, gy];
                if (c == 0 && pvy >= Height - 2)
                {
                    var pux = Math.Floor(u) + 0.5 - (BodyX + cx);
                    var puy = pvy + 0.5 - (Ground + 0.5);
                    if (pux * pux / (srx * srx) + puy * puy / (1.2 * 1.2) <= 1) c = shadowColour;
                }
                px[y * w + x] = c;
            }
        }
        foreach (var p in pose.Effects) Draw(px, w, h, scale, ox, p);
        return new PetFrame(px, w, h);
    }

    /// <summary>A particle too faint or too small to read (a speck while it pops in or out) is not drawn.</summary>
    private static bool Shows(PetParticle p) =>
        p.Opacity > 0.05 && p.Scale >= 0.3;

    private static void Draw(uint[] px, int w, int h, double scale, int ox, PetParticle p)
    {
        if (!Shows(p)) return;
        var s = p.Sprite;
        var (sw, sh) = (s.Width, s.Height);
        var ccx = p.X + sw / 2.0;
        var ccy = p.Y + sh / 2.0;
        // Only the output pixels the sprite can cover
        var halfW = sw * p.Scale / 2 + 1;
        var halfH = sh * p.Scale / 2 + 1;
        int x0 = Math.Max(0, (int)((ccx - halfW - ox) * scale)), x1 = Math.Min(w, (int)Math.Ceiling((ccx + halfW - ox) * scale));
        int y0 = Math.Max(0, (int)((ccy - halfH) * scale)), y1 = Math.Min(h, (int)Math.Ceiling((ccy + halfH) * scale));
        // Snapped to whole output pixels, so a moving sprite keeps its shape
        var snapX = Math.Round((ccx - sw * p.Scale / 2) * scale) / scale - (ccx - sw * p.Scale / 2);
        var snapY = Math.Round((ccy - sh * p.Scale / 2) * scale) / scale - (ccy - sh * p.Scale / 2);
        var a = Math.Clamp(p.Opacity, 0, 1);
        for (var y = y0; y < y1; y++)
        {
            var v = (y + 0.5) / scale - snapY;
            var sv = (int)Math.Floor((v - ccy) / p.Scale + sh / 2.0);
            if (sv < 0 || sv >= sh) continue;
            var row = s.Rows[sv];
            for (var x = x0; x < x1; x++)
            {
                var u = (x + 0.5) / scale + ox - snapX;
                var su = (int)Math.Floor((u - ccx) / p.Scale + sw / 2.0);
                if (su < 0 || su >= row.Length || !s.Palette.TryGetValue(row[su], out var c)) continue;
                ref var d = ref px[y * w + x];
                d = a >= 0.999 ? c : Over(c, a, d);
            }
        }
    }

    /// <summary>Colour <paramref name="c"/> at opacity <paramref name="a"/> over <paramref name="d"/> (straight alpha).</summary>
    private static uint Over(uint c, double a, uint d)
    {
        var da = (d >> 24) / 255.0;
        var oa = a + da * (1 - a);
        if (oa <= 0) return 0;
        uint Ch(int sh) => (uint)Math.Round((((c >> sh) & 0xFF) * a + ((d >> sh) & 0xFF) * da * (1 - a)) / oa);
        return (uint)Math.Round(oa * 255) << 24 | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    /// <summary>
    /// What a pose looks like at <paramref name="scale"/>, as a string: equal keys render the same pixels, so the view
    /// redraws (and its timer wakes) only when something visible changes.
    /// </summary>
    public static string Key(PetPose pose, double scale)
    {
        static string I(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);
        var b = new StringBuilder(64);
        b.Append(pose.Face.Key).Append('|').Append(I(pose.Dx * scale)).Append(',').Append(I(pose.Dy * scale))
            .Append(',').Append(I(PetArt.BodySize * pose.Sx * scale)).Append(',').Append(I(PetArt.BodySize * pose.Sy * scale))
            .Append(',').Append(I(pose.Shadow * 6));
        foreach (var p in pose.Effects)
        {
            if (!Shows(p)) continue;
            b.Append('|').Append(p.Sprite.Id).Append(',').Append(I(p.X * scale)).Append(',').Append(I(p.Y * scale))
                .Append(',').Append(I(p.Opacity * 8)).Append(',').Append(I(p.Scale * p.Sprite.Width * scale));
        }
        return b.ToString();
    }
}
