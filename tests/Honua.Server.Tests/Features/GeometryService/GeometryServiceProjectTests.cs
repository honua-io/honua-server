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
public sealed class GeometryServiceProjectTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/geometry/project")]
    public async Task Project_Wgs84ToWebMercator_ReturnsCorrectCoordinates()
    {
        // Arrange - a point in WGS84, project to Web Mercator (3857)
        var request = new ProjectRequest
        {
            Geometries = [JsonDocument.Parse("""{"x": 0, "y": 0, "spatialReference": {"wkid": 4326}}""").RootElement],
            InSR = 4326,
            OutSR = 3857
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.ProjectRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/project", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        // Origin point (0,0) in WGS84 should be (0,0) in Web Mercator too
        var geom = result.Geometries![0];
        geom.TryGetProperty("x", out var x).Should().BeTrue();
        x.GetDouble().Should().BeApproximately(0, 1.0);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/geometry/project")]
    public async Task Project_SameSrid_ReturnsUnchanged()
    {
        // Arrange - project 4326 to 4326 (no-op)
        var request = new ProjectRequest
        {
            Geometries = [JsonDocument.Parse("""{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}""").RootElement],
            InSR = 4326,
            OutSR = 4326
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.ProjectRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/project", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        // Coordinates should be unchanged
        var geom = result.Geometries![0];
        geom.TryGetProperty("x", out var x).Should().BeTrue();
        x.GetDouble().Should().BeApproximately(-122.4194, 0.001);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/geometry/project")]
    public async Task Project_InvalidSrid_Returns400()
    {
        // Arrange - invalid inSR
        var request = new ProjectRequest
        {
            Geometries = [JsonDocument.Parse("""{"x": 0, "y": 0}""").RootElement],
            InSR = 0,
            OutSR = 4326
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.ProjectRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/project", content);

        // Assert
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/geometry/project")]
    public async Task Project_BatchGeometries_ReturnsAll()
    {
        // Arrange - multiple points
        var request = new ProjectRequest
        {
            Geometries =
            [
                JsonDocument.Parse("""{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}""").RootElement,
                JsonDocument.Parse("""{"x": -73.9857, "y": 40.7484, "spatialReference": {"wkid": 4326}}""").RootElement,
                JsonDocument.Parse("""{"x": 2.3522, "y": 48.8566, "spatialReference": {"wkid": 4326}}""").RootElement
            ],
            InSR = 4326,
            OutSR = 3857
        };

        var json = JsonSerializer.Serialize(request, GeometryServiceJsonContext.Default.ProjectRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync("/rest/services/geometry/project", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(3, "all input geometries should be projected");
    }
}
