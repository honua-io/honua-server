// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrTileSlicePlannerTests
{
    [Fact]
    public async Task TryPlan_TileWithinExtent_ResolvesSpatialWindow()
    {
        var metadata = await BuildMetadataAsync(
            ZarrFixtureBuilder.BuildGroupedZlib(
                root: "tiles/grid",
                rows: 8,
                cols: 8,
                chunkRows: 4,
                chunkCols: 4,
                sample: (r, c) => r + c,
                srid: 4326,
                xMin: 0,
                yMin: 0,
                xMax: 8,
                yMax: 8),
            "tiles/grid");

        // Bottom-left quadrant of the extent (x in [0,4], y in [0,4]).
        var bounds = new ZarrTileBounds(0, 0, 4, 4);
        var ok = ZarrTileSlicePlanner.TryPlan(
            metadata, variable: null, bounds, datetime: null, verticalIndex: null,
            maxOutputBytes: 1024 * 1024, out var slice, out var error);

        ok.Should().BeTrue(error);
        slice.Should().NotBeNull();
        var request = slice!.Plan.Request;
        // Y dimension is index 0 (row), X is index 1 (col).
        slice.YDimensionIndex.Should().Be(0);
        slice.XDimensionIndex.Should().Be(1);
        // X window [0,4); Y window covers the southern half -> rows [4,8).
        request.Start[1].Should().Be(0);
        request.Stop[1].Should().Be(4);
        request.Start[0].Should().Be(4);
        request.Stop[0].Should().Be(8);
    }

    [Fact]
    public async Task TryPlan_TileOutsideExtent_ReportsNoIntersection()
    {
        var metadata = await BuildMetadataAsync(
            ZarrFixtureBuilder.BuildGroupedZlib(
                root: "tiles/miss",
                rows: 8,
                cols: 8,
                chunkRows: 4,
                chunkCols: 4,
                sample: (r, c) => 1f,
                srid: 4326,
                xMin: 0,
                yMin: 0,
                xMax: 8,
                yMax: 8),
            "tiles/miss");

        var bounds = new ZarrTileBounds(100, 100, 110, 110);
        var ok = ZarrTileSlicePlanner.TryPlan(
            metadata, variable: null, bounds, datetime: null, verticalIndex: null,
            maxOutputBytes: 1024 * 1024, out var slice, out var error);

        ok.Should().BeFalse();
        slice.Should().BeNull();
        error.Should().Contain("does not intersect");
    }

    [Fact]
    public async Task TryPlan_NotGeoreferenced_Rejected()
    {
        var metadata = await BuildMetadataAsync(
            ZarrFixtureBuilder.BuildSingleVariableUncompressed(
                root: "tiles/plain",
                rows: 8,
                cols: 8,
                chunkRows: 4,
                chunkCols: 4,
                sample: (r, c) => 1f),
            "tiles/plain");

        var bounds = new ZarrTileBounds(0, 0, 4, 4);
        var ok = ZarrTileSlicePlanner.TryPlan(
            metadata, variable: null, bounds, datetime: null, verticalIndex: null,
            maxOutputBytes: 1024 * 1024, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("georeferenced");
    }

    [Fact]
    public async Task TryPlan_TimeAxis_ResolvesSingleTimeIndex()
    {
        var metadata = await BuildMetadataAsync(
            ZarrFixtureBuilder.BuildGroupedWithTime(
                root: "tiles/ts",
                timeSteps: 5,
                rows: 8,
                cols: 8,
                includeTemporalAttrs: true),
            "tiles/ts");

        var bounds = new ZarrTileBounds(-180, -90, 0, 90);
        var ok = ZarrTileSlicePlanner.TryPlan(
            metadata,
            variable: null,
            bounds,
            datetime: (new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)),
            verticalIndex: null,
            maxOutputBytes: 4 * 1024 * 1024,
            out var slice,
            out var error);

        ok.Should().BeTrue(error);
        var request = slice!.Plan.Request;
        // time axis is dimension 0; 2026-01-03 is the 3rd sample (index 2).
        request.Start[0].Should().Be(2);
        request.Stop[0].Should().Be(3);
    }

    private static async Task<ZarrStoreMetadata> BuildMetadataAsync(System.Collections.Generic.Dictionary<string, byte[]> objects, string root)
    {
        var reader = new InMemoryZarrRangeReader(objects);
        return await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", root);
    }
}
