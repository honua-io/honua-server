// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>Packages a decoded tile as a single-tile GeoTIFF without changing its numeric samples.</summary>
public static class CogTiffTileEncoder
{
    /// <summary>
    /// Writes unsigned, signed or floating-point chunky grayscale/RGB samples, with pixel-area
    /// georeferencing for the requested tile. Unsupported layouts return null.
    /// </summary>
    public static byte[]? Encode(byte[] samples, CogMetadata metadata, RasterExtent extent)
    {
        var bits = metadata.BitsPerSample;
        var sampleFormat = metadata.PixelType == $"uint{bits}" ? 1
            : metadata.PixelType == $"int{bits}" ? 2
            : metadata.PixelType == $"float{bits}" && bits is 32 or 64 ? 3 : 0;
        if (sampleFormat == 0 || bits is not (8 or 16 or 32 or 64) || metadata.PlanarConfiguration != 1
            || extent.Srid != 3857 || metadata.TileWidth % 16 != 0 || metadata.TileHeight % 16 != 0
            || !((metadata.BandCount == 1 && metadata.PhotometricInterpretation is 0 or 1)
                || (metadata.BandCount == 3 && metadata.PhotometricInterpretation == 2)))
        {
            return null;
        }
        var expected = checked((long)metadata.TileWidth * metadata.TileHeight * metadata.BandCount * (bits / 8));
        if (metadata.TileWidth <= 0 || metadata.TileHeight <= 0
            || expected > TileDecompressor.DefaultMaxDecompressedBytes || samples.Length != expected)
        {
            throw new InvalidDataException("Decoded COG tile length does not match its pixel layout.");
        }

        var fields = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (256, 4, 1, LongValue(metadata.TileWidth)),
            (257, 4, 1, LongValue(metadata.TileHeight)),
            (258, 3, (uint)metadata.BandCount, ShortValues(Enumerable.Repeat((ushort)bits, metadata.BandCount).ToArray())),
            (259, 3, 1, ShortValues(1)),
            (262, 3, 1, ShortValues((ushort)metadata.PhotometricInterpretation)),
            (277, 3, 1, ShortValues((ushort)metadata.BandCount)),
            (284, 3, 1, ShortValues(1)),
            (322, 4, 1, LongValue(metadata.TileWidth)),
            (323, 4, 1, LongValue(metadata.TileHeight)),
            (324, 4, 1, LongValue(0)), // Filled once all IFD and tag payload sizes are known.
            (325, 4, 1, LongValue(samples.Length)),
            (339, 3, (uint)metadata.BandCount, ShortValues(Enumerable.Repeat((ushort)sampleFormat, metadata.BandCount).ToArray())),
            (33550, 12, 3, DoubleValues((extent.XMax - extent.XMin) / metadata.TileWidth,
                (extent.YMax - extent.YMin) / metadata.TileHeight, 0)),
            (33922, 12, 6, DoubleValues(0, 0, 0, extent.XMin, extent.YMax, 0)),
            (34735, 3, 16, ShortValues(1, 1, 0, 3, 1024, 0, 1, 1, 1025, 0, 1, 1,
                3072, 0, 1, checked((ushort)extent.Srid)))
        };
        if (metadata.NoData is not null)
        {
            var noData = Encoding.ASCII.GetBytes(metadata.NoData + "\0");
            fields.Add((42113, 2, (uint)noData.Length, noData));
        }

        var externalOffset = 8 + 2 + fields.Count * 12 + 4;
        var tileOffset = externalOffset + fields.Where(field => field.Value.Length > 4)
            .Sum(field => (field.Value.Length + 1) & ~1);
        using var stream = new MemoryStream(checked(tileOffset + samples.Length));
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8u);
        writer.Write((ushort)fields.Count);
        foreach (var field in fields)
        {
            writer.Write(field.Tag);
            writer.Write(field.Type);
            writer.Write(field.Count);
            if (field.Tag == 324)
            {
                writer.Write((uint)tileOffset);
            }
            else if (field.Value.Length <= 4)
            {
                writer.Write(field.Value);
                for (var pad = field.Value.Length; pad < 4; pad++) writer.Write((byte)0);
            }
            else
            {
                writer.Write((uint)externalOffset);
                externalOffset += (field.Value.Length + 1) & ~1;
            }
        }
        writer.Write(0u);
        foreach (var field in fields.Where(field => field.Value.Length > 4))
        {
            writer.Write(field.Value);
            if (field.Value.Length % 2 != 0) writer.Write((byte)0);
        }
        if (metadata.IsLittleEndian || bits == 8)
        {
            writer.Write(samples);
        }
        else
        {
            for (var sample = 0; sample < samples.Length; sample += bits / 8)
                for (var b = bits / 8 - 1; b >= 0; b--) writer.Write(samples[sample + b]);
        }
        return stream.ToArray();
    }

    private static byte[] LongValue(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] ShortValues(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    private static byte[] DoubleValues(params double[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), values[i]);
        return bytes;
    }
}
