using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ZXing;
using ZXing.Rendering;
using ZXing.Common;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

public class QrCodeService : IDisposable
{
    // Используем record для хранения данных токена
    private record TokenData(long UserId, DateTime Expiry, int? Balance);
    
    private readonly ConcurrentDictionary<string, TokenData> _tokens = new();
    private const int TokenLifetimeMinutes = 10;
    private readonly string _secretKey;
    private readonly Timer _cleanupTimer;

    public QrCodeService()
    {
        _secretKey = Environment.GetEnvironmentVariable("QR_SECRET_KEY")
                   ?? "default-secret-key-change-me";

        _cleanupTimer = new Timer(_ => CleanupExpiredTokens(), null,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(5));
    }

    public (MemoryStream ImageStream, string Token, DateTime Expiry) GeneratePaymentQrCode(
        long userId, 
        int balance)
    {
        var token = GeneratePaymentToken(userId, balance);
        var expiry = DateTime.UtcNow.AddMinutes(TokenLifetimeMinutes);
        
        _tokens[token] = new TokenData(userId, expiry, balance);
        
        var stream = GenerateQrImage(token);
        return (stream, token, expiry);
    }

    private string GeneratePaymentToken(long userId, int balance)
    {
        var data = $"{userId}|{balance}|{DateTime.UtcNow:O}|{Guid.NewGuid()}";
        
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        var signature = Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        
        return $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(data))}.{signature}";
    }

    public (bool Valid, long UserId, int MaxBonus) ValidatePaymentToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) 
                return (false, 0, 0);
            
            var data = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            var dataParts = data.Split('|');
            if (dataParts.Length != 4) 
                return (false, 0, 0);
            
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secretKey));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            var computedSignature = Convert.ToBase64String(computedHash)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
            
            if (!computedSignature.Equals(parts[1], StringComparison.Ordinal))
                return (false, 0, 0);
            
            if (!long.TryParse(dataParts[0], out var userId))
                return (false, 0, 0);
            
            if (!int.TryParse(dataParts[1], out var maxBonus))
                return (false, 0, 0);
            
            if (!_tokens.TryGetValue(token, out var tokenData))
                return (false, 0, 0);
            
            if (tokenData.Expiry < DateTime.UtcNow)
            {
                _tokens.TryRemove(token, out _);
                return (false, 0, 0);
            }
            
            return (true, userId, maxBonus);
        }
        catch
        {
            return (false, 0, 0);
        }
    }

    public (MemoryStream ImageStream, string Token, DateTime Expiry) GenerateQrCode(long userId)
    {
        var token = GenerateSignedToken(userId);
        var expiry = DateTime.UtcNow.AddMinutes(TokenLifetimeMinutes);

        _tokens[token] = new TokenData(userId, expiry, null); // Balance = null для обычных QR
        
        var stream = GenerateQrImage(token);
        return (stream, token, expiry);
    }

    private string GenerateSignedToken(long userId)
    {
        var uniquePart = $"{userId}|{DateTime.UtcNow:O}|{Guid.NewGuid()}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(uniquePart));
        var signature = Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(uniquePart))}.{signature}";
    }

    public (bool Valid, long UserId) ValidateToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2)
                return (false, 0);

            var data = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            var dataParts = data.Split('|');
            if (dataParts.Length != 3)
                return (false, 0);

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secretKey));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            var computedSignature = Convert.ToBase64String(computedHash)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            if (!computedSignature.Equals(parts[1], StringComparison.Ordinal))
                return (false, 0);

            if (!long.TryParse(dataParts[0], out var userId))
                return (false, 0);

            if (!_tokens.TryGetValue(token, out var tokenData))
                return (false, 0);

            if (tokenData.Expiry < DateTime.UtcNow)
            {
                _tokens.TryRemove(token, out _);
                return (false, 0);
            }

            if (tokenData.UserId != userId)
                return (false, 0);

            return (true, userId);
        }
        catch
        {
            return (false, 0);
        }
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
                r: pixelBytes[offset + 2],
                g: pixelBytes[offset + 1],
                b: pixelBytes[offset],
                a: pixelBytes[offset + 3]
            );
        }

        return pixels;
    }

    public void CleanupExpiredTokens()
    {
        var now = DateTime.UtcNow;
        foreach (var (token, tokenData) in _tokens)
        {
            if (tokenData.Expiry < now)
            {
                _tokens.TryRemove(token, out _);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}