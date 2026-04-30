// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Coverages;

[Collection("Database")]
[Protocol(TestProtocols.OgcApiCoverages)]
public sealed class OgcCoveragesEndpointsTests : IAsyncLifetime
{
    private const long TestRasterId = 521;
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly List<RasterQuery> _exportQueries = [];
    private readonly WebAppFixture _fixture;

    public OgcCoveragesEndpointsTests()
    {
        ConfigureRasterStore(_rasterStore, _exportQueries);
        _fixture = new WebAppFixture().ReplaceService(_rasterStore);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/coverages")]
    [Endpoint("GET /ogc/coverages/conformance")]
    [Endpoint("GET /ogc/coverages/api")]
    [Endpoint("GET /ogc/coverages/openapi.json")]
    [Endpoint("GET /ogc/coverages/collections")]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}")]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/schema")]
    public async Task Coverages_MetadataEndpoints_ReturnOgcCoverageDocuments()
    {
        var landing = await _fixture.Client.GetAsync("/ogc/coverages");
        var landingContent = await landing.Content.ReadAsStringAsync();
        landing.StatusCode.Should().Be(HttpStatusCode.OK, landingContent);
        landingContent.Should().Contain("Honua OGC API Coverages");

        var conformance = await _fixture.Client.GetAsync("/ogc/coverages/conformance");
        var conformanceContent = await conformance.Content.ReadAsStringAsync();
        conformance.StatusCode.Should().Be(HttpStatusCode.OK, conformanceContent);
        conformanceContent.Should().Contain("ogcapi-coverages");

        var api = await _fixture.Client.GetAsync("/ogc/coverages/api");
        var apiContent = await api.Content.ReadAsStringAsync();
        api.StatusCode.Should().Be(HttpStatusCode.OK, apiContent);
        api.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.oai.openapi+json");
        apiContent.Should().Contain("/collections/{collectionId}/coverage");

        var openApi = await _fixture.Client.GetAsync("/ogc/coverages/openapi.json");
        openApi.StatusCode.Should().Be(HttpStatusCode.OK);

        using var collectionsDocument = await GetJsonAsync("/ogc/coverages/collections");
        var collections = collectionsDocument.RootElement.GetProperty("collections").EnumerateArray().ToArray();
        collections.Should().ContainSingle();
        collections[0].GetProperty("id").GetString().Should().Be(WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture));
        collections[0].GetProperty("itemType").GetString().Should().Be("coverage");
        collections[0].GetProperty("links").EnumerateArray()
            .Should().Contain(link => link.GetProperty("href").GetString()!.EndsWith("/coverage", StringComparison.Ordinal));

        using var collectionDocument = await GetJsonAsync($"/ogc/coverages/collections/{WebAppFixture.TestLayerId}");
        var collection = collectionDocument.RootElement;
        collection.GetProperty("itemType").GetString().Should().Be("coverage");
        collection.GetProperty("storageCrs").GetString().Should().Contain("4326");
        collection.TryGetProperty("storageCrsBbox", out _).Should().BeFalse();
        var crsValues = collection.GetProperty("crs").EnumerateArray()
            .Select(crs => crs.GetString())
            .ToArray();
        crsValues.Should().Contain("http://www.opengis.net/def/crs/OGC/1.3/CRS84");
        crsValues.Should().Contain("http://www.opengis.net/def/crs/EPSG/0/4326");
        crsValues.Should().Contain("http://www.opengis.net/def/crs/EPSG/0/3857");

        var spatialExtent = collection.GetProperty("extent").GetProperty("spatial");
        spatialExtent.GetProperty("bbox").EnumerateArray()
            .Should().ContainSingle();
        var storageCrsBbox = spatialExtent.GetProperty("storageCrsBbox").EnumerateArray().ToArray();
        storageCrsBbox.Should().ContainSingle();
        storageCrsBbox[0].EnumerateArray().Should().HaveCount(4);
        collection.GetProperty("grid").GetProperty("width").GetInt32().Should().Be(64);
        collection.GetProperty("defaultFields").EnumerateArray().Select(field => field.GetString())
            .Should().Equal("band_1", "band_2", "band_3");

        using var schemaDocument = await GetJsonAsync($"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/schema");
        var properties = schemaDocument.RootElement.GetProperty("properties");
        properties.TryGetProperty("band_1", out var band1).Should().BeTrue();
        band1.GetProperty("x-ogc-propertySeq").GetInt32().Should().Be(1);
        band1.GetProperty("type").GetString().Should().Be("number");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_GetCoverage_DefaultGeoTiffWithBbox_ReturnsRasterBytesAndHeaders()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?bbox=-122.5,37.7,-122.3,37.9");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/tiff");
        bytes.Should().Equal([0x49, 0x49, 0x2A, 0x00]);
        response.Headers.TryGetValues("Content-Bbox", out var bboxes).Should().BeTrue();
        bboxes!.Single().Should().Contain("-122.5");

        _exportQueries.Should().ContainSingle();
        _exportQueries.Single().OutputFormat.Should().Be(RasterFormat.TIFF);
        _exportQueries.Single().ClipRegion.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_GetCoverage_PropertiesCrsAndScale_MapToRasterQuery()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?f=geotiff&properties=band_3,band_1&crs=EPSG:3857&scale-size=Lon(32),Lat(16)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Content-Crs", out var contentCrs).Should().BeTrue();
        contentCrs!.Single().Should().Be("<https://www.opengis.net/def/crs/EPSG/0/3857>");
        response.Headers.TryGetValues("Link", out var links).Should().BeTrue();
        var decodedLinks = Uri.UnescapeDataString(links!.Single());
        decodedLinks.Should().Contain("properties=band_3,band_1");
        decodedLinks.Should().Contain("crs=EPSG:3857");
        decodedLinks.Should().Contain("scale-size=Lon(32),Lat(16)");
        decodedLinks.Should().Contain("f=geotiff");
        decodedLinks.Should().Contain("f=png");

        _exportQueries.Should().ContainSingle();
        var query = _exportQueries.Single();
        query.OutputFormat.Should().Be(RasterFormat.TIFF);
        query.OutputSrid.Should().Be(3857);
        query.Bands.Should().Equal(3, 1);
        query.OutputWidth.Should().Be(32);
        query.OutputHeight.Should().Be(16);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_GetCoverage_UnsupportedParameters_ReturnProblemDetails()
    {
        var invalidRequests = new[]
        {
            ("datetime=2026-04-29T00:00:00Z", "datetime"),
            ("subset=x(1,2)", "subset"),
            ("scale-axes=x(64)", "scale-axes"),
            ("f=netcdf", "NetCDF"),
            ("properties=missing", "missing"),
            ("crs=EPSG:0", "crs"),
            ("crs=EPSG:999999", "Unsupported CRS"),
            ("bbox=-122.5,37.7,-122.3,37.9&bbox-crs=EPSG:999999", "Unsupported CRS"),
            ("bbox=-122.5,37.7,-122.3", "bbox"),
            ("resolution=0.000001", "8192"),
            ("scale-factor=0.001", "8192")
        };

        foreach (var (query, expectedDetail) in invalidRequests)
        {
            var response = await _fixture.Client.GetAsync(
                $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?{query}");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
            content.Should().Contain(expectedDetail);
        }

        _exportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_GetCoverage_AcceptPng_ReturnsPng()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        _exportQueries.Should().ContainSingle();
        _exportQueries.Single().OutputFormat.Should().Be(RasterFormat.PNG);
    }

    private async Task<JsonDocument> GetJsonAsync(string uri)
    {
        var response = await _fixture.Client.GetAsync(uri);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        return JsonDocument.Parse(content);
    }

    private static void ConfigureRasterStore(IRasterStore rasterStore, List<RasterQuery> exportQueries)
    {
        var raster = CreateRasterInfo();

        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(null));
        rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));

        rasterStore.GetExtentAsync(WebAppFixture.TestLayerId, TestRasterId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterExtent?>(raster.Extent));

        rasterStore.GetStatisticsAsync(
                WebAppFixture.TestLayerId,
                TestRasterId,
                Arg.Any<int[]?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[]
            {
                new RasterStatistics { Band = 1, MinValue = 1, MaxValue = 10, MeanValue = 5, StandardDeviation = 1.5, ValidPixelCount = 4096 },
                new RasterStatistics { Band = 2, MinValue = 2, MaxValue = 20, MeanValue = 10, StandardDeviation = 2.5, ValidPixelCount = 4096 },
                new RasterStatistics { Band = 3, MinValue = 3, MaxValue = 30, MeanValue = 15, StandardDeviation = 3.5, ValidPixelCount = 4096 }
            }));

        rasterStore.ExportImageAsync(
                WebAppFixture.TestLayerId,
                TestRasterId,
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<RasterQuery>(2);
                exportQueries.Add(query);
                return Task.FromResult(new RasterResult
                {
                    Data = query.OutputFormat == RasterFormat.PNG
                        ? [0x89, 0x50, 0x4E, 0x47]
                        : [0x49, 0x49, 0x2A, 0x00],
                    ContentType = query.OutputFormat.ToContentType(),
                    Width = query.OutputWidth ?? 64,
                    Height = query.OutputHeight ?? 64,
                    Srid = query.OutputSrid ?? 4326,
                    Extent = new RasterExtent
                    {
                        XMin = -122.5,
                        YMin = 37.7,
                        XMax = -122.3,
                        YMax = 37.9,
                        Srid = query.OutputSrid ?? 4326
                    },
                    BandCount = query.Bands?.Length ?? 3,
                    PixelType = "32BF"
                });
            });
    }

    private static RasterInfo CreateRasterInfo()
        => new()
        {
            Id = TestRasterId,
            LayerId = WebAppFixture.TestLayerId,
            Name = "test-coverage",
            Width = 64,
            Height = 64,
            BandCount = 3,
            PixelType = "32BF",
            Srid = 4326,
            NoDataValue = -9999,
            GeoTransform = [-122.5, 0.003125, 0, 37.9, 0, -0.003125],
            Extent = new RasterExtent
            {
                XMin = -122.5,
                YMin = 37.7,
                XMax = -122.3,
                YMax = 37.9,
                Srid = 4326
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
}
