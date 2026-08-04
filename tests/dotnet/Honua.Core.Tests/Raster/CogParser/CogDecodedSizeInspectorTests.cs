// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;
using Xunit;

namespace Honua.Core.Tests.Raster.CogParser;

/// <summary>
/// Unit tests for <see cref="CogDecodedSizeInspector"/> — the submit-time decompression-bomb
/// guard (#3090). Proves a tiny compressed TIFF that declares an enormous decoded grid is
/// rejected within the header/IFD read caps, that legitimate rasters pass, and that the probe
/// never reads beyond the header/first-IFD region (no tile-offset arrays or pixel content).
/// </summary>
public class CogDecodedSizeInspectorTests
{
    private const long OneMiB = 1024 * 1024;

    [Fact]
    public async Task InspectAsync_DeclaredHugeDecodedGrid_RejectedWithinCaps()
    {
        // A ~200-byte TIFF that declares a 50000 x 50000 single-band uint8 grid: the compressed
        // artifact is tiny but the decoded raster is ~2.3 GiB — a decompression bomb.
        var tiff = BuildClassicTiff(width: 50_000, height: 50_000, samplesPerPixel: 1, bitsPerSample: 8);
        var reader = new TrackingRangeReader(tiff);
        var inspector = new CogDecodedSizeInspector();

        var result = await inspector.InspectAsync(reader, "bucket", "bomb.tif", maxDecodedBytes: 64 * OneMiB);

        result.Accepted.Should().BeFalse();
        result.RejectionReason.Should().NotBeNullOrWhiteSpace();
        result.Width.Should().Be(50_000);
        result.Height.Should().Be(50_000);
        result.ProjectedDecodedBytes.Should().Be(50_000L * 50_000L); // width*height*1band*1byte

        // The probe must never touch pixel/tile-offset content: every read stayed within the caps
        // and inside the small header+IFD prefix of the file.
        reader.MaxReadLength.Should().BeLessThanOrEqualTo(64 * 1024);
        reader.MaxOffsetReached.Should().BeLessThanOrEqualTo(tiff.Length);
        reader.TotalBytesServed.Should().BeLessThan(4 * 1024);
    }

    [Fact]
    public async Task InspectAsync_NormalRaster_Accepted()
    {
        // A legitimate 1024 x 1024 single-band uint8 grid decodes to 1 MiB — well within the cap.
        var tiff = BuildClassicTiff(width: 1024, height: 1024, samplesPerPixel: 1, bitsPerSample: 8);
        var reader = new TrackingRangeReader(tiff);
        var inspector = new CogDecodedSizeInspector();

        var result = await inspector.InspectAsync(reader, "bucket", "ok.tif", maxDecodedBytes: 64 * OneMiB);

        result.Accepted.Should().BeTrue();
        result.RejectionReason.Should().BeNull();
        result.Width.Should().Be(1024);
        result.Height.Should().Be(1024);
        result.BandCount.Should().Be(1);
        result.BitsPerSample.Should().Be(8);
        result.ProjectedDecodedBytes.Should().Be(1024L * 1024L);
    }

