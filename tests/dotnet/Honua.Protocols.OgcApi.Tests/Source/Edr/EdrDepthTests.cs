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
    private readonly IRasterStore _rasterStore;

    public EdrDepthTests(EdrDepthTestsFixture fixture)
    {
        _fixture = fixture.App;
        _rasterStore = fixture.RasterStore;
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
        ranges.GetProperty("band_2").GetProperty("values")[0].GetDouble()
            .Should().BeApproximately(EdrDepthTestsFixture.ExpectedBandValue(2, -122.4, 37.8), 1e-6);
        ranges.GetProperty("band_3").GetProperty("values")[0].GetDouble()
            .Should().BeApproximately(EdrDepthTestsFixture.ExpectedBandValue(3, -122.4, 37.8), 1e-6);

        doc.RootElement.GetProperty("parameters").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("band_2", "band_3");
    }

    // Enabled once #3184 landed (#3189): EdrHandler validates parameter-name against the
    // collection's enumerated names instead of falling back to band_1 data.
    [IntegrationTest]
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
    [Operation(Operations.Metadata)]
    [Endpoint("GET /edr/collections/{collectionId}")]
    public async Task Edr_Collection_AdvertisesRasterInstantAsTemporalExtent()
    {
        using var doc = await GetJsonAsync(CollectionPath);

        var temporal = doc.RootElement.GetProperty("extent").GetProperty("temporal");
        temporal.GetProperty("interval")[0].EnumerateArray()
            .Select(value => value.GetString())
            .Should().Equal(EdrDepthTestsFixture.RasterCreatedAtIso, EdrDepthTestsFixture.RasterCreatedAtIso);
    }

    // #4151: the raster's only temporal geometry is 2026-01-05T00:00:00Z. Each datetime below
    // intersects that instant, so the pixel values are returned and the t-axis carries the
    // raster's own time — never the requested instant or interval start.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_DatetimeIntersectingRasterTime_ReturnsValuesStampedWithRasterTime()
    {
        var intersecting = new[]
        {
            "2026-01-05T00:00:00Z",
            "2026-01-05T00:00:00.000Z",
            "2025-12-01T00:00:00Z/2026-02-01T00:00:00Z",
            "2026-01-05T00:00:00Z/2026-01-05T00:00:00Z",
            "../2026-01-05T00:00:00Z",
            "2026-01-05T00:00:00Z/..",
            "2026-01-05T09:00:00+09:00"
        };

        foreach (var datetime in intersecting)
        {
            using var doc = await GetJsonAsync(
                $"{CollectionPath}/position?coords={Uri.EscapeDataString("POINT(-122.4 37.8)")}&datetime={Uri.EscapeDataString(datetime)}");

            doc.RootElement.GetProperty("domain").GetProperty("axes").GetProperty("t").GetProperty("values")
                .EnumerateArray()
                .Select(value => value.GetString())
                .Should().Equal([EdrDepthTestsFixture.RasterCreatedAtIso], $"datetime '{datetime}' intersects the raster instant");

            var ranges = doc.RootElement.GetProperty("ranges");
            foreach (var band in new[] { 1, 2, 3 })
            {
                ranges.GetProperty($"band_{band}").GetProperty("values").EnumerateArray()
                    .Select(value => value.GetDouble())
                    .Should().ContainSingle().Which
                    .Should().BeApproximately(
                        EdrDepthTestsFixture.ExpectedBandValue(band, -122.4, 37.8),
                        1e-6,
                        $"datetime '{datetime}' must return the pixel function at the requested coordinate");
            }
        }
    }

    // #4151 repro: a datetime disjoint from the raster instant selects no data. The handler must
    // not sample the raster and stamp the requested time on its values; it answers 204.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_DatetimeDisjointFromRasterTime_ReturnsNoContentWithoutSampling()
    {
        var disjoint = new[]
        {
            "1999-01-01T00:00:00Z",
            "1999-01-01T00:00:00Z/2000-01-01T00:00:00Z",
            "2026-01-05T00:00:01Z",
            "2026-01-05T00:00:01Z/..",
            "../2026-01-04T23:59:59Z",
            // The intersection is exact: a millisecond either side of the instant misses it.
            "2026-01-05T00:00:00.001Z",
            "../2026-01-04T23:59:59.999Z"
        };

        foreach (var datetime in disjoint)
        {
            _rasterStore.ClearReceivedCalls();

            var response = await _fixture.Client.GetAsync(
                $"{CollectionPath}/position?coords={Uri.EscapeDataString("POINT(-122.4 37.8)")}&datetime={Uri.EscapeDataString(datetime)}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"datetime '{datetime}' is disjoint from the raster instant: {content}");
            content.Should().BeEmpty();
            await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyAsync(
                default, default, default, default, default, default, default);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/position")]
    public async Task Edr_Position_MalformedDatetime_ReturnsBadRequest()
    {
        foreach (var datetime in MalformedDatetimes)
        {
            _rasterStore.ClearReceivedCalls();

            var response = await _fixture.Client.GetAsync(
                $"{CollectionPath}/position?coords={Uri.EscapeDataString("POINT(-122.4 37.8)")}&datetime={Uri.EscapeDataString(datetime)}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"datetime '{datetime}' is malformed: {content}");
            content.Should().Contain("datetime");
            await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyAsync(
                default, default, default, default, default, default, default);
        }
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
        EdrDepthTestsFixture.AssertCubeValuesDerivedFromDomain(doc.RootElement, band: 3, "band_3");
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

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_DatetimeSelectsOnlyIntersectingRasterTime()
    {
        using (var doc = await GetJsonAsync(
            $"{CollectionPath}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=2&datetime={Uri.EscapeDataString("2025-06-01T00:00:00Z/..")}"))
        {
            doc.RootElement.GetProperty("domain").GetProperty("axes").GetProperty("t").GetProperty("values")
                .EnumerateArray()
                .Select(value => value.GetString())
                .Should().Equal(EdrDepthTestsFixture.RasterCreatedAtIso);
            EdrDepthTestsFixture.AssertCubeValuesDerivedFromDomain(doc.RootElement, band: 2, "band_2");
        }

        _rasterStore.ClearReceivedCalls();
        var disjoint = await _fixture.Client.GetAsync(
            $"{CollectionPath}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=2&datetime={Uri.EscapeDataString("1999-01-01T00:00:00Z/2000-01-01T00:00:00Z")}");
        var disjointContent = await disjoint.Content.ReadAsStringAsync();

        disjoint.StatusCode.Should().Be(HttpStatusCode.NoContent, disjointContent);
        disjointContent.Should().BeEmpty();
        await _rasterStore.DidNotReceiveWithAnyArgs().IdentifyAsync(
            default, default, default, default, default, default, default);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /edr/collections/{collectionId}/cube")]
    public async Task Edr_Cube_MalformedDatetime_ReturnsBadRequest()
    {
        foreach (var datetime in MalformedDatetimes)
        {
            var response = await _fixture.Client.GetAsync(
                $"{CollectionPath}/cube?bbox=-122.5,37.7,-122.3,37.9&resolution-x=2&datetime={Uri.EscapeDataString(datetime)}");
            var content = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"datetime '{datetime}' is malformed: {content}");
            content.Should().Contain("datetime");
        }
    }

    private static readonly string[] MalformedDatetimes =
    [
        "not-a-date",
        "2026-02-01T00:00:00Z/2026-01-01T00:00:00Z", // start after end
        "../..",
        "2026-01-01T00:00:00Z/garbage",
        "2026-01-01T00:00:00Z/2026-01-02T00:00:00Z/2026-01-03T00:00:00Z",
        // Not RFC 3339 date-times, although DateTimeOffset.TryParse would accept them.
        "2026-01-05",
        "2026-01-05T00:00:00",
        "01/05/2026 00:00:00 +00:00"
    ];

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
        RasterStore = Substitute.For<IRasterStore>();
        ConfigureRasterStore(RasterStore);
        App = new WebAppFixture()
            .ConfigureWebHost(builder => builder.UseSetting(
                "Capabilities:Experimental:serve.ogc-api-edr:Enabled", "true"))
            .ReplaceService(RasterStore);
    }

    public WebAppFixture App { get; }

    /// <summary>The mocked raster store, exposed so tests can assert no sample was read.</summary>
    public IRasterStore RasterStore { get; }

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

        // #4424: the sample is a function of the requested coordinate, not a constant, so a
        // coordinate-transposition, datum or off-by-one-pixel defect changes the answer. The
        // designated nodata longitude still reports no band values at all.
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
                        : new Dictionary<int, object?>
                        {
                            [1] = ExpectedBandValue(1, x, y),
                            [2] = ExpectedBandValue(2, x, y),
                            [3] = ExpectedBandValue(3, x, y)
                        }
                });
            });
    }

    /// <summary>
    /// The fixture raster's synthetic pixel function. Band index, longitude and latitude each
    /// carry different weights, so the tested coordinates distinguish band selection and axis swaps.
    /// </summary>
    public static double ExpectedBandValue(int band, double x, double y)
        => (band * 1000.0) + (Math.Round(x, 6) * 10.0) + Math.Round(y, 6);

    /// <summary>
    /// Asserts that every value of <paramref name="parameterName"/> in a cube response is the
    /// pixel function evaluated at the corresponding advertised grid coordinate in row-major order. A handler that sampled the wrong
    /// coordinates, transposed the axes or reused one sample for the whole cube fails here.
    /// </summary>
    public static void AssertCubeValuesDerivedFromDomain(
        JsonElement root,
        int band,
        string parameterName)
    {
        var axes = root.GetProperty("domain").GetProperty("axes");
        var xValues = axes.GetProperty("x").GetProperty("values").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        var yValues = axes.GetProperty("y").GetProperty("values").EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();

        var expected = (from y in yValues
                        from x in xValues
                        select ExpectedBandValue(band, x, y))
            .ToArray();

        var range = root.GetProperty("ranges").GetProperty(parameterName);
        range.GetProperty("axisNames").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("y", "x");
        range.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32())
            .Should().Equal(yValues.Length, xValues.Length);

        // Preserve row-major order: sorting hides a transposed or permuted grid.
        var actual = range.GetProperty("values")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();

        actual.Should().HaveCount(
            expected.Length,
            $"'{parameterName}' must carry one sample per advertised grid cell");

        for (var i = 0; i < expected.Length; i++)
        {
            actual[i].Should().BeApproximately(
                expected[i],
                1e-6,
                $"'{parameterName}' cell {i} must be the pixel function at its advertised grid coordinate");
        }
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
