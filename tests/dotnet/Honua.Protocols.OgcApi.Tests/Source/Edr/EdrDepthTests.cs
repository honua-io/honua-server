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
/// Depth tests for the OGC API - EDR surface (#2983): exact conformance classes,
/// position extent/coords validation, parameter-name band filtering, default temporal
/// instant resolution, nodata handling, cube grid sizing (default, resolution-x
/// clamping, single-sample), cube bbox validation, and unknown-collection handling for
/// the cube query. Complements the happy-path coverage in <see cref="EdrEndpointsTests"/>.
/// Raster reads are mocked so all sampling rides the canonical point-sample pipeline
/// deterministically; tests share one server (read-only requests).
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiCoverages)]
public sealed class EdrDepthTests : IClassFixture<EdrDepthTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public EdrDepthTests(EdrDepthTestsFixture fixture)
    {
        _fixture = fixture.App;
    }

    private static string CollectionPath => $"/edr/collections/{WebAppFixture.TestLayerId}";

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /edr/conformance")]
    public async Task Edr_Conformance_DeclaresCorePositionCubeAndCovJsonClasses()
    {
        using var doc = await GetJsonAsync("/edr/conformance");
        var conformsTo = doc.RootElement.GetProperty("conformsTo")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/core");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/position");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/cube");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/json");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/covjson");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /edr/collections/{collectionId}")]
    public async Task Edr_Collection_ExposesParameterNamesExtentAndOutputFormats()
    {
        using var doc = await GetJsonAsync(CollectionPath);
        var root = doc.RootElement;

        var parameterNames = root.GetProperty("parameter_names").EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        parameterNames.Should().BeEquivalentTo("band_1", "band_2", "band_3");

        var bbox = root.GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0]
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        bbox.Should().Equal(-122.5, 37.7, -122.3, 37.9);

        root.GetProperty("output_formats").EnumerateArray()
            .Select(value => value.GetString())
            .Should().Equal("CoverageJSON");

        var dataQueries = root.GetProperty("data_queries");
        dataQueries.GetProperty("position").GetProperty("link").GetProperty("href").GetString()
            .Should().EndWith("/position");
        dataQueries.GetProperty("cube").GetProperty("link").GetProperty("href").GetString()
            .Should().EndWith("/cube");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_OutsideCollectionExtent_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"{CollectionPath}/position?coords={Uri.EscapeDataString("POINT(0 0)")}");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("outside the collection spatial extent");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_MissingOrNonPointCoords_ReturnsBadRequest()
    {
        var missing = await _fixture.Client.GetAsync($"{CollectionPath}/position");
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var lineString = await _fixture.Client.GetAsync(
            $"{CollectionPath}/position?coords={Uri.EscapeDataString("LINESTRING(-122.4 37.8, -122.3 37.9)")}");
        lineString.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_ParameterNameFilter_ReturnsOnlyRequestedBands()
    {
        using var doc = await GetJsonAsync(
            $"{CollectionPath}/position?coords={Uri.EscapeDataString("POINT(-122.4 37.8)")}&parameter-name=band_2,band_3");

        var ranges = doc.RootElement.GetProperty("ranges");
        ranges.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("band_2", "band_3");
        ranges.GetProperty("band_2").GetProperty("values")[0].GetDouble().Should().Be(12.0);
        ranges.GetProperty("band_3").GetProperty("values")[0].GetDouble().Should().Be(13.0);

        doc.RootElement.GetProperty("parameters").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("band_2", "band_3");
    }

    [IntegrationTest(Skip = "Candidate product bug (#2983 depth recon): an unknown parameter-name " +
        "silently falls back to band_1 data instead of rejecting the request, so a client asking " +
        "for a parameter the collection does not offer receives band_1 values labeled band_1. " +
        "EdrHandler.BuildParameters inserts a band_1 fallback whenever the requested names match " +
        "nothing. Needs a separate ticket; enable once the handler returns 400 for unknown names.")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_UnknownParameterName_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"{CollectionPath}/position?coords={Uri.EscapeDataString("POINT(-122.4 37.8)")}&parameter-name=temperature");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_WithoutDatetime_UsesRasterTimestamp()
    {
        using var doc = await GetJsonAsync(
            $"{CollectionPath}/position?coords={Uri.EscapeDataString("POINT(-122.4 37.8)")}");

        var timeValues = doc.RootElement.GetProperty("domain").GetProperty("axes").GetProperty("t")
            .GetProperty("values")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        timeValues.Should().Equal(EdrDepthTestsFixture.RasterCreatedAtIso);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_NoDataPixel_ReturnsNullValues()
    {
        // The fixture's raster store reports no band values at the nodata longitude.
        using var doc = await GetJsonAsync(
            $"{CollectionPath}/position?coords={Uri.EscapeDataString($"POINT({EdrDepthTestsFixture.NoDataLongitude} 37.8)")}");

        var values = doc.RootElement.GetProperty("ranges").GetProperty("band_1").GetProperty("values")
            .EnumerateArray()
            .ToArray();
        values.Should().HaveCount(1);
        values[0].ValueKind.Should().Be(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_WithoutResolution_ReturnsDefaultEightByEightGrid()
    {
        var response = await _fixture.Client.GetAsync(
            $"{CollectionPath}/cube?bbox=-122.5,37.7,-122.3,37.9");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/prs.coveragejson+json");

        using var doc = JsonDocument.Parse(content);
        var range = doc.RootElement.GetProperty("ranges").GetProperty("band_1");
        range.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32()).Should().Equal(8, 8);
        range.GetProperty("values").GetArrayLength().Should().Be(64);

        // Cell-centre sampling keeps every axis value strictly inside the bbox.
        var xValues = doc.RootElement.GetProperty("domain").GetProperty("axes").GetProperty("x")
            .GetProperty("values")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        xValues.Should().HaveCount(8);
        xValues.Should().OnlyContain(x => x > -122.5 && x < -122.3);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_ResolutionX_IsClampedToMaximumSampleCount()
    {
        using var doc = await GetJsonAsync(
            $"{CollectionPath}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=200");

        var range = doc.RootElement.GetProperty("ranges").GetProperty("band_1");
        range.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32()).Should().Equal(50, 50);
        range.GetProperty("values").GetArrayLength().Should().Be(2500);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_ResolutionXOne_ReturnsSingleCentreSample()
    {
        using var doc = await GetJsonAsync(
            $"{CollectionPath}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=1");

        var range = doc.RootElement.GetProperty("ranges").GetProperty("band_1");
        range.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32()).Should().Equal(1, 1);
        range.GetProperty("values").GetArrayLength().Should().Be(1);

        var axes = doc.RootElement.GetProperty("domain").GetProperty("axes");
        axes.GetProperty("x").GetProperty("values")[0].GetDouble().Should().BeApproximately(-122.4, 1e-9);
        axes.GetProperty("y").GetProperty("values")[0].GetDouble().Should().BeApproximately(37.8, 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_ParameterNameFilter_ReturnsOnlyRequestedBand()
    {
        using var doc = await GetJsonAsync(
            $"{CollectionPath}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=2&parameter-name=band_3");

        var ranges = doc.RootElement.GetProperty("ranges");
        ranges.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("band_3");
        ranges.GetProperty("band_3").GetProperty("values").EnumerateArray()
            .Should().OnlyContain(value => value.GetDouble() == 13.0);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_MalformedOrDegenerateBbox_ReturnsBadRequest()
    {
        var invalidBboxes = new[]
        {
            "10,10,5,20",     // maxX < minX (EDR cube bbox does not wrap the antimeridian)
            "10,20,20,10",    // maxY < minY
            "0,0,0,0",        // degenerate
            "1,2,3",          // three ordinates
            "a,b,c,d"         // non-numeric
        };

        foreach (var bbox in invalidBboxes)
        {
            var response = await _fixture.Client.GetAsync($"{CollectionPath}/cube?bbox={bbox}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"bbox '{bbox}' must be rejected");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_UnknownCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/edr/collections/9999999/cube?bbox=-122.5,37.7,-122.3,37.9");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<JsonDocument> GetJsonAsync(string uri)
    {
        var response = await _fixture.Client.GetAsync(uri);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        return JsonDocument.Parse(content);
    }
}

/// <summary>
/// Shared server fixture for <see cref="EdrDepthTests"/>: a mocked <see cref="IRasterStore"/>
/// serving a 3-band coverage with a fixed creation timestamp (so the default EDR time
/// instant is deterministic) and a designated nodata longitude that yields no band values.
/// </summary>
public sealed class EdrDepthTestsFixture : IAsyncLifetime
{
    /// <summary>Longitude at which the mocked raster store reports a nodata pixel.</summary>
    public const double NoDataLongitude = -122.31;

    /// <summary>The mocked raster's creation instant in EDR ISO-8601 form.</summary>
    public const string RasterCreatedAtIso = "2026-01-05T00:00:00Z";

    private const long TestRasterId = 741;

    public EdrDepthTestsFixture()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        ConfigureRasterStore(rasterStore);
        App = new WebAppFixture().ReplaceService(rasterStore);
    }

    public WebAppFixture App { get; }

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();

    private static void ConfigureRasterStore(IRasterStore rasterStore)
    {
        var raster = CreateRasterInfo();

        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(null));
        rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));

        rasterStore.GetExtentAsync(WebAppFixture.TestLayerId, TestRasterId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterExtent?>(raster.Extent));

        // Deterministic point-sample: band_n returns 10 + n at every coordinate, except the
        // designated nodata longitude which reports no band values at all.
        rasterStore.IdentifyAsync(
                WebAppFixture.TestLayerId,
                TestRasterId,
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<int?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var x = call.ArgAt<double>(2);
                var y = call.ArgAt<double>(3);
                var isNoData = Math.Abs(x - NoDataLongitude) < 1e-9;
                return Task.FromResult(new PixelValueResult
                {
                    X = x,
                    Y = y,
                    Srid = 4326,
                    HasData = !isNoData,
                    BandValues = isNoData
                        ? new Dictionary<int, object?>()
                        : new Dictionary<int, object?> { [1] = 11.0, [2] = 12.0, [3] = 13.0 }
                });
            });
    }

    private static RasterInfo CreateRasterInfo()
        => new()
        {
            Id = TestRasterId,
            LayerId = WebAppFixture.TestLayerId,
            Name = "test-coverage-edr-depth",
            Width = 64,
            Height = 64,
            BandCount = 3,
            PixelType = "32BF",
            Srid = 4326,
            NoDataValue = -9999,
            GeoTransform = [-122.5, 0.003125, 0, 37.9, 0, -0.003125],
            Extent = new RasterExtent { XMin = -122.5, YMin = 37.7, XMax = -122.3, YMax = 37.9, Srid = 4326 },
            CreatedAt = DateTimeOffset.Parse(RasterCreatedAtIso, CultureInfo.InvariantCulture)
        };
}