    [Fact]
    public async Task InspectAsync_JustOverCeiling_Rejected()
    {
        // 2049 x 1024 x 1 x 1 byte = 2,098,176 bytes, one row over a 2 MiB ceiling.
        var tiff = BuildClassicTiff(width: 2049, height: 1024, samplesPerPixel: 1, bitsPerSample: 8);
        var inspector = new CogDecodedSizeInspector();

        var result = await inspector.InspectAsync(
            new TrackingRangeReader(tiff), "bucket", "edge.tif", maxDecodedBytes: 2 * OneMiB);

        result.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task InspectAsync_MultiBandFloat_ProjectsBandsAndSampleBytes()
    {
        // 4000 x 4000 x 3 bands x float32 (4 bytes/sample) = 192,000,000 bytes.
        // BitsPerSample for 3 bands is stored EXTERNALLY (3 shorts > 4-byte inline field); the
        // probe must read only the first element, never the whole array.
        var tiff = BuildClassicTiff(width: 4000, height: 4000, samplesPerPixel: 3, bitsPerSample: 32);
        var inspector = new CogDecodedSizeInspector();

        var accepted = await inspector.InspectAsync(
            new TrackingRangeReader(tiff), "bucket", "rgb.tif", maxDecodedBytes: 256 * OneMiB);
        accepted.Accepted.Should().BeTrue();
        accepted.BandCount.Should().Be(3);
        accepted.BitsPerSample.Should().Be(32);
        accepted.ProjectedDecodedBytes.Should().Be(4000L * 4000L * 3L * 4L);

        var rejected = await inspector.InspectAsync(
            new TrackingRangeReader(tiff), "bucket", "rgb.tif", maxDecodedBytes: 128 * OneMiB);
        rejected.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task InspectAsync_NotATiff_FailsClosed()
    {
        var garbage = new byte[512];
        Array.Fill(garbage, (byte)0xAB);
        var inspector = new CogDecodedSizeInspector();

        var result = await inspector.InspectAsync(
            new TrackingRangeReader(garbage), "bucket", "garbage.bin", maxDecodedBytes: long.MaxValue);

        result.Accepted.Should().BeFalse();
        result.RejectionReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InspectAsync_TruncatedHeader_FailsClosed()
    {
        var inspector = new CogDecodedSizeInspector();

        var result = await inspector.InspectAsync(
            new TrackingRangeReader([0x49, 0x49]), "bucket", "tiny.tif", maxDecodedBytes: long.MaxValue);

        result.Accepted.Should().BeFalse();
    }

    /// <summary>
    /// Builds a minimal classic little-endian TIFF carrying only the tags the probe reads plus
    /// dummy tile tags. No pixel data is written, so any read past the header/IFD prefix is a bug.
    /// </summary>
    private static byte[] BuildClassicTiff(int width, int height, int samplesPerPixel, int bitsPerSample)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0x4949); // "II" little-endian
        writer.Write((ushort)42);     // classic TIFF magic
        writer.Write((uint)8);        // first IFD at offset 8

        const int entryCount = 9;
        writer.Write((ushort)entryCount);

        // IFD ends at 8 + 2 + 9*12 + 4 = 122; external BitsPerSample (when >2 bands) follows.
        const uint externalBitsOffset = 122;
        var bitsInline = samplesPerPixel <= 2;

        WriteEntry(writer, 256, 4, 1, (uint)width);                 // ImageWidth (LONG)
        WriteEntry(writer, 257, 4, 1, (uint)height);                // ImageLength (LONG)
        if (bitsInline)
        {
            var packed = samplesPerPixel == 2
                ? (uint)((ushort)bitsPerSample | ((ushort)bitsPerSample << 16))
                : (uint)(ushort)bitsPerSample;
            WriteEntry(writer, 258, 3, (uint)samplesPerPixel, packed); // BitsPerSample (SHORT, inline)
        }
        else
        {
            WriteEntry(writer, 258, 3, (uint)samplesPerPixel, externalBitsOffset); // BitsPerSample (external)
        }

        WriteEntry(writer, 259, 3, 1, 1);                          // Compression = NONE
        WriteEntry(writer, 277, 3, 1, (uint)samplesPerPixel);      // SamplesPerPixel
        WriteEntry(writer, 322, 4, 1, 256);                        // TileWidth
        WriteEntry(writer, 323, 4, 1, 256);                        // TileLength
        WriteEntry(writer, 324, 4, 1, 5000);                       // TileOffsets (dummy — never read)
        WriteEntry(writer, 325, 4, 1, 1000);                       // TileByteCounts (dummy — never read)

        writer.Write((uint)0); // no next IFD

        if (!bitsInline)
        {
            for (var i = 0; i < samplesPerPixel; i++)
            {
                writer.Write((ushort)bitsPerSample);
            }
        }

        return ms.ToArray();
    }

    private static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint valueOrOffset)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(valueOrOffset);
    }

    /// <summary>
    /// In-memory range reader that records the largest read length, the furthest byte offset
    /// reached, and the total bytes served, so tests can prove the probe stays within its caps
    /// and never reaches the (unwritten) pixel/tile region.
    /// </summary>
    private sealed class TrackingRangeReader : ICloudRangeReader
    {
        private readonly byte[] _data;

        public TrackingRangeReader(byte[] data) => _data = data;

        public int MaxReadLength { get; private set; }

        public long MaxOffsetReached { get; private set; }

        public long TotalBytesServed { get; private set; }

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            MaxReadLength = Math.Max(MaxReadLength, length);
            var available = Math.Max(0, _data.Length - (int)offset);
            var bytesToRead = Math.Min(length, available);
            MaxOffsetReached = Math.Max(MaxOffsetReached, offset + bytesToRead);
            TotalBytesServed += bytesToRead;
            var result = new byte[bytesToRead];
            if (bytesToRead > 0)
            {
                Buffer.BlockCopy(_data, (int)offset, result, 0, bytesToRead);
            }

            return Task.FromResult(result);
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult((long)_data.Length);
    }
}
