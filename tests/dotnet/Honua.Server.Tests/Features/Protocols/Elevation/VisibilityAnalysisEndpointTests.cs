// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
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

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/elevation/0/line-of-sight",
            content);

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

        using var content = new StringContent("{\"radiusMeters\":1000}", Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/elevation/0/viewshed",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    // ---- Ridge/occlusion terrain (honua-server#2945) ----
    //
    // The tests above only seed FLAT terrain, so "visible" is trivially true for every
    // sample regardless of whether occlusion logic is correct (Core.Tests already covers
    // real occlusion via a synthetic 1-D "elevation as a function of distance" stub, but no
    // endpoint-level test ever exercises a real, non-constant raster). These tests seed a
    // real 2-D DEM shaped like a "crater rim": a flat floor (10m) with a 3000m-tall square
    // ring between 1500m and 2000m from the observer at the Web Mercator origin (0,0). The
    // ring is a complete hollow square frame (four raster tiles: N/S/E/W bands, newer
    // acquisition date than the flat floor so the mosaic "newest wins" rule lets the ring
    // override the floor within its footprint) — so unlike a single occluding wall, ANY
    // straight line from the observer to a point beyond ~2000m in ANY direction must cross
    // the ring and be blocked.
    private const double RingInnerRadiusMeters = 1500;
    private const double RingOuterRadiusMeters = 2000;
    private const double RidgeFloorElevationMeters = 10;
    private const double RidgeRimElevationMeters = 3000;

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /elevation/{datasetId}/line-of-sight")]
    public async Task PostLineOfSight_TargetInsideRidgeRing_IsVisible()
    {
        await SeedRidgeRingRasterAsync();

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/line-of-sight",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                observerHeight = 2.0,
                targetLon = 0.004491576, // ~500m east: inside the 1500m ridge inner radius
                targetLat = 0.0,
                targetHeight = 2.0,
                sampleCount = 256
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("visible").GetBoolean().Should().BeTrue(
            "both observer and target sit on the flat crater floor, well inside the ridge ring");
        root.GetProperty("obstruction").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /elevation/{datasetId}/line-of-sight")]
    public async Task PostLineOfSight_TargetBeyondRidgeRing_IsBlockedByIntervalRidge()
    {
        await SeedRidgeRingRasterAsync();

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/line-of-sight",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                observerHeight = 2.0,
                targetLon = 0.035932611, // ~4000m east: beyond the 2000m outer ridge radius
                targetLat = 0.0,
                targetHeight = 2.0,
                sampleCount = 256
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("visible").GetBoolean().Should().BeFalse(
            "the 3000m ridge ring at 1500-2000m sits directly on the line between the observer and a target 4000m away");
        root.GetProperty("obstruction").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /elevation/{datasetId}/viewshed")]
    public async Task PostViewshed_RidgeRingTerrain_OccludesFarSamplesButNotNearSamples()
    {
        await SeedRidgeRingRasterAsync();

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/viewshed",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                observerHeight = 2.0,
                radiusMeters = 4500.0,
                rayCount = 36,
                samplesPerRay = 60
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var sampleCount = root.GetProperty("sampleCount").GetInt32();
        var visibleSampleCount = root.GetProperty("visibleSampleCount").GetInt32();

        // Unlike the flat-terrain viewshed (100% visible; see the tests above), the ring
        // must occlude a real share of samples: it fully encircles the observer, so every
        // ray's far-side samples are blocked.
        visibleSampleCount.Should().BeLessThan(sampleCount,
            "the ridge ring fully encircles the observer, so far-side samples in every direction must be occluded");
        visibleSampleCount.Should().BeGreaterThan(0,
            "samples inside the ring, on the crater floor, must remain visible");

        var samples = root.GetProperty("samples").EnumerateArray().ToArray();

        var nearSamples = samples.Where(s => s.GetProperty("distanceMeters").GetDouble() < 1000).ToArray();
        nearSamples.Should().NotBeEmpty();
        nearSamples.Should().OnlyContain(s => s.GetProperty("visible").GetBoolean(),
            "samples well inside the ridge ring (< 1000m) sit on the flat crater floor and must be visible");

        var farSamples = samples.Where(s => s.GetProperty("distanceMeters").GetDouble() > 2500).ToArray();
        farSamples.Should().NotBeEmpty();
        farSamples.Should().OnlyContain(s => !s.GetProperty("visible").GetBoolean(),
            "samples beyond the ridge ring (> 2500m), in every direction, are blocked by the intervening 3000m ridge");
    }

    /// <summary>
    /// Seeds a real 2-D "crater rim" DEM: a flat floor tile plus four raster tiles
    /// (north/south/east/west bands, newer acquisition date so the mosaic "newest wins"
    /// rule overrides the floor within each band's footprint) that together form a
    /// complete hollow square ring around the Web Mercator origin (the observer position
    /// used by every ridge test above). All rasters are stored directly in EPSG:3857
    /// meters, matching <see cref="SeedFullWorldRasterAsync"/>/<see cref="SeedSeQuadrantRasterAsync"/>,
    /// so <see cref="RingInnerRadiusMeters"/>/<see cref="RingOuterRadiusMeters"/> are exact
    /// (not degree-approximated) distances from the observer.
    /// </summary>
    private Task SeedRidgeRingRasterAsync()
    {
        var floorTime = RasterIntegrationTestData.WestAcquisition;
        var ridgeTime = RasterIntegrationTestData.EastAcquisition;

        // All rasters share one 250 m grid (identical scale, origins at multiples of the cell
        // size): the elevation profile path merges the layer's rasters with PostGIS raster
        // union, and rt_raster_from_two_rasters hard-fails on mixed alignment. Mixed-resolution
        // mosaics currently surface that as an unmapped 500 — tracked separately; this fixture
        // stays aligned so these tests prove occlusion math, not mosaic alignment handling.
        const double cell = 250;
        const double floorHalfExtent = RingOuterRadiusMeters * 3;

        return RasterIntegrationTestData.ReplaceLayerRastersAsync(
            _fixture,
            WebAppFixture.TestLayerId,
            new RasterSeed(
                Name: "ridge-floor",
                Width: (int)(floorHalfExtent * 2 / cell),
                Height: (int)(floorHalfExtent * 2 / cell),
                UpperLeftX: -floorHalfExtent,
                UpperLeftY: floorHalfExtent,
                ScaleX: cell,
                ScaleY: -cell,
                Value: RidgeFloorElevationMeters,
                AcquisitionDate: floorTime,
                CreatedAt: floorTime,
                Srid: 3857),
            new RasterSeed(
                Name: "ridge-north",
                Width: (int)(RingOuterRadiusMeters * 2 / cell),
                Height: (int)((RingOuterRadiusMeters - RingInnerRadiusMeters) / cell),
                UpperLeftX: -RingOuterRadiusMeters,
                UpperLeftY: RingOuterRadiusMeters,
                ScaleX: cell,
                ScaleY: -cell,
                Value: RidgeRimElevationMeters,
                AcquisitionDate: ridgeTime,
                CreatedAt: ridgeTime,
                Srid: 3857),
            new RasterSeed(
                Name: "ridge-south",
                Width: (int)(RingOuterRadiusMeters * 2 / cell),
                Height: (int)((RingOuterRadiusMeters - RingInnerRadiusMeters) / cell),
                UpperLeftX: -RingOuterRadiusMeters,
                UpperLeftY: -RingInnerRadiusMeters,
                ScaleX: cell,
                ScaleY: -cell,
                Value: RidgeRimElevationMeters,
                AcquisitionDate: ridgeTime,
                CreatedAt: ridgeTime,
                Srid: 3857),
            new RasterSeed(
                Name: "ridge-east",
                Width: (int)((RingOuterRadiusMeters - RingInnerRadiusMeters) / cell),
                Height: (int)(RingInnerRadiusMeters * 2 / cell),
                UpperLeftX: RingInnerRadiusMeters,
                UpperLeftY: RingInnerRadiusMeters,
                ScaleX: cell,
                ScaleY: -cell,
                Value: RidgeRimElevationMeters,
                AcquisitionDate: ridgeTime,
                CreatedAt: ridgeTime,
                Srid: 3857),
            new RasterSeed(
                Name: "ridge-west",
                Width: (int)((RingOuterRadiusMeters - RingInnerRadiusMeters) / cell),
                Height: (int)(RingInnerRadiusMeters * 2 / cell),
                UpperLeftX: -RingOuterRadiusMeters,
                UpperLeftY: RingInnerRadiusMeters,
                ScaleX: cell,
                ScaleY: -cell,
                Value: RidgeRimElevationMeters,
                AcquisitionDate: ridgeTime,
                CreatedAt: ridgeTime,
                Srid: 3857));
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
