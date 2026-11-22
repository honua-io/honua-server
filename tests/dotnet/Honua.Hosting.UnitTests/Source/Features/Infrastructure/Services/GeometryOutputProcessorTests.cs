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
    public void ApplyLimits_WithSridlessGeometryAndRequestSrid_SimplifiesInThatSrid()
    {
        var limits = GeometryOutputProcessor.ForGeometrySrid(GeometryOutputProcessor.CreateEffectiveLimits(
            _simplifyLimits, precisionOverride: null, simplifyToleranceOverride: 100d, forceSimplify: true), 4326);

        var result = GeometryOutputProcessor.ApplyLimits(WiggleWithoutSrid(), limits);

        result!.NumPoints.Should().Be(2);
    }

    [UnitTest]
    public void ApplyLimits_WithSridlessGeometryAndNoRequestSrid_SkipsSimplification()
    {
        var limits = GeometryOutputProcessor.CreateEffectiveLimits(
            _simplifyLimits, precisionOverride: null, simplifyToleranceOverride: 100d, forceSimplify: true);

        var result = GeometryOutputProcessor.ApplyLimits(WiggleWithoutSrid(), limits);

        result!.NumPoints.Should().Be(20);
    }

    [UnitTest]
    public void ApplyLimits_WithExplicitGeometrySrid_UsesGeometrySridOverRequestSrid()
    {
        // A 20-vertex line in EPSG:3857 (metres) whose 10 m wiggle simplifies under 100 m in 3857,
        // whereas interpreting those metre coordinates as 4326 degrees would not simplify at 100 m.
        var line = new GeometryFactory(new PrecisionModel(), 3857).CreateLineString(
            Enumerable.Range(0, 20).Select(static i => new Coordinate(i * 100d, (i % 2) * 10d)).ToArray());
        var limits = GeometryOutputProcessor.ForGeometrySrid(GeometryOutputProcessor.CreateEffectiveLimits(
            _simplifyLimits, precisionOverride: null, simplifyToleranceOverride: 100d, forceSimplify: true), 4326);

        var result = GeometryOutputProcessor.ApplyLimits(line, limits);

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

    // Decoded OGC WKB carries no SRID; a 20-vertex line that wiggles by about 10 m.
    private static LineString WiggleWithoutSrid() => new GeometryFactory().CreateLineString(
        Enumerable.Range(0, 20).Select(static i => new Coordinate(i * 0.001d, (i % 2) * 0.0001d)).ToArray());
}
