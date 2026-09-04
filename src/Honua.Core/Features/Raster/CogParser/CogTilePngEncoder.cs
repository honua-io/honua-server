// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// Encodes decoded COG tile samples as an 8-bit RGBA PNG for map clients.
/// This keeps the direct cloud tile path managed and independent of GDAL/native imaging.
/// </summary>
public static class CogTilePngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes a decoded, full-tile pixel buffer as a PNG.</summary>
    public static byte[] Encode(byte[] pixels, CogMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(metadata.TileWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(metadata.TileHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(metadata.BandCount);

        var bytesPerSample = checked((metadata.BitsPerSample + 7) / 8);
        var expected = checked(metadata.TileWidth * metadata.TileHeight * metadata.BandCount * bytesPerSample);
        if (pixels.Length < expected)
        {
            throw new InvalidDataException(
                $"COG tile contains {pixels.Length} decoded bytes, but {expected} are required for its declared pixel layout.");
        }

        var rgba = new byte[checked(metadata.TileWidth * metadata.TileHeight * 4)];
        for (var pixel = 0; pixel < metadata.TileWidth * metadata.TileHeight; pixel++)
        {
            var source = pixel * metadata.BandCount * bytesPerSample;
            var target = pixel * 4;
            var first = ToByte(pixels, source, metadata.BitsPerSample, metadata.PixelType, metadata.IsLittleEndian);
            rgba[target] = first;
            rgba[target + 1] = metadata.BandCount >= 2
                ? ToByte(pixels, source + bytesPerSample, metadata.BitsPerSample, metadata.PixelType, metadata.IsLittleEndian)
                : first;
            rgba[target + 2] = metadata.BandCount >= 3
                ? ToByte(pixels, source + (2 * bytesPerSample), metadata.BitsPerSample, metadata.PixelType, metadata.IsLittleEndian)
                : first;
            rgba[target + 3] = metadata.BandCount >= 4
                ? ToByte(pixels, source + (3 * bytesPerSample), metadata.BitsPerSample, metadata.PixelType, metadata.IsLittleEndian)
                : (byte)255;
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], metadata.TileWidth);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], metadata.TileHeight);
        header[8] = 8;
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var stride = metadata.TileWidth * 4;
            var scanline = new byte[stride + 1];
            for (var row = 0; row < metadata.TileHeight; row++)
            {
                scanline[0] = 0;
                Buffer.BlockCopy(rgba, row * stride, scanline, 1, stride);
                zlib.Write(scanline);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static byte ToByte(byte[] pixels, int offset, int bits, string pixelType, bool littleEndian)
    {
        double value = bits switch
        {
            8 => pixels[offset],
            16 => littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(offset))
                : BinaryPrimitives.ReadUInt16BigEndian(pixels.AsSpan(offset)),
            32 when pixelType.StartsWith("float", StringComparison.Ordinal) =>
                BitConverter.Int32BitsToSingle(littleEndian
                    ? BinaryPrimitives.ReadInt32LittleEndian(pixels.AsSpan(offset))
                    : BinaryPrimitives.ReadInt32BigEndian(pixels.AsSpan(offset))),
            32 => littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(offset))
                : BinaryPrimitives.ReadUInt32BigEndian(pixels.AsSpan(offset)),
            64 when pixelType.StartsWith("float", StringComparison.Ordinal) =>
                BitConverter.Int64BitsToDouble(littleEndian
                    ? BinaryPrimitives.ReadInt64LittleEndian(pixels.AsSpan(offset))
                    : BinaryPrimitives.ReadInt64BigEndian(pixels.AsSpan(offset))),
            64 => littleEndian
                ? BinaryPrimitives.ReadUInt64LittleEndian(pixels.AsSpan(offset))
                : BinaryPrimitives.ReadUInt64BigEndian(pixels.AsSpan(offset)),
            _ => throw new NotSupportedException($"COG PNG encoding does not support {bits}-bit samples.")
        };

        if (!double.IsFinite(value))
        {
            return 0;
        }

        if (bits > 8 && !pixelType.StartsWith("float", StringComparison.Ordinal))
        {
            value /= Math.Pow(2, bits) - 1;
            value *= 255;
        }

        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        output.Write(typeBytes);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type) crc = CrcByte(crc, value);
        foreach (var value in data) crc = CrcByte(crc, value);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }
}
