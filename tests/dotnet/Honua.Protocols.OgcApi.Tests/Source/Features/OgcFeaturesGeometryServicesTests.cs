// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Geometries;
using Honua.Infrastructure.Services;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

public sealed class OgcFeaturesGeometryServicesTests
{
    [Fact]
    public void ConvertGeoJsonToSimpleGeometry_WithNorthEastAxis_SwapsCoordinates()
    {
        var sut = CreateSut();

        var result = sut.ConvertGeoJsonToSimpleGeometry(
            """{"type":"Point","coordinates":[-122.5,37.5]}""",
            AxisOrder.NorthEast);

        result.Should().NotBeNull();
        result!.Type.Should().Be("Point");

        using var coordinates = JsonDocument.Parse(result.CoordinatesJson!);
        var values = coordinates.RootElement.EnumerateArray().ToArray();
        values[0].GetDouble().Should().Be(37.5);
        values[1].GetDouble().Should().Be(-122.5);
    }

    [Fact]
    public void ConvertGeoJsonToSimpleGeometry_WithInvalidJson_ReturnsNull()
    {
        var sut = CreateSut();

        var result = sut.ConvertGeoJsonToSimpleGeometry("{\"type\":\"Point\",\"coordinates\":", AxisOrder.EastNorth);

        result.Should().BeNull();
    }

    [Fact]
    public void ConvertGeoJsonToSimpleGeometry_WithMissingCoordinates_ReturnsNull()
    {
        var sut = CreateSut();

        var result = sut.ConvertGeoJsonToSimpleGeometry("""{"type":"Point"}""", AxisOrder.EastNorth);

        result.Should().BeNull();
    }

    [Fact]
    public void ConvertGeoJsonToSimpleGeometry_WithMalformedCoordinates_ReturnsNull()
    {
        var sut = CreateSut();

        var result = sut.ConvertGeoJsonToSimpleGeometry(
            """{"type":"Point","coordinates":"invalid"}""",
            AxisOrder.EastNorth);

        result.Should().BeNull();
    }

    [Fact]
    public void ConvertGeoJsonToSimpleGeometry_WithMalformedGeometryCollection_ReturnsNull()
    {
        var sut = CreateSut();

        var result = sut.ConvertGeoJsonToSimpleGeometry(
            """{"type":"GeometryCollection","geometries":[{"type":"Point"}]}""",
            AxisOrder.EastNorth);

        result.Should().BeNull();
    }

    [Fact]
    public void ConvertGeoJsonToSimpleGeometry_WithEmptyGeometryCollection_ReturnsGeometryCollection()
    {
        var sut = CreateSut();

        var result = sut.ConvertGeoJsonToSimpleGeometry(
            """{"type":"GeometryCollection","geometries":[]}""",
            AxisOrder.EastNorth);

        result.Should().NotBeNull();
        result!.Type.Should().Be("GeometryCollection");
        result.GeometriesJson.Should().Be("[]");
    }

    [Theory]
    [InlineData("""{"type":"LineString","coordinates":[[0,0]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[0,0]]]}""")]
    public void ConvertGeoJsonToSimpleGeometry_WithInvalidTopology_ReturnsNull(string geoJson)
    {
        var sut = CreateSut();

        var result = sut.ConvertGeoJsonToSimpleGeometry(geoJson, AxisOrder.EastNorth);

        result.Should().BeNull();
    }

    [Fact]
    public void ConvertGeoJsonToSimpleGeometry_WithEastNorthAxis_ReusesRoundedCoordinates()
    {
        var sut = CreateSut();

        var result = sut.ConvertGeoJsonToSimpleGeometry(
            """{"type":"Point","coordinates":[-122.123456789,37.987654321]}""",
            AxisOrder.EastNorth);

        result.Should().NotBeNull();
        result!.Type.Should().Be("Point");
        result.CoordinatesJson.Should().Be("[-122.12345679,37.98765432]");
    }

