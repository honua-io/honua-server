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
public sealed class GeometryServiceBufferTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/geometry/buffer")]
    public async Task Buffer_PointGeometry_ReturnsPolygon()
    {
        // Arrange - a point in WGS84
        var request = new BufferRequest
        {
            Geometries = [JsonDocument.Parse("""{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}""").RootElement],
            InSR = 4326,
            Distances = [1000],
            Unit = "esriMeters",
            Geodesic = true
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.BufferRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/buffer", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        // Buffer of a point should produce a polygon with rings
        var geom = result.Geometries![0];
        geom.GetProperty("rings").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/geometry/buffer")]
    public async Task Buffer_MultipleDistances_ReturnsMultipleGeometries()
    {
        // Arrange - two points with two distances
        var request = new BufferRequest
        {
            Geometries =
            [
                JsonDocument.Parse("""{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}""").RootElement,
                JsonDocument.Parse("""{"x": -73.9857, "y": 40.7484, "spatialReference": {"wkid": 4326}}""").RootElement
            ],
            InSR = 4326,
            Distances = [500, 1000],
            Unit = "esriMeters",
            Geodesic = true
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.BufferRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/buffer", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/geometry/buffer")]
    public async Task Buffer_UnionResults_ReturnsSingleGeometry()
    {
        // Arrange - two nearby points that will overlap when buffered
        var request = new BufferRequest
        {
            Geometries =
            [
                JsonDocument.Parse("""{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}""").RootElement,
                JsonDocument.Parse("""{"x": -122.4180, "y": 37.7760, "spatialReference": {"wkid": 4326}}""").RootElement
            ],
            InSR = 4326,
            Distances = [5000],
            Unit = "esriMeters",
            UnionResults = true,
            Geodesic = true
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.BufferRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/buffer", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1, "union should combine into a single geometry");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/geometry/buffer")]
    public async Task Buffer_InvalidGeometry_Returns400()
    {
        // Arrange - empty geometries array
        var request = new BufferRequest
        {
            Geometries = [],
            InSR = 4326,
            Distances = [100]
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.BufferRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/buffer", content);

        // Assert
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/geometry/buffer")]
    public async Task Buffer_MissingDistance_Returns400()
    {
        // Arrange - no distances
        var request = new BufferRequest
        {
            Geometries = [JsonDocument.Parse("""{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}""").RootElement],
            InSR = 4326,
            Distances = []
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.BufferRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/buffer", content);

        // Assert
        response.Be400BadRequest();
    }
}
