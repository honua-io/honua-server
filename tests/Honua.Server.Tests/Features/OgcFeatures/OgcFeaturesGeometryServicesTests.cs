// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.OgcFeatures.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Honua.Server.Tests.Features.OgcFeatures;

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

    private static OgcFeaturesGeometryServices CreateSut()
        => new(Options.Create(new LimitsOptions()), NullLogger<OgcFeaturesGeometryServices>.Instance);
}
