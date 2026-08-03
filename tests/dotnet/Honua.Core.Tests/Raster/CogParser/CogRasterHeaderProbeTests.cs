// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;

namespace Honua.Core.Tests.Raster.CogParser;

public sealed class CogRasterHeaderProbeTests
{
    [Fact]
    public async Task ReadAsync_BigTiffWithExternalBitsArray_UsesOnlyBoundedConditionalRanges()
    {
        const string etag = "immutable-etag";
        var reader = new ConditionalRangeReader(BuildBigTiff(), etag);

        var result = await CogRasterHeaderProbe.ReadAsync(
            reader,
            "imagery",
            "five-band.tif",
            etag);

        Assert.Equal(4096, result.Dimensions.Width);
        Assert.Equal(2048, result.Dimensions.Height);
        Assert.Equal(5, result.Dimensions.BandCount);
        Assert.Equal(16, result.Dimensions.BitsPerSample);
        Assert.Equal(4, result.RangeCount);
        Assert.Equal(result.RequestedBytes, reader.Requests.Sum(request => request.Length));
        Assert.Equal(0, reader.UnconditionalReadCount);
        Assert.All(reader.Requests, request => Assert.InRange(request.Length, 1, 64 * 1024));
        Assert.All(reader.Requests, request => Assert.Equal(etag, request.ExpectedETag));
    }

    [Fact]
    public async Task ReadAsync_UnsupportedDimensionFieldType_FailsClosed()
    {
        var reader = new ConditionalRangeReader(BuildClassicTiffWithRationalWidth(), "etag-a");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CogRasterHeaderProbe.ReadAsync(reader, "imagery", "malformed.tif", "etag-a"));

        Assert.Contains("unsigned scalar width", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, reader.UnconditionalReadCount);
    }

    [Fact]
    public async Task ReadAsync_ChangedObjectIdentity_IsRejectedByConditionalReader()
    {
        var reader = new ConditionalRangeReader(BuildBigTiff(), "current-etag");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CogRasterHeaderProbe.ReadAsync(
                reader,
                "imagery",
                "changed.tif",
                "stale-etag"));

        Assert.Contains("ETag", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, reader.UnconditionalReadCount);
    }

    private static byte[] BuildBigTiff()
    {
        const int firstIfdOffset = 4096;
        const int bitsOffset = 8192;
        const long entryCount = 4;
        var bytes = new byte[bitsOffset + 10];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 43);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 8);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), firstIfdOffset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(firstIfdOffset), entryCount);

        var entryOffset = firstIfdOffset + 8;
        WriteBigTiffEntry(bytes, entryOffset, tag: 256, type: 16, count: 1, value: 4096);
        WriteBigTiffEntry(bytes, entryOffset + 20, tag: 257, type: 16, count: 1, value: 2048);
        WriteBigTiffEntry(bytes, entryOffset + 40, tag: 258, type: 3, count: 5, value: bitsOffset);
        WriteBigTiffEntry(bytes, entryOffset + 60, tag: 277, type: 3, count: 1, value: 5);
        for (var i = 0; i < 5; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsOffset + (i * 2)), 16);
        }

        return bytes;
    }

    private static byte[] BuildClassicTiffWithRationalWidth()
    {
        const int ifdOffset = 8;
        const ushort entryCount = 4;
        var bytes = new byte[ifdOffset + 2 + (entryCount * 12) + 4];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ifdOffset), entryCount);

        var entryOffset = ifdOffset + 2;
        WriteClassicTiffEntry(bytes, entryOffset, tag: 256, type: 5, count: 1, value: 1);
        WriteClassicTiffEntry(bytes, entryOffset + 12, tag: 257, type: 4, count: 1, value: 64);
        WriteClassicTiffEntry(bytes, entryOffset + 24, tag: 258, type: 3, count: 1, value: 8);
        WriteClassicTiffEntry(bytes, entryOffset + 36, tag: 277, type: 3, count: 1, value: 1);
        return bytes;
    }

    private static void WriteBigTiffEntry(
        byte[] bytes,
        int offset,
        ushort tag,
        ushort type,
        long count,
        long value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), type);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 4), count);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 12), value);
    }

    private static void WriteClassicTiffEntry(
        byte[] bytes,
        int offset,
        ushort tag,
        ushort type,
        uint count,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), value);
    }

    private sealed class ConditionalRangeReader(byte[] payload, string currentETag) : ICloudRangeReader
    {
        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public int UnconditionalReadCount { get; private set; }

        public List<(long Offset, int Length, string ExpectedETag)> Requests { get; } = [];

        public Task<byte[]> ReadRangeAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            UnconditionalReadCount++;
            throw new InvalidOperationException("An unconditional object read was attempted.");
        }

        public Task<byte[]> ReadRangeAsync(
            string bucket,
            string key,
            long offset,
            int length,
            string expectedETag,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(expectedETag, currentETag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The object ETag no longer matches.");
            }

            if (offset < 0 || offset >= payload.LongLength || length <= 0)
            {
                throw new InvalidDataException("Requested range is outside the test object.");
            }

            Requests.Add((offset, length, expectedETag));
            var returnedLength = Math.Min(length, payload.Length - checked((int)offset));
            return Task.FromResult(payload.AsSpan(checked((int)offset), returnedLength).ToArray());
        }

        public Task<Stream> ReadRangeStreamAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> GetObjectSizeAsync(
            string bucket,
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromResult((long)payload.Length);
    }
}
