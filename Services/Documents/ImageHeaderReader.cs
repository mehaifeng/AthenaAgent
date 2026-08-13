using System;
using System.Buffers.Binary;
using System.IO;

namespace Athena.UI.Services.Documents;

/// <summary>
/// Reads pixel dimensions and resolution straight from an image header. Embedding a picture needs
/// its intrinsic size in EMUs, and decoding the whole bitmap just to learn its width would pull in
/// an imaging stack Athena does not otherwise need.
/// </summary>
internal static class ImageHeaderReader
{
    private const double DefaultDpi = 96d;

    public static (int Width, int Height, double Dpi) Measure(byte[] content, string contentType)
    {
        var measured = contentType switch
        {
            "image/png" => ReadPng(content),
            "image/jpeg" => ReadJpeg(content),
            "image/gif" => ReadGif(content),
            "image/bmp" => ReadBmp(content),
            _ => null
        };

        if (measured is not { Width: > 0, Height: > 0 })
            throw new InvalidDataException("Unable to read the image dimensions; the file may be truncated or not the type its extension claims.");

        var dpi = measured.Value.Dpi is > 1 and < 2400 ? measured.Value.Dpi : DefaultDpi;
        return (measured.Value.Width, measured.Value.Height, dpi);
    }

    private static (int Width, int Height, double Dpi)? ReadPng(byte[] content)
    {
        if (content.Length < 24) return null;
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (!content.AsSpan(0, 8).SequenceEqual(signature)) return null;
        if (System.Text.Encoding.ASCII.GetString(content, 12, 4) != "IHDR") return null;

        var width = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(20, 4));
        var dpi = DefaultDpi;

        // Walk the chunk list for pHYs, which stores pixels per metre.
        var offset = 8;
        while (offset + 12 <= content.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(offset, 4));
            if (length < 0 || offset + 12 + length > content.Length) break;
            var type = System.Text.Encoding.ASCII.GetString(content, offset + 4, 4);
            if (type == "pHYs" && length >= 9)
            {
                var perUnitX = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(offset + 8, 4));
                var unit = content[offset + 16];
                if (unit == 1 && perUnitX > 0) dpi = perUnitX * 0.0254d;
                break;
            }
            if (type == "IDAT") break;
            offset += 12 + length;
        }

        return (width, height, dpi);
    }

    private static (int Width, int Height, double Dpi)? ReadJpeg(byte[] content)
    {
        if (content.Length < 4 || content[0] != 0xFF || content[1] != 0xD8) return null;
        var dpi = DefaultDpi;
        var offset = 2;

        while (offset + 4 <= content.Length)
        {
            if (content[offset] != 0xFF) { offset++; continue; }
            var marker = content[offset + 1];
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { offset += 2; continue; }
            if (offset + 4 > content.Length) break;

            var length = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(offset + 2, 2));
            if (length < 2 || offset + 2 + length > content.Length) break;
            var data = offset + 4;

            if (marker == 0xE0 && length >= 14 && System.Text.Encoding.ASCII.GetString(content, data, 4) == "JFIF")
            {
                var units = content[data + 7];
                var densityX = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(data + 8, 2));
                if (densityX > 0)
                {
                    if (units == 1) dpi = densityX;
                    else if (units == 2) dpi = densityX * 2.54d;
                }
            }

            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isStartOfFrame && length >= 7)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(data + 1, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(data + 3, 2));
                return (width, height, dpi);
            }

            offset += 2 + length;
        }

        return null;
    }

    private static (int Width, int Height, double Dpi)? ReadGif(byte[] content)
    {
        if (content.Length < 10) return null;
        var header = System.Text.Encoding.ASCII.GetString(content, 0, 6);
        if (header is not ("GIF87a" or "GIF89a")) return null;
        return (BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(6, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(8, 2)),
                DefaultDpi);
    }

    private static (int Width, int Height, double Dpi)? ReadBmp(byte[] content)
    {
        if (content.Length < 46 || content[0] != 'B' || content[1] != 'M') return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(18, 4));
        var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(22, 4)));
        var pixelsPerMetre = BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(38, 4));
        var dpi = pixelsPerMetre > 0 ? pixelsPerMetre * 0.0254d : DefaultDpi;
        return (width, height, dpi);
    }
}
