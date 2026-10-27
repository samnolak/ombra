using Avalonia;
using Avalonia.Media.Imaging;
using Ombra.ClientCore;

namespace Ombra.App.Services;

/// <summary>
/// Turns picked, pasted or dropped images into attachments that fit omp's inbound limit: every command is one JSONL
/// line that should stay under 1 MiB (docs/rpc.md), and images travel base64-encoded inside it. Small PNG / JPEG /
/// GIF / WebP files go as they are; anything larger is scaled down and re-encoded as JPEG.
/// </summary>
public static class ImageAttachments
{
    public const int MaxImages = 3;
    /// <summary>Per image, raw bytes: 3 × 230 KB ≈ 920 KB base64, leaving room for the text.</summary>
    public const int MaxImageBytes = 230 * 1024;
    private const int MaxSide = 1568;
    private const int MinSide = 256;

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    public static bool IsImagePath(string path) => MimeByExtension.ContainsKey(Path.GetExtension(path));

    public static string? MimeOf(byte[] data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };

    /// <summary>An attachment from encoded image bytes; null when the data is not a readable image.</summary>
    public static ImageAttachment? FromBytes(string name, byte[] data)
    {
        var mime = MimeOf(data);
        if (mime is not null && data.Length <= MaxImageBytes) return new ImageAttachment(name, mime, data);
        try
        {
            using var stream = new MemoryStream(data);
            using var bitmap = new Bitmap(stream);
            return FromBitmap(name, bitmap);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>An attachment from a decoded bitmap (clipboard), scaled and JPEG-encoded to fit.</summary>
    public static ImageAttachment? FromBitmap(string name, Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        if (size.Width <= 0 || size.Height <= 0) return null;
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(size.Width, size.Height));
        while (true)
        {
            var target = new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
            using var scaled = scale < 1.0 ? bitmap.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality) : null;
            using var output = new MemoryStream();
            (scaled ?? bitmap).Save(output, new JpegBitmapEncoderOptions { Quality = 82 });
            var bytes = output.ToArray();
            // The name stays the one the user gave (a dropped "shot.png" read as "shot.jpg" in the viewer); the bytes'
            // type travels as the MIME type, and SaveToTemp names its copy by the bytes.
            if (bytes.Length <= MaxImageBytes || Math.Max(target.Width, target.Height) <= MinSide)
                return bytes.Length <= MaxImageBytes ? new ImageAttachment(name, "image/jpeg", bytes) : null;
            scale *= 0.75;
        }
    }

    /// <summary>Per sketch picture, raw bytes: several fit in one message next to the user's own images.</summary>
    public const int MaxSketchBytes = 120 * 1024;
    /// <summary>The longest side of a sketch picture.</summary>
    public const int MaxSketchSide = 1600;