    [Fact]
    public void TryCreateWkbFromGeoJson_WithTooManyVertices_ReturnsFailure()
    {
        var sut = CreateSut();
        var coordinates = string.Join(",", Enumerable.Range(0, 50_001).Select(i => $"[{i},0]"));
        var geometry = new SimpleGeoJsonGeometry
        {
            Type = "LineString",
            CoordinatesJson = $"[{coordinates}]"
        };

        var result = sut.TryCreateWkbFromGeoJson(geometry, 4326);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid geometry.");
    }

    [UnitTheory]
    [InlineData(AxisOrder.EastNorth)]
    [InlineData(AxisOrder.NorthEast)]
    public void ConvertWkbToSimpleGeometry_ReusedReaderMatchesFreshReadersAcrossFormats(AxisOrder axisOrder)
    {
        var sut = CreateSut();
        var textReader = new WKTReader();
        var formats = new[]
        {
            "POINT (-122.123456789 37.987654321)",
            "POINT Z (10 20 30)",
            "POINT M (40 50 60)",
            "POINT ZM (1 2 3 4)",
            "POINT EMPTY",
            "LINESTRING (1 2, 3 4)",
            "POLYGON ((0 0, 4 0, 4 4, 0 0))",
            "GEOMETRYCOLLECTION (POINT (5 6), LINESTRING (7 8, 9 10))",
            "GEOMETRYCOLLECTION EMPTY"
        };
        foreach (var byteOrder in new[] { ByteOrder.BigEndian, ByteOrder.LittleEndian })
        {
            foreach (var includeSrid in new[] { true, false })
            {
                foreach (var wkt in formats)
                {
                    var geometry = textReader.Read(wkt);
                    geometry.SRID = includeSrid ? 3857 : 4326;
                    var wkb = new WKBWriter(byteOrder, includeSrid, true, true).Write(geometry);
                    var expected = CreateSut().ConvertWkbToSimpleGeometry(wkb, axisOrder);
                    sut.ConvertWkbToSimpleGeometry(wkb, axisOrder).Should().BeEquivalentTo(expected,
                        "parser state must not leak between endian, SRID, dimension or geometry variants");
                }
            }
        }
    }

    [UnitTest]
    public void ConvertWkbToSimpleGeometry_MalformedThenValid_DoesNotRetainParserState()
    {
        var sut = CreateSut();
        var wkb = new WKBWriter(ByteOrder.BigEndian, true, true, true)
            .Write(new WKTReader().Read("POINT ZM (1 2 3 4)"));
        Action malformed = () => sut.ConvertWkbToSimpleGeometry(wkb[..^1], AxisOrder.EastNorth);
        malformed.Should().Throw<Exception>();
        sut.ConvertWkbToSimpleGeometry(null, AxisOrder.EastNorth).Should().BeNull();
        sut.ConvertWkbToSimpleGeometry([], AxisOrder.EastNorth).Should().BeNull();
        var valid = new WKBWriter(ByteOrder.LittleEndian).Write(new WKTReader().Read("POINT (5 6)"));
        sut.ConvertWkbToSimpleGeometry(valid, AxisOrder.NorthEast).Should().BeEquivalentTo(
            CreateSut().ConvertWkbToSimpleGeometry(valid, AxisOrder.NorthEast));
    }

    [UnitTheory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(15)]
    public void ConvertWkbToSimpleGeometry_MatchesLegacyOutputAcrossPrecisionAndGeometryTypes(int precision)
    {
        var limits = new LimitsOptions();
        limits.Geometry.MaxCoordinatePrecision = precision;
        // Exercise the normal limits path even when simplification is configured.
        limits.Geometry.SimplifyTolerance = 1;
        var formats = new[]
        {
            "POINT (-122.123456789 37.987654321)",
            "POINT (1.25 -2.25)",
            "POINT (-0.0 0.0)",
            "POINT (1E-12 -1E20)",
            "POINT Z (10 20 30.123456789)",
            "POINT M (40 50 60)",
            "POINT ZM (1 2 3 4)",
            "POINT EMPTY",
            "LINESTRING (1 2, 3 4)",
            "POLYGON ((0 0, 0 4, 4 4, 0 0))",
            "GEOMETRYCOLLECTION (POINT (5 6), LINESTRING (7 8, 9 10))"
        };
        foreach (var axisOrder in new[] { AxisOrder.EastNorth, AxisOrder.NorthEast })
        {
            var sut = CreateSut(limits);
            foreach (var wkt in formats)
            {
                var geometry = new WKTReader().Read(wkt);
                geometry.SRID = 4326;
                var wkb = new WKBWriter(ByteOrder.LittleEndian, true, true, true).Write(geometry);
                AssertMatchesLegacy(sut, wkb, axisOrder, limits);
            }
        }
    }

