using SkiaSharp;

namespace Ombra.App.Pets.Oni;

/// <summary>Everything one picture of ONI needs, taken on the UI thread and drawn on the render thread.</summary>
public readonly record struct OniScene
{
    /// <summary>The pet's height (logical px) and the point between its feet (in the view).</summary>
    public required double H { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    /// <summary>The lean from the nape while carried (radians).</summary>
    public double Angle { get; init; }
    public bool Light { get; init; }
    /// <summary>Reduced motion or resting: the pose's still drawing, effects at rest.</summary>
    public bool Still { get; init; }
    public required string State { get; init; }
    public required string Mode { get; init; }
    public double Phase { get; init; }
    public int Frame { get; init; }
    public required string Cue { get; init; }
    /// <summary>Seconds in the cue (the effects' clock).</summary>
    public double CueSeconds { get; init; }
    public OniSpiritPose Spirit0 { get; init; }
    public OniSpiritPose Spirit1 { get; init; }
    public double Fire0 { get; init; }
    public double Fire1 { get; init; }
    /// <summary>Seconds since a landing with dust (infinity: none), and where it was from the body.</summary>
    public double DustAge { get; init; }
    public double DustDx { get; init; }
    /// <summary>How far below the body the ground is (lifted: the shadow stays on the ground).</summary>
    public double GroundDy { get; init; }
}

/// <summary>
/// Draws ONI as interaction-preview.js's <c>stagePaint</c> does, layer by layer: the ground shadow, the effects behind
/// (working's seal and rings, error's interference), the spirits behind, the body (a whole drawing from its sheet, or
/// its entry transition, turned about the nape while carried), landing dust over the boots, the spirits in front, the
/// sign over the head. The effects as effects-player.cjs draws them, in the light theme with its filters and colours.
/// Logical pixels: the canvas carries the screen's scale.
/// </summary>
public static class OniPainter
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
    private static readonly SKColorFilter SealLight = OniColor.Filter(OniColor.Brightness(.65), OniColor.Saturate(1.8));
    private static readonly SKColorFilter VfxLight = OniColor.Filter(OniColor.Brightness(.7), OniColor.Saturate(1.7));

    public static void Paint(SKCanvas canvas, in OniScene s, OniAtlases atlases)
    {
        var h = s.H;
        using var paint = new SKPaint { IsAntialias = true };
        // The ground under it
        paint.Color = s.Light ? new SKColor(0x39, 0x2c, 0x3c, 0x28) : new SKColor(0, 0, 0, 0x55);
        var groundY = s.Y + s.GroundDy;
        canvas.DrawOval(new SKPoint((float)s.X, (float)(groundY + h * 3 / 128)), new SKSize((float)(h * .3), (float)(h * .035)), paint);

        Effects(canvas, s, atlases, front: false);
        Spirits(canvas, s, atlases, front: false);

        // The body, turned about the nape
        var neckY = s.Y - h * .42;
        canvas.Save();
        canvas.Translate((float)s.X, (float)neckY);
        if (!s.Still && s.Angle != 0) canvas.RotateRadians((float)s.Angle);
        Body(canvas, s, atlases, 0, h * .42, h);
        canvas.Restore();

        // Dust over the boots, then fading
        if (!s.Still && s.DustAge < OniCarry.DustFor)
            Event(canvas, atlases, s.Light, 0, s.DustAge, s.X + s.DustDx, groundY - h * 8 / 128, h * 1.15, OniCarry.DustFor);

        Spirits(canvas, s, atlases, front: true);
        Effects(canvas, s, atlases, front: true);
    }

    // ───────────────────────────── The body ─────────────────────────────

    private static void Body(SKCanvas canvas, in OniScene s, OniAtlases atlases, double x, double y, double h)
    {
        var spec = atlases.Spec;
        if (s.Still)
        {
            var cue = spec.States[s.Cue];
            Raw(canvas, atlases, cue, cue.StaticFrame, x, y, h);
            return;
        }
        if (s.Mode == "bridge")
        {
            if (OniSpec.Bridges.TryGetValue(s.State, out var id))
                Transition(canvas, atlases, spec.Sheets[id], s.Phase, x, id == "pickup" ? y - h * .98 : y, h);
            return;
        }
        Raw(canvas, atlases, spec.States[s.State], s.Frame, x, y, h);
    }

    /// <summary>A pose's drawing, its anchor on the ground (or the head held up, for being carried): states-preview's <c>raw</c>.</summary>
    private static void Raw(SKCanvas canvas, OniAtlases atlases, OniState st, int frame, double x, double baseline, double h)
    {
        if (atlases.Image(st.Source) is not { } sheet || frame < 0 || frame >= st.Frames.Count) return;
        var f = st.Frames[frame];
        var k = h * st.Scale;
        var y = st.HeadAnchored ? baseline - h * .98 : baseline;
        Draw(canvas, sheet, f, SKRect.Create((float)(x - f.AnchorX * k), (float)(y - f.AnchorY * k), (float)(f.Cw * k), (float)(f.Ch * k)), null);
    }

    /// <summary>An entry transition's drawing at <paramref name="phase"/>: interaction-preview's <c>transition</c>.</summary>
    private static void Transition(SKCanvas canvas, OniAtlases atlases, OniSheet sheet, double phase, double x, double y, double h)
    {
        if (atlases.Image(sheet.Source) is not { } image) return;
        var f = sheet.Frames[sheet.FrameAt(phase)];
        var k = h / sheet.ReferenceHeight;
        Draw(canvas, image, f, SKRect.Create((float)(x - f.AnchorX * k), (float)(y - f.AnchorY * k), (float)(f.Cw * k), (float)(f.Ch * k)), null);
    }

    private static void Draw(SKCanvas canvas, (SKImage Image, float Scale) sheet, OniFrame f, SKRect dest, SKPaint? paint)
    {
        var k = sheet.Scale;
        var src = SKRect.Create(f.Sx * k, f.Sy * k, f.Cw * k, f.Ch * k);
        canvas.DrawImage(sheet.Image, src, dest, Sampling, paint);
    }

    // ───────────────────────────── The spirits ─────────────────────────────

    private static void Spirits(SKCanvas canvas, in OniScene s, OniAtlases atlases, bool front)
    {
        Spirit(canvas, s, atlases, 0, s.Still ? OniEffects.SpiritPose(s.Cue, s.Cue == "sleeping" ? 3 : 0, 0) : s.Spirit0, s.Fire0, front);
        Spirit(canvas, s, atlases, 1, s.Still ? OniEffects.SpiritPose(s.Cue, s.Cue == "sleeping" ? 3 : 0, 1) : s.Spirit1, s.Fire1, front);
    }

    /// <summary>effects-player.cjs <c>spirit</c>: a living flame with its glow, drawn once per theme (<see cref="OniAtlases.Glow"/>).</summary>
    private static void Spirit(SKCanvas canvas, in OniScene s, OniAtlases atlases, int color, OniSpiritPose v, double fire, bool front)
    {
        if (v.Depth > 0 != front || v.Alpha < .01) return;
        var frame = atlases.Glow[s.Light ? 1 : 0][OniEffects.SpiritFrame(color, s.Still ? 0 : fire, s.Still)];
        if (frame is null) return;
        var height = s.H * v.Scale;
        canvas.Save();
        canvas.Translate((float)(s.X + v.X * s.H), (float)(s.Y + v.Y * s.H));
        if (!s.Still && v.Lean != 0) canvas.RotateRadians((float)v.Lean);
        // The glow was drawn for a spirit Level · SpiritBake bake pixels tall: one bake pixel is this many logical px here
        var perPixel = (float)(height / (atlases.Level * OniAtlases.SpiritBake));
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(v.Alpha, 0, 1) * 255)) };
        canvas.DrawImage(frame.Image,
            SKRect.Create(-frame.OriginX * perPixel, -frame.OriginY * perPixel, frame.Image.Width * perPixel, frame.Image.Height * perPixel),
            Sampling, paint);
        canvas.Restore();
    }

    // ───────────────────────────── Effects ─────────────────────────────

    /// <summary>effects-player.cjs <c>draw</c> without its spirits (the stage draws its own).</summary>
    private static void Effects(SKCanvas canvas, in OniScene s, OniAtlases atlases, bool front)
    {
        var id = s.Cue;
        if (id == "dragging") return;
        var h = s.H;
        var t = s.Still ? (id == "sleeping" ? 3 : 0) : s.CueSeconds;
        if (!front)
        {
            if (id == "working") Seal(canvas, s, atlases, t);
            // Wide interference behind the body, seen on both sides
            if (id == "error" && !s.Still) Event(canvas, atlases, s.Light, 2, t % 2.1, s.X, s.Y - h * .47, h * 1.3, 1.05);
            return;
        }
        Sign(canvas, s, t);
    }

    private static void Seal(SKCanvas canvas, in OniScene s, OniAtlases atlases, double t)
    {
        var h = s.H;
        var rx = (float)s.X;
        var ry = (float)(s.Y - h * .43);
        var r = (float)(h * .47);
        var pulse = .9 + Math.Sin(t * 1.5) * .1;
        // The seal (effects-player.cjs sprite 3), whole, behind the silhouette
        if (atlases.Image(atlases.Spec.Effects.Source) is { } sheet)
        {
            var f = atlases.Spec.Effects.Frames[3];
            var w = h * 1.08;
            var sh = w * f.Ch / f.Cw;
            var alpha = .64 * pulse;
            using var sp = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Min(1, s.Light ? alpha * 1.3 : alpha) * 255)),
                ColorFilter = s.Light ? SealLight : null,
            };
            canvas.Save();
            canvas.Translate(rx, ry);
            if (!s.Still) canvas.RotateRadians((float)(t * .12));
            Draw(canvas, sheet, f, SKRect.Create((float)(-w / 2), (float)(-sh / 2), (float)w, (float)sh), sp);
            canvas.Restore();
        }
        // Rings and light packets, glowing in the dark theme
        using var glow = s.Light ? null : SKImageFilter.CreateDropShadow(0, 0, (float)(h * .055 / 2), (float)(h * .055 / 2), SKColor.Parse("#65d9ef"));
        using var layer = new SKPaint { ImageFilter = glow };
        canvas.SaveLayer(layer);
        canvas.Translate(rx, ry);
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(1, h * .006) };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        stroke.Color = s.Light ? new SKColor(0x14, 0x62, 0x8c) : new SKColor(0x58, 0xca, 0xdb, 0xaa);
        canvas.DrawCircle(0, 0, r, stroke);
        var still = s.Still;
        for (var j = 0; j < 3; j++)
        {
            var a = (still ? 0 : t * .4) + j * Math.PI * 2 / 3;
            stroke.Color = s.Light ? (j == 1 ? new SKColor(0x86, 0x3c, 0x97) : new SKColor(0x14, 0x62, 0x8c))
                                   : (j == 1 ? new SKColor(0xce, 0x75, 0xde, 0x99) : new SKColor(0x78, 0xed, 0xff, 0xbb));
            using (var arc = new SKPath())
            {
                arc.AddArc(SKRect.Create(-r * .84f, -r * .84f, r * 1.68f, r * 1.68f), (float)(a * 180 / Math.PI), (float)(.42 * 180 / Math.PI));
                canvas.DrawPath(arc, stroke);
            }
            fill.Color = s.Light ? new SKColor(0x14, 0x62, 0x8c) : new SKColor(0xad, 0xea, 0xff);
            canvas.DrawCircle((float)(Math.Cos(-a * .8) * r), (float)(Math.Sin(-a * .8) * r), (float)Math.Max(1, h * .012), fill);
        }
        stroke.StrokeWidth = (float)Math.Max(1, h * .005);
        var g = (float)(h * .025);
        using var rune = new SKPath();
        rune.MoveTo(-g, 0);
        rune.LineTo(0, -g * .7f);
        rune.LineTo(g, 0);
        rune.LineTo(0, g * .7f);
        rune.Close();
        rune.MoveTo(g * 1.4f, 0);
        rune.LineTo(g * 2, 0);
        for (var j = 0; j < 8; j++)
        {
            var a = j * Math.PI / 4 - (still ? 0 : t * .08);
            stroke.Color = s.Light ? (j % 2 != 0 ? new SKColor(0x86, 0x3c, 0x97) : new SKColor(0x14, 0x62, 0x8c))
                                   : (j % 2 != 0 ? new SKColor(0xc8, 0x91, 0xf2, 0xc0) : new SKColor(0x7c, 0xe9, 0xf5, 0xd0));
            canvas.Save();
            canvas.RotateRadians((float)a);
            canvas.Translate(r * .94f, 0);
            canvas.DrawPath(rune, stroke);
            canvas.Restore();
        }
        canvas.Restore();
    }

    /// <summary>effects-player.cjs <c>event</c>: one of the effect sheet's short bursts (row 0 landing dust, row 2 interference).</summary>
    private static void Event(SKCanvas canvas, OniAtlases atlases, bool light, int row, double age, double x, double y, double h, double duration)
    {
        if (age < 0 || age >= duration || atlases.Image(atlases.Spec.Vfx.Source) is not { } sheet) return;
        var u = age / duration;
        var f = atlases.Spec.Vfx.Frames[row * 4 + Math.Min(3, (int)Math.Floor(u * 4))];
        var scale = h / OniAtlases.VfxSpan(atlases.Spec.Vfx, row);
        var alpha = Math.Min(1, u * 12) * (1 - Math.Pow(u, 3));
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255)),
            ColorFilter = light ? VfxLight : null,
        };
        Draw(canvas, sheet, f, SKRect.Create(
            (float)(x - (f.Left + f.Right) / 2.0 * scale), (float)(y - (f.Top + f.Bottom) / 2.0 * scale),
            (float)(f.Cw * scale), (float)(f.Ch * scale)), paint);
    }

    /// <summary>The sign over the head: bouncing dots thinking, ? waiting, !! error, rising z and Z asleep.</summary>
    private static void Sign(SKCanvas canvas, in OniScene s, double t)
    {
        var token = OniEffects.Indicator(s.Cue);
        if (token.Length == 0) return;
        var h = s.H;
        var x = s.X;
        var y = s.Y;
        using var shadow = s.Light ? null : SKImageFilter.CreateDropShadow(0, 0, (float)(h * .04 / 2), (float)(h * .04 / 2), new SKColor(0xff, 0x16, 0x4b));
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = s.Light ? new SKColor(0xad, 0x12, 0x3c) : new SKColor(0xff, 0x60, 0x7e),
            ImageFilter = shadow,
        };
        if (token == "dots")
        {
            for (var j = 0; j < 3; j++)
            {
                var lift = s.Still ? 0 : Math.Max(0, Math.Sin(t * 4 - j * .8)) * h * .045;
                canvas.DrawCircle((float)(x + (j - 1) * h * .085), (float)(y - h * 1.06 - lift), (float)Math.Max(2, h * .023), paint);
            }
            return;
        }
        using var font = new SKFont(Bold, (float)Math.Max(13, h * .17)) { Edging = SKFontEdging.Antialias, Subpixel = true };
        if (token == "z")
        {
            var newest = Math.Floor(t / .9);
            for (var j = 0; j < 4; j++)
            {
                var age = s.Still ? .35 + j * .8 : t - (newest - j) * .9;
                if ((!s.Still && newest - j < 0) || age < 0 || age >= 2.8) continue;
                var u = age / 2.8;
                var alpha = Math.Min(1, age / .22) * Math.Min(1, (2.8 - age) / .7);
                paint.Color = (s.Light ? new SKColor(0x77, 0x34, 0x8d) : new SKColor(0xd1, 0xa0, 0xe8)).WithAlpha((byte)Math.Round(alpha * .9 * 255));
                font.Size = (float)Math.Max(14, h * (.09 + .12 * u));
                Centered(canvas, u < .3 ? "z" : "Z", x + h * (.28 + .22 * u) + Math.Sin(u * 3) * h * .025, y - h * (.7 + .43 * u), font, paint);
            }
            return;
        }
        var bob = Math.Sin(t * 1.3) * h * .02;
        Centered(canvas, token, x + h * .38, y - h * .92 + (s.Cue == "waiting" ? bob : 0), font, paint);
    }

    /// <summary>Text centred on a point (canvas <c>textAlign center</c>, <c>textBaseline middle</c>).</summary>
    private static void Centered(SKCanvas canvas, string text, double x, double y, SKFont font, SKPaint paint)
    {
        var width = font.MeasureText(text);
        var m = font.Metrics;
        canvas.DrawText(text, (float)(x - width / 2), (float)(y - (m.Ascent + m.Descent) / 2), SKTextAlign.Left, font, paint);
    }
}
