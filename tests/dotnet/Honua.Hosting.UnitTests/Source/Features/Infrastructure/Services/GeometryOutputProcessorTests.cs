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
    public void ApplyLimits_WithHighLatitudeWgs84Geometry_ScalesToleranceWithCosineLatitude()
    {
        var limits = new GeometryLimits
        {
            MaxVerticesPerGeometry = 0,
            MaxCoordinatePrecision = -1,
            SimplifyTolerance = 100d
        };

        // At equator (lat=0), 100m is ~0.000898 deg (< 0.0012 deg offset), so all 3 vertices are kept.
        var equatorLine = new GeometryFactory(new PrecisionModel(), 4326).CreateLineString(
        [
            new Coordinate(0d, 0d),
            new Coordinate(0.1d, 0.0012d),
            new Coordinate(0.2d, 0d)
        ]);
        // At lat=75 deg, cos(75 deg) ~ 0.2588, so geometric-mean meters/deg is ~56,593 m/deg and
        // 100m is ~0.001767 deg (> 0.0012 deg offset), simplifying the middle vertex.
        var highLatLine = new GeometryFactory(new PrecisionModel(), 4326).CreateLineString(
        [
            new Coordinate(0d, 75d),
            new Coordinate(0.1d, 75.0012d),
            new Coordinate(0.2d, 75d)
        ]);

        var equatorResult = GeometryOutputProcessor.ApplyLimits(equatorLine, limits);
        var highLatResult = GeometryOutputProcessor.ApplyLimits(highLatLine, limits);

        equatorResult!.NumPoints.Should().Be(3);
        highLatResult!.NumPoints.Should().Be(2);
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

    [UnitTest]
    public void GeoParquetFeatureWriter_ProcessGeometry_With3DPoint_EmitsStrictIsoSqlMmWkb()
    {
        var point3D = CreatePrecisePointZ();
        point3D.SRID = 4326;
        var inputEwkb = new NetTopologySuite.IO.WKBWriter(
            NetTopologySuite.IO.ByteOrder.LittleEndian,
            handleSRID: true,
            emitZ: true,
            emitM: false)
        { Strict = false }.Write(point3D);

        var outputWkb = GeoParquetFeatureWriter.ProcessGeometry(
            inputEwkb,
            outputSrid: 4326,
            geometryLimits: new GeometryLimits(),
            returnZ: true,
            returnM: false);

        outputWkb.Should().NotBeNull();
        // ISO SQL/MM Point Z type code is 1001 (not PostGIS EWKB 0x80000001).
        var typeCode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(outputWkb.AsSpan(1, 4));
        typeCode.Should().Be(1001u);
    }

    private static Point CreatePrecisePointZ()
        => new GeometryFactory().CreatePoint(new CoordinateZ(1.123456, 2.987654, 7.5));
}
