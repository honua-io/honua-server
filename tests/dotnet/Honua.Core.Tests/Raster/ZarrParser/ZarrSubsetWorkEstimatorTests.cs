// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Core.Tests.Raster.ZarrParser;

public sealed class ZarrSubsetWorkEstimatorTests
{
    [Fact]
    public void Estimate_CountsIntersectingChunksAndIndependentWebBuffers()
    {
        var array = new ZarrArrayMetadata(
            "temperature",
            ZarrFormatVersion.V2,
            string.Empty,
            [100, 100],
            [16, 16],
            "<f4",
            "C",
            null,
            null,
            ["y", "x"]);
        var request = new ZarrSubsetRequest
        {
            Variable = array.Name,
            Start = [8, 8],
            Stop = [40, 40],
        };

        var work = ZarrSubsetWorkEstimator.Estimate(array, request, outputWidth: 256, outputHeight: 256);

        work.WebOutputCells.Should().Be(65_536);
        work.WebOutputBytes.Should().Be((32L * 32L * sizeof(float)) + (256L * 256L * 4L));
        work.ObjectRequests.Should().Be(9);
        work.ObjectRangeBytes.Should().Be(9L * 16L * 16L * sizeof(float));
        work.PostGisWorkUnits.Should().Be(0);
    }

    [Fact]
    public void Estimate_InvalidRank_FailsBeforeAnyReadCanBeScheduled()
    {
        var array = new ZarrArrayMetadata(
            "temperature",
            ZarrFormatVersion.V2,
            string.Empty,
            [100, 100],
            [16, 16],
            "<f4",
            "C",
            null,
            null,
            ["y", "x"]);
        var request = new ZarrSubsetRequest
        {
            Variable = array.Name,
            Start = [0],
            Stop = [1],
        };

        var act = () => ZarrSubsetWorkEstimator.Estimate(array, request, 256, 256);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Estimate_UncompressedChunk_CoversActualTrackingReaderCalls()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "estimate/uncompressed",
            rows: 4,
            cols: 4,
            chunkRows: 4,
            chunkCols: 4,
            sample: (row, column) => (row * 4) + column);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(
            new InMemoryZarrRangeReader(objects),
            "bucket",
            "estimate/uncompressed");
        var array = metadata.Arrays.Single();
        var request = FullChunkRequest(array);
        var trackingReader = new TrackingZarrRangeReader(new InMemoryZarrRangeReader(objects));

        var work = ZarrSubsetWorkEstimator.Estimate(array, request, outputWidth: 4, outputHeight: 4);
        await new ZarrSubsetReader().ReadSubsetAsync(
            trackingReader,
            "bucket",
            "estimate/uncompressed",
            metadata,
            request);

