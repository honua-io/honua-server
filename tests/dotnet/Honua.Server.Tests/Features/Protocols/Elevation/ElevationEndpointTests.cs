// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Elevation;

[Collection("Database")]
[Protocol(TestProtocols.Elevation)]
public sealed class ElevationEndpointTests : IAsyncLifetime
{
    private const double WebMercatorExtent = 20037508.342789244;
    private const string ElevationProtocolName = "Elevation";

    private static readonly string[] AllProtocols =
    [
        "FeatureServer",
        "MapServer",
        "ImageServer",
        "GPServer",
        "OgcFeatures",
        "OGC-API-Maps",
        "OGC-API-Coverages",
        "OGC-API-Tiles",
        "Wfs20",
        "Wms",
        "Wmts",
        "Wcs",
        "OData",
        "Grpc",
        "Stac",
        "Terrain",
        ElevationProtocolName,
    ];

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_WithCoveredPoint_ReturnsElevationAndSourceMetadata()
    {
        await SeedFullWorldRasterAsync(123.4);

        var response = await _fixture.Client.GetAsync(
            "/elevation/0/value?x=0&y=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("datasetId").GetString().Should().Be("0");
        root.GetProperty("layerId").GetInt32().Should().Be(0);
        root.GetProperty("elevation").GetDouble().Should().BeApproximately(123.4, 0.0001);
        root.GetProperty("noData").GetBoolean().Should().BeFalse();
        root.GetProperty("outOfBounds").GetBoolean().Should().BeFalse();
        root.GetProperty("x").GetDouble().Should().Be(0);
        root.GetProperty("y").GetDouble().Should().Be(0);
        root.GetProperty("querySrid").GetInt32().Should().Be(4326);
        root.GetProperty("mosaicRule").GetString().Should().Be("newest");

        var source = root.GetProperty("source");
        source.GetProperty("rasterCount").GetInt32().Should().Be(1);
        source.GetProperty("sourceSrid").GetInt32().Should().Be(3857);
        source.GetProperty("sourceCrs").GetString().Should().Be("EPSG:3857");
        source.GetProperty("pixelType").GetString().Should().Be("32BF");
        source.GetProperty("verticalUnit").ValueKind.Should().Be(JsonValueKind.Null);
        source.GetProperty("verticalDatum").ValueKind.Should().Be(JsonValueKind.Null);
        source.GetProperty("verticalUnitAssumption").GetString().Should().Contain("meters");
        source.GetProperty("band").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_WithCollectionName_ResolvesDataset()
    {
        await SeedFullWorldRasterAsync(50);

        var response = await _fixture.Client.GetAsync(
            "/elevation/Test%20Layer/value?x=0&y=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("datasetId").GetString().Should().Be("Test Layer");
        json.RootElement.GetProperty("layerId").GetInt32().Should().Be(WebAppFixture.TestLayerId);
        json.RootElement.GetProperty("elevation").GetDouble().Should().BeApproximately(50, 0.0001);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_WithPointOutsideCoverage_ReturnsOutOfBoundsAndNoData()
    {
        await RasterIntegrationTestData.ReplaceLayerRastersAsync(
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
                Value: 50,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: 3857));

        // Sample a coordinate well outside the seeded raster (north-west of equator).
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/value?x=-150&y=80&srid=4326");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("elevation").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("noData").GetBoolean().Should().BeTrue();
        root.GetProperty("outOfBounds").GetBoolean().Should().BeTrue();
        root.GetProperty("source").GetProperty("rasterCount").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_MissingCoordinates_ReturnsBadRequest()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/elevation/0/value?x=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_NonFiniteCoordinate_ReturnsBadRequest()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/value?x=NaN&y=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_UnsupportedSrid_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/value?x=0&y=0&srid=999999");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_DefaultSridLatitudeOutOfRange_ReturnsUnprocessableEntity()
    {
        // Regression: default SRID is 4326. PostGIS geography only accepts SRID
        // 4326, so latitudes outside [-90, 90] must be rejected at the edge as
        // a stable 422 rather than leaking a provider-side geography exception.
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/elevation/0/value?x=0&y=100");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("WGS 84");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_ExplicitWgs84LongitudeOutOfRange_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/elevation/0/value?x=200&y=0&srid=4326");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_UnknownDataset_ReturnsNotFound()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/elevation/999999/value?x=0&y=0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_NoRasterRegistered_ReturnsNotFound()
    {
        await ClearLayerRastersAsync();

        var response = await _fixture.Client.GetAsync("/elevation/0/value?x=0&y=0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("No raster data");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_SourceRasterMissingCrs_ReturnsUnprocessableEntity()
    {
        // Regression: PostgresElevationService must reject source rasters with
        // SRID 0/unset before issuing geometry-transform SQL, otherwise the
        // failure surfaces as a provider-side ST_Transform exception instead
        // of the documented 422 problem response.
        await SeedRasterAsync(srid: 0, value: 1);

        var response = await _fixture.Client.GetAsync("/elevation/0/value?x=0&y=0");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("coordinate reference system");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_WhenElevationProtocolDisabled_ReturnsNotFound()
    {
        await SeedFullWorldRasterAsync(1);
        // V2 cutover (#1035 72/N): protocol gating reads MetadataV2Service.Protocols.
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            enabledProtocols: AllProtocols
                .Where(static protocol => !string.Equals(protocol, ElevationProtocolName, StringComparison.Ordinal))
                .ToArray(),
            accessPolicy: anonymous);

        try
        {
            var response = await _fixture.Client.GetAsync("/elevation/0/value?x=0&y=0");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            _fixture.UpdateV2ServiceMetadata(
                WebAppFixture.TestServiceId,
                enabledProtocols: AllProtocols,
                accessPolicy: anonymous);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_WithCoveredLine_ReturnsOrderedSamples()
    {
        await SeedFullWorldRasterAsync(75);

        var line = "LINESTRING(0 0, 0.5 0.5, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&sampleCount=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("datasetId").GetString().Should().Be("0");
        root.GetProperty("layerId").GetInt32().Should().Be(0);
        root.GetProperty("sampleCount").GetInt32().Should().Be(10);
        root.GetProperty("lineSrid").GetInt32().Should().Be(4326);
        root.GetProperty("mosaicRule").GetString().Should().Be("newest");
        root.GetProperty("isAllNoData").GetBoolean().Should().BeFalse();
        root.GetProperty("lineLengthMeters").GetDouble().Should().BeGreaterThan(0);

        var samples = root.GetProperty("samples");
        samples.GetArrayLength().Should().Be(10);

        double previousDistance = -1;
        foreach (var sample in samples.EnumerateArray())
        {
            var distance = sample.GetProperty("distanceMeters").GetDouble();
            distance.Should().BeGreaterThanOrEqualTo(previousDistance);
            previousDistance = distance;

            sample.GetProperty("noData").GetBoolean().Should().BeFalse();
            sample.GetProperty("elevation").GetDouble().Should().BeApproximately(75, 0.0001);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_WithLineOutsideCoverage_ReturnsAllNoDataSamples()
    {
        await RasterIntegrationTestData.ReplaceLayerRastersAsync(
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
                Value: 50,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: 3857));

        // Line wholly outside coverage region (north-west quadrant).
        var line = "LINESTRING(-150 80, -100 80)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&sampleCount=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("isAllNoData").GetBoolean().Should().BeTrue();
        root.GetProperty("samples").GetArrayLength().Should().Be(5);
        foreach (var sample in root.GetProperty("samples").EnumerateArray())
        {
            sample.GetProperty("noData").GetBoolean().Should().BeTrue();
            sample.GetProperty("elevation").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_WithInvalidWkt_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString("NOT_VALID_WKT")}");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_WithNonLineStringGeometry_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString("POINT(0 0)")}");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("LineString");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_MissingLineParameter_ReturnsBadRequest()
    {
        await SeedFullWorldRasterAsync(1);

        var response = await _fixture.Client.GetAsync("/elevation/0/profile");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_SampleCountAboveLimit_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(1);

        var line = "LINESTRING(0 0, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&sampleCount=999999");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("sampleCount");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_SampleCountBelowMinimum_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(1);

        var line = "LINESTRING(0 0, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&sampleCount=1");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_UnsupportedSrid_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(1);

        var line = "LINESTRING(0 0, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&srid=999999");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_DefaultSridLatitudeOutOfRange_ReturnsUnprocessableEntity()
    {
        // Regression: default SRID is 4326. Reject any LineString vertex with
        // latitude outside [-90, 90] before reaching the PostGIS geography cast.
        await SeedFullWorldRasterAsync(1);

        var line = "LINESTRING(0 100, 1 100)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&sampleCount=5");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("WGS 84");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_ExplicitWgs84LongitudeOutOfRange_ReturnsUnprocessableEntity()
    {
        await SeedFullWorldRasterAsync(1);

        var line = "LINESTRING(-200 0, 0 0)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&srid=4326&sampleCount=5");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_NoRasterRegistered_ReturnsNotFound()
    {
        await ClearLayerRastersAsync();

        var line = "LINESTRING(0 0, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&sampleCount=5");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_SourceRasterMissingCrs_ReturnsUnprocessableEntity()
    {
        // Regression: profile SQL transforms each sample point into the source
        // raster SRID; a raster registered with SRID 0 must be rejected as a
        // 422 problem response before any PostGIS transform runs.
        await SeedRasterAsync(srid: 0, value: 1);

        var line = "LINESTRING(0 0, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&sampleCount=5");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("coordinate reference system");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_DefaultSampleCount_AppliesConfiguredDefault()
    {
        await SeedFullWorldRasterAsync(10);

        var line = "LINESTRING(0 0, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Default sample count from ElevationLimits is 100.
        json.RootElement.GetProperty("sampleCount").GetInt32().Should().Be(100);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_WithProjectedSrid_ReturnsValidGeodesicLength()
    {
        // Regression: profile lines in projected CRSs (e.g. EPSG:3857) must be
        // transformed to WGS84 before any geography cast — PostGIS geography
        // only accepts SRID 4326 input.
        await SeedFullWorldRasterAsync(42);

        // Web Mercator coordinates roughly equivalent to (0,0) → (1,1) in 4326.
        // 1° lon at the equator ≈ 111 195 m → ~157 km diagonal.
        var line = "LINESTRING(0 0, 111319.49 111325.14)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&srid=3857&sampleCount=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("lineSrid").GetInt32().Should().Be(3857);
        root.GetProperty("sampleCount").GetInt32().Should().Be(5);
        root.GetProperty("lineLengthMeters").GetDouble().Should().BeApproximately(157_249, 5_000);
        root.GetProperty("isAllNoData").GetBoolean().Should().BeFalse();

        var samples = root.GetProperty("samples");
        samples.GetArrayLength().Should().Be(5);
        foreach (var sample in samples.EnumerateArray())
        {
            sample.GetProperty("noData").GetBoolean().Should().BeFalse();
            sample.GetProperty("elevation").GetDouble().Should().BeApproximately(42, 0.0001);
        }

        // Distances must be non-decreasing and end at the geodesic length.
        var first = samples[0].GetProperty("distanceMeters").GetDouble();
        var last = samples[samples.GetArrayLength() - 1].GetProperty("distanceMeters").GetDouble();
        first.Should().Be(0);
        last.Should().BeApproximately(root.GetProperty("lineLengthMeters").GetDouble(), 0.0001);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_WithIntervalOnly_DerivesSampleCountFromLineLength()
    {
        // Regression: ?interval was previously ignored (always set to MaxSampleCount).
        // Now the SQL derives effective_count = ceil(length / interval) + 1, capped.
        await SeedFullWorldRasterAsync(7);

        // ~157 km geodesic line. With interval=50 km we expect ceil(157/50)+1 = 5 samples.
        var line = "LINESTRING(0 0, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&interval=50000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        // Roughly 4-6 samples for a ~157 km line at 50 km interval.
        var sampleCount = root.GetProperty("sampleCount").GetInt32();
        sampleCount.Should().BeInRange(4, 6);
        root.GetProperty("samples").GetArrayLength().Should().Be(sampleCount);

        // Smaller intervals should produce more samples than the default of 100
        // would give for this short line.
        var denserResponse = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&interval=1000");
        denserResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var denser = JsonDocument.Parse(await denserResponse.Content.ReadAsStringAsync());
        denser.RootElement.GetProperty("sampleCount").GetInt32().Should().BeGreaterThan(sampleCount);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevationProfile_WithIntervalAndSampleCount_PrefersExplicitSampleCount()
    {
        await SeedFullWorldRasterAsync(7);

        var line = "LINESTRING(0 0, 1 1)";
        var response = await _fixture.Client.GetAsync(
            $"/elevation/0/profile?line={Uri.EscapeDataString(line)}&sampleCount=12&interval=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Explicit sampleCount wins; interval is ignored when both are supplied.
        json.RootElement.GetProperty("sampleCount").GetInt32().Should().Be(12);
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

    private Task SeedRasterAsync(int srid, double value)
        => RasterIntegrationTestData.ReplaceLayerRastersAsync(
            _fixture,
            WebAppFixture.TestLayerId,
            new RasterSeed(
                Name: $"srid-{srid}-dem",
                Width: 2,
                Height: 2,
                UpperLeftX: -WebMercatorExtent,
                UpperLeftY: WebMercatorExtent,
                ScaleX: WebMercatorExtent,
                ScaleY: -WebMercatorExtent,
                Value: value,
                AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
                CreatedAt: RasterIntegrationTestData.WestAcquisition,
                Srid: srid));

    private async Task ClearLayerRastersAsync()
    {
        var schemaName = _fixture.CurrentSchema
            ?? throw new InvalidOperationException("Fixture schema is not initialized.");

        await using var connection = await _fixture.Postgres.GetConnectionAsync(schemaName);
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM honua.raster_data WHERE layer_id = @layerId;";
        delete.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        await delete.ExecuteNonQueryAsync();
    }
}
