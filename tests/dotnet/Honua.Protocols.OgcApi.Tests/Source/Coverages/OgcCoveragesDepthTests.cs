// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Coverages;

/// <summary>
/// Depth tests for the OGC API Coverages surface (#2983): Accept-header negotiation
/// (406, q-values, wildcards, f-parameter precedence), scaling-parameter exclusivity and
/// bounds, properties/band selection error paths, response headers, unknown-parameter and
/// unknown-collection handling. Complements the happy-path coverage in
/// <see cref="OgcCoveragesEndpointsTests"/>. Tests share one server; the export-query log
/// is cleared per test (xUnit runs same-class tests sequentially).
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiCoverages)]
public sealed class OgcCoveragesDepthTests : IClassFixture<OgcCoveragesDepthTestsFixture>
{
    private readonly OgcCoveragesDepthTestsFixture _fixture;

    public OgcCoveragesDepthTests(OgcCoveragesDepthTestsFixture fixture)
    {
        _fixture = fixture;
        _fixture.ExportQueries.Clear();
    }

    private HttpClient Client => _fixture.App.Client;

    private static string CoveragePath => $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage";

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_AcceptUnsupportedMediaType_ReturnsNotAcceptable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CoveragePath);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        _fixture.ExportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_AcceptQualityValues_SelectPreferredFormat()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CoveragePath);
        request.Headers.TryAddWithoutValidation("Accept", "image/tiff;q=0.4, image/png;q=0.9");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        _fixture.ExportQueries.Should().ContainSingle();
        _fixture.ExportQueries.Single().OutputFormat.Should().Be(RasterFormat.PNG);
    }

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_AcceptWildcard_DefaultsToGeoTiff()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CoveragePath);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/tiff");
        _fixture.ExportQueries.Should().ContainSingle();
        _fixture.ExportQueries.Single().OutputFormat.Should().Be(RasterFormat.TIFF);
    }

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_FormatParameter_OverridesAcceptHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{CoveragePath}?f=png");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/tiff"));

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        _fixture.ExportQueries.Should().ContainSingle();
        _fixture.ExportQueries.Single().OutputFormat.Should().Be(RasterFormat.PNG);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_UnsupportedFormatParameters_ReturnBadRequest()
    {
        var invalidFormats = new (string Format, string ExpectedDetail)[]
        {
            ("jpeg", "JPEG"),
            ("jpg", "JPEG"),
            ("gif", "Unsupported coverage format"),
            ("json", "Unsupported coverage format")
        };

        foreach (var (format, expectedDetail) in invalidFormats)
        {
            var response = await Client.GetAsync($"{CoveragePath}?f={format}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
            content.Should().Contain(expectedDetail);
        }

        _fixture.ExportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_MultipleScalingParameters_ReturnsBadRequest()
    {
        var conflictingQueries = new[]
        {
            "resolution=0.003125&scale-factor=1",
            "scale-factor=1&scale-size=32,32",
            "resolution=0.003125&scale-size=32,32"
        };

        foreach (var query in conflictingQueries)
        {
            var response = await Client.GetAsync($"{CoveragePath}?{query}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
            content.Should().Contain("only one of resolution, scale-factor, or scale-size");
        }

        _fixture.ExportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_InvalidScaleSize_ReturnsBadRequest()
    {
        var invalidScaleSizes = new[]
        {
            "512",              // single value
            "0,512",            // zero
            "-1,512",           // negative
            "9000,512",         // above the 8192 cap
            "x(32)",            // one axis only
            "y(16),x(32)",      // axes swapped
            "x(32),x(16)"       // duplicate axis
        };

        foreach (var scaleSize in invalidScaleSizes)
        {
            var response = await Client.GetAsync(
                $"{CoveragePath}?scale-size={Uri.EscapeDataString(scaleSize)}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"scale-size '{scaleSize}': {content}");
            content.Should().Contain("scale-size");
        }

        _fixture.ExportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_InvalidProperties_ReturnsBadRequest()
    {
        var invalidProperties = new (string Properties, string ExpectedDetail)[]
        {
            ("band_1,band_1", "more than once"),
            ("band_4", "band range"),
            ("band_0", "band range"),
            ("band_1,,band_2", "comma-separated")
        };

        foreach (var (properties, expectedDetail) in invalidProperties)
        {
            var response = await Client.GetAsync($"{CoveragePath}?properties={properties}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"properties '{properties}': {content}");
            content.Should().Contain(expectedDetail);
        }

        _fixture.ExportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_Wgs84Output_OmitsContentCrsHeader()
    {
        // Content-Crs is only emitted for non-default CRS output; the CRS84/WGS84
        // default carries Content-Bbox and self/alternate links but no Content-Crs.
        var response = await Client.GetAsync($"{CoveragePath}?bbox=-122.5,37.7,-122.3,37.9");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Content-Crs").Should().BeFalse();
        response.Headers.TryGetValues("Content-Bbox", out _).Should().BeTrue();
        response.Headers.TryGetValues("Link", out var links).Should().BeTrue();
        links!.Single().Should().Contain("rel=\"self\"");
        links!.Single().Should().Contain("rel=\"alternate\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections")]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}")]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/schema")]
    public async Task MetadataEndpoints_WithUnknownQueryParameter_ReturnBadRequest()
    {
        var collections = await Client.GetAsync("/ogc/coverages/collections?unsupported=1");
        collections.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var collection = await Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}?bbox=-122.5,37.7,-122.3,37.9");
        collection.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var schema = await Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/schema?properties=band_1");
        schema.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}")]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/schema")]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task CoverageEndpoints_WithUnknownCollection_ReturnNotFound()
    {
        var collection = await Client.GetAsync("/ogc/coverages/collections/9999999");
        collection.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var schema = await Client.GetAsync("/ogc/coverages/collections/9999999/schema");
        schema.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var coverage = await Client.GetAsync("/ogc/coverages/collections/9999999/coverage");
        coverage.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/coverages")]
    [Endpoint("GET /ogc/coverages/collections")]
    public async Task Metadata_FormatParameter_SupportsHtml()
    {
        var landing = await Client.GetAsync("/ogc/coverages?f=html");
        landing.StatusCode.Should().Be(HttpStatusCode.OK);
        landing.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        var collections = await Client.GetAsync("/ogc/coverages/collections?f=html");
        collections.StatusCode.Should().Be(HttpStatusCode.OK);
        collections.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }
}

/// <summary>
/// Depth tests for the OGC API Coverages native-size guard and empty-result mapping
/// (#2983), using a raster whose native grid exceeds the 8192-pixel axis cap.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiCoverages)]
public sealed class OgcCoveragesOversizeDepthTests : IClassFixture<OgcCoveragesOversizeDepthTestsFixture>
{
    private readonly OgcCoveragesOversizeDepthTestsFixture _fixture;

    public OgcCoveragesOversizeDepthTests(OgcCoveragesOversizeDepthTestsFixture fixture)
    {
        _fixture = fixture;
    }

    private static string CoveragePath => $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage";

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_NativeSizeExceedsCap_WithoutSubsetting_ReturnsBadRequest()
    {
        var response = await _fixture.App.Client.GetAsync(CoveragePath);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("8192");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_NativeSizeExceedsCap_WithBboxOrDownsampling_Succeeds()
    {
        var bboxResponse = await _fixture.App.Client.GetAsync(
            $"{CoveragePath}?bbox=-122.41,37.79,-122.39,37.81");
        var bboxContent = await bboxResponse.Content.ReadAsStringAsync();
        bboxResponse.StatusCode.Should().Be(HttpStatusCode.OK, bboxContent);

        var scaledResponse = await _fixture.App.Client.GetAsync($"{CoveragePath}?scale-size=64,64");
        var scaledContent = await scaledResponse.Content.ReadAsStringAsync();
        scaledResponse.StatusCode.Should().Be(HttpStatusCode.OK, scaledContent);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverage_EmptyExportResult_ReturnsNotFound()
    {
        // The fixture's raster store returns an empty payload when only band 2 is
        // selected, modeling a store that produced no data for the request.
        var response = await _fixture.App.Client.GetAsync(
            $"{CoveragePath}?bbox=-122.41,37.79,-122.39,37.81&properties=band_2");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// Shared server fixture for <see cref="OgcCoveragesDepthTests"/>: a mocked
/// <see cref="IRasterStore"/> serving a 3-band 64x64 raster, recording every export query.
/// </summary>
public sealed class OgcCoveragesDepthTestsFixture : IAsyncLifetime
{
    private const long TestRasterId = 731;

    public OgcCoveragesDepthTestsFixture()
    {
        var raster = CoverageDepthRasterStore.CreateRasterInfo(TestRasterId, width: 64, height: 64, pixelSize: 0.003125);
        RasterStore = CoverageDepthRasterStore.Create(raster, ExportQueries, emptyDataForSingleBand: null);
        App = new WebAppFixture().ReplaceService(RasterStore);
    }

    public IRasterStore RasterStore { get; }

    public List<RasterQuery> ExportQueries { get; } = [];

    public WebAppFixture App { get; }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}

/// <summary>
/// Shared server fixture for <see cref="OgcCoveragesOversizeDepthTests"/>: a mocked
/// <see cref="IRasterStore"/> serving a 20000x20000 raster that exceeds the native-size
/// cap, and returning an empty export payload when only band 2 is selected.
/// </summary>
public sealed class OgcCoveragesOversizeDepthTestsFixture : IAsyncLifetime
{
    private const long TestRasterId = 733;

    public OgcCoveragesOversizeDepthTestsFixture()
    {
        var raster = CoverageDepthRasterStore.CreateRasterInfo(TestRasterId, width: 20_000, height: 20_000, pixelSize: 0.00001);
        RasterStore = CoverageDepthRasterStore.Create(raster, ExportQueries, emptyDataForSingleBand: 2);
        App = new WebAppFixture().ReplaceService(RasterStore);
    }

    public IRasterStore RasterStore { get; }

    public List<RasterQuery> ExportQueries { get; } = [];

    public WebAppFixture App { get; }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}

/// <summary>
/// Builds mocked <see cref="IRasterStore"/> instances for the coverage depth fixtures.
/// </summary>
internal static class CoverageDepthRasterStore
{
    public static IRasterStore Create(RasterInfo raster, List<RasterQuery> exportQueries, int? emptyDataForSingleBand)
    {
        var rasterStore = Substitute.For<IRasterStore>();

        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(null));
        rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));

        rasterStore.GetExtentAsync(WebAppFixture.TestLayerId, raster.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterExtent?>(raster.Extent));

        rasterStore.GetStatisticsAsync(
                WebAppFixture.TestLayerId,
                raster.Id,
                Arg.Any<int[]?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[]
            {
                new RasterStatistics { Band = 1, MinValue = 1, MaxValue = 10, MeanValue = 5, StandardDeviation = 1.5, ValidPixelCount = 4096 },
                new RasterStatistics { Band = 2, MinValue = 2, MaxValue = 20, MeanValue = 10, StandardDeviation = 2.5, ValidPixelCount = 4096 },
                new RasterStatistics { Band = 3, MinValue = 3, MaxValue = 30, MeanValue = 15, StandardDeviation = 3.5, ValidPixelCount = 4096 }
            }));

        rasterStore.ExportImageAsync(
                WebAppFixture.TestLayerId,
                raster.Id,
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<RasterQuery>(2);
                exportQueries.Add(query);
                var isEmpty = emptyDataForSingleBand.HasValue &&
                              query.Bands is { Length: 1 } bands &&
                              bands[0] == emptyDataForSingleBand.Value;
                return Task.FromResult(new RasterResult
                {
                    Data = isEmpty
                        ? []
                        : query.OutputFormat == RasterFormat.PNG
                            ? [0x89, 0x50, 0x4E, 0x47]
                            : [0x49, 0x49, 0x2A, 0x00],
                    ContentType = query.OutputFormat.ToContentType(),
                    Width = query.OutputWidth ?? 64,
                    Height = query.OutputHeight ?? 64,
                    Srid = query.OutputSrid ?? 4326,
                    Extent = raster.Extent,
                    BandCount = query.Bands?.Length ?? 3,
                    PixelType = "32BF"
                });
            });

        return rasterStore;
    }

    public static RasterInfo CreateRasterInfo(long rasterId, int width, int height, double pixelSize)
        => new()
        {
            Id = rasterId,
            LayerId = WebAppFixture.TestLayerId,
            Name = "test-coverage-depth",
            Width = width,
            Height = height,
            BandCount = 3,
            PixelType = "32BF",
            Srid = 4326,
            NoDataValue = -9999,
            GeoTransform = [-122.5, pixelSize, 0, 37.9, 0, -pixelSize],
            Extent = new RasterExtent
            {
                XMin = -122.5,
                YMin = 37.9 - (height * pixelSize),
                XMax = -122.5 + (width * pixelSize),
                YMax = 37.9,
                Srid = 4326
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
}