        work.ObjectRequests.Should().Be(1);
        work.ObjectRangeBytes.Should().Be(4L * 4L * sizeof(float));
        AssertEstimateCoversIssuedObjectWork(work, trackingReader);
    }

    [Fact]
    public async Task Estimate_CompressedChunk_CoversDecodedPlusOverheadReadCeiling()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "estimate/compressed",
            rows: 4,
            cols: 4,
            chunkRows: 4,
            chunkCols: 4,
            sample: (row, column) => (row * 4) + column,
            srid: 4326,
            xMin: -180,
            yMin: -90,
            xMax: 180,
            yMax: 90);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(
            new InMemoryZarrRangeReader(objects),
            "bucket",
            "estimate/compressed");
        var array = metadata.Arrays.Single(item => item.Name == "temperature");
        var request = FullChunkRequest(array);
        var trackingReader = new TrackingZarrRangeReader(new InMemoryZarrRangeReader(objects));

        var work = ZarrSubsetWorkEstimator.Estimate(array, request, outputWidth: 4, outputHeight: 4);
        await new ZarrSubsetReader().ReadSubsetAsync(
            trackingReader,
            "bucket",
            "estimate/compressed",
            metadata,
            request);

        work.ObjectRequests.Should().Be(2, "a compressed chunk can require a conditional object-size probe");
        work.ObjectRangeBytes.Should().Be(
            (4L * 4L * sizeof(float)) + ZarrReadSupportMatrix.MaxEncodedChunkOverheadBytes);
        trackingReader.ObjectSizeRequestCount.Should().Be(0, "the normal compressed object is below the ceiling");
        AssertEstimateCoversIssuedObjectWork(work, trackingReader);
    }

    [Fact]
    public async Task Estimate_CompressedCeilingBoundary_CoversConditionalObjectSizeProbe()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "estimate/boundary",
            rows: 2,
            cols: 2,
            chunkRows: 2,
            chunkCols: 2,
            sample: (row, column) => (row * 2) + column,
            srid: 4326,
            xMin: -180,
            yMin: -90,
            xMax: 180,
            yMax: 90);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(
            new InMemoryZarrRangeReader(objects),
            "bucket",
            "estimate/boundary");
        var array = metadata.Arrays.Single(item => item.Name == "temperature");
        var request = FullChunkRequest(array);
        var trackingReader = new TrackingZarrRangeReader(
            new InMemoryZarrRangeReader(objects),
            padRangeResponseToRequestedCeiling: true);

        var work = ZarrSubsetWorkEstimator.Estimate(array, request, outputWidth: 2, outputHeight: 2);
        await new ZarrSubsetReader().ReadSubsetAsync(
            trackingReader,
            "bucket",
            "estimate/boundary",
            metadata,
            request);

        trackingReader.RangeRequestLengths.Should().ContainSingle();
        trackingReader.ObjectSizeRequestCount.Should().Be(1);
        trackingReader.TotalObjectRequests.Should().Be(2);
        work.ObjectRequests.Should().Be(2);
        AssertEstimateCoversIssuedObjectWork(work, trackingReader);
    }

    private static ZarrSubsetRequest FullChunkRequest(ZarrArrayMetadata array) => new()
    {
        Variable = array.Name,
        Start = new int[array.Shape.Length],
        Stop = (int[])array.Chunks.Clone(),
    };

    private static void AssertEstimateCoversIssuedObjectWork(
        Honua.Core.Features.Raster.Capacity.RasterCapacityWork work,
        TrackingZarrRangeReader trackingReader)
    {
        work.ObjectRequests.Should().BeGreaterOrEqualTo(trackingReader.TotalObjectRequests);
        work.ObjectRangeBytes.Should().BeGreaterOrEqualTo(
            trackingReader.RangeRequestLengths.Sum(length => (long)length));
    }

    private sealed class TrackingZarrRangeReader(
        ICloudRangeReader inner,
        bool padRangeResponseToRequestedCeiling = false) : ICloudRangeReader
    {
        public List<int> RangeRequestLengths { get; } = [];

        public int ObjectSizeRequestCount { get; private set; }

        public int TotalObjectRequests => RangeRequestLengths.Count + ObjectSizeRequestCount;

        public CloudStorageProvider Provider => inner.Provider;

        public async Task<byte[]> ReadRangeAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            RangeRequestLengths.Add(length);
            var bytes = await inner.ReadRangeAsync(bucket, key, offset, length, cancellationToken);
            if (!padRangeResponseToRequestedCeiling || bytes.Length == length)
            {
                return bytes;
            }

            Array.Resize(ref bytes, length);
            return bytes;
        }

        public Task<Stream> ReadRangeStreamAsync(
            string bucket,
            string key,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
            => inner.ReadRangeStreamAsync(bucket, key, offset, length, cancellationToken);

        public Task<long> GetObjectSizeAsync(
            string bucket,
            string key,
            CancellationToken cancellationToken = default)
        {
            ObjectSizeRequestCount++;
            return padRangeResponseToRequestedCeiling
                ? Task.FromResult((long)RangeRequestLengths[^1])
                : inner.GetObjectSizeAsync(bucket, key, cancellationToken);
        }
    }
}
