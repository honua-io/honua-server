// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// AOT-safe, pure-managed implementation of <see cref="ICogDecodedSizeInspector"/> built on the
/// existing <see cref="TiffIfdParser"/>. It reads at most two bounded windows (the header window
/// and, when the first IFD lies beyond it, the IFD window) plus — only when BitsPerSample is
/// stored externally — a single element read, all within <see cref="MaxProbeReadBytes"/>. It
/// deliberately stops after the first IFD and never touches TileOffsets/TileByteCounts or pixel
/// tiles, so the projected decoded size is computed without materializing the raster (#3090).
/// </summary>
public sealed class CogDecodedSizeInspector : ICogDecodedSizeInspector
{
    /// <summary>
    /// Upper bound on any single range read the probe issues, and therefore on the largest
    /// buffer it allocates. 64 KiB comfortably holds a TIFF header plus a first IFD of several
    /// thousand entries (real COGs carry well under 100), while capping a crafted IFD that
    /// declares a huge entry count from driving a large read. An IFD whose exact size exceeds
    /// this cap is rejected (fail closed) rather than truncated.
    /// </summary>
    internal const int MaxProbeReadBytes = 64 * 1024;

    /// <inheritdoc />
    public async Task<CogDecodedSizeInspection> InspectAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        long maxDecodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        try
        {
            return await InspectCoreAsync(reader, bucket, key, maxDecodedBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Fail closed: any malformed-header/parse/read failure means we could not prove the
            // decoded size is bounded, so the source must be rejected rather than materialized.
            return Reject("the raster header could not be read within the inspection limits.");
        }
    }

