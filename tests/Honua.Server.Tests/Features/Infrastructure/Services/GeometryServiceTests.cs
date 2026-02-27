// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Infrastructure.Services;

public sealed class GeometryServiceTests
{
    private readonly Honua.Server.Features.Infrastructure.Services.GeometryService _service = new(Options.Create(new LimitsOptions()));

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
}
