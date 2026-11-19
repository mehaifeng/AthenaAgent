using System;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Athena.UI.Styles;

internal static class EmptyConversationLogo
{
    public const string ResourceKey = "App.EmptyConversationLogo";

    private const int BitmapWidth = 512;
    private const double TintStrength = 0.58;
    private static readonly Lazy<WriteableBitmap> Original = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://Athena.UI/Assets/EmptyConversationLogo.png"));
        return WriteableBitmap.DecodeToWidth(stream, BitmapWidth, BitmapInterpolationMode.HighQuality);
    });

    public static Bitmap Create(Color? accent)
    {
        var source = Original.Value;
        if (accent is not { } color) return source;

        var bitmap = new WriteableBitmap(source.PixelSize, source.Dpi, PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        try
        {
            using var frame = bitmap.Lock();
            source.CopyPixels(frame);
            ApplyTint(frame, color);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void ApplyTint(ILockedFramebuffer frame, Color accent)
    {
        // Use the accent's hue while keeping the original weighted RGB brightness.
        // The tint peaks in the midtones and tapers to zero at black and white.
        var brightness = Brightness(accent.R, accent.G, accent.B);
        var red = (accent.R - brightness) * TintStrength;
        var green = (accent.G - brightness) * TintStrength;
        var blue = (accent.B - brightness) * TintStrength;
        var row = new byte[frame.Size.Width * 4];
        for (var y = 0; y < frame.Size.Height; y++)
        {
            var address = IntPtr.Add(frame.Address, y * frame.RowBytes);
            Marshal.Copy(address, row, 0, row.Length);
            for (var i = 0; i < row.Length; i += 4)
            {
                if (row[i + 3] == 0) continue;
                var tone = Brightness(row[i], row[i + 1], row[i + 2]) / 255;
                var weight = 4 * tone * (1 - tone);
                row[i] = Shift(row[i], red * weight);
                row[i + 1] = Shift(row[i + 1], green * weight);
                row[i + 2] = Shift(row[i + 2], blue * weight);
            }
            Marshal.Copy(row, 0, address, row.Length);
        }
    }

    private static double Brightness(byte red, byte green, byte blue) => 0.2126 * red + 0.7152 * green + 0.0722 * blue;

    private static byte Shift(byte channel, double amount) =>
        (byte)Math.Clamp(Math.Round(channel + amount), 0, 255);
}
