// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.GeometryService.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.GeometryService;

[Protocol(Protocols.GeometryService)]
[Collection("Database")]
public sealed class GeometryServiceSimplifyTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Simplify)]
    [Endpoint("POST /rest/services/geometry/simplify")]
    public async Task Simplify_ComplexPolygon_ReturnsSimplifiedGeometry()
    {
        // Arrange - a polygon with many vertices
        var polygonJson = """
        {
            "rings": [
                [
                    [-122.42, 37.78], [-122.41, 37.78], [-122.415, 37.785],
                    [-122.41, 37.79], [-122.42, 37.79], [-122.425, 37.785],
                    [-122.42, 37.78]
                ]
            ],
            "spatialReference": {"wkid": 4326}
        }
        """;

        var request = new SimplifyRequest
        {
            Geometries = [JsonDocument.Parse(polygonJson).RootElement],
            InSR = 4326,
            MaxDeviation = 0.001
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.SimplifyRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/simplify", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Simplify)]
    [Endpoint("POST /rest/services/geometry/simplify")]
    public async Task Simplify_PreserveTopology_MaintainsValidity()
    {
        // Arrange - a polygon that simplification should keep valid
        var polygonJson = """
        {
            "rings": [
                [
                    [0.0, 0.0], [1.0, 0.0], [1.0, 0.5], [1.0, 1.0],
                    [0.5, 1.0], [0.0, 1.0], [0.0, 0.5], [0.0, 0.0]
                ]
            ],
            "spatialReference": {"wkid": 4326}
        }
        """;

        var request = new SimplifyRequest
        {
            Geometries = [JsonDocument.Parse(polygonJson).RootElement],
            InSR = 4326,
            MaxDeviation = 0.1
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.SimplifyRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/simplify", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        // Result should still be a polygon (have rings)
        var geom = result.Geometries![0];
        geom.GetProperty("rings").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Simplify)]
    [Endpoint("POST /rest/services/geometry/simplify")]
    public async Task Simplify_InvalidTolerance_Returns400()
    {
        // Arrange - negative tolerance
        var request = new SimplifyRequest
        {
            Geometries = [JsonDocument.Parse("""{"x": 0, "y": 0}""").RootElement],
            InSR = 4326,
            MaxDeviation = -1.0
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.SimplifyRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/simplify", content);

        // Assert
        response.Be400BadRequest();
    }
}
