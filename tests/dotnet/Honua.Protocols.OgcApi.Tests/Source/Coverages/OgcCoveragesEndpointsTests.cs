// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Coverages;

[Collection("Database.OgcApiData")]
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
        collection.GetProperty("links").EnumerateArray().Should().Contain(link =>
            link.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/coverage" &&
            link.GetProperty("type").GetString() == "image/tiff; application=geotiff");
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
        var generalGrid = collection.GetProperty("domainset").GetProperty("generalGrid");
        generalGrid.GetProperty("srsName").GetString().Should().EndWith("CRS84");
        generalGrid.GetProperty("axisLabels").EnumerateArray().Select(axis => axis.GetString()).Should().Equal("Lon", "Lat");
        generalGrid.GetProperty("axis")[0].GetProperty("resolution").GetDouble().Should().BeApproximately(0.003125, 1e-12);
        collection.GetProperty("rangetype").GetProperty("field").GetArrayLength().Should().Be(3);
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

        _exportQueries.Should().ContainSingle();
        var query = _exportQueries.Single();
        query.OutputFormat.Should().Be(RasterFormat.TIFF);
        query.ClipRegion.Should().NotBeNull();

        // #4424: the requested bbox is the whole raster footprint, so the covered extent is
        // the footprint — but it is now DERIVED from the clip the handler passed down, and the
        // payload is derived from that extent. A handler that dropped `bbox` on the floor, or
        // clipped to the wrong window, produces different bytes and a different Content-Bbox.
        var expectedExtent = ClippedExtent(query, query.OutputSrid ?? 4326);
        bytes.Should().Equal(SyntheticRasterBytes(
            RasterFormat.TIFF,
            expectedExtent,
            query.OutputWidth ?? 64,
            query.OutputHeight ?? 64));

        response.Headers.TryGetValues("Content-Bbox", out var bboxes).Should().BeTrue();
        var reported = bboxes!.Single()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        reported.Should().HaveCount(4);
        reported[0].Should().BeApproximately(expectedExtent.XMin, 1e-9);
        reported[1].Should().BeApproximately(expectedExtent.YMin, 1e-9);
        reported[2].Should().BeApproximately(expectedExtent.XMax, 1e-9);
        reported[3].Should().BeApproximately(expectedExtent.YMax, 1e-9);

        // The discriminating case the old constant-extent fixture could not express: a bbox
        // strictly INSIDE the footprint must come back as its own extent, in the headers and
        // in the payload. Under the old mock both were the full footprint whatever was asked.
        _exportQueries.Clear();
        var narrow = await _fixture.Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?bbox=-122.45,37.75,-122.4,37.8");
        narrow.StatusCode.Should().Be(HttpStatusCode.OK);

        _exportQueries.Should().ContainSingle();
        var narrowQuery = _exportQueries.Single();
        var narrowExtent = ClippedExtent(narrowQuery, narrowQuery.OutputSrid ?? 4326);

        narrowExtent.XMin.Should().BeApproximately(-122.45, 1e-9);
        narrowExtent.YMin.Should().BeApproximately(37.75, 1e-9);
        narrowExtent.XMax.Should().BeApproximately(-122.4, 1e-9);
        narrowExtent.YMax.Should().BeApproximately(37.8, 1e-9);

        narrow.Headers.TryGetValues("Content-Bbox", out var narrowBboxes).Should().BeTrue();
        var narrowReported = narrowBboxes!.Single()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        narrowReported.Should().HaveCount(4);
        narrowReported[0].Should().BeApproximately(-122.45, 1e-9);
        narrowReported[1].Should().BeApproximately(37.75, 1e-9);
        narrowReported[2].Should().BeApproximately(-122.4, 1e-9);
        narrowReported[3].Should().BeApproximately(37.8, 1e-9);

        (await narrow.Content.ReadAsByteArrayAsync()).Should().NotEqual(
            bytes,
            "a narrower bbox must not produce the same payload as the full footprint");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_GetCoverage_AntimeridianBbox_UsesSplitClipGeometry()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?bbox=170,-10,-170,10");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _exportQueries.Should().ContainSingle();

        var clip = _exportQueries.Single().ClipRegion;
        clip.Should().NotBeNull();
        clip!.Value.Srid.Should().Be(4326);
        var geometry = new WKBReader().Read(clip!.Value.Geometry);
        geometry.GeometryType.Should().Be("MultiPolygon");
        geometry.NumGeometries.Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_GetCoverage_ProjectedBboxScaling_UsesStorageCrsUnits()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?bbox=-13650000,4530000,-13600000,4570000&bbox-crs=EPSG:3857&crs=EPSG:3857&resolution=0.003125");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _exportQueries.Should().ContainSingle();
        var resolutionQuery = _exportQueries.Single();
        resolutionQuery.OutputSrid.Should().Be(3857);
        resolutionQuery.ClipRegion.Should().NotBeNull();
        resolutionQuery.ClipRegion!.Value.Srid.Should().Be(3857);
        resolutionQuery.PixelSize.Should().NotBeNull();
        resolutionQuery.PixelSize!.Value.Width.Should().BeApproximately(0.003125, 0.000000001);
        resolutionQuery.PixelSize!.Value.Height.Should().BeApproximately(0.003125, 0.000000001);

        // The clip is measured in metres. A fixture that intersects it with the native
        // longitude/latitude ordinates produces an inverted, meaningless response bbox.
        response.Headers.TryGetValues("Content-Bbox", out var projectedBboxes).Should().BeTrue();
        var projectedBbox = projectedBboxes!.Single()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        var (nativeWest, nativeSouth) = WebMercatorMath.LonLatToWebMercator(NativeExtent.XMin, NativeExtent.YMin);
        var (nativeEast, nativeNorth) = WebMercatorMath.LonLatToWebMercator(NativeExtent.XMax, NativeExtent.YMax);
        projectedBbox.Should().HaveCount(4);
        projectedBbox[0].Should().BeApproximately(Math.Max(nativeWest, -13_650_000), 1e-6);
        projectedBbox[1].Should().BeApproximately(Math.Max(nativeSouth, 4_530_000), 1e-6);
        projectedBbox[2].Should().BeApproximately(Math.Min(nativeEast, -13_600_000), 1e-6);
        projectedBbox[3].Should().BeApproximately(Math.Min(nativeNorth, 4_570_000), 1e-6);
        projectedBbox[0].Should().BeLessThan(projectedBbox[2]);
        projectedBbox[1].Should().BeLessThan(projectedBbox[3]);

        _exportQueries.Clear();
        response = await _fixture.Client.GetAsync(
            $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?bbox=-13650000,4530000,-13600000,4570000&bbox-crs=EPSG:3857&scale-factor=1");

        content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _exportQueries.Should().ContainSingle();
        var scaleFactorQuery = _exportQueries.Single();
        scaleFactorQuery.ClipRegion.Should().NotBeNull();
        scaleFactorQuery.ClipRegion!.Value.Srid.Should().Be(3857);
        scaleFactorQuery.PixelSize.Should().NotBeNull();
        scaleFactorQuery.PixelSize!.Value.Width.Should().BeApproximately(0.003125, 0.000000001);
        scaleFactorQuery.PixelSize!.Value.Height.Should().BeApproximately(0.003125, 0.000000001);
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
            ("datetime=2026-04-29T00:00:00Z", "not applicable to a single-raster coverage"),
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
                TestRasterId,
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<RasterQuery>(2);
                exportQueries.Add(query);
                var srid = query.OutputSrid ?? 4326;
                var width = query.OutputWidth ?? 64;
                var height = query.OutputHeight ?? 64;

                // #4424: the exported extent and payload are DERIVED from the request, not
                // hard-coded. Previously the mock returned the same constant extent and the
                // same four bytes whatever was asked for, so `Content-Bbox` contained
                // "-122.5" for any bbox on the planet and a regression in which `bbox` was
                // parsed but never applied passed every Coverages test.
                var extent = ClippedExtent(query, srid);
                return Task.FromResult(new RasterResult
                {
                    Data = SyntheticRasterBytes(query.OutputFormat, extent, width, height),
                    ContentType = query.OutputFormat.ToContentType(),
                    Width = width,
                    Height = height,
                    Srid = srid,
                    Extent = extent,
                    BandCount = query.Bands?.Length ?? 3,
                    PixelType = "32BF"
                });
            });
    }

    /// <summary>
    /// The native extent of the fixture raster, in CRS84.
    /// </summary>
    internal static readonly RasterExtent NativeExtent = new()
    {
        XMin = -122.5,
        YMin = 37.7,
        XMax = -122.3,
        YMax = 37.9,
        Srid = 4326
    };

    /// <summary>
    /// The extent a correct export would cover: the requested clip envelope intersected with
    /// the raster footprint, or the whole footprint when no clip was requested. A handler that
    /// parsed <c>bbox</c> but never applied it returns the native extent and so fails any
    /// assertion derived from the requested one.
    /// </summary>
    internal static RasterExtent ClippedExtent(RasterQuery query, int srid)
    {
        var extent = NativeExtent;
        if (query.ClipRegion is { } clip)
        {
            var clipSrid = clip.Srid ?? NativeExtent.Srid!.Value;
            var footprint = TransformExtent(NativeExtent, clipSrid);
            var envelope = new WKBReader().Read(clip.Geometry).EnvelopeInternal;
            extent = new RasterExtent
            {
                XMin = Math.Max(footprint.XMin, envelope.MinX),
                YMin = Math.Max(footprint.YMin, envelope.MinY),
                XMax = Math.Min(footprint.XMax, envelope.MaxX),
                YMax = Math.Min(footprint.YMax, envelope.MaxY),
                Srid = clipSrid
            };
        }

        return TransformExtent(extent, srid);
    }

    private static RasterExtent TransformExtent(RasterExtent extent, int srid)
    {
        if (extent.Srid == srid)
        {
            return extent;
        }

        // The fixture's native CRS is 4326 and its projected requests use 3857.
        // Use the same shared projection math as the provider instead of comparing
        // degree and metre ordinates directly.
        Func<double, double, (double X, double Y)> transform = (extent.Srid, srid) switch
        {
            (4326, 3857) => WebMercatorMath.LonLatToWebMercator,
            (3857, 4326) => WebMercatorMath.WebMercatorToLonLat,
            _ => throw new NotSupportedException($"Fixture cannot transform {extent.Srid} to {srid}.")
        };
        var (minX, minY, maxX, maxY) = WebMercatorMath.TransformSampledExtent(
            extent.XMin, extent.YMin, extent.XMax, extent.YMax, transform, sampleSegmentsPerEdge: 4);
        return new RasterExtent
        {
            XMin = minX,
            YMin = minY,
            XMax = maxX,
            YMax = maxY,
            Srid = srid
        };
    }

    /// <summary>
    /// A synthetic raster payload: the real format signature followed by the little-endian
    /// encoding of the covered extent and the output grid size. There is no encoder in this
    /// repository, so the bytes cannot be a real GeoTIFF — but they are a pure function of the
    /// answer, which is what makes a byte-equality assertion falsifiable. A wrong bbox, a
    /// wrong output size or a wrong SRID all change the payload.
    /// </summary>
    internal static byte[] SyntheticRasterBytes(RasterFormat format, RasterExtent extent, int width, int height)
    {
        var signature = format == RasterFormat.PNG
            ? new byte[] { 0x89, 0x50, 0x4E, 0x47 }
            : [0x49, 0x49, 0x2A, 0x00];

        var payload = new List<byte>(signature);
        foreach (var value in new[] { extent.XMin, extent.YMin, extent.XMax, extent.YMax })
        {
            payload.AddRange(BitConverter.GetBytes(Math.Round(value, 9)));
        }

        payload.AddRange(BitConverter.GetBytes(width));
        payload.AddRange(BitConverter.GetBytes(height));
        payload.AddRange(BitConverter.GetBytes(extent.Srid ?? 0));
        return [.. payload];
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_GeographicSubset_OrderIndependent_AndGdalScalingAlias()
    {
        foreach (var subset in new[]
        {
            "Lon(-122.44:-122.42),Lat(37.74:37.76)",
            "Lat(37.74:37.76),Lon(-122.44:-122.42)",
        })
        {
            _exportQueries.Clear();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?subset={Uri.EscapeDataString(subset)}&scaleSize=Lat(2),Long(3)");
            request.Headers.TryAddWithoutValidation("Accept", "image/tiff;application=geotiff");
            var response = await _fixture.Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var query = _exportQueries.Should().ContainSingle().Subject;
            query.OutputWidth.Should().Be(3);
            query.OutputHeight.Should().Be(2);
            query.ClipRegion.Should().NotBeNull();
            var clip = query.ClipRegion ?? throw new InvalidOperationException("Expected a spatial clip.");
            clip.Srid.Should().Be(4326);
            var envelope = new WKBReader().Read(clip.Geometry).EnvelopeInternal;
            envelope.MinX.Should().Be(-122.44);
            envelope.MaxX.Should().Be(-122.42);
            envelope.MinY.Should().Be(37.74);
            envelope.MaxY.Should().Be(37.76);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}/coverage")]
    public async Task Coverages_InvalidSubsetOrAmbiguousScaling_RejectsBeforeExport()
    {
        foreach (var query in new[]
        {
            "subset=",
            "subset=Lon(NaN:1),Lat(0:1)",
            "subset=Lon(0:Infinity),Lat(0:1)",
            "subset=Lon(1:0),Lat(0:1)",
            "subset=Lon(0:1),Lon(2:3)",
            "subset=Time(0:1),Lat(0:1)",
            "subset=Lon(0:1),Lat(0:91)",
            "subset=Lon(0:1),Lat(0:1)&bbox=0,0,1,1",
            "scale-size=2,2&scaleSize=3,3",
            "scaleSize=Lat(2),Lat(3)",
        })
        {
            var response = await _fixture.Client.GetAsync(
                $"/ogc/coverages/collections/{WebAppFixture.TestLayerId}/coverage?{query}");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
        }
        _exportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/coverages/collections/{collectionId}")]
    public async Task Coverages_SignedByteRange_ReportsEightBitType()
    {
        var raster = CreateRasterInfo() with { PixelType = "8BSI" };
        _rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));
        using var document = await GetJsonAsync($"/ogc/coverages/collections/{WebAppFixture.TestLayerId}");
        document.RootElement.GetProperty("rangetype").GetProperty("field").EnumerateArray()
            .Select(field => field.GetProperty("definition").GetString()).Should().Equal("INT8", "INT8", "INT8");
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
