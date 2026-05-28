// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Elevation;

/// <summary>
/// Endpoint-level integration tests for the Pro-tier 3D visibility analysis
/// endpoints (line-of-sight and viewshed). The fixture is provisioned with a Pro
/// license so the <c>analytics.line-of-sight</c> / <c>analytics.viewshed</c>
/// entitlements are active; without these the endpoints return 402 before any
/// analysis runs.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Elevation)]
public sealed class VisibilityAnalysisEndpointTests : IAsyncLifetime
{
    private const double WebMercatorExtent = 20037508.342789244;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /elevation/{datasetId}/line-of-sight")]
    public async Task PostLineOfSight_WithCoveredFlatTerrain_ReportsVisible()
    {
        await SeedFullWorldRasterAsync(100);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/line-of-sight",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                observerHeight = 2.0,
                targetLon = 0.1,
                targetLat = 0.0,
                targetHeight = 2.0,
                sampleCount = 32
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("datasetId").GetString().Should().Be("0");
        root.GetProperty("layerId").GetInt32().Should().Be(0);
        root.GetProperty("visible").GetBoolean().Should().BeTrue();
        root.GetProperty("observerGroundElevation").GetDouble().Should().BeApproximately(100, 0.0001);
        root.GetProperty("observerElevation").GetDouble().Should().BeApproximately(102, 0.0001);
        root.GetProperty("hasNoDataSamples").GetBoolean().Should().BeFalse();
        root.GetProperty("distanceMeters").GetDouble().Should().BeGreaterThan(0);
        root.GetProperty("obstruction").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/line-of-sight")]
    public async Task PostLineOfSight_ObserverEqualsTarget_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(100);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/line-of-sight",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                targetLon = 0.0,
                targetLat = 0.0
            });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/line-of-sight")]
    public async Task PostLineOfSight_MissingCoordinates_ReturnsBadRequest()
    {
        await SeedFullWorldRasterAsync(100);

        var response = await _fixture.Client.PostAsync(
            "/elevation/0/line-of-sight",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/line-of-sight")]
    public async Task PostLineOfSight_EndpointOutsideCoverage_ReturnsNotFoundAndNotVisible()
    {
        // Regression: an observer/target ground sample that resolves to no-data
        // (outside coverage) must not be coerced to 0.0 and reported visible.
        // The service now fails the request rather than fabricating sea-level
        // terrain, so the endpoint maps it to a 404 and never claims visibility.
        await SeedSeQuadrantRasterAsync(50);

        // Both endpoints are in the uncovered north-west quadrant.
        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/line-of-sight",
            new
            {
                observerLon = -150.0,
                observerLat = 80.0,
                targetLon = -140.0,
                targetLat = 80.0,
                sampleCount = 2
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"visible\":true");
        body.Should().Contain("no elevation data");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /elevation/{datasetId}/viewshed")]
    public async Task PostViewshed_WithCoveredFlatTerrain_ReturnsVisibleSamples()
    {
        await SeedFullWorldRasterAsync(50);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/viewshed",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                observerHeight = 100.0,
                radiusMeters = 5000.0,
                rayCount = 16,
                samplesPerRay = 16
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("datasetId").GetString().Should().Be("0");
        root.GetProperty("layerId").GetInt32().Should().Be(0);
        root.GetProperty("rayCount").GetInt32().Should().Be(16);
        root.GetProperty("samplesPerRay").GetInt32().Should().Be(16);
        root.GetProperty("observerNoData").GetBoolean().Should().BeFalse();
        root.GetProperty("observerGroundElevation").GetDouble().Should().BeApproximately(50, 0.0001);
        root.GetProperty("sampleCount").GetInt32().Should().BeGreaterThan(0);
        // On flat terrain with an elevated observer every sampled point is visible.
        root.GetProperty("visibleSampleCount").GetInt32().Should().Be(root.GetProperty("sampleCount").GetInt32());
        root.GetProperty("samples").GetArrayLength().Should().Be(root.GetProperty("sampleCount").GetInt32());
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/viewshed")]
    public async Task PostViewshed_NonPositiveRadius_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(50);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/viewshed",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                radiusMeters = 0.0
            });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("radiusMeters");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/viewshed")]
    public async Task PostViewshed_MissingObserver_ReturnsBadRequest()
    {
        await SeedFullWorldRasterAsync(50);

        var response = await _fixture.Client.PostAsync(
            "/elevation/0/viewshed",
            new StringContent("{\"radiusMeters\":1000}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    private Task SeedFullWorldRasterAsync(double elevationMeters)
        => RasterIntegrationTestData.ReplaceLayerRastersAsync(
            _fixture,
            WebAppFixture.TestLayerId,
            new RasterSeed(
                Name: "world-dem",
                Width: 2,
                Height: 2,
                UpperLeftX: -WebMercatorExtent,
                UpperLeftY: WebMercatorExtent,
                ScaleX: WebMercatorExtent,
                ScaleY: -WebMercatorExtent,
                Value: elevationMeters,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: 3857));

    private Task SeedSeQuadrantRasterAsync(double elevationMeters)
        => RasterIntegrationTestData.ReplaceLayerRastersAsync(
            _fixture,
            WebAppFixture.TestLayerId,
            new RasterSeed(
                Name: "se-quadrant-dem",
                Width: 1,
                Height: 1,
                UpperLeftX: 0,
                UpperLeftY: 0,
                ScaleX: WebMercatorExtent,
                ScaleY: -WebMercatorExtent,
                Value: elevationMeters,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: 3857));
}
