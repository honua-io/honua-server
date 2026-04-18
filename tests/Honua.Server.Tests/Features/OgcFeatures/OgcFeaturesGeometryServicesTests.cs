// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.OgcFeatures.Models;
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
            new Honua.Server.Features.Infrastructure.Services.GeometryService(Options.Create(new LimitsOptions())),
            Options.Create(new LimitsOptions()),
            NullLogger<OgcFeaturesGeometryServices>.Instance);
}
