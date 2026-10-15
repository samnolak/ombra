using Avalonia.Threading;
using SkiaSharp;

namespace Ombra.App.Pets.Oni;

/// <summary>A spirit's flame drawing with its glow, drawn once: the image and where the spirit's point is in it (bake pixels).</summary>
public sealed record OniGlowFrame(SKImage Image, float OriginX, float OriginY);

/// <summary>
/// ONI-01's sheets, decoded once for a pet height in device pixels and shared by every view of that height (the perch,
/// the desktop window, the settings' preview). Each of the eleven sheets the pet draws from is decoded on a worker
/// thread, then scaled down once, with a high-quality filter, to the pixels it is shown at (a 1448 px sheet of
/// 330 px drawings shown 160 px tall is kept at about half its size, a quarter of the memory), so drawing a frame is a
/// near 1:1 copy and nothing is decoded or scaled per frame. The spirits' sheet is not kept: its sixteen drawings are
/// drawn once with their glow, in each theme's colours. Freed a few seconds after the last view lets go (a view moving
/// from the message box to the desktop takes it over).
/// </summary>
public sealed class OniAtlases
{
    /// <summary>Spirits are drawn at most this tall (pet heights): their glow is drawn for that, and scaled down below.</summary>
    public const double SpiritBake = .25;

    private static readonly Dictionary<int, OniAtlases> Live = [];
    private static readonly TimeSpan FreeAfter = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, (SKImage Image, float Scale)> _images = [];
    private int _users;
    private DispatcherTimer? _free;

    private OniAtlases(OniSpec spec, int level, Func<string, Stream> open)
    {
        Spec = spec;
        Level = level;
        Loaded = Task.Run(() => Load(open));
        Loaded.ContinueWith(_ => Dispatcher.UIThread.Post(() => Ready?.Invoke()), TaskScheduler.Default);
    }

    public OniSpec Spec { get; }

    /// <summary>The pet height (device pixels) the sheets are kept for.</summary>
    public int Level { get; }

    public Task Loaded { get; }
    public bool IsReady => Loaded.IsCompletedSuccessfully;

    /// <summary>Raised on the UI thread once every sheet is decoded.</summary>
    public event Action? Ready;

    /// <summary>The spirits with their glow: [light ? 1 : 0][frame].</summary>
    public OniGlowFrame[][] Glow { get; } = [new OniGlowFrame[16], new OniGlowFrame[16]];

    /// <summary>The decoded bytes held (tests, the report).</summary>
    public long Bytes { get; private set; }

    /// <summary>How long decoding took (the report).</summary>
    public TimeSpan DecodeTime { get; private set; }

    /// <summary>The level for a pet <paramref name="height"/> device pixels tall: steps of 32 px, so a small change shares sheets.</summary>
    public static int LevelFor(double height) => Math.Max(32, (int)Math.Ceiling(height / 32) * 32);

    /// <summary>The sheets for pets about <paramref name="deviceHeight"/> device pixels tall; <see cref="Release"/> when done.</summary>
    public static OniAtlases Acquire(OniSpec spec, double deviceHeight, Func<string, Stream> open)
    {
        Dispatcher.UIThread.VerifyAccess();
        var level = LevelFor(deviceHeight);
        if (!Live.TryGetValue(level, out var set) || set.Spec != spec) Live[level] = set = new OniAtlases(spec, level, open);
        set._users++;
        set._free?.Stop();
        return set;
    }

