using Avalonia;
using Avalonia.Media.Imaging;
using OmpGui.ClientCore;

namespace OmpGui.App.Services;

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

    /// <summary>
    /// Writes the image to the temp folder for another app to open, named by its content (the same image is written
    /// once) and its type. Returns the path.
    /// </summary>
    public static string SaveToTemp(ImageAttachment image)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OmpGui images");
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
