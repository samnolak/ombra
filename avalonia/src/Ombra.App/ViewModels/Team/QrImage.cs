using Avalonia.Media.Imaging;
using QRCoder;

namespace Ombra.App.ViewModels;

/// <summary>A QR code of a text (the pairing link) as a bitmap, black on white whatever the theme (scanners need contrast).</summary>
internal static class QrImage
{
    public static Bitmap Of(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        using var ms = new MemoryStream(png);
        return new Bitmap(ms);
    }
}