    public void Release()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (--_users > 0) return;
        _free ??= new DispatcherTimer(FreeAfter, DispatcherPriority.Background, (_, _) => Free());
        _free.Start();
    }

    /// <summary>Its images are disposed (tests: let go once no view holds it).</summary>
    internal bool IsFreed { get; private set; }

    private void Free()
    {
        _free?.Stop();
        if (_users > 0) return;
        if (Live.TryGetValue(Level, out var set) && set == this) Live.Remove(Level);
        // Not loaded yet: freed when it is
        Loaded.ContinueWith(_ =>
        {
            lock (_images)
            {
                foreach (var (image, _) in _images.Values) image.Dispose();
                _images.Clear();
                foreach (var theme in Glow)
                    for (var i = 0; i < theme.Length; i++)
                    {
                        theme[i]?.Image.Dispose();
                        theme[i] = null!;
                    }
                Bytes = 0;
                IsFreed = true;
            }
        }, TaskScheduler.Default);
    }

    /// <summary>A sheet and the size it was kept at (decoded pixels per source pixel); null before it is loaded.</summary>
    public (SKImage Image, float Scale)? Image(string source)
    {
        lock (_images) return _images.TryGetValue(source, out var v) ? v : null;
    }

    /// <summary>How many decoded pixels each source pixel of a sheet needs at this level (above 1: kept at full size).</summary>
    private Dictionary<string, double> Needs()
    {
        var needs = new Dictionary<string, double>();
        void Need(string source, double perSourcePixel) =>
            needs[source] = Math.Max(needs.GetValueOrDefault(source), perSourcePixel);
        foreach (var s in Spec.States.Values) Need(s.Source, Level * s.Scale);
        foreach (var id in OniSpec.Bridges.Values) Need(Spec.Sheets[id].Source, Level / Spec.Sheets[id].ReferenceHeight);
        Need(Spec.Effects.Source, Level * 1.08 / Spec.Effects.Frames[3].Cw);
        Need(Spec.Vfx.Source, Level * Math.Max(1.3 / VfxSpan(Spec.Vfx, 2), 1.15 / VfxSpan(Spec.Vfx, 0)));
        return needs;
    }

    /// <summary>The largest ink extent in a row of effect drawings (effects-player.cjs <c>event</c> sizes by it).</summary>
    public static double VfxSpan(OniSheet vfx, int row)
    {
        var span = 1.0;
        for (var i = row * 4; i < row * 4 + 4; i++)
        {
            var f = vfx.Frames[i];
            span = Math.Max(span, Math.Max(f.Bottom - f.Top + 1, f.Right - f.Left + 1));
        }
        return span;
    }

    private void Load(Func<string, Stream> open)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var needs = Needs();
        Parallel.ForEach(needs, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, pair =>
        {
            using var full = Decode(open, pair.Key);
            // A little more than shown: transitions and the seal are drawn a few percent larger than their level
            var k = Math.Min(1, pair.Value * 1.1);
            SKImage image;
            float scale;
            if (k >= .95)
            {
                full.SetImmutable();
                image = SKImage.FromBitmap(full);
                scale = 1;
            }
            else
            {
                var w = Math.Max(1, (int)Math.Round(full.Width * k));
                var h = Math.Max(1, (int)Math.Round(full.Height * k));
                using var small = full.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
                                  ?? throw new InvalidDataException("Could not scale " + pair.Key);
                small.SetImmutable();
                image = SKImage.FromBitmap(small);
                scale = (float)w / full.Width;
            }
            lock (_images)
            {
                _images[pair.Key] = (image, scale);
                Bytes += (long)image.Width * image.Height * 4;
            }
        });
        using (var flames = Decode(open, Spec.Flames.Source))
        {
            BakeGlow(flames, light: false);
            BakeGlow(flames, light: true);
        }
        DecodeTime = watch.Elapsed;
    }

    private static SKBitmap Decode(Func<string, Stream> open, string source)
    {
        using var stream = open(source);
        using var data = SKData.Create(stream);
        return SKBitmap.Decode(data) ?? throw new InvalidDataException("Could not decode " + source);
    }

    /// <summary>The spirits' sixteen drawings with their glow (effects-player.cjs <c>spirit</c>), at the tallest they are drawn.</summary>
    private void BakeGlow(SKBitmap flames, bool light)
    {
        var height = Level * SpiritBake;
        var s = height / 280;
        var blur = light ? Math.Max(1, height * .035) : height * .18;
        var sigma = (float)(blur / 2); // canvas shadowBlur is twice the Gaussian's deviation
        var pad = (int)Math.Ceiling(sigma * 3) + 2;
        using var source = SKImage.FromBitmap(flames);
        for (var i = 0; i < 16; i++)
        {
            var f = Spec.Flames.Frames[i];
            var color = i / 8;
            var dest = SKRect.Create((float)(-f.AnchorX * s), (float)(-(f.AnchorY - 125) * s), (float)(f.Cw * s), (float)(f.Ch * s));
            var bounds = SKRectI.Ceiling(SKRect.Inflate(dest, pad, pad));
            var shadow = SKColor.Parse(light ? (color != 0 ? "#602078" : "#9b1238") : (color != 0 ? "#b66aff" : "#ff285a"));
            using var surface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-bounds.Left, -bounds.Top);
            // The canvas filters the drawing, then shadows what it drew: the paint's colour filter, then the drop shadow
            using var tone = light ? OniColor.Filter(OniColor.Saturate(1.6), OniColor.Contrast(1.12)) : null;
            using var drop = SKImageFilter.CreateDropShadow(0, 0, sigma, sigma, shadow);
            using var paint = new SKPaint { ImageFilter = drop, ColorFilter = tone };
            canvas.DrawImage(source, SKRect.Create(f.Sx, f.Sy, f.Cw, f.Ch), dest, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            canvas.Flush();
            var image = surface.Snapshot();
            lock (_images)
            {
                Glow[light ? 1 : 0][i] = new OniGlowFrame(image, -bounds.Left, -bounds.Top);
                Bytes += (long)image.Width * image.Height * 4;
            }
        }
    }
}

/// <summary>The CSS filters the reference players use (brightness, saturate, contrast) as one colour matrix.</summary>
public static class OniColor
{
    public static float[] Brightness(double b)
    {
        var f = (float)b;
        return [f, 0, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, 1, 0];
    }

    public static float[] Saturate(double s)
    {
        var a = (float)s;
        return
        [
            .213f + .787f * a, .715f - .715f * a, .072f - .072f * a, 0, 0,
            .213f - .213f * a, .715f + .285f * a, .072f - .072f * a, 0, 0,
            .213f - .213f * a, .715f - .715f * a, .072f + .928f * a, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    public static float[] Contrast(double c)
    {
        var f = (float)c;
        var t = .5f - .5f * f; // Skia's translation column is in 0–1
        return [f, 0, 0, 0, t, 0, f, 0, 0, t, 0, 0, f, 0, t, 0, 0, 0, 1, 0];
    }

    /// <summary>The filters applied in order, as CSS's <c>filter: a b c</c>.</summary>
    public static SKColorFilter Filter(params float[][] steps)
    {
        float[] m = [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];
        foreach (var s in steps) m = Multiply(s, m);
        return SKColorFilter.CreateColorMatrix(m);
    }

    /// <summary><paramref name="a"/> after <paramref name="b"/>, both 4×5 with an implied last row (0 0 0 0 1).</summary>
    private static float[] Multiply(float[] a, float[] b)
    {
        var m = new float[20];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
                m[r * 5 + c] = a[r * 5] * b[c] + a[r * 5 + 1] * b[5 + c] + a[r * 5 + 2] * b[10 + c] + a[r * 5 + 3] * b[15 + c];
            m[r * 5 + 4] = a[r * 5] * b[4] + a[r * 5 + 1] * b[9] + a[r * 5 + 2] * b[14] + a[r * 5 + 3] * b[19] + a[r * 5 + 4];
        }
        return m;
    }
}
