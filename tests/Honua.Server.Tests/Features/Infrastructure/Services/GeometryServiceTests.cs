// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Infrastructure.Services;

public sealed class GeometryServiceTests
{
    private readonly GeometryService _service = new(Options.Create(new LimitsOptions()));

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
}