    [UnitTheory]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(1, double.NegativeInfinity)]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.NaN)]
    [InlineData(double.MaxValue, double.MinValue)]
    public void ConvertWkbToSimpleGeometry_UnusualOrdinatesPreserveLegacyBehavior(double x, double y)
    {
        var limits = new LimitsOptions();
        limits.Geometry.MaxCoordinatePrecision = -1;
        var geometry = new GeometryFactory().CreatePoint(new Coordinate(x, y));
        var wkb = new WKBWriter().Write(geometry);
        foreach (var axisOrder in new[] { AxisOrder.EastNorth, AxisOrder.NorthEast })
        {
            AssertMatchesLegacy(CreateSut(limits), wkb, axisOrder, limits);
        }
    }

    [UnitTest]
    public void ConvertWkbToSimpleGeometry_PointRoundingRetainsMidpointAndAxisSemantics()
    {
        var limits = new LimitsOptions();
        limits.Geometry.MaxCoordinatePrecision = 1;
        var wkb = new WKBWriter().Write(new WKTReader().Read("POINT (1.25 -2.25)"));
        var result = CreateSut(limits).ConvertWkbToSimpleGeometry(wkb, AxisOrder.NorthEast);
        result!.CoordinatesJson.Should().Be("[-2.3,1.3]");
    }

    private static void AssertMatchesLegacy(
        OgcFeaturesGeometryServices sut, byte[] wkb, AxisOrder axisOrder, LimitsOptions limits)
    {
        // The pre-optimization path is an independent compatibility oracle; using a
        // second optimized service would conceal serialization regressions.
        var geometry = new WKBReader().Read(wkb);
        if (axisOrder == AxisOrder.NorthEast)
        {
            geometry = geometry.Copy();
            geometry.Apply(new AxisSwapCoordinateFilter());
            geometry.GeometryChanged();
        }
        geometry = GeometryOutputProcessor.ApplyLimits(geometry, limits.Geometry) ?? geometry;
        using var expected = JsonDocument.Parse(RingWindingNormalizer.WriteGeoJson(new GeoJsonWriter(), geometry));
        var actual = sut.ConvertWkbToSimpleGeometry(wkb, axisOrder);
        actual.Should().NotBeNull();
        actual!.Type.Should().Be(expected.RootElement.GetProperty("type").GetString());
        if (expected.RootElement.TryGetProperty("coordinates", out var coordinates))
        {
            using var actualCoordinates = JsonDocument.Parse(actual.CoordinatesJson!);
            JsonElement.DeepEquals(coordinates, actualCoordinates.RootElement).Should().BeTrue();
        }
        else
        {
            actual.CoordinatesJson.Should().BeNull();
        }
        actual.GeometriesJson.Should().Be(expected.RootElement.TryGetProperty("geometries", out var geometries)
            ? geometries.GetRawText() : null);
    }

    private static OgcFeaturesGeometryServices CreateSut(LimitsOptions? limits = null)
        => new(
            new Honua.Infrastructure.Services.GeometryService(Options.Create(limits ?? new LimitsOptions())),
            new IdentityCoordinateTransformService(),
            Options.Create(limits ?? new LimitsOptions()),
            NullLogger<OgcFeaturesGeometryServices>.Instance);

    private sealed class IdentityCoordinateTransformService : ICoordinateTransformService
    {
        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX,
            double minY,
            double maxX,
            double maxY,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double MinX, double MinY, double MaxX, double MaxY)?>((minX, minY, maxX, maxY));

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x,
            double y,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double X, double Y)?>((x, y));
    }
}
