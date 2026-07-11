// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrDimensionSliceResolverTests
{
    private static ZarrArrayMetadata Array3D(string axisName)
        => new(
            Name: "temperature",
            ZarrFormat: ZarrFormatVersion.V2,
            RelativePath: "temperature",
            Shape: [5, 4, 4],
            Chunks: [5, 4, 4],
            DataType: "<f4",
            Order: "C",
            Compressor: null,
            FillValue: null,
            DimensionNames: [axisName, "y", "x"]);

    private static ZarrStoreMetadata WithVerticalAxis()
        => new(
            ZarrFormat: ZarrFormatVersion.V2,
            Srid: 4326,
            Extent: new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            Arrays: [Array3D("elevation")],
            PrimaryVariable: "temperature",
            SpatialXDimension: "x",
            SpatialYDimension: "y",
            TemporalDimension: null,
            Temporal: null,
            Axes: [new ZarrAxis("elevation", 5, Coordinates: null, 0, 1000)]);

    [Fact]
    public void ResolvesVerticalCoordinateToIndex()
    {
        var ok = ZarrDimensionSliceResolver.TryResolveSliceIndex(
            WithVerticalAxis(), "elevation", 500, out var index, out var error);

        ok.Should().BeTrue(error);
        index.Should().Be(2);
    }

    [Fact]
    public void UnknownAxis_IsRejectedWithAvailableAxes()
    {
        var ok = ZarrDimensionSliceResolver.TryResolveSliceIndex(
            WithVerticalAxis(), "salinity", 5, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("elevation");
    }

    [Fact]
    public void OutOfRangeCoordinate_IsRejected()
    {
        var ok = ZarrDimensionSliceResolver.TryResolveSliceIndex(
            WithVerticalAxis(), "elevation", 50000, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void ResolvesTimeAxisFromEpochMillis()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var metadata = new ZarrStoreMetadata(
            ZarrFormatVersion.V2,
            4326,
            new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            [Array3D("time")],
            "temperature",
            "x",
            "y",
            "time",
            new TemporalExtent(start, end, 5));

        var midMillis = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var ok = ZarrDimensionSliceResolver.TryResolveSliceIndex(metadata, "time", midMillis, out var index, out var error);

        ok.Should().BeTrue(error);
        index.Should().Be(2);
    }

    [Fact]
    public void TimeCoordinateOutsideDateTimeRange_IsRejectedWithoutThrowing()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var metadata = new ZarrStoreMetadata(
            ZarrFormatVersion.V2,
            4326,
            new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
            [Array3D("time")],
            "temperature",
            "x",
            "y",
            "time",
            new TemporalExtent(start, start.AddDays(1), 2));

        var ok = ZarrDimensionSliceResolver.TryResolveSliceIndex(
            metadata,
            "time",
            double.MaxValue,
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("supported instant range");
    }

    [Fact]
    public void TryFindArrayDimension_LocatesNamedDimension()
    {
        ZarrDimensionSliceResolver.TryFindArrayDimension(Array3D("elevation"), "elevation", out var dim).Should().BeTrue();
        dim.Should().Be(0);

        ZarrDimensionSliceResolver.TryFindArrayDimension(Array3D("elevation"), "missing", out var missing).Should().BeFalse();
        missing.Should().Be(-1);
    }
}
