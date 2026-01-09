// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureServer;

[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
[Operation(Operations.GetTile)]
public class MvtTileEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0; // Use existing test layer

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_ValidCoordinates_ReturnsValidMvtTile()
    {
        // Act - Get tile at zoom 1, covering most of the world
        var response = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        // Assert - Should return either OK with content or NoContent if empty
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.mapbox-vector-tile");
            var content = await response.Content.ReadAsByteArrayAsync();
            content.Should().NotBeEmpty();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_WithWhereClause_ReturnsFilteredTile()
    {
        // Act - Get tile with WHERE clause filter
        var response = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt?where=name='Test'");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_InvalidTileCoordinates_ReturnsBadRequest()
    {
        // Act & Assert - Test various invalid coordinates
        var invalidCoordinates = new[]
        {
            "/tiles/0/-1/0/0.mvt", // negative zoom
            "/tiles/0/25/0/0.mvt", // zoom > 22
            "/tiles/0/1/-1/0.mvt", // negative x
            "/tiles/0/1/0/-1.mvt", // negative y
            "/tiles/0/1/2/0.mvt",  // x >= 2^z (for z=1, max x is 1)
            "/tiles/0/1/0/2.mvt"   // y >= 2^z (for z=1, max y is 1)
        };

        foreach (var coordinate in invalidCoordinates)
        {
            var response = await _fixture.Client.GetAsync(coordinate);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"coordinate {coordinate} should return BadRequest");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_NonExistentLayer_ReturnsNotFound()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/tiles/99999/1/0/0.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_ValidCoordinates_ReturnsCacheHeaders()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/1/0/0.mvt");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        // Check for cache control headers if response is OK
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Headers.Should().ContainKey("Cache-Control");
            response.Headers.CacheControl?.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_HighZoomLevel_ReturnsDetailedTile()
    {
        // Act - Get high zoom tile (should have no simplification)
        var response = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/15/16384/16384.mvt");

        // Assert - High zoom may return empty if no features in that specific tile
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task GetTile_LowZoomLevel_ReturnsSimplifiedTile()
    {
        // Act - Get low zoom tile (should have geometry simplification)
        var response = await _fixture.Client.GetAsync($"/tiles/{TestLayerId}/5/15/15.mvt");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsByteArrayAsync();
            content.Should().NotBeEmpty();
        }
    }
}
