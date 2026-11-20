// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;

namespace Honua.Server.Tests.Features.Infrastructure.Services;

public sealed class GeometryOutputProcessorTests
{
    private static readonly GeometryLimits _simplifyLimits = new()
    {
        MaxVerticesPerGeometry = 0,
        MaxCoordinatePrecision = -1,
        SimplifyTolerance = 10d
    };

    private static readonly GeometryLimits _roundingLimits = new()
    {
        MaxVerticesPerGeometry = 0,
        MaxCoordinatePrecision = 2,
        SimplifyTolerance = null
    };

    [UnitTest]
    public void ApplyLimits_WithWgs84Geometry_ConvertsMetersToDegreesBeforeSimplifying()
    {
        var geometry = new GeometryFactory(new PrecisionModel(), 4326).CreateLineString(
        [
            new Coordinate(0d, 0d),
            new Coordinate(0.1d, 0.0005d),
            new Coordinate(0.2d, 0d)
        ]);

        var result = GeometryOutputProcessor.ApplyLimits(geometry, _simplifyLimits);

        result.Should().NotBeNull();
        result!.NumPoints.Should().Be(3);
    }

    [UnitTest]
    public void ApplyLimits_WithUnsupportedProjectedGeometry_SkipsSimplification()
    {
        var geometry = new GeometryFactory(new PrecisionModel(), 2230).CreateLineString(
        [
            new Coordinate(0d, 0d),
            new Coordinate(10d, 5d),
            new Coordinate(20d, 0d)
        ]);

        var result = GeometryOutputProcessor.ApplyLimits(geometry, _simplifyLimits);

        result.Should().NotBeNull();
        result!.NumPoints.Should().Be(3);
    }

    [UnitTest]
    public void ApplyLimits_WithWebMercatorGeometry_UsesMeterTolerance()
    {
        var geometry = new GeometryFactory(new PrecisionModel(), 3857).CreateLineString(
        [
            new Coordinate(0d, 0d),
            new Coordinate(10d, 5d),
            new Coordinate(20d, 0d)
        ]);

        var result = GeometryOutputProcessor.ApplyLimits(geometry, _simplifyLimits);

        result.Should().NotBeNull();
        result!.NumPoints.Should().Be(2);
    }

    [UnitTest]
    public void ApplyLimits_WithSharedGeometry_RoundsACopy()
    {
        var shared = CreatePrecisePointZ();

        var result = (Point)GeometryOutputProcessor.ApplyLimits(shared, _roundingLimits)!;

        result.Should().NotBeSameAs(shared);
        result.X.Should().Be(1.12);
        shared.X.Should().Be(1.123456);
    }

    [UnitTest]
    public void ApplyLimitsInPlace_WithOwnedGeometry_RoundsInPlace()
    {
        var owned = CreatePrecisePointZ();

        var result = GeometryOutputProcessor.ApplyLimitsInPlace(owned, _roundingLimits);

        result.Should().BeSameAs(owned);
        owned.X.Should().Be(1.12);
        owned.Y.Should().Be(2.99);
    }

    [UnitTest]
    public void ApplyDimensionFilter_WithSharedGeometry_DropsZOnACopy()
    {
        var shared = CreatePrecisePointZ();

        var copied = GeometryOutputProcessor.ApplyDimensionFilter(shared, includeZ: false, includeM: false);

        copied.Should().NotBeSameAs(shared);
        double.IsNaN(copied.Coordinate.Z).Should().BeTrue();
        shared.Z.Should().Be(7.5);
    }

    [UnitTest]
    public void ApplyDimensionFilterInPlace_WithOwnedGeometry_DropsZInPlace()
    {
        var owned = CreatePrecisePointZ();

        var filtered = GeometryOutputProcessor.ApplyDimensionFilterInPlace(owned, includeZ: false, includeM: false);

        filtered.Should().BeSameAs(owned);
        double.IsNaN(owned.Coordinate.Z).Should().BeTrue();
    }

    private static Point CreatePrecisePointZ()
        => new GeometryFactory().CreatePoint(new CoordinateZ(1.123456, 2.987654, 7.5));
}
