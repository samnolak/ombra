using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OmpGui.App.Pets;

namespace OmpGui.Tests;

/// <summary>
/// The pets' art for review, written as PNGs to OMPGUI_REVIEW_DIR/pets (skipped without it), each on the light and the
/// dark background: every pet in every mood over its first seconds, every accessory and coat, and the frames of an
/// animated preview (every pet going idle → thinking → working → done), which a script turns into a GIF. The frames
/// themselves are checked by <see cref="PetsTests"/>; this is for the eye.
/// </summary>
public sealed class PetSheetTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");
    private const uint Light = 0xFFFFFFFF, Dark = 0xFF202020;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Sheet(int width, int height)
    {
        public readonly uint[] Px = new uint[width * height];

        public void Fill(int x, int y, int w, int h, uint c)
        {
            for (var yy = y; yy < y + h; yy++)
                for (var xx = x; xx < x + w; xx++)
                    if (xx >= 0 && yy >= 0 && xx < width && yy < height) Px[yy * width + xx] = c;
        }

        /// <summary>A frame at 1:1, blended over what is there.</summary>
        public void Draw(PetFrame f, int x, int y)
        {
            for (var py = 0; py < f.Height; py++)
                for (var px = 0; px < f.Width; px++)
                {
                    var c = f.Pixels[py * f.Width + px];
                    var (tx, ty) = (x + px, y + py);
                    if (c == 0 || tx < 0 || ty < 0 || tx >= width || ty >= height) continue;
                    var a = (c >> 24) / 255.0;
                    var d = Px[ty * width + tx];
                    uint Ch(int s) => (uint)Math.Round(((c >> s) & 0xFF) * a + ((d >> s) & 0xFF) * (1 - a));
                    Px[ty * width + tx] = 0xFF000000u | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
                }
        }

        public void Save(string path)
        {
            using var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bmp.Lock())
            {
                var row = new int[width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++) row[x] = unchecked((int)Px[y * width + x]);
                    Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, width);
                }
            }
            bmp.Save(path, new PngBitmapEncoderOptions());
        }
    }

    /// <summary>A grid of cells, each drawn twice: on the light half and on the dark half.</summary>
    private static void Grid(string path, int rows, int cols, int scale, Func<int, int, PetFrame?> cell)
    {
        const int pad = 8;
        int cw = PetFrames.Width * scale + pad, ch = PetFrames.Height * scale + pad;
        var sheet = new Sheet(cols * cw * 2 + pad * 2, rows * ch + pad);
        sheet.Fill(0, 0, cols * cw + pad, rows * ch + pad, Light);
        sheet.Fill(cols * cw + pad, 0, cols * cw + pad, rows * ch + pad, Dark);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                if (cell(r, c) is { } f)
                    for (var side = 0; side < 2; side++)
                        sheet.Draw(f, pad + side * (cols * cw + pad) + c * cw, pad + r * ch);
        sheet.Save(path);
    }

    [AvaloniaFact]
    public void Sheets_for_review()
    {
        if (Dir is null) Assert.Skip("OMPGUI_REVIEW_DIR not set");
        var dir = Path.Combine(Dir, "pets");
        Directory.CreateDirectory(dir);
        const int scale = 4;
        var bodies = PetArt.Bodies;
        // Every mood: its still, then its first seconds every 0.25 s
        foreach (var mood in Enum.GetValues<PetMood>())
        {
            const int cols = 9;
            Grid(Path.Combine(dir, $"mood-{mood}.png"), bodies.Count, cols, scale, (r, c) =>
            {
                var look = new PetLook(bodies[r]);
                if (c == 0) return PetFrames.Still(look, mood, scale);
                var life = new PetLife(T0, seed: r);
                life.Begin(mood, T0);
                var at = T0 + TimeSpan.FromSeconds(0.25 * c);
                life.Plan(at);
                return PetFrames.Render(look, life.Sample(look.Body, at), scale);
            });
        }
        // Faces: every eye style
        var faces = new[] { PetArt.Open, PetArt.Half, PetArt.Closed, PetArt.Sleep, PetArt.Happy, PetArt.LookUp, PetArt.Wide, PetArt.Squint };
        Grid(Path.Combine(dir, "faces.png"), bodies.Count, faces.Length + 1, scale, (r, c) => PetFrames.Render(new PetLook(bodies[r]),
            c < faces.Length ? new PetPose(new PetFace(faces[c])) : new PetPose(new PetFace(PetArt.Open, Twitch: true)), scale));
        // Accessories: every pet wearing each
        var acc = PetArt.Accessories;
        Grid(Path.Combine(dir, "accessories.png"), bodies.Count, acc.Count, scale, (r, c) => PetFrames.Still(new PetLook(bodies[r], null, acc[c]), PetMood.Idle, scale));
        // Coats: every pet in each colour
        var coats = PetArt.Coats;
        Grid(Path.Combine(dir, "coats.png"), bodies.Count, coats.Count, scale, (r, c) => PetFrames.Still(new PetLook(bodies[r], coats[c], PetArt.None), PetMood.Idle, scale));
        // The gallery, at the size it has on the message box (medium) and small
        Grid(Path.Combine(dir, "gallery-medium.png"), 1, bodies.Count, 2, (_, c) => PetFrames.Still(new PetLook(bodies[c]), PetMood.Idle, 2));
        Grid(Path.Combine(dir, "gallery-small.png"), 1, bodies.Count, 2, (_, c) => PetFrames.Still(new PetLook(bodies[c]), PetMood.Idle, 4 / 3.0));
    }

    /// <summary>
    /// Frames of the preview animation: all nine pets in a 3×3 grid at 3×, 20 frames a second, idle (with a hop and a
    /// blink) → thinking → working → done → idle. Written to pets/anim-{light,dark}/NNNN.png.
    /// </summary>
    [AvaloniaFact]
    public void Animated_preview_frames()
    {
        if (Dir is null) Assert.Skip("OMPGUI_REVIEW_DIR not set");
        const int scale = 3, pad = 10, fps = 20;
        var bodies = PetArt.Bodies;
        int cw = PetFrames.Width * scale + pad, ch = PetFrames.Height * scale + pad;
        (double At, PetMood Mood)[] script = [(0, PetMood.Idle), (4.5, PetMood.Thinking), (7.5, PetMood.Working), (10.5, PetMood.Done), (13, PetMood.Idle)];
        const double length = 15;
        foreach (var (name, bg) in new[] { ("light", Light), ("dark", Dark) })
        {
            var dir = Path.Combine(Dir, "pets", "anim-" + name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var run = bodies.Select((b, i) => new PetLife(T0, seed: 11 + i)).ToArray();
            foreach (var l in run) l.Begin(PetMood.Idle, T0);
            var step = 0;
            for (var frame = 0; frame < length * fps; frame++)
            {
                var t = frame / (double)fps;
                var now = T0 + TimeSpan.FromSeconds(t);
                while (step + 1 < script.Length && t >= script[step + 1].At)
                {
                    step++;
                    for (var i = 0; i < run.Length; i++) run[i].SetMood(script[step].Mood, T0 + TimeSpan.FromSeconds(script[step].At + 0.07 * i));
                }
                // Show off idle life early: a hop and a look around, staggered
                if (frame == fps * 1) for (var i = 0; i < run.Length; i += 2) run[i].Act(PetLife.IdleAction.Hop, now);
                if (frame == fps * 2) for (var i = 1; i < run.Length; i += 2) run[i].Act(PetLife.IdleAction.LookAround, now);
                if (frame == fps * 3) for (var i = 0; i < run.Length; i += 3) run[i].Act(PetLife.IdleAction.Twitch, now);
                if (frame == (int)(fps * 13.6)) for (var i = 1; i < run.Length; i += 2) run[i].Act(PetLife.IdleAction.Stretch, now);
                var sheet = new Sheet(3 * cw + pad, 3 * ch + pad);
                sheet.Fill(0, 0, 3 * cw + pad, 3 * ch + pad, bg);
                for (var i = 0; i < bodies.Count; i++)
                {
                    run[i].Plan(now);
                    var f = PetFrames.Render(new PetLook(bodies[i]), run[i].Sample(bodies[i], now), scale);
                    sheet.Draw(f, pad + i % 3 * cw, pad + i / 3 * ch);
                }
                sheet.Save(Path.Combine(dir, frame.ToString("0000", CultureInfo.InvariantCulture) + ".png"));
            }
        }
    }
}
