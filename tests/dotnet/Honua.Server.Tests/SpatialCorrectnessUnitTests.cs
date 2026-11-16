// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Helpers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests;

// Geometry parsing and encoding require neither an HTTP host nor a database.
[Protocol(TestProtocols.FeatureServer)]
public sealed class SpatialCorrectnessUnitTests
{
    public enum BoundingBoxValidity
    {
        Invalid,
        Valid
    }

    #region GEOGRAPHIC RANGE VALIDATION (Primary Target)

    [UnitTheory]
    [InlineData("179,-10,-179,10", BoundingBoxValidity.Valid)]    // Crosses antimeridian (valid Pacific)
    [InlineData("170,-5,-170,5", BoundingBoxValidity.Valid)]      // Crosses antimeridian (valid Pacific)
    [InlineData("-200,-10,200,10", BoundingBoxValidity.Invalid)]   // Outside longitude range (invalid)
    [InlineData("179.999,-10,-179.999,10", BoundingBoxValidity.Valid)] // Near antimeridian (valid)
    [InlineData("0,-10,360,10", BoundingBoxValidity.Invalid)]      // Longitude > 180 (invalid)
    [Trait("Category", "Unit")]
    [Operation(Operations.Query)]
    public void ValidateGeographicRange_AntimeridianCrossingBoxes_CorrectValidation(
        string bbox, BoundingBoxValidity expectedValidity)
    {
        var expectedValid = expectedValidity == BoundingBoxValidity.Valid;
        var result = Honua.Infrastructure.Services.RasterParsingHelpers
            .TryParseBoundingBox(
                bbox,
                Honua.Core.Features.Shared.Models.AxisOrder.EastNorth,
                isGeographic: true,
                out var minX, out var minY, out var maxX, out var maxY);

        result.Should().Be(expectedValid,
            $"bbox '{bbox}' should be {(expectedValid ? "valid" : "invalid")} for geographic coordinates");

        if (expectedValid && bbox == "179,-10,-179,10")
        {
            // Special case: antimeridian crossing should parse correctly
            minX.Should().Be(179);
            maxX.Should().Be(-179);
            // Note: This validates the parsing, but spatial logic needs to handle wrap-around
        }
    }

    [UnitTheory]
    [InlineData("-180,-90,180,90", BoundingBoxValidity.Valid)]    // Full world extent (valid)
    [InlineData("-180,-90.1,180,90", BoundingBoxValidity.Invalid)] // South of South Pole (invalid)
    [InlineData("-180,-90,180,90.1", BoundingBoxValidity.Invalid)] // North of North Pole (invalid)
    [InlineData("-180,-89.999,180,89.999", BoundingBoxValidity.Valid)] // Near poles (valid)
    [Trait("Category", "Unit")]
    [Operation(Operations.Query)]
    public void ValidateGeographicRange_PolarBoundaries_CorrectValidation(
        string bbox, BoundingBoxValidity expectedValidity)
    {
        var expectedValid = expectedValidity == BoundingBoxValidity.Valid;
        var result = Honua.Infrastructure.Services.RasterParsingHelpers
            .TryParseBoundingBox(
                bbox,
                Honua.Core.Features.Shared.Models.AxisOrder.EastNorth,
                isGeographic: true,
                out _, out _, out _, out _);

        result.Should().Be(expectedValid,
            $"bbox '{bbox}' polar boundary validation should be {expectedValid}");
    }

    #endregion

    #region SpatialFilterHelpers Direct Testing

    [UnitTest]
    [Operation(Operations.Query)]
    public void CreateEnvelopeWkb_AntimeridianCrossing_GeneratesValidWkb()
    {
        // Test antimeridian-crossing coordinates directly
        var wkb = SpatialFilterHelpers.CreateEnvelopeWkb(170, -10, -170, 10);

        // WKB should be valid format
        wkb.Should().NotBeNull();
        wkb[0].Should().Be(1);
        var geometry = new WKBReader().Read(wkb);
        geometry.Should().BeOfType<MultiPolygon>();
        ((MultiPolygon)geometry).NumGeometries.Should().Be(2);

        // Should not throw exceptions during creation
        // Critical: Antimeridian coordinates don't break WKB encoding
    }

    [UnitTheory]
    [InlineData(new double[] { -180, -90, 180, 90 })]     // Full world extent
    [InlineData(new double[] { 179.999, -5, -179.999, 5 })] // Near antimeridian
    [InlineData(new double[] { 0, 85.05, 1, 85.051 })]   // Near Web Mercator limit
    [Trait("Category", "Unit")]
    [Operation(Operations.Query)]
    public void CreateEnvelopeWkb_ExtemeCoordinates_GeneratesValidWkb(
        double[] bounds)
    {
        var minX = bounds[0];
        var minY = bounds[1];
        var maxX = bounds[2];
        var maxY = bounds[3];
        var wkb = SpatialFilterHelpers.CreateEnvelopeWkb(minX, minY, maxX, maxY);

        wkb.Should().NotBeNull();
        wkb[0].Should().Be(1); // Little-endian
        var geometry = new WKBReader().Read(wkb);
        geometry.Should().NotBeNull();

        if (minX > maxX)
        {
            geometry.Should().BeOfType<MultiPolygon>();
            ((MultiPolygon)geometry).NumGeometries.Should().Be(2);
            return;
        }

        geometry.Should().BeOfType<Polygon>();

        // WKB should encode coordinates without overflow or corruption
        // Reading the coordinates back would require a full WKB parser test
    }

    #endregion
}
