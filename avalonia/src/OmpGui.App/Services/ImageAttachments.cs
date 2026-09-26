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
            if (bytes.Length <= MaxImageBytes || Math.Max(target.Width, target.Height) <= MinSide)
                return bytes.Length <= MaxImageBytes ? new ImageAttachment(Path.ChangeExtension(name, ".jpg"), "image/jpeg", bytes) : null;
            scale *= 0.75;
        }
    }
}
