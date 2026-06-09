// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    private static OgcFeaturesGeometryServices CreateSut()
        => new(
            new Honua.Infrastructure.Services.GeometryService(Options.Create(new LimitsOptions())),
            new IdentityCoordinateTransformService(),
            Options.Create(new LimitsOptions()),
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
