// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Core.Features.Raster.CogParser;

public static class CogTileEncoder
{
    // Returns true when the COG metadata describes a chunky unsigned 8- or 16-bit grayscale or RGB
    // tile layout supported by the shared display encoders.
    public static bool CanEncodeDisplaySamples(CogMetadata metadata)
        => metadata.PlanarConfiguration == 1
           && metadata.BitsPerSample is 8 or 16
           && metadata.PixelType == $"uint{metadata.BitsPerSample}"
           && ((metadata.BandCount == 1 && metadata.PhotometricInterpretation is 0 or 1)
               || (metadata.BandCount == 3 && metadata.PhotometricInterpretation == 2));

    public static byte[]? EncodePng(byte[] samples, CogMetadata metadata)
    {
        if (!CanEncodeDisplaySamples(metadata))
        {
            return null;
        }

        ValidateSampleLength(samples.Length, metadata);
        var noData = ParseDisplayNoData(metadata);
        if (noData.IsInvalid)
        {
            return null;
        }

        var transparency = BuildPngTransparencyChunk(noData.Value, metadata);
        var pngSamples = BuildPngSampleBuffer(samples, metadata);
        return PngEncoder.EncodeSamples(pngSamples, metadata.TileWidth, metadata.TileHeight,
            metadata.BandCount, metadata.BitsPerSample, transparency);
    }

    // Converts decoded chunky COG samples directly into an unpremultiplied RGBA8888 buffer for
    // compositing without an intermediate PNG encode/decode cycle.
    public static byte[]? DecodeToRgba8888(ReadOnlySpan<byte> samples, CogMetadata metadata)
    {
        if (!CanEncodeDisplaySamples(metadata) || !HasValidSampleLength(samples.Length, metadata))
        {
            return null;
        }

        var noData = ParseDisplayNoData(metadata);
        if (noData.IsInvalid)
        {
            return null;
        }

        var pixelCount = checked(metadata.TileWidth * metadata.TileHeight);
        var rgba = new byte[checked(pixelCount * 4)];
        var stride = metadata.BandCount * (metadata.BitsPerSample / 8);
        var context = new RgbaDecodeContext { Metadata = metadata, NoData = noData.Value };
        for (var i = 0; i < pixelCount; i++)
        {
            WriteRgbaPixel(samples.Slice(i * stride, stride), rgba.AsSpan(i * 4, 4), context);
        }

        return rgba;
    }

    private static bool HasValidSampleLength(int sampleLength, CogMetadata metadata)
    {
        if (metadata.TileWidth <= 0 || metadata.TileHeight <= 0)
        {
            return false;
        }

        var expected = checked((long)metadata.TileWidth * metadata.TileHeight * metadata.BandCount * (metadata.BitsPerSample / 8));
        return expected <= TileDecompressor.DefaultMaxDecompressedBytes && sampleLength == expected;
    }

    private static void ValidateSampleLength(int sampleLength, CogMetadata metadata)
    {
        if (!HasValidSampleLength(sampleLength, metadata))
        {
            throw new InvalidDataException("Decoded COG tile length does not match its pixel layout.");
        }
    }

    private static ParsedNoData ParseDisplayNoData(CogMetadata metadata)
    {
        if (metadata.NoData is null)
        {
            return new ParsedNoData { Value = null };
        }

        var maximum = SampleMaximum(metadata.BitsPerSample);
        if (!double.TryParse(metadata.NoData, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0 || value > maximum
            || BitConverter.DoubleToInt64Bits(value + 0d) != BitConverter.DoubleToInt64Bits(Math.Truncate(value) + 0d))
        {
            return new ParsedNoData { IsInvalid = true };
        }

        return new ParsedNoData { Value = (int)value };
    }

    private static byte[]? BuildPngTransparencyChunk(int? noData, CogMetadata metadata)
    {
        if (noData is not { } code)
        {
            return null;
        }

        var encoded = (ushort)NormalizeSample(code, metadata);
        var transparency = new byte[metadata.BandCount * 2];
        for (var band = 0; band < metadata.BandCount; band++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(transparency.AsSpan(band * 2), encoded);
        }

        return transparency;
    }

    private static byte[] BuildPngSampleBuffer(ReadOnlySpan<byte> samples, CogMetadata metadata)
    {
        var bytesPerSample = metadata.BitsPerSample / 8;
        var pngSamples = new byte[samples.Length];
        for (var offset = 0; offset < samples.Length; offset += bytesPerSample)
        {
            var value = NormalizeSample(ReadUnsignedSample(samples, offset, metadata), metadata);
            if (bytesPerSample == 1)
            {
                pngSamples[offset] = (byte)value;
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(pngSamples.AsSpan(offset), (ushort)value);
            }
        }

        return pngSamples;
    }

    private static void WriteRgbaPixel(ReadOnlySpan<byte> src, Span<byte> dst, RgbaDecodeContext context)
    {
        var metadata = context.Metadata;
        if (metadata.BandCount == 1)
        {
            var raw = ReadUnsignedSample(src, 0, metadata);
            if (context.NoData == raw)
            {
                dst.Clear();
                return;
            }

            var gray = ToByte(NormalizeSample(raw, metadata), metadata.BitsPerSample);
            dst[0] = gray;
            dst[1] = gray;
            dst[2] = gray;
            dst[3] = 255;
            return;
        }

        var bytesPerSample = metadata.BitsPerSample / 8;
        var r = ReadUnsignedSample(src, 0, metadata);
        var g = ReadUnsignedSample(src, bytesPerSample, metadata);
        var b = ReadUnsignedSample(src, bytesPerSample * 2, metadata);
        if (context.NoData is { } code && r == code && g == code && b == code)
        {
            dst.Clear();
            return;
        }

        dst[0] = ToByte(r, metadata.BitsPerSample);
        dst[1] = ToByte(g, metadata.BitsPerSample);
        dst[2] = ToByte(b, metadata.BitsPerSample);
        dst[3] = 255;
    }

    private static int ReadUnsignedSample(ReadOnlySpan<byte> src, int offset, CogMetadata metadata)
        => metadata.BitsPerSample == 8
            ? src[offset]
            : metadata.IsLittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(offset))
                : BinaryPrimitives.ReadUInt16BigEndian(src.Slice(offset));

    private static int NormalizeSample(int value, CogMetadata metadata)
        => metadata.PhotometricInterpretation == 0 ? SampleMaximum(metadata.BitsPerSample) - value : value;

    private static int SampleMaximum(int bitsPerSample)
        => bitsPerSample == 8 ? byte.MaxValue : ushort.MaxValue;

    private static byte ToByte(int value, int bitsPerSample)
        => bitsPerSample == 16 ? (byte)(value >> 8) : (byte)value;

    private readonly record struct ParsedNoData
    {
        public int? Value { get; init; }

        public bool IsInvalid { get; init; }
    }

    private readonly record struct RgbaDecodeContext
    {
        public required CogMetadata Metadata { get; init; }

        public int? NoData { get; init; }
    }
}