    /// <summary>
    /// A sketch's picture from a screenshot of the page's viewport (<paramref name="png"/>, <paramref name="viewportWidth"/>
    /// CSS px wide): the sketch's box (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>,
    /// <paramref name="height"/>, viewport CSS px) with as much again of the page around it, at most
    /// <see cref="MaxSketchSide"/> px on its longer side, PNG when that fits <see cref="MaxSketchBytes"/>, else JPEG made
    /// smaller until it does. Null when the screenshot is unreadable or the box is not on it. Not on the UI thread.
    /// </summary>
    public static ImageAttachment? SketchPicture(string name, byte[] png, int viewportWidth, int x, int y, int width, int height)
    {
        using var shot = SkiaSharp.SKBitmap.Decode(png);
        if (shot is null || shot.Width <= 0 || viewportWidth <= 0) return null;
        var scale = (double)shot.Width / viewportWidth;
        // The context around the drawing: half its size on each side, at least 120 CSS px
        double padX = Math.Max(120, width / 2.0), padY = Math.Max(120, height / 2.0);
        var rect = SkiaSharp.SKRectI.Intersect(new SkiaSharp.SKRectI(0, 0, shot.Width, shot.Height), new SkiaSharp.SKRectI(
            (int)Math.Floor((x - padX) * scale), (int)Math.Floor((y - padY) * scale),
            (int)Math.Ceiling((x + width + padX) * scale), (int)Math.Ceiling((y + height + padY) * scale)));
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        using var crop = new SkiaSharp.SKBitmap();
        if (!shot.ExtractSubset(crop, rect)) return null;
        var fit = Math.Min(1.0, (double)MaxSketchSide / Math.Max(rect.Width, rect.Height));
        for (var quality = 0; ; quality++)
        {
            var size = new SkiaSharp.SKImageInfo(Math.Max(1, (int)(rect.Width * fit)), Math.Max(1, (int)(rect.Height * fit)));
            using var scaled = size.Width == rect.Width && size.Height == rect.Height ? null
                : crop.Resize(size, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
            var source = scaled ?? crop;
            if (quality == 0)
            {
                using var asPng = source.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                if (asPng is not null && asPng.Size <= MaxSketchBytes) return new ImageAttachment(name, "image/png", asPng.ToArray());
            }
            using var jpeg = source.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
            if (jpeg is not null && jpeg.Size <= MaxSketchBytes) return new ImageAttachment(name, "image/jpeg", jpeg.ToArray());
            if (Math.Max(size.Width, size.Height) <= MinSide) return null;
            fit *= 0.75;
        }
    }

    /// <summary>
    /// Writes the image to the temp folder for another app to open, named by its content (the same image is written
    /// once) and its type. Returns the path.
    /// </summary>
    public static string SaveToTemp(ImageAttachment image)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Ombra images");
        Directory.CreateDirectory(dir);
        var ext = MimeByExtension.FirstOrDefault(p => p.Value == (MimeOf(image.Data) ?? image.MimeType)).Key ?? ".png";
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image.Data))[..16];
        var path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(image.Name)}-{hash}{ext}");
        if (!File.Exists(path)) File.WriteAllBytes(path, image.Data);
        return path;
    }

    /// <summary>The largest side the viewer decodes to: a 100 MP image would take 400 MB as a bitmap.</summary>
    public const int MaxViewerSide = 4096;

    /// <summary>
    /// A thumbnail whose shorter side is at least <paramref name="minSide"/> pixels (never larger than the image), for
    /// square previews that crop to fill. Null when the data is not a readable image. Slow on big images: not on the UI thread.
    /// </summary>
    public static Bitmap? DecodeThumbnail(byte[] data, int minSide)
    {
        if (Dimensions(data) is not { } size) return null;
        return Decode(data, size, size.Width <= size.Height, Math.Min(minSide, Math.Min(size.Width, size.Height)));
    }

    /// <summary>
    /// The image for the viewer, at full size up to <see cref="MaxViewerSide"/> on its longer side, with its real size
    /// in pixels. Null when the data is not a readable image. Slow on big images: not on the UI thread.
    /// </summary>
    public static (Bitmap Bitmap, PixelSize Size)? DecodeFull(byte[] data)
    {
        if (Dimensions(data) is not { } size) return null;
        var byWidth = size.Width >= size.Height;
        var bitmap = Decode(data, size, byWidth, Math.Min(MaxViewerSide, byWidth ? size.Width : size.Height));
        return bitmap is null ? null : (bitmap, size);
    }

    /// <summary>The image's size from its header, without decoding it; null when it is not an image Skia reads.</summary>
    private static PixelSize? Dimensions(byte[] data)
    {
        if (data.Length == 0) return null;
        using var stream = new SkiaSharp.SKMemoryStream(data);
        using var codec = SkiaSharp.SKCodec.Create(stream);
        return codec is { Info: { Width: > 0, Height: > 0 } info } ? new PixelSize(info.Width, info.Height) : null;
    }

    private static Bitmap? Decode(byte[] data, PixelSize size, bool byWidth, int side)
    {
        try
        {
            using var stream = new MemoryStream(data);
            // Decoding at full size skips the scaler (and an image as small as asked stays sharp)
            if (side == (byWidth ? size.Width : size.Height)) return new Bitmap(stream);
            return byWidth ? Bitmap.DecodeToWidth(stream, side) : Bitmap.DecodeToHeight(stream, side);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            // The header was readable but the pixels are not (a truncated or corrupt file)
            return null;
        }
    }
}
