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
}
