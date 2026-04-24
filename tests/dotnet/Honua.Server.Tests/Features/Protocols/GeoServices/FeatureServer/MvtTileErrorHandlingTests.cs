// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public class MvtTileErrorHandlingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task GetTile_InvalidZoomBelowMinimum_ReturnsGeoServicesErrorFormat()
    {
        // Act - Request tile with zoom below minimum (0)
        var response = await _fixture.Client.GetAsync("/tiles/0/-1/0/0.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
        error.Error.Details.Should().NotBeNull();
        error.Error.Details.Should().Contain("Zoom level -1 is outside supported range");
    }

    [Fact]
    public async Task GetTile_InvalidZoomAboveMaximum_ReturnsGeoServicesErrorFormat()
    {
        // Act - Request tile with zoom above maximum (typically 22)
        var response = await _fixture.Client.GetAsync("/tiles/0/25/0/0.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
        error.Error.Details.Should().NotBeNull();
        error.Error.Details.Should().Contain("Zoom level 25 is outside supported range");
    }

    [Fact]
    public async Task GetTile_InvalidTileCoordinatesNegativeX_ReturnsGeoServicesErrorFormat()
    {
        // Act - Request tile with negative X coordinate
        var response = await _fixture.Client.GetAsync("/tiles/0/1/-1/0.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
        error.Error.Details.Should().NotBeNull();
        error.Error.Details.Should().Contain("Invalid tile coordinates: x=-1, y=0, z=1");
    }

    [Fact]
    public async Task GetTile_InvalidTileCoordinatesNegativeY_ReturnsGeoServicesErrorFormat()
    {
        // Act - Request tile with negative Y coordinate
        var response = await _fixture.Client.GetAsync("/tiles/0/1/0/-1.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
        error.Error.Details.Should().NotBeNull();
        error.Error.Details.Should().Contain("Invalid tile coordinates: x=0, y=-1, z=1");
    }

    [Fact]
    public async Task GetTile_InvalidTileCoordinatesXOutOfRange_ReturnsGeoServicesErrorFormat()
    {
        // Act - Request tile with X coordinate out of range for zoom level
        // For zoom 1, max X is 1, so X=2 is invalid
        var response = await _fixture.Client.GetAsync("/tiles/0/1/2/0.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
        error.Error.Details.Should().NotBeNull();
        error.Error.Details.Should().Contain("Invalid tile coordinates: x=2, y=0, z=1");
    }

    [Fact]
    public async Task GetTile_NonExistentLayer_ReturnsGeoServicesNotFoundError()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/tiles/99999/1/0/0.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(404);
        error.Error.Message.Should().Be("Not Found");
        error.Error.Details.Should().NotBeNull();
        error.Error.Details.Should().Contain(detail => detail.Contains("Layer 99999 not found"));
    }

    [Fact]
    public async Task GetTile_ErrorResponseIncludesCorrelationId_ForTraceability()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/tiles/99999/1/0/0.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Should include correlation ID header for request tracing
        response.Headers.Should().ContainKey("X-Correlation-ID");
        var correlationId = response.Headers.GetValues("X-Correlation-ID").First();
        correlationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetTile_ErrorResponseFormat_ConsistentWithOtherFeatureServerEndpoints()
    {
        // Act - Get error from MVT endpoint
        var mvtResponse = await _fixture.Client.GetAsync("/tiles/99999/1/0/0.mvt");

        // Act - Get error from FeatureServer query endpoint for comparison
        var queryResponse = await _fixture.Client.GetAsync("/rest/services/99999/FeatureServer/0/query");

        // Assert - Both should return the same error response format
        mvtResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        queryResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var mvtContent = await mvtResponse.Content.ReadAsStringAsync();
        var queryContent = await queryResponse.Content.ReadAsStringAsync();

        var mvtError = JsonSerializer.Deserialize<ApiErrorResponse>(mvtContent);
        var queryError = JsonSerializer.Deserialize<ApiErrorResponse>(queryContent);

        // Both should use the same error response structure
        mvtError.Should().NotBeNull();
        queryError.Should().NotBeNull();

        mvtError!.Error.Code.Should().Be(queryError!.Error.Code);
        mvtError.Error.Should().BeEquivalentTo(queryError.Error, options =>
            options.Excluding(e => e.Message).Excluding(e => e.Details)); // Message content may differ
    }
}
