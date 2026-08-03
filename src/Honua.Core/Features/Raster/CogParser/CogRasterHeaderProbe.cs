// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>Bounded header-probe result and observable range budget.</summary>
/// <param name="Dimensions">Trusted dimensions from the ETag-pinned object.</param>
/// <param name="RangeCount">Number of conditional object ranges read.</param>
/// <param name="RequestedBytes">Total bytes requested across all ranges.</param>
/// <param name="ReceivedBytes">Total bytes returned across all ranges.</param>
public sealed record CogRasterHeaderProbeResult(
    RasterSourceDimensions Dimensions,
    int RangeCount,
    long RequestedBytes,
    long ReceivedBytes);

/// <summary>
/// Reads only the first TIFF/BigTIFF directory fields needed for raster resource admission.
/// Every range and allocation is capped; tile-offset arrays and pixel content are never read.
/// </summary>
public static class CogRasterHeaderProbe
{
    private const int InitialReadBytes = 4 * 1024;
    private const int MaxIfdBytes = 64 * 1024;
    private const int MaxBitsPerSampleBytes = 8 * 1024;

    /// <summary>Reads width, height, band count, and maximum bits per sample.</summary>
    public static async Task<CogRasterHeaderProbeResult> ReadAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        string expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);

        var probe = new BoundedRangeProbe(reader, bucket, key, expectedETag);
        var initial = await probe
            .ReadAsync(0, InitialReadBytes, cancellationToken)
            .ConfigureAwait(false);
        var (parser, firstIfdOffset) = TiffIfdParser.ParseHeader(initial);
        if (firstIfdOffset <= 0)
        {
            throw new InvalidDataException("TIFF header has no base-image IFD.");
        }

        var countBytes = parser.IsBigTiff ? 8 : 2;
        long declaredEntryCount;
        if (firstIfdOffset <= int.MaxValue - countBytes
            && firstIfdOffset + countBytes <= initial.Length)
        {
            declaredEntryCount = parser.ReadIfdEntryCount(
                initial.AsSpan((int)firstIfdOffset));
        }
        else
        {
            var prefix = await probe
                .ReadAsync(firstIfdOffset, countBytes, cancellationToken)
                .ConfigureAwait(false);
            declaredEntryCount = parser.ReadIfdEntryCount(prefix);
        }

        if (declaredEntryCount is <= 0 or > TiffIfdParser.MaxIfdEntryCount)
        {
            throw new InvalidDataException(
                $"TIFF base-image IFD entry count {declaredEntryCount} is outside the supported range.");
        }

        var exactIfdBytes = parser.CalculateIfdReadSize(declaredEntryCount);
        if (exactIfdBytes > MaxIfdBytes)
        {
            throw new InvalidDataException(
                $"TIFF base-image IFD requires {exactIfdBytes} bytes, exceeding the bounded {MaxIfdBytes}-byte admission probe.");
        }

        IfdEntry[] entries;
        if (firstIfdOffset <= int.MaxValue - exactIfdBytes
            && firstIfdOffset + exactIfdBytes <= initial.Length)
        {
            entries = parser.ParseIfd(initial.AsSpan((int)firstIfdOffset, exactIfdBytes)).Entries;
        }
        else
        {
            var ifd = await probe
                .ReadAsync(firstIfdOffset, exactIfdBytes, cancellationToken)
                .ConfigureAwait(false);
            entries = parser.ParseIfd(ifd).Entries;
        }

        var width = ReadRequiredScalar(entries, TiffConstants.TagImageWidth, "width");
        var height = ReadRequiredScalar(entries, TiffConstants.TagImageLength, "height");
        var bandCountValue = ReadOptionalScalar(entries, TiffConstants.TagSamplesPerPixel, 1);
        if (bandCountValue is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException($"TIFF declares invalid band count {bandCountValue}.");
        }

        var bitsPerSample = await ReadMaximumBitsPerSampleAsync(
                probe,
                parser,
                entries,
                bandCountValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (width <= 0 || height <= 0 || bitsPerSample <= 0)
        {
            throw new InvalidDataException("TIFF declares non-positive raster dimensions.");
        }

        return new CogRasterHeaderProbeResult(
            new RasterSourceDimensions(
                width,
                height,
                checked((int)bandCountValue),
                bitsPerSample),
            probe.RangeCount,
            probe.RequestedBytes,
            probe.ReceivedBytes);
    }

    private static long ReadRequiredScalar(
        IReadOnlyList<IfdEntry> entries,
        ushort tag,
        string field)
    {
        var entry = entries.FirstOrDefault(candidate => candidate.Tag == tag);
        if (entry.Tag != tag
            || entry.Count != 1
            || !entry.IsInline
            || entry.Type is not (TiffConstants.TypeShort
                or TiffConstants.TypeLong
                or TiffConstants.TypeLong8))
        {
            throw new InvalidDataException(
                $"TIFF base-image IFD has no supported unsigned scalar {field}.");
        }

        return entry.ValueOrOffset;
    }

    private static long ReadOptionalScalar(
        IReadOnlyList<IfdEntry> entries,
        ushort tag,
        long defaultValue)
    {
        var entry = entries.FirstOrDefault(candidate => candidate.Tag == tag);
        if (entry.Tag != tag)
        {
            return defaultValue;
        }

        if (entry.Count != 1 || !entry.IsInline || entry.Type != TiffConstants.TypeShort)
        {
            throw new InvalidDataException(
                "TIFF base-image IFD has an unsupported SamplesPerPixel field.");
        }

        return entry.ValueOrOffset;
    }

    private static async Task<int> ReadMaximumBitsPerSampleAsync(
        BoundedRangeProbe probe,
        TiffIfdParser parser,
        IReadOnlyList<IfdEntry> entries,
        long bandCount,
        CancellationToken cancellationToken)
    {
        var entry = entries.FirstOrDefault(candidate =>
            candidate.Tag == TiffConstants.TagBitsPerSample);
        if (entry.Tag != TiffConstants.TagBitsPerSample)
        {
            return 8;
        }

        if (entry.Type != TiffConstants.TypeShort
            || entry.Count is <= 0 or > int.MaxValue
            || (entry.Count != 1 && entry.Count != bandCount))
        {
            throw new InvalidDataException(
                "TIFF BitsPerSample must contain one shared unsigned SHORT or one value per declared band.");
        }

        int[] values;
        if (entry.Count == 1)
        {
            values = [checked((int)entry.ValueOrOffset)];
        }
        else if (entry.IsInline)
        {
            values = parser.ReadInlineIntArray(
                entry.ValueOrOffset,
                checked((int)entry.Count),
                entry.Type);
        }
        else
        {
            var byteCount = checked((int)(entry.Count * TiffConstants.GetTypeSize(entry.Type)));
            if (byteCount > MaxBitsPerSampleBytes)
            {
                throw new InvalidDataException(
                    $"TIFF BitsPerSample array requires {byteCount} bytes, exceeding the bounded {MaxBitsPerSampleBytes}-byte admission probe.");
            }

            var data = await probe
                .ReadAsync(entry.ValueOrOffset, byteCount, cancellationToken)
                .ConfigureAwait(false);
            values = parser.ReadIntArray(data, checked((int)entry.Count), entry.Type);
        }

        if (values.Length == 0 || values.Any(value => value <= 0))
        {
            throw new InvalidDataException("TIFF declares invalid BitsPerSample values.");
        }

        return values.Max();
    }

    private sealed class BoundedRangeProbe(
        ICloudRangeReader reader,
        string bucket,
        string key,
        string expectedETag)
    {
        public int RangeCount { get; private set; }

        public long RequestedBytes { get; private set; }

        public long ReceivedBytes { get; private set; }

        public async Task<byte[]> ReadAsync(
            long offset,
            int length,
            CancellationToken cancellationToken)
        {
            if (offset < 0
                || length <= 0
                || offset > long.MaxValue - (length - 1L))
            {
                throw new InvalidDataException(
                    "TIFF admission probe requested an invalid or overflowing object range.");
            }

            var bytes = await reader
                .ReadRangeAsync(bucket, key, offset, length, expectedETag, cancellationToken)
                .ConfigureAwait(false);
            if (bytes.Length > length)
            {
                throw new InvalidDataException(
                    "Object range reader returned more data than the bounded admission probe requested.");
            }

            RangeCount = checked(RangeCount + 1);
            RequestedBytes = checked(RequestedBytes + length);
            ReceivedBytes = checked(ReceivedBytes + bytes.Length);
            return bytes;
        }
    }
}
