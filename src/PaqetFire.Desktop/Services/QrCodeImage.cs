using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;

namespace PaqetFire.Desktop.Services;

/// <summary>Renders share URIs as scannable QR images (dark modules on white, with a quiet zone).</summary>
public static class QrCodeImage
{
    public static async Task<ImageSource?> CreateAsync(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        // Medium error correction keeps the code small enough to scan from a laptop screen.
        var png = await Task.Run(() => PngByteQRCodeHelper.GetQRCode(
                payload,
                QRCodeGenerator.ECCLevel.M,
                8,
                drawQuietZones: true))
            .ConfigureAwait(true);
        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }
}
