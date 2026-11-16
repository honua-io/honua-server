// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Honua.Core.Features.Scene.PointCloud;

public static class PntsTileWriter
{
    private const uint Magic = 0x73746E70; // "pnts" little-endian.
    private const uint Version = 1;
    private const int HeaderLength = 28;

    public static byte[] Build(IReadOnlyList<PntsPoint> points, bool eightBitColor)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is required to build a .pnts tile.", nameof(points));
        }

        var count = points.Count;
        var hasColor = points[0].HasColor;

        // RTC center = arithmetic mean of ECEF positions. Subtracting it keeps
        // the Float32 positions small and precise.
        double cx = 0, cy = 0, cz = 0;
        for (var i = 0; i < count; i++)
        {
            cx += points[i].EcefX;
            cy += points[i].EcefY;
            cz += points[i].EcefZ;
        }
        cx /= count;
        cy /= count;
        cz /= count;

        // Feature-table binary: POSITION (VEC3 float32) then optional RGB
        // (3 x uint8). Sizes are known up front, so allocate the final buffer
        // once and write each semantic into its slice — no intermediate copies.
        var positionLength = count * 12;
        var rgbByteOffset = hasColor ? positionLength : -1;
        var featureBinaryArray = new byte[positionLength + (hasColor ? count * 3 : 0)];
        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            BinaryPrimitives.WriteSingleLittleEndian(featureBinaryArray.AsSpan(i * 12, 4), (float)(p.EcefX - cx));
            BinaryPrimitives.WriteSingleLittleEndian(featureBinaryArray.AsSpan(i * 12 + 4, 4), (float)(p.EcefY - cy));
            BinaryPrimitives.WriteSingleLittleEndian(featureBinaryArray.AsSpan(i * 12 + 8, 4), (float)(p.EcefZ - cz));
        }

        if (hasColor)
        {
            // LAS stores colour in 16-bit fields, but many producers stuff 8-bit
            // (0-255) values into those fields unscaled. A blind >>8 would map
            // those to 0 (black). The 8-bit-vs-16-bit interpretation is decided
            // ONCE for the whole cloud by the caller (PointCloudTilesetBuilder)
            // and passed in via eightBitColor, so every tile — leaves and
            // interior coarse-LOD samples alike — uses the same encoding and no
            // seam appears at tile boundaries.
            for (var i = 0; i < count; i++)
            {
                var p = points[i];
                var rgbStart = rgbByteOffset + (i * 3);
                if (eightBitColor)
                {
                    featureBinaryArray[rgbStart] = (byte)p.Red;
                    featureBinaryArray[rgbStart + 1] = (byte)p.Green;
                    featureBinaryArray[rgbStart + 2] = (byte)p.Blue;
                }
                else
                {
                    featureBinaryArray[rgbStart] = (byte)(p.Red >> 8);
                    featureBinaryArray[rgbStart + 1] = (byte)(p.Green >> 8);
                    featureBinaryArray[rgbStart + 2] = (byte)(p.Blue >> 8);
                }
            }
        }

        // Batch-table binary: INTENSITY (uint16) then CLASSIFICATION (uint8).
        var intensityOffset = 0;
        var classificationOffset = count * 2;
        var batchBinaryArray = new byte[(count * 2) + count];
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(batchBinaryArray.AsSpan(i * 2, 2), points[i].Intensity);
            batchBinaryArray[classificationOffset + i] = points[i].Classification;
        }

        var featureTableJson = BuildFeatureTableJson(count, cx, cy, cz, hasColor, rgbByteOffset);
        var batchTableJson = BuildBatchTableJson(intensityOffset, classificationOffset);

        // Pad each JSON section to an 8-byte boundary (PNTS spec) with spaces.
        var featureJsonPadded = PadJson(featureTableJson, HeaderLength);

        // The 3D Tiles PNTS spec requires every binary body to START AND END on
        // an 8-byte boundary. The feature-table JSON above guarantees the
        // feature BINARY starts aligned, but the feature binary itself is
        // count*12 (uncoloured) or count*15 (coloured) bytes — only a multiple
        // of 8 for even uncoloured counts / coloured counts divisible by 8. Pad
        // it with deterministic zero bytes up to the next multiple of 8 so the
        // following batch-table JSON header also starts 8-aligned. The
        // featureTableBinaryByteLength written at offset 16 reflects this padded
        // length so a strict reader sees an aligned layout.
        var featureBinaryPad = (8 - (featureBinaryArray.Length & 7)) & 7;
        var featureBinaryLength = featureBinaryArray.Length + featureBinaryPad;

        var batchJsonPadded = PadJson(batchTableJson, HeaderLength + featureJsonPadded.Length + featureBinaryLength);

        var unpaddedLength = HeaderLength
            + featureJsonPadded.Length + featureBinaryLength
            + batchJsonPadded.Length + batchBinaryArray.Length;

        // The whole tile's byteLength must be 8-byte aligned (3D Tiles PNTS
        // spec). Only the trailing batch-table binary (INTENSITY uint16 +
        // CLASSIFICATION uint8 = count*3 bytes) can leave the total unaligned;
        // pad it with deterministic zero bytes up to the next multiple of 8.
        var trailingPad = (8 - (unpaddedLength & 7)) & 7;
        var batchBinaryLength = batchBinaryArray.Length + trailingPad;

        var totalLength = unpaddedLength + trailingPad;

        var tile = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(tile.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(tile.AsSpan(4, 4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(tile.AsSpan(8, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(tile.AsSpan(12, 4), (uint)featureJsonPadded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(tile.AsSpan(16, 4), (uint)featureBinaryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(tile.AsSpan(20, 4), (uint)batchJsonPadded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(tile.AsSpan(24, 4), (uint)batchBinaryLength);

        var offset = HeaderLength;
        featureJsonPadded.CopyTo(tile.AsSpan(offset));
        offset += featureJsonPadded.Length;
        featureBinaryArray.CopyTo(tile.AsSpan(offset));
        // Skip the zero-padding bytes already present in the zero-initialised
        // tile buffer so the batch-table JSON lands on its 8-aligned offset.
        offset += featureBinaryLength;
        batchJsonPadded.CopyTo(tile.AsSpan(offset));
        offset += batchJsonPadded.Length;
        batchBinaryArray.CopyTo(tile.AsSpan(offset));

        return tile;
    }

    private static byte[] BuildFeatureTableJson(
        int count,
        double cx,
        double cy,
        double cz,
        bool hasColor,
        int rgbByteOffset)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("POINTS_LENGTH", count);

            writer.WriteStartArray("RTC_CENTER");
            writer.WriteNumberValue(cx);
            writer.WriteNumberValue(cy);
            writer.WriteNumberValue(cz);
            writer.WriteEndArray();

            writer.WriteStartObject("POSITION");
            writer.WriteNumber("byteOffset", 0);
            writer.WriteEndObject();

            if (hasColor)
            {
                writer.WriteStartObject("RGB");
                writer.WriteNumber("byteOffset", rgbByteOffset);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] BuildBatchTableJson(int intensityOffset, int classificationOffset)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("INTENSITY");
            writer.WriteNumber("byteOffset", intensityOffset);
            writer.WriteString("componentType", "UNSIGNED_SHORT");
            writer.WriteString("type", "SCALAR");
            writer.WriteEndObject();

            writer.WriteStartObject("CLASSIFICATION");
            writer.WriteNumber("byteOffset", classificationOffset);
            writer.WriteString("componentType", "UNSIGNED_BYTE");
            writer.WriteString("type", "SCALAR");
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] PadJson(byte[] json, int precedingLength)
    {
        var end = precedingLength + json.Length;
        var pad = (8 - (end & 7)) & 7;
        if (pad == 0)
        {
            return json;
        }
        var padded = new byte[json.Length + pad];
        json.CopyTo(padded, 0);
        for (var i = 0; i < pad; i++)
        {
            padded[json.Length + i] = (byte)' ';
        }
        return padded;
    }
}

public readonly record struct PntsPoint(
    double EcefX,
    double EcefY,
    double EcefZ,
    ushort Intensity,
    byte Classification,
    bool HasColor,
    ushort Red,
    ushort Green,
    ushort Blue);
