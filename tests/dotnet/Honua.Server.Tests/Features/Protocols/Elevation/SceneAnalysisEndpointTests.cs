// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Tests.Features.Licensing;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Elevation;

/// <summary>
/// Integration coverage for the Pro-tier scene analysis endpoints (#1204):
/// <c>POST /elevation/{datasetId}/sun-shadow</c> and
/// <c>POST /elevation/{datasetId}/slice</c>. The sun/shadow + slice math itself
/// is unit-tested in <c>Honua.Core.Tests</c>; these tests exercise the HTTP
/// pipeline (license gate, validation, JSON contract) against a seeded DEM.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Elevation)]
public sealed class SceneAnalysisEndpointTests : IAsyncLifetime
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
    [Endpoint("POST /elevation/{datasetId}/sun-shadow")]
    public async Task PostSunShadow_DaytimeSun_CastsShadowOverSurface()
    {
        await SeedFullWorldRasterAsync(0);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/sun-shadow",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                observerHeight = 25.0,
                timestamp = "2026-03-20T08:00:00Z",
                maxShadowLengthMeters = 20000.0,
                sampleCount = 256
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("datasetId").GetString().Should().Be("0");
        root.GetProperty("shadowCast").GetBoolean().Should().BeTrue();
        root.GetProperty("solarPosition").GetProperty("aboveHorizon").GetBoolean().Should().BeTrue();
        root.GetProperty("solarPosition").GetProperty("altitudeDegrees").GetDouble().Should().BeGreaterThan(0);
        root.GetProperty("shadowLengthMeters").GetDouble().Should().BeGreaterThan(0);
        root.GetProperty("observerTopElevation").GetDouble().Should().BeApproximately(25.0, 0.0001);
        root.GetProperty("samples").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /elevation/{datasetId}/sun-shadow")]
    public async Task PostSunShadow_SunBelowHorizon_ReportsNoShadow()
    {
        await SeedFullWorldRasterAsync(0);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/sun-shadow",
            new
            {
                observerLon = 0.0,
                observerLat = 51.5,
                observerHeight = 10.0,
                // Local midnight at longitude 0 in winter => sun below horizon.
                timestamp = "2026-01-01T00:00:00Z",
                maxShadowLengthMeters = 1000.0
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("shadowCast").GetBoolean().Should().BeFalse();
        root.GetProperty("solarPosition").GetProperty("aboveHorizon").GetBoolean().Should().BeFalse();
        root.GetProperty("shadowLengthMeters").GetDouble().Should().Be(0);
        root.GetProperty("noShadowReason").GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/sun-shadow")]
    public async Task PostSunShadow_MissingTimestamp_ReturnsBadRequest()
    {
        await SeedFullWorldRasterAsync(0);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/sun-shadow",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                maxShadowLengthMeters = 1000.0
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/sun-shadow")]
    public async Task PostSunShadow_WithoutProLicense_ReturnsPaymentRequired()
    {
        await using var community = new WebAppFixture().WithTestLicense(HonuaEdition.Community);
        await community.InitializeAsync();

        var response = await community.Client.PostAsJsonAsync(
            "/elevation/0/sun-shadow",
            new
            {
                observerLon = 0.0,
                observerLat = 0.0,
                observerHeight = 10.0,
                timestamp = "2026-03-20T12:00:00Z",
                maxShadowLengthMeters = 1000.0
            });

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /elevation/{datasetId}/slice")]
    public async Task PostSlice_OverSurface_ReturnsIntersectionMetadata()
    {
        await SeedFullWorldRasterAsync(123.0);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/slice",
            new
            {
                startLon = 0.0,
                startLat = 0.0,
                endLon = 1.0,
                endLat = 1.0,
                sampleCount = 25
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("datasetId").GetString().Should().Be("0");
        root.GetProperty("layerId").GetInt32().Should().Be(0);
        root.GetProperty("sampleCount").GetInt32().Should().Be(25);
        root.GetProperty("lengthMeters").GetDouble().Should().BeGreaterThan(0);
        root.GetProperty("minElevation").GetDouble().Should().BeApproximately(123.0, 0.0001);
        root.GetProperty("maxElevation").GetDouble().Should().BeApproximately(123.0, 0.0001);
        root.GetProperty("reliefMeters").GetDouble().Should().BeApproximately(0.0, 0.0001);
        root.GetProperty("hasNoDataSamples").GetBoolean().Should().BeFalse();

        var samples = root.GetProperty("samples");
        samples.GetArrayLength().Should().Be(25);

        double previousDistance = -1;
        foreach (var sample in samples.EnumerateArray())
        {
            var distance = sample.GetProperty("distanceMeters").GetDouble();
            distance.Should().BeGreaterThanOrEqualTo(previousDistance);
            previousDistance = distance;
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/slice")]
    public async Task PostSlice_DegeneratePlane_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(0);

        var response = await _fixture.Client.PostAsJsonAsync(
            "/elevation/0/slice",
            new
            {
                startLon = 0.0,
                startLat = 0.0,
                endLon = 0.0,
                endLat = 0.0,
                sampleCount = 10
            });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /elevation/{datasetId}/slice")]
    public async Task PostSlice_WithoutProLicense_ReturnsPaymentRequired()
    {
        await using var community = new WebAppFixture().WithTestLicense(HonuaEdition.Community);
        await community.InitializeAsync();

        var response = await community.Client.PostAsJsonAsync(
            "/elevation/0/slice",
            new
            {
                startLon = 0.0,
                startLat = 0.0,
                endLon = 1.0,
                endLat = 1.0
            });

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
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
}
