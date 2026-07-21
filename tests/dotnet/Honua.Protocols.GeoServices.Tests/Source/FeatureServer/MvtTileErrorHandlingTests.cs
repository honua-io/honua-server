// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public class MvtTileErrorHandlingTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public MvtTileErrorHandlingTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetTile_InvalidZoomBelowMinimum_ReturnsGeoServicesErrorFormat()
    {
        // Act - Request tile with zoom below minimum (0)
        var response = await _fixture.Client.GetAsync("/tiles/0/-1/0/0.mvt");

        // Assert
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

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
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(content);

        error.Should().NotBeNull();
        error!.Error.Code.Should().Be(400);
        error.Error.Message.Should().Be("Bad Request");
        error.Error.Details.Should().NotBeNull();
        error.Error.Details.Should().Contain("Invalid tile coordinates: x=2, y=0, z=1");
    }

    [Fact]
    public async Task GetTile_NonExistentLayer_ReturnsRealNotFoundWithProblemJson()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/tiles/99999/1/0/0.mvt");

        // Assert
        // honua-server#2945: /tiles is not an Esri protocol surface, so unlike
        // /rest/services it must return a real HTTP 404 with an RFC 7807
        // problem+json body rather than the GeoServices 200-envelope.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var content = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(content);

        problem.GetProperty("title").GetString().Should().Be("Not Found");
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("detail").GetString().Should().Contain("Layer 99999 not found");
    }

    [Fact]
    public async Task GetTile_ErrorResponseIncludesCorrelationId_ForTraceability()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/tiles/99999/1/0/0.mvt");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Should include correlation ID header for request tracing regardless of
        // the response body format (added by the shared correlation-id middleware).
        response.Headers.Should().ContainKey("X-Correlation-ID");
        var correlationId = response.Headers.GetValues("X-Correlation-ID").First();
        correlationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetTile_ErrorResponseFormat_DivergesFromGeoServicesRestByDesign()
    {
        // honua-server#2945: the /tiles surface is a real HTTP-status protocol, not an
        // Esri GeoServices surface, so its 404 must NOT match the GeoServices REST
        // 200-envelope shape — this test locks in the intentional divergence (the
        // opposite of the old "ConsistentWith..." assertion this replaces).

        // Act - Get error from MVT endpoint (tiles surface)
        var mvtResponse = await _fixture.Client.GetAsync("/tiles/99999/1/0/0.mvt");

        // Act - Get error from FeatureServer query endpoint (GeoServices REST surface)
        var queryResponse = await _fixture.Client.GetAsync("/rest/services/99999/FeatureServer/0/query");

        // Assert - the tiles surface returns a real 404 problem+json response...
        mvtResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        mvtResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        // ...while the GeoServices REST surface keeps its 200-wrapped error envelope.
        await queryResponse.AssertGeoServicesErrorAsync(404);
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
