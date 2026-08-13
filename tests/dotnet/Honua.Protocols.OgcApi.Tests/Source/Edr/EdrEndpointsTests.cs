// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Edr;

/// <summary>
/// Integration tests for the OGC API - Environmental Data Retrieval (EDR) surface (#1757):
/// collections discovery, the position (point time-series) query, and the cube (area/subset)
/// query over the registered coverage collection, returning CoverageJSON. Raster reads are
/// mocked so position/cube exercise the canonical point-sample pipeline deterministically.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiCoverages)]
public sealed class EdrEndpointsTests : IAsyncLifetime
{
    private const long TestRasterId = 521;
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly WebAppFixture _fixture;

    public EdrEndpointsTests()
    {
        ConfigureRasterStore(_rasterStore);
        _fixture = new WebAppFixture().ReplaceService(_rasterStore);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /edr")]
    [Endpoint("GET /edr/conformance")]
    [Endpoint("GET /edr/collections")]
    [Endpoint("GET /edr/collections/{collectionId}")]
    public async Task Edr_MetadataEndpoints_ExposeCollectionsWithPositionAndCubeQueries()
    {
        var landing = await _fixture.Client.GetAsync("/edr");
        landing.StatusCode.Should().Be(HttpStatusCode.OK);

        var conformance = await _fixture.Client.GetAsync("/edr/conformance");
        var conformanceContent = await conformance.Content.ReadAsStringAsync();
        conformance.StatusCode.Should().Be(HttpStatusCode.OK);
        conformanceContent.Should().Contain("ogcapi-edr");

        using var collectionsDoc = await GetJsonAsync("/edr/collections");
        var collections = collectionsDoc.RootElement.GetProperty("collections").EnumerateArray().ToArray();
        collections.Should().ContainSingle();
        var collectionId = collections[0].GetProperty("id").GetString();
        collectionId.Should().Be(WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture));

        using var collectionDoc = await GetJsonAsync($"/edr/collections/{WebAppFixture.TestLayerId}");
        var dataQueries = collectionDoc.RootElement.GetProperty("data_queries");
        dataQueries.TryGetProperty("position", out _).Should().BeTrue();
        dataQueries.TryGetProperty("cube", out _).Should().BeTrue();
        collectionDoc.RootElement.GetProperty("parameter_names").TryGetProperty("band_1", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_ReturnsCoverageJsonPointSeries()
    {
        var response = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)&datetime=2026-06-20T00:00:00Z");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/prs.coveragejson+json");

        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("type").GetString().Should().Be("Coverage");
        doc.RootElement.GetProperty("domain").GetProperty("domainType").GetString().Should().Be("PointSeries");
        doc.RootElement.GetProperty("domain").GetProperty("axes").GetProperty("t").GetProperty("values")
            .EnumerateArray().First().GetString().Should().Contain("2026-06-20");

        var range = doc.RootElement.GetProperty("ranges").GetProperty("band_1");
        range.GetProperty("values").EnumerateArray().First().GetDouble().Should().Be(11.0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_ReturnsCoverageJsonGridSubset()
    {
        var response = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=2");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("domain").GetProperty("domainType").GetString().Should().Be("Grid");
        var range = doc.RootElement.GetProperty("ranges").GetProperty("band_1");
        // 2x2 sampling grid.
        range.GetProperty("shape").EnumerateArray().Select(v => v.GetInt32()).Should().Equal(2, 2);
        range.GetProperty("values").GetArrayLength().Should().Be(4);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_InvalidCoords_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=notapoint");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_MissingBbox_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/cube");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_ValidParameterNameSubset_ReturnsOnlyRequestedParameter()
    {
        using var doc = await GetJsonAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)&parameter-name=band_2");

        doc.RootElement.GetProperty("parameters").EnumerateObject().Select(p => p.Name).Should().Equal("band_2");
        var ranges = doc.RootElement.GetProperty("ranges");
        ranges.EnumerateObject().Select(p => p.Name).Should().Equal("band_2");
        ranges.GetProperty("band_2").GetProperty("values").EnumerateArray().First().GetDouble().Should().Be(12.0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_ValidParameterNameSubset_ReturnsOnlyRequestedParameters()
    {
        using var doc = await GetJsonAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=2&parameter-name=band_1,band_3");

        var ranges = doc.RootElement.GetProperty("ranges");
        ranges.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("band_1", "band_3");
        ranges.GetProperty("band_3").GetProperty("values").EnumerateArray()
            .Select(v => v.GetDouble()).Should().AllBeEquivalentTo(13.0);
    }

    // #3184: an unknown parameter-name previously fell back to band_1, returning a different
    // physical quantity than the client asked for. OGC API - EDR /req/edr/parameter-name-response
    // requires the value to come from the collection's enumerated parameter list and that only
    // the listed parameters be returned, so an unknown name is an invalid query-parameter value.
    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_UnknownParameterName_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)&parameter-name=temperature");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        doc.RootElement.GetProperty("title").GetString().Should().Be("Bad Request");
        var detail = doc.RootElement.GetProperty("detail").GetString();
        detail.Should().Contain("parameter-name").And.Contain("temperature").And.Contain("band_1");

        // The rejected quantity must never leak through as substituted band data.
        content.Should().NotContain("\"ranges\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_UnknownParameterName_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/cube?bbox=-122.5,37.7,-122.3,37.9&parameter-name=temperature");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        doc.RootElement.GetProperty("detail").GetString().Should().Contain("temperature");
        content.Should().NotContain("\"ranges\"");
    }

    // Mixed valid + unknown names are rejected too (documented deterministic choice): a
    // silently narrowed selection is indistinguishable from a complete result to the client.
    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_MixedValidAndUnknownParameterNames_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)&parameter-name=band_1,temperature");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);

        using var doc = JsonDocument.Parse(content);
        var detail = doc.RootElement.GetProperty("detail").GetString();
        detail.Should().Contain("temperature");
        // Only the unknown name is reported as offending; the valid one is not.
        detail.Should().NotContain("offer: band_1");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_MixedValidAndUnknownParameterNames_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/cube?bbox=-122.5,37.7,-122.3,37.9&parameter-name=band_1,temperature");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(400);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}")]
    public async Task Edr_UnknownCollection_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/edr/collections/9999999/position?coords=POINT(-122.4 37.8)");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<JsonDocument> GetJsonAsync(string uri)
    {
        var response = await _fixture.Client.GetAsync(uri);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        return JsonDocument.Parse(content);
    }

    private static void ConfigureRasterStore(IRasterStore rasterStore)
    {
        var raster = CreateRasterInfo();

        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(null));
        rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));

        rasterStore.GetExtentAsync(WebAppFixture.TestLayerId, TestRasterId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterExtent?>(raster.Extent));

        // Deterministic point-sample: band_n returns 10 + n at every coordinate.
        rasterStore.IdentifyAsync(
                WebAppFixture.TestLayerId,
                TestRasterId,
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<int?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new PixelValueResult
            {
                X = call.ArgAt<double>(2),
                Y = call.ArgAt<double>(3),
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 11.0, [2] = 12.0, [3] = 13.0 }
            }));
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
            Extent = new RasterExtent { XMin = -122.5, YMin = 37.7, XMax = -122.3, YMax = 37.9, Srid = 4326 },
            CreatedAt = DateTimeOffset.UtcNow
        };
}
