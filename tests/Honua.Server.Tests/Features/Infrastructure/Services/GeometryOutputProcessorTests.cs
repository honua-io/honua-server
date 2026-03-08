// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Services;
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
}
