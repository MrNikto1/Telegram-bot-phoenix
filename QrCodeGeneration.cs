using System.Collections.Concurrent;
using ZXing;
using ZXing.Rendering;
using ZXing.Common;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

public class QrCodeService
{
    private static readonly ConcurrentDictionary<long, (string Token, DateTime Expiry)> UserTokens = new();
    private const int TokenLifetimeMinutes = 10;

    public (MemoryStream ImageStream, DateTime Expiry) GenerateQrCode(long userId)
    {
        if (UserTokens.TryGetValue(userId, out var existing) && existing.Expiry > DateTime.UtcNow)
        {
            return (GenerateQrImage(existing.Token), existing.Expiry);
        }

        var token = GenerateSecureToken(userId);
        var expiry = DateTime.UtcNow.AddMinutes(TokenLifetimeMinutes);
        UserTokens[userId] = (token, expiry);
        return (GenerateQrImage(token), expiry);
    }

    private static string GenerateSecureToken(long userId)
    {
        var uuid = Guid.NewGuid().ToString("N");
        var timestamp = DateTime.UtcNow.Ticks;
        return $"{uuid}:{userId}:{timestamp}";
    }

    private static MemoryStream GenerateQrImage(string data)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Height = 300,
                Width = 300,
                Margin = 1
            }
        };

        var pixelData = writer.Write(data);
        return ConvertPixelDataToPngStream(pixelData);
    }

    private static MemoryStream ConvertPixelDataToPngStream(PixelData pixelData)
    {
        // Конвертируем пиксели в формат Rgba32
        var rgba32Pixels = ConvertToRgba32Pixels(pixelData.Pixels);
        
        using var image = Image.LoadPixelData<Rgba32>(
            rgba32Pixels,
            pixelData.Width,
            pixelData.Height
        );
        
        var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        stream.Position = 0;
        return stream;
    }

    private static Rgba32[] ConvertToRgba32Pixels(byte[] pixelBytes)
    {
        int pixelCount = pixelBytes.Length / 4;
        var pixels = new Rgba32[pixelCount];
        
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            pixels[i] = new Rgba32(
                r: pixelBytes[offset + 2],  // R
                g: pixelBytes[offset + 1],  // G
                b: pixelBytes[offset],      // B
                a: pixelBytes[offset + 3]   // A
            );
        }
        
        return pixels;
    }

    public void CleanupExpiredTokens()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in UserTokens)
        {
            if (kvp.Value.Expiry < now)
            {
                UserTokens.TryRemove(kvp.Key, out _);
            }
        }
    }
}