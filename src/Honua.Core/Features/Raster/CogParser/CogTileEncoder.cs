// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>Losslessly packages decoded COG samples as a client-readable PNG.</summary>
public static class CogTileEncoder
{
    /// <summary>
    /// Encodes chunky unsigned 8/16-bit grayscale or RGB samples, retaining their bit depth
    /// and representing a declared nodata sample with PNG transparency. Unsupported layouts
    /// return null; malformed sample buffers are rejected before encoding.
    /// </summary>
    public static byte[]? EncodePng(byte[] samples, CogMetadata metadata)
    {
        if (metadata.PlanarConfiguration != 1 || metadata.BitsPerSample is not (8 or 16)
            || metadata.PixelType != $"uint{metadata.BitsPerSample}"
            || !((metadata.BandCount == 1 && metadata.PhotometricInterpretation is 0 or 1)
                || (metadata.BandCount == 3 && metadata.PhotometricInterpretation == 2)))
        {
            return null;
        }

        var bytesPerSample = metadata.BitsPerSample / 8;
        var expected = checked((long)metadata.TileWidth * metadata.TileHeight * metadata.BandCount * bytesPerSample);
        if (metadata.TileWidth <= 0 || metadata.TileHeight <= 0
            || expected > TileDecompressor.DefaultMaxDecompressedBytes || samples.Length != expected)
        {
            throw new InvalidDataException("Decoded COG tile length does not match its pixel layout.");
        }

        var maximum = metadata.BitsPerSample == 8 ? byte.MaxValue : ushort.MaxValue;
        byte[]? transparency = null;
        if (metadata.NoData is not null)
        {
            if (!double.TryParse(metadata.NoData, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value < 0 || value > maximum || value != Math.Truncate(value))
            {
                return null;
            }
            var noData = (ushort)value;
            if (metadata.PhotometricInterpretation == 0)
            {
                noData = (ushort)(maximum - noData);
            }
            transparency = new byte[metadata.BandCount * 2];
            for (var band = 0; band < metadata.BandCount; band++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(transparency.AsSpan(band * 2), noData);
            }
        }

        // PNG stores 16-bit samples in network byte order and grayscale as black-is-zero.
        var pngSamples = new byte[samples.Length];
        for (var offset = 0; offset < samples.Length; offset += bytesPerSample)
        {
            int value = bytesPerSample == 1 ? samples[offset]
                : metadata.IsLittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(samples.AsSpan(offset))
                : BinaryPrimitives.ReadUInt16BigEndian(samples.AsSpan(offset));
            if (metadata.PhotometricInterpretation == 0)
            {
                value = maximum - value;
            }
            if (bytesPerSample == 1)
            {
                pngSamples[offset] = (byte)value;
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(pngSamples.AsSpan(offset), (ushort)value);
            }
        }
        return PngEncoder.EncodeSamples(pngSamples, metadata.TileWidth, metadata.TileHeight,
            metadata.BandCount, metadata.BitsPerSample, transparency);
    }
}