    private static async Task<CogDecodedSizeInspection> InspectCoreAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        long maxDecodedBytes,
        CancellationToken cancellationToken)
    {
        // Window 1: the file header (+ hopefully the first IFD for compact COGs).
        var headerWindow = await reader.ReadRangeAsync(bucket, key, 0, MaxProbeReadBytes, cancellationToken)
            .ConfigureAwait(false);
        if (headerWindow.Length < TiffConstants.ClassicHeaderSize)
        {
            return Reject("the raster object is too small to contain a TIFF header.");
        }

        var (parser, firstIfdOffset) = TiffIfdParser.ParseHeader(headerWindow);
        if (firstIfdOffset <= 0)
        {
            return Reject("the raster header does not declare a valid image directory offset.");
        }

        // Resolve a buffer whose span, at spanOffset, begins at the first IFD. Prefer slicing the
        // header window; otherwise issue the single IFD window read.
        var minCountBytes = parser.IsBigTiff ? 8 : 2;
        byte[] ifdBuffer;
        int spanOffset;
        if (firstIfdOffset <= headerWindow.Length - minCountBytes)
        {
            ifdBuffer = headerWindow;
            spanOffset = (int)firstIfdOffset;
        }
        else
        {
            ifdBuffer = await reader.ReadRangeAsync(bucket, key, firstIfdOffset, MaxProbeReadBytes, cancellationToken)
                .ConfigureAwait(false);
            spanOffset = 0;
        }

        if (ifdBuffer.Length - spanOffset < minCountBytes)
        {
            return Reject("the raster image directory is truncated within the inspection limits.");
        }

        var declaredEntryCount = parser.ReadIfdEntryCount(ifdBuffer.AsSpan(spanOffset));
        if (declaredEntryCount <= 0 || declaredEntryCount > TiffIfdParser.MaxIfdEntryCount)
        {
            return Reject("the raster image directory declares an invalid entry count.");
        }

        var exactIfdSize = parser.CalculateIfdReadSize(declaredEntryCount);
        if (exactIfdSize > MaxProbeReadBytes)
        {
            return Reject("the raster image directory is too large to inspect within the limits.");
        }

        // Ensure the chosen buffer actually contains the full IFD; re-read at the exact size when
        // the header-window slice falls short. The size is bounded by MaxProbeReadBytes above.
        if (ifdBuffer.Length - spanOffset < exactIfdSize)
        {
            ifdBuffer = await reader.ReadRangeAsync(bucket, key, firstIfdOffset, exactIfdSize, cancellationToken)
                .ConfigureAwait(false);
            spanOffset = 0;
            if (ifdBuffer.Length < exactIfdSize)
            {
                return Reject("the raster image directory is truncated within the inspection limits.");
            }
        }

        var (entries, _) = parser.ParseIfd(ifdBuffer.AsSpan(spanOffset));

        long width = 0, height = 0, bands = 1;
        IfdEntry? bitsPerSampleEntry = null;
        foreach (var entry in entries)
        {
            switch (entry.Tag)
            {
                case TiffConstants.TagImageWidth:
                    width = entry.ValueOrOffset;
                    break;
                case TiffConstants.TagImageLength:
                    height = entry.ValueOrOffset;
                    break;
                case TiffConstants.TagSamplesPerPixel:
                    bands = entry.ValueOrOffset;
                    break;
                case TiffConstants.TagBitsPerSample:
                    bitsPerSampleEntry = entry;
                    break;
            }
        }

        if (width <= 0 || height <= 0)
        {
            return Reject("the raster header does not declare valid image dimensions.");
        }

        // Clamp the multipliers to at least 1 so a declared 0 cannot zero-out the projection and
        // slip past the ceiling; an over-declared value only inflates the projection (fail closed).
        if (bands < 1)
        {
            bands = 1;
        }

        var bitsPerSample = bitsPerSampleEntry is { } bps
            ? await ReadFirstBitsPerSampleAsync(reader, bucket, key, bps, parser, cancellationToken).ConfigureAwait(false)
            : 8;
        if (bitsPerSample < 1)
        {
            bitsPerSample = 1;
        }

        var bytesPerSample = (bitsPerSample + 7) / 8;
        var projected = SaturatingMultiply(
            SaturatingMultiply(SaturatingMultiply(width, height), bands),
            bytesPerSample);

        var bandCount = bands > int.MaxValue ? int.MaxValue : (int)bands;

        if (projected > maxDecodedBytes)
        {
            return new CogDecodedSizeInspection(
                Accepted: false,
                Width: width,
                Height: height,
                BandCount: bandCount,
                BitsPerSample: bitsPerSample,
                ProjectedDecodedBytes: projected,
                RejectionReason:
                    $"the raster declares a decoded size of {DescribeBytes(projected)} "
                    + $"(width {width} x height {height} x {bandCount} band(s) x {bytesPerSample} byte(s)/sample), "
                    + $"which exceeds the maximum {DescribeBytes(maxDecodedBytes)} accepted for inline sourcing.");
        }

        return new CogDecodedSizeInspection(
            Accepted: true,
            Width: width,
            Height: height,
            BandCount: bandCount,
            BitsPerSample: bitsPerSample,
            ProjectedDecodedBytes: projected,
            RejectionReason: null);
    }

    /// <summary>
    /// Reads only the first BitsPerSample element. When the value is stored inline it is decoded
    /// from the entry field; when external, a single-element range read (a few bytes) is issued at
    /// the entry offset — never the whole array — keeping the probe within its allocation caps.
    /// </summary>
    private static async Task<int> ReadFirstBitsPerSampleAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        IfdEntry entry,
        TiffIfdParser parser,
        CancellationToken cancellationToken)
    {
        if (entry.Count < 1)
        {
            return 8;
        }

        if (entry.IsInline)
        {
            var inline = parser.ReadInlineIntArray(entry.ValueOrOffset, 1, entry.Type);
            return inline.Length > 0 ? inline[0] : 8;
        }

        var typeSize = TiffConstants.GetTypeSize(entry.Type);
        var data = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, typeSize, cancellationToken)
            .ConfigureAwait(false);
        if (data.Length < typeSize)
        {
            return 8;
        }

        var values = parser.ReadIntArray(data, 1, entry.Type);
        return values.Length > 0 ? values[0] : 8;
    }

    /// <summary>
    /// Multiplies two non-negative longs, returning <see cref="long.MaxValue"/> on overflow so a
    /// crafted grid can never wrap to a small (accepted) value.
    /// </summary>
    private static long SaturatingMultiply(long a, long b)
    {
        if (a <= 0 || b <= 0)
        {
            return 0;
        }

        if (a > long.MaxValue / b)
        {
            return long.MaxValue;
        }

        return a * b;
    }

    private static CogDecodedSizeInspection Reject(string reason) =>
        new(Accepted: false, Width: 0, Height: 0, BandCount: 0, BitsPerSample: 0,
            ProjectedDecodedBytes: 0, RejectionReason: reason);

    private static string DescribeBytes(long bytes)
    {
        if (bytes >= long.MaxValue)
        {
            return "an unbounded number of bytes";
        }

        const long mib = 1024 * 1024;
        if (bytes >= mib)
        {
            return $"{bytes / mib} MiB";
        }

        return $"{bytes} bytes";
    }
}
