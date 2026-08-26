// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Infrastructure.Services;

public sealed class GeometryServiceTests
{
    private readonly Honua.Infrastructure.Services.GeometryService _service = new(Options.Create(new LimitsOptions()));

    [Fact]
    public void DetectZM_WhenZPresentInLaterCoordinate_ReturnsHasZ()
    {
        var geometry = new LineString(new Coordinate[]
        {
            new Coordinate(0, 0),
            new CoordinateZ(1, 1, 5)
        });
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false)
            .Write(geometry);

        var result = _service.DetectZM(wkb);

        result.HasZ.Should().BeTrue();
        result.HasM.Should().BeFalse();
    }

    [Fact]
    public void DetectZM_WhenMPresentInLaterCoordinate_ReturnsHasM()
    {
        var geometry = new LineString(new Coordinate[]
        {
            new Coordinate(0, 0),
            new CoordinateZM(1, 1, double.NaN, 7)
        });
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true)
            .Write(geometry);

        var result = _service.DetectZM(wkb);

        result.HasZ.Should().BeFalse();
        result.HasM.Should().BeTrue();
    }

    [Fact]
    public void ConvertWkbToGeoJson_WithClockwiseExteriorPolygon_EmitsCounterClockwiseExterior()
    {
        // Stored clockwise-exterior polygon (common via Esri applyEdits / shapefile imports).
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var cwPolygon = factory.CreatePolygon(new Coordinate[]
        {
            new(0, 0),
            new(0, 1),
            new(1, 1),
            new(1, 0),
            new(0, 0),
        });
        var wkb = new WKBWriter().Write(cwPolygon);

        var geoJson = _service.ConvertWkbToGeoJson(wkb);

        geoJson.Should().NotBeNull();
        var readBack = (Polygon)new GeoJsonReader().Read<Geometry>(geoJson!);
        Orientation.IsCCW(readBack.ExteriorRing.CoordinateSequence).Should().BeTrue();
    }

    [Fact]
    public void ConvertGeoJsonToWkb_WithMalformedPayload_ReturnsSanitizedError()
    {
        const string malformed = """{"type":"Point","coordinates":SENTINEL_GEOMETRY_TOKEN}""";

        var action = () => _service.ConvertGeoJsonToWkb(malformed);

        var ex = action.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("Invalid GeoJSON format.");
        ex.Message.Should().NotContain("SENTINEL_GEOMETRY_TOKEN");
        ex.Message.Should().NotContain("BytePositionInLine");
        ex.Message.Should().NotContain("LineNumber");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"point\"")]
    public void ConvertGeoJsonToWkb_WithNonObjectRoot_ReturnsSanitizedError(string geoJson)
    {
        var action = () => _service.ConvertGeoJsonToWkb(geoJson);

        var ex = action.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Be("Invalid GeoJSON format.");
    }

    [Theory]
    [InlineData("""{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{}}""")]
    [InlineData("""{"type":"FeatureCollection","features":[]}""")]
    public void ConvertGeoJsonToWkb_WithContainerWithoutOptIn_ReturnsSanitizedError(string geoJson)
    {
        var action = () => _service.ConvertGeoJsonToWkb(geoJson);

        var ex = action.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Be("Invalid GeoJSON format.");
    }

    [Theory]
    [InlineData("""{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{}}""", 1)]
    [InlineData("""{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{}},{"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{}}]}""", 2)]
    public void ConvertGeoJsonToWkb_WithGeoJsonContainer_ExtractsAllGeometries(string geoJson, int expectedCount)
    {
        var wkb = _service.ConvertGeoJsonToWkb(geoJson, 4326, allowContainers: true);

        wkb.Should().NotBeNull();
        var geometry = new WKBReader().Read(wkb!);
        geometry.SRID.Should().Be(4326);
        geometry.NumGeometries.Should().Be(expectedCount);
    }

    [Theory]
    [InlineData("""{"geometry":{"type":"Point","coordinates":[1,2]}}""")]
    [InlineData("""{"type":"Point","geometry":{"type":"Point","coordinates":[1,2]}}""")]
    public void ConvertGeoJsonToWkb_WithInvalidFeatureCollectionMember_ReturnsSanitizedError(string member)
    {
        var geoJson = $$"""{"type":"FeatureCollection","features":[{{member}}]}""";
        var action = () => _service.ConvertGeoJsonToWkb(geoJson, 4326, allowContainers: true);

        var ex = action.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Be("Invalid GeoJSON format.");
    }

    [Theory]
    [InlineData("""{"type":"FeatureCollection","features":[]}""", true)]
    [InlineData("""{"type":"GeometryCollection","geometries":[]}""", false)]
    [InlineData("""{"type":"Polygon","coordinates":[]}""", false)]
    [InlineData("""{"type":"MultiPoint","coordinates":[]}""", false)]
    [InlineData("""{"type":"MultiLineString","coordinates":[]}""", false)]
    [InlineData("""{"type":"MultiPolygon","coordinates":[]}""", false)]
    [InlineData("""{"type":"Feature","geometry":{"type":"GeometryCollection","geometries":[]},"properties":{}}""", true)]
    public void ConvertGeoJsonToWkb_WithEmptyGeometry_ReturnsSanitizedError(string geoJson, bool allowContainers)
    {
        var action = () => _service.ConvertGeoJsonToWkb(geoJson, 4326, allowContainers);

        var ex = action.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Be("Invalid GeoJSON format.");
    }

    [Fact]
    public void GeometryServiceContract_PreservesLegacyGeoJsonConversionSignature()
    {
        var method = typeof(IGeometryService).GetMethod(
            nameof(IGeometryService.ConvertGeoJsonToWkb),
            [typeof(string), typeof(int?)]);

        method.Should().NotBeNull();
        method!.IsAbstract.Should().BeTrue();
        method.GetParameters()[1].HasDefaultValue.Should().BeTrue();
        method.GetParameters()[1].DefaultValue.Should().BeNull();
    }

    [Fact]
    public void GeometryServiceContract_LegacyImplementationSupportsCompatibleCalls()
    {
        IGeometryService service = new LegacyGeometryService();

        service.ConvertGeoJsonToWkb("legacy", 4326).Should().Equal(1, 2, 3);
        service.ConvertGeoJsonToWkb("legacy", 4326, allowContainers: false).Should().Equal(1, 2, 3);

        var action = () => service.ConvertGeoJsonToWkb("legacy", 4326, allowContainers: true);
        var exception = action.Should().Throw<NotSupportedException>().Which;
        exception.Message.Should().Be(
            "GeoJSON container conversion is not supported by this geometry service implementation.");
    }


    [Fact]
    public void ConvertGeoJsonToWkb_WhenPayloadExceedsConfiguredLimit_ReturnsSanitizedError()
    {
        var service = new Honua.Infrastructure.Services.GeometryService(
            Options.Create(new LimitsOptions
            {
                Geometry = new GeometryLimits { MaxGeometrySize = 64 },
                Validation = new GeometryValidationOptions { MaxWkbSize = 64 }
            }));
        var oversized = "{\"type\":\"LineString\",\"coordinates\":[" + string.Join(',', Enumerable.Repeat("[0,0]", 64)) + "]}";

        var action = () => service.ConvertGeoJsonToWkb(oversized);

        var ex = action.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("maximum size");
        ex.Message.Should().NotContain("[0,0]");
    }

    [Fact]
    public void ConvertWktToWkb_WhenPayloadExceedsConfiguredLimit_ReturnsSanitizedError()
    {
        var service = new Honua.Infrastructure.Services.GeometryService(
            Options.Create(new LimitsOptions
            {
                Geometry = new GeometryLimits { MaxGeometrySize = 64 },
                Validation = new GeometryValidationOptions { MaxWkbSize = 64 }
            }));
        var oversized = "LINESTRING(" + string.Join(',', Enumerable.Repeat("0 0", 64)) + ")";

        var action = () => service.ConvertWktToWkb(oversized);

        var ex = action.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("maximum size");
        ex.Message.Should().NotContain("LINESTRING(");
    }

    [Fact]
    public void ConvertWkbToGeoJson_WithMalformedPayload_ReturnsSanitizedError()
    {
        var malformed = new byte[] { 0x01, 0x02, 0x03 };

        var action = () => _service.ConvertWkbToGeoJson(malformed);

        var ex = action.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("Invalid WKB geometry format.");
        ex.Message.Should().NotContain("BytePositionInLine");
        ex.Message.Should().NotContain("LineNumber");
    }

    private sealed class LegacyGeometryService : IGeometryService
    {
        public (bool HasZ, bool HasM) DetectZM(byte[]? wkb) => (false, false);

        public (bool HasZ, bool HasM) DetectZM(Memory<byte> wkb) => (false, false);

        public string? ConvertWkbToGeoJson(byte[]? wkb) => null;

        public string? ConvertWkbToGeoJson(Memory<byte> wkb) => null;

        public byte[]? ConvertGeoJsonToWkb(string? geoJson, int? srid = null) => [1, 2, 3];

        public byte[]? ConvertWktToWkb(string? wkt, int? srid = null) => null;

        public GeometryInfo? GetGeometryInfo(byte[]? wkb) => null;
    }
}
