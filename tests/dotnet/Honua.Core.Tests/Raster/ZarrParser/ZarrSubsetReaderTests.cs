// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrSubsetReaderTests
{
    [Fact]
    public async Task ReadSubsetAsync_UncompressedSpansMultipleChunks_ReturnsContiguousRowMajor()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "datasets/temp",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r * 100 + c);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/temp");

        var request = new ZarrSubsetRequest
        {
            Variable = "temp",
            Start = new[] { 2, 2 },
            Stop = new[] { 6, 6 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/temp", metadata, request);

        subset.Shape.Should().Equal(4, 4);
        subset.DataType.Should().Be("<f4");
        subset.Data.Length.Should().Be(4 * 4 * sizeof(float));
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                var globalRow = r + 2;
                var globalCol = c + 2;
                var expected = (float)(globalRow * 100 + globalCol);
                var actual = BitConverter.ToSingle(subset.Data, (r * 4 + c) * sizeof(float));
                actual.Should().Be(expected, $"cell ({r},{c}) should reflect global ({globalRow},{globalCol})");
            }
        }
    }

    [Fact]
    public async Task ReadSubsetAsync_ZlibCompressed_DecodesCorrectly()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "datasets/grid",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r + c * 0.5f,
            srid: 4326,
            xMin: 0,
            yMin: 0,
            xMax: 8,
            yMax: 8);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/grid");

        var request = new ZarrSubsetRequest
        {
            Variable = "temperature",
            Start = new[] { 0, 0 },
            Stop = new[] { 5, 5 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/grid", metadata, request);

        subset.Shape.Should().Equal(5, 5);
        for (var r = 0; r < 5; r++)
        {
            for (var c = 0; c < 5; c++)
            {
                var expected = r + c * 0.5f;
                var actual = BitConverter.ToSingle(subset.Data, (r * 5 + c) * sizeof(float));
                actual.Should().Be(expected);
            }
        }
    }

    [Fact]
    public async Task ReadSubsetAsync_V3UncompressedCPrefixedChunks_ReturnsContiguousRowMajor()
    {
        var objects = ZarrFixtureBuilder.BuildV3SingleArray(
            root: "datasets/v3temp",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r * 100 + c,
            gzip: false);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/v3temp");

        var request = new ZarrSubsetRequest
        {
            Variable = "v3temp",
            Start = new[] { 2, 2 },
            Stop = new[] { 6, 6 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/v3temp", metadata, request);

        subset.Shape.Should().Equal(4, 4);
        subset.DataType.Should().Be("<f4");
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                var globalRow = r + 2;
                var globalCol = c + 2;
                var expected = (float)(globalRow * 100 + globalCol);
                var actual = BitConverter.ToSingle(subset.Data, (r * 4 + c) * sizeof(float));
                actual.Should().Be(expected, $"v3 cell ({r},{c}) should reflect global ({globalRow},{globalCol})");
            }
        }
    }

    [Fact]
    public async Task ReadSubsetAsync_V3GzipCoded_DecodesCorrectly()
    {
        var objects = ZarrFixtureBuilder.BuildV3SingleArray(
            root: "datasets/v3gzip",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r + c * 0.5f,
            gzip: true);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/v3gzip");
        metadata.Arrays[0].Compressor.Should().Be("gzip");

        var request = new ZarrSubsetRequest
        {
            Variable = "v3gzip",
            Start = new[] { 0, 0 },
            Stop = new[] { 5, 5 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/v3gzip", metadata, request);

        subset.Shape.Should().Equal(5, 5);
        for (var r = 0; r < 5; r++)
        {
            for (var c = 0; c < 5; c++)
            {
                var expected = r + c * 0.5f;
                var actual = BitConverter.ToSingle(subset.Data, (r * 5 + c) * sizeof(float));
                actual.Should().Be(expected);
            }
        }
    }

    [Fact]
    public async Task ReadSubsetAsync_OutOfBounds_Throws()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "datasets/bounds",
            rows: 4,
            cols: 4,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => 0f);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/bounds");

        var request = new ZarrSubsetRequest
        {
            Variable = "bounds",
            Start = new[] { 0, 0 },
            Stop = new[] { 5, 5 }
        };

        var act = () => new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/bounds", metadata, request);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// Regression test for BH-017: a Zarr store that declares chunk dimensions whose
    /// product × element-size exceeds MaxBytesPerRequest must be rejected before any
    /// per-chunk heap allocation is attempted.  With MaxDegreeOfParallelism=8, reading
    /// 8 such chunks would otherwise allocate ~2 GB of heap before the caller receives
    /// any error.
    /// </summary>
    [Fact]
    public async Task ReadSubsetAsync_OversizedChunkDimensions_ThrowsBeforeAllocatingChunkBuffer()
    {
        // shape=[4,4] but chunks=[8200,8200] with dtype=<f4:
        //   chunkBytes = 8200 * 8200 * 4 = 268,960,000 > MaxBytesPerRequest (256 MiB)
        // The array total (4*4*4=64 bytes) is well under the total-bytes cap so
        // only the per-chunk guard added by BH-017 stops the allocation.
        var objects = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["datasets/huge/.zarray"] = System.Text.Encoding.UTF8.GetBytes(
                """{"chunks":[8200,8200],"compressor":null,"dtype":"<f4","fill_value":0,"filters":null,"order":"C","shape":[4,4],"zarr_format":2}""")
        };
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/huge");

        var request = new ZarrSubsetRequest
        {
            Variable = "huge",
            Start = new[] { 0, 0 },
            Stop = new[] { 4, 4 }
        };

        var act = () => new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/huge", metadata, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*chunk size*exceeds*");
    }

    /// <summary>
    /// Regression test for BH-017: ResolveElementSize must reject unrealistically
    /// large element-size suffixes (> 16) before any array-wide computation occurs.
    /// </summary>
    [Fact]
    public void ResolveElementSize_ElementSizeExceedsMaxSupported_Throws()
    {
        var act = () => ZarrSubsetReader.ResolveElementSize("<f32");

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*element size*exceeds*");
    }
}
