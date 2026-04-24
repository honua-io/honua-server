// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Services;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit tests for GeometryConverter service
/// </summary>
public class GeometryConverterTests
{
    private readonly GeometryConverter _converter = new();

    private static Geometry ReadGeometry(byte[] wkb)
    {
        var reader = new WKBReader();
        return reader.Read(wkb);
    }

    #region Point Geometry Tests

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithPointGeometry_ShouldReturnValidWkb()
    {
        // Arrange
        var geoServicesJson = """{"x": -122.4194, "y": 37.7749}""";

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<Point>();

        var point = (Point)geometry;
        point.X.Should().BeApproximately(-122.4194, 0.0001);
        point.Y.Should().BeApproximately(37.7749, 0.0001);
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithPointGeometryAndIntegerCoords_ShouldReturnValidWkb()
    {
        // Arrange
        var geoServicesJson = """{"x": -122, "y": 38}""";

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<Point>();

        var point = (Point)geometry;
        point.X.Should().BeApproximately(-122.0, 0.0001);
        point.Y.Should().BeApproximately(38.0, 0.0001);
    }

    #endregion

    #region LineString Geometry Tests

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithLineStringGeometry_ShouldReturnValidWkb()
    {
        // Arrange - Simple linestring with 3 points
        var geoServicesJson = """
        {
            "paths": [
                [[-122.4, 37.8], [-122.3, 37.9], [-122.2, 38.0]]
            ]
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<LineString>();

        var lineString = (LineString)geometry;
        lineString.NumPoints.Should().Be(3);
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithMultiLineStringGeometry_ShouldReturnValidWkb()
    {
        // Arrange - Multi-path linestring
        var geoServicesJson = """
        {
            "paths": [
                [[-122.4, 37.8], [-122.3, 37.9]],
                [[-121.4, 38.8], [-121.3, 38.9]]
            ]
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<MultiLineString>();

        var multiLineString = (MultiLineString)geometry;
        multiLineString.NumGeometries.Should().Be(2);
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithEmptyPathsArray_ShouldThrowArgumentException()
    {
        // Arrange
        var geoServicesJson = """{"paths": []}""";

        // Act & Assert
        var action = () => _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);
        action.Should().Throw<ArgumentException>()
            .WithMessage("No valid paths found in linestring geometry");
    }

    #endregion

    #region MultiPoint Geometry Tests

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithMultiPointGeometry_ShouldReturnValidWkb()
    {
        // Arrange
        var geoServicesJson = """
        {
            "points": [
                [-122.4, 37.8],
                [-122.3, 37.9],
                [-122.2, 38.0]
            ]
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<MultiPoint>();

        var multiPoint = (MultiPoint)geometry;
        multiPoint.NumGeometries.Should().Be(3);
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithSinglePointInMultiPoint_ShouldReturnValidWkb()
    {
        // Arrange
        var geoServicesJson = """
        {
            "points": [
                [-122.4, 37.8]
            ]
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<MultiPoint>();

        var multiPoint = (MultiPoint)geometry;
        multiPoint.NumGeometries.Should().Be(1);
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithEmptyPointsArray_ShouldThrowArgumentException()
    {
        // Arrange
        var geoServicesJson = """{"points": []}""";

        // Act & Assert
        var action = () => _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);
        action.Should().Throw<ArgumentException>()
            .WithMessage("No valid points found in multipoint geometry");
    }

    #endregion

    #region Polygon Geometry Tests

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithPolygonGeometry_ShouldReturnValidWkb()
    {
        // Arrange - Simple polygon (square)
        var geoServicesJson = """
        {
            "rings": [
                [
                    [-122.0, 37.0],
                    [-121.0, 37.0],
                    [-121.0, 38.0],
                    [-122.0, 38.0],
                    [-122.0, 37.0]
                ]
            ]
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<Polygon>();

        var polygon = (Polygon)geometry;
        polygon.NumInteriorRings.Should().Be(0);
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithPolygonWithHole_ShouldReturnValidWkb()
    {
        // Arrange - Polygon with a hole (outer ring + inner ring)
        var geoServicesJson = """
        {
            "rings": [
                [
                    [-122.0, 37.0],
                    [-122.0, 38.0],
                    [-121.0, 38.0],
                    [-121.0, 37.0],
                    [-122.0, 37.0]
                ],
                [
                    [-121.8, 37.2],
                    [-121.2, 37.2],
                    [-121.2, 37.8],
                    [-121.8, 37.8],
                    [-121.8, 37.2]
                ]
            ]
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<Polygon>();

        var polygon = (Polygon)geometry;
        polygon.NumInteriorRings.Should().Be(1);
    }

    #endregion

    #region Envelope Geometry Tests

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithEnvelopeGeometry_ShouldReturnValidPolygonWkb()
    {
        // Arrange
        var geoServicesJson = """
        {
            "xmin": -122.5,
            "ymin": 37.7,
            "xmax": -122.3,
            "ymax": 37.9
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<Polygon>();

        var polygon = (Polygon)geometry;
        polygon.NumInteriorRings.Should().Be(0);
        polygon.ExteriorRing.NumPoints.Should().Be(5);
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithEnvelopeGeometryIntegerCoords_ShouldReturnValidPolygonWkb()
    {
        // Arrange
        var geoServicesJson = """
        {
            "xmin": -123,
            "ymin": 37,
            "xmax": -122,
            "ymax": 38
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<Polygon>();

        var polygon = (Polygon)geometry;
        polygon.NumInteriorRings.Should().Be(0);
    }

    #endregion

    #region Error Handling Tests

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithInvalidJson_ShouldThrowArgumentException()
    {
        // Arrange
        var invalidJson = """{invalid json}""";

        // Act & Assert
        var action = () => _converter.ConvertGeoServicesJsonToWkb(invalidJson);
        action.Should().Throw<ArgumentException>()
            .WithMessage("Invalid JSON format in geometry parameter");
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithUnsupportedGeometry_ShouldThrowArgumentException()
    {
        // Arrange
        var unsupportedJson = """{"unsupported": "geometry"}""";

        // Act & Assert
        var action = () => _converter.ConvertGeoServicesJsonToWkb(unsupportedJson);
        action.Should().Throw<ArgumentException>()
            .WithMessage("Invalid GeoServices JSON geometry format. Supported types: Point (x, y), Polygon (rings), LineString (paths), MultiPoint (points), Envelope (xmin, ymin, xmax, ymax)");
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithEmptyRingsArray_ShouldThrowArgumentException()
    {
        // Arrange
        var geoServicesJson = """{"rings": []}""";

        // Act & Assert
        var action = () => _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);
        action.Should().Throw<ArgumentException>()
            .WithMessage("No valid rings found in polygon geometry");
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_WithMalformedCoordinates_ShouldHandleGracefully()
    {
        // Arrange - Points with insufficient coordinates
        var geoServicesJson = """
        {
            "points": [
                [-122.4],
                [-122.3, 37.9]
            ]
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert - Should only process valid points
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<MultiPoint>();

        var multiPoint = (MultiPoint)geometry;
        multiPoint.NumGeometries.Should().Be(1); // Only one valid point processed
    }

    #endregion

    #region Integration with Spatial Query Tests

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_LineStringForSpatialQuery_ShouldProduceCompatibleWkb()
    {
        // Arrange - LineString that could be used in spatial queries
        var geoServicesJson = """
        {
            "paths": [
                [
                    [-122.5, 37.7],
                    [-122.4, 37.8],
                    [-122.3, 37.9]
                ]
            ]
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert - Basic WKB structure validation
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<LineString>();
    }

    [UnitTest]
    public void ConvertGeoServicesJsonToWkb_EnvelopeForBoundingBoxQuery_ShouldProduceCompatibleWkb()
    {
        // Arrange - Envelope representing a bounding box
        var geoServicesJson = """
        {
            "xmin": -123.0,
            "ymin": 37.0,
            "xmax": -122.0,
            "ymax": 38.0
        }
        """;

        // Act
        var result = _converter.ConvertGeoServicesJsonToWkb(geoServicesJson);

        // Assert - Should be converted to a rectangular polygon
        result.Should().NotBeNull();
        var geometry = ReadGeometry(result);
        geometry.Should().BeOfType<Polygon>();

        var polygon = (Polygon)geometry;
        polygon.NumInteriorRings.Should().Be(0);
        polygon.ExteriorRing.NumPoints.Should().Be(5); // Rectangle with closing point
    }

    #endregion
}
