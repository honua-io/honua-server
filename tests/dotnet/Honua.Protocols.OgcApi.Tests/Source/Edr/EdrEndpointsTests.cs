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
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.UseSetting(
                "Capabilities:Experimental:serve.ogc-api-edr:Enabled", "true"))
            .ReplaceService(_rasterStore);
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
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)&datetime=2026-06-20T00:00:00.4Z");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/prs.coveragejson+json");

        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("type").GetString().Should().Be("Coverage");
        doc.RootElement.GetProperty("domain").GetProperty("domainType").GetString().Should().Be("PointSeries");
        doc.RootElement.GetProperty("domain").GetProperty("axes").GetProperty("t").GetProperty("values")
            .EnumerateArray().Select(value => value.GetString())
            .Should().Equal("2026-06-20T00:00:00.4Z");

        var range = doc.RootElement.GetProperty("ranges").GetProperty("band_1");
        range.GetProperty("values").EnumerateArray().First().GetDouble().Should().Be(11.0);

        // Sub-second precision decides intersection (#4151): the whole second and an interval
        // ending 300 ms before the acquisition instant both select no data.
        foreach (var disjoint in new[] { "2026-06-20T00:00:00Z", "../2026-06-20T00:00:00.100Z" })
        {
            var disjointResponse = await _fixture.Client.GetAsync(
                $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)&datetime={Uri.EscapeDataString(disjoint)}");
            disjointResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, disjoint);
        }
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

    [IntegrationTest]
    [Operation(Operations.Metadata, Operations.Query, Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections")]
    [Endpoint("GET /edr/collections/{collectionId}")]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_WebMercatorRaster_AdvertisesCrs84ExtentAndGatesPositionInCrs84()
    {
        // #4149: RasterInfo.Extent is in the storage SRID. A San Francisco raster stored in EPSG:3857
        // previously advertised its metre bbox labelled CRS84 and rejected every lon/lat position.
        // The expected CRS84 bbox uses the spherical inverse Mercator closed form, independent of
        // the server's transform code.
        const double minX = -13637750;
        const double minY = 4539250;
        const double maxX = -13614250;
        const double maxY = 4560250;
        UsePrimaryRaster(CreateRasterInfo() with
        {
            Srid = 3857,
            GeoTransform = [minX, 367.1875, 0, maxY, 0, -328.125],
            Extent = new RasterExtent { XMin = minX, YMin = minY, XMax = maxX, YMax = maxY, Srid = 3857 }
        });

        var (minLon, minLat) = InverseWebMercator(minX, minY);
        var (maxLon, maxLat) = InverseWebMercator(maxX, maxY);

        using (var collectionDoc = await GetJsonAsync($"/edr/collections/{WebAppFixture.TestLayerId}"))
        {
            AssertCrs84Bbox(collectionDoc.RootElement, minLon, minLat, maxLon, maxLat);
        }

        using (var collectionsDoc = await GetJsonAsync("/edr/collections"))
        {
            AssertCrs84Bbox(collectionsDoc.RootElement.GetProperty("collections")[0], minLon, minLat, maxLon, maxLat);
        }

        using (var positionDoc = await GetJsonAsync(
                   $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)"))
        {
            positionDoc.RootElement.GetProperty("ranges").GetProperty("band_1").GetProperty("values")
                .EnumerateArray().First().GetDouble().Should().Be(11.0);
        }

        await _rasterStore.Received().IdentifyAsync(
            WebAppFixture.TestLayerId,
            TestRasterId,
            -122.4,
            37.8,
            4326,
            null,
            Arg.Any<CancellationToken>());

        // A lon/lat outside the CRS84 extent (Sacramento) is still rejected.
        var outside = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-121.5 38.6)");
        outside.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata, Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}")]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_UtmRaster_AdvertisesCrs84ExtentAndAcceptsLonLatPosition()
    {
        // #4149 for a CRS the in-memory transformer does not cover: a UTM zone 10N (EPSG:32610)
        // raster around San Francisco reprojects through the coordinate transform service.
        UsePrimaryRaster(CreateRasterInfo() with
        {
            Srid = 32610,
            GeoTransform = [540000, 312.5, 0, 4190000, 0, -312.5],
            Extent = new RasterExtent { XMin = 540000, YMin = 4170000, XMax = 560000, YMax = 4190000, Srid = 32610 }
        });

        using (var collectionDoc = await GetJsonAsync($"/edr/collections/{WebAppFixture.TestLayerId}"))
        {
            var spatial = collectionDoc.RootElement.GetProperty("extent").GetProperty("spatial");
            spatial.GetProperty("crs").GetString().Should().Be("http://www.opengis.net/def/crs/OGC/1.3/CRS84");
            var bbox = spatial.GetProperty("bbox")[0].EnumerateArray().Select(v => v.GetDouble()).ToArray();

            // Zone 10N is centred on -123 degrees; eastings 540-560 km near 37.8N sit between
            // roughly -122.55 and -122.32 degrees longitude, northings 4170-4190 km between 37.67N
            // and 37.86N. The bounds must be degrees bracketing the city, never metres.
            bbox[0].Should().BeInRange(-122.6, -122.4);
            bbox[1].Should().BeInRange(37.6, 37.8);
            bbox[2].Should().BeInRange(-122.4, -122.2);
            bbox[3].Should().BeInRange(37.8, 37.95);
        }

        using var positionDoc = await GetJsonAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)");
        positionDoc.RootElement.GetProperty("ranges").GetProperty("band_1").GetProperty("values")
            .EnumerateArray().First().GetDouble().Should().Be(11.0);
    }

    [IntegrationTest]
    [Operation(Operations.Query, Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_PositionAndCube_FormatOutsideOutputFormats_Returns400()
    {
        // #4153: the collection advertises output_formats=[CoverageJSON]. OGC API - Common Part 1
        // (8.1.3) maps any other `f` value to 400 instead of a CoverageJSON 200.
        var position = $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)";
        var cube = $"/edr/collections/{WebAppFixture.TestLayerId}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=1";
        foreach (var format in new[] { "csv", "GeoJSON", "netcdf" })
        {
            var positionResponse = await _fixture.Client.GetAsync($"{position}&f={format}");
            positionResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"position f={format}");
            (await positionResponse.Content.ReadAsStringAsync()).Should().Contain("CoverageJSON");

            var cubeResponse = await _fixture.Client.GetAsync($"{cube}&f={format}");
            cubeResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"cube f={format}");
        }

        await _rasterStore.DidNotReceive().IdentifyAsync(
            Arg.Any<int>(),
            Arg.Any<long>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<int?>(),
            Arg.Any<RasterIdentifyRendering?>(),
            Arg.Any<CancellationToken>());

        var advertised = await _fixture.Client.GetAsync($"{position}&f=CoverageJSON");
        advertised.StatusCode.Should().Be(HttpStatusCode.OK, await advertised.Content.ReadAsStringAsync());
        advertised.Content.Headers.ContentType?.MediaType.Should().Be("application/prs.coveragejson+json");
    }

    [IntegrationTest]
    [Operation(Operations.Query, Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_PositionAndCube_CrsOutsideCollectionCrs_Returns400WithoutSampling()
    {
        // #4153: the collection advertises crs=[CRS84]. EPSG:3857 metres were previously read as
        // CRS84 degrees; they must be rejected before any pixel is sampled.
        var position = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-13625000 4550000)&crs=EPSG:3857");
        position.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await position.Content.ReadAsStringAsync()).Should().Contain("crs");

        var cube = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/cube?bbox=-13637750,4539250,-13614250,4560250&crs=EPSG:3857");
        cube.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await _rasterStore.DidNotReceive().IdentifyAsync(
            Arg.Any<int>(),
            Arg.Any<long>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<int?>(),
            Arg.Any<RasterIdentifyRendering?>(),
            Arg.Any<CancellationToken>());

        // The advertised CRS, by code or by URI, is accepted and still sampled as lon/lat.
        foreach (var crs in new[] { "CRS84", "http://www.opengis.net/def/crs/OGC/1.3/CRS84" })
        {
            using var doc = await GetJsonAsync(
                $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords=POINT(-122.4 37.8)&crs={Uri.EscapeDataString(crs)}");
            doc.RootElement.GetProperty("domain").GetProperty("axes").GetProperty("x").GetProperty("values")[0]
                .GetDouble().Should().Be(-122.4);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_MultiPoint_ReturnsCoverageCollectionSampledAtEachPosition()
    {
        // #4153: EDR 1.1 position examples use coords=MULTIPOINT(...). The store stub derives each
        // band value from the sampled coordinate (band_1 = lon + 200, band_2 = lat), so the expected
        // values below are computed from the request coordinates, not captured from the handler.
        _rasterStore.IdentifyAsync(
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
                BandValues = new Dictionary<int, object?>
                {
                    [1] = call.ArgAt<double>(2) + 200.0,
                    [2] = call.ArgAt<double>(3),
                    [3] = 13.0
                }
            }));

        (double Lon, double Lat)[] positions = [(-122.4, 37.8), (-122.35, 37.75)];
        foreach (var coords in new[]
                 {
                     "MULTIPOINT((-122.4 37.8),(-122.35 37.75))",
                     "MULTIPOINT(-122.4 37.8, -122.35 37.75)"
                 })
        {
            using var doc = await GetJsonAsync(
                $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords={Uri.EscapeDataString(coords)}&parameter-name=band_1,band_2&datetime=2026-06-20T00:00:00Z");
            var root = doc.RootElement;
            root.GetProperty("type").GetString().Should().Be("CoverageCollection", coords);
            root.GetProperty("domainType").GetString().Should().Be("PointSeries");
            root.GetProperty("parameters").EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo("band_1", "band_2");

            var coverages = root.GetProperty("coverages").EnumerateArray().ToArray();
            coverages.Should().HaveCount(positions.Length);
            for (var i = 0; i < positions.Length; i++)
            {
                var (lon, lat) = positions[i];
                var domain = coverages[i].GetProperty("domain");
                domain.GetProperty("domainType").GetString().Should().Be("PointSeries");
                domain.GetProperty("axes").GetProperty("x").GetProperty("values")[0].GetDouble().Should().Be(lon);
                domain.GetProperty("axes").GetProperty("y").GetProperty("values")[0].GetDouble().Should().Be(lat);
                domain.GetProperty("axes").GetProperty("t").GetProperty("values")[0].GetString()
                    .Should().StartWith("2026-06-20");

                var ranges = coverages[i].GetProperty("ranges");
                ranges.GetProperty("band_1").GetProperty("values")[0].GetDouble()
                    .Should().BeApproximately(lon + 200.0, 1e-9);
                ranges.GetProperty("band_2").GetProperty("values")[0].GetDouble()
                    .Should().BeApproximately(lat, 1e-9);
                ranges.TryGetProperty("band_3", out _).Should().BeFalse();
            }
        }

        foreach (var (lon, lat) in positions)
        {
            await _rasterStore.Received(2).IdentifyAsync(
                WebAppFixture.TestLayerId,
                TestRasterId,
                lon,
                lat,
                4326,
                null,
                Arg.Any<CancellationToken>());
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_MultiPointWithMemberOutsideExtent_Returns400()
    {
        var inside = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords={Uri.EscapeDataString("MULTIPOINT((-122.4 37.8),(0 0))")}");
        inside.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await inside.Content.ReadAsStringAsync()).Should().Contain("outside the collection spatial extent");

        var malformed = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords={Uri.EscapeDataString("MULTIPOINT((-122.4 37.8),(-122.3))")}");
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await malformed.Content.ReadAsStringAsync()).Should().Contain("WKT POINT or MULTIPOINT");

        // The parenthesised MULTIPOINT form is exempt from LDAP inspection only while it is pure WKT;
        // filter syntax smuggled into coords is still rejected before the handler runs.
        var filter = await _fixture.Client.GetAsync(
            $"/edr/collections/{WebAppFixture.TestLayerId}/position?coords={Uri.EscapeDataString("MULTIPOINT((-122.4 37.8))(|(uid=*))")}");
        filter.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await filter.Content.ReadAsStringAsync()).Should().Contain("attempt detected in query parameter 'coords'");
    }

    private void UsePrimaryRaster(RasterInfo raster)
    {
        _rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));
    }

    private static (double Lon, double Lat) InverseWebMercator(double x, double y)
    {
        const double earthRadius = 6378137.0;
        return (
            x / earthRadius * 180.0 / Math.PI,
            (2.0 * Math.Atan(Math.Exp(y / earthRadius)) - Math.PI / 2.0) * 180.0 / Math.PI);
    }

    private static void AssertCrs84Bbox(JsonElement collection, double minLon, double minLat, double maxLon, double maxLat)
    {
        var spatial = collection.GetProperty("extent").GetProperty("spatial");
        spatial.GetProperty("crs").GetString().Should().Be("http://www.opengis.net/def/crs/OGC/1.3/CRS84");
        var bbox = spatial.GetProperty("bbox")[0].EnumerateArray().Select(v => v.GetDouble()).ToArray();
        bbox.Should().HaveCount(4);
        bbox[0].Should().BeApproximately(minLon, 1e-9);
        bbox[1].Should().BeApproximately(minLat, 1e-9);
        bbox[2].Should().BeApproximately(maxLon, 1e-9);
        bbox[3].Should().BeApproximately(maxLat, 1e-9);
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
            // The position query's datetime selects this acquisition instant (#4151): it carries
            // sub-second precision, which the t-axis must echo exactly, and differs from CreatedAt
            // so a handler reading the creation date selects nothing.
            AcquisitionDate = new DateTimeOffset(2026, 6, 20, 0, 0, 0, 400, TimeSpan.Zero),
            CreatedAt = DateTimeOffset.UtcNow
        };
}
