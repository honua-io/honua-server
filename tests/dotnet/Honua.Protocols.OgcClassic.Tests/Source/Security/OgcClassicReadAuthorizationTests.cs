// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Security;

/// <summary>
/// WMS <c>GetMap</c> returns imagery, WMS <c>GetFeatureInfo</c> returns attribute data, and
/// WMTS <c>GetTile</c> / WCS <c>GetCoverage</c> return raster data. Before these tests the only
/// authorization coverage on any OGC-classic surface was WMS <c>GetCapabilities</c>, so a
/// denial that produced a picture, a tile or a row of attributes would have gone unnoticed.
/// <para>
/// Each operation gets two denial shapes and one paired success. A <em>service</em>-scoped
/// denial produces the protocol-correct access-denied document. A <em>layer</em>-scoped denial
/// filters the layer out of the accessible set before dispatch, so the surface answers as
/// though it does not exist (existence non-disclosure) rather than confirming the layer and
/// refusing. In both cases the assertion is the same: no imagery, no tile, no coverage bytes,
/// no attribute values. The paired success runs the identical request as a granted principal
/// against the same non-empty fixture, so no denial can be passing because the endpoint is
/// broken for everyone.
/// </para>
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicReadAuthorizationTests : IAsyncLifetime
{
    private const string ReaderRole = "ogc-reader";
    private const string DeniedPrincipal = "ogc-denied-user";
    private const string GrantedPrincipal = "ogc-granted-user";
    private const long TestRasterId = 377;

    // Seeded layer 0, objectid 1 (tests/seed/server.yaml): the attribute values a denied
    // GetFeatureInfo must not disclose.
    private const string SeededFeatureName = "Test Feature";
    private const string SeededFeatureDescription = "A test feature for integration tests";

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];

    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly List<RasterQuery> _exportQueries = [];
    private readonly WebAppFixture _fixture;

    public OgcClassicReadAuthorizationTests()
    {
        ConfigureRasterStore(_rasterStore, _exportQueries);
        _fixture = OgcClassicAuthorizationFixture.Create().ReplaceService(_rasterStore);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region WMS GetMap

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_WithServiceDenial_ReturnsAccessDeniedAndNoImagery()
    {
        RestrictServiceToReaders();
        using var client = _fixture.CreateClientAs(DeniedPrincipal);

        var response = await client.GetAsync(GetMapUrl());

        await AssertWmsAccessDeniedAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_WithLayerDenial_ReturnsNoImagery()
    {
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(DeniedPrincipal);

        var response = await client.GetAsync(GetMapUrl());

        await AssertNoImageryAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetMap_WithReadAccess_ReturnsPngImagery()
    {
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(GrantedPrincipal, ReaderRole);

        var response = await client.GetAsync(GetMapUrl());

        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Take(PngMagic.Length).Should().Equal(PngMagic);
    }

    #endregion

    #region WMS GetFeatureInfo

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_WithServiceDenial_ReturnsAccessDeniedAndNoAttributes()
    {
        RestrictServiceToReaders();
        using var client = _fixture.CreateClientAs(DeniedPrincipal);

        var response = await client.GetAsync(GetFeatureInfoUrl());

        await AssertWmsAccessDeniedAsync(response);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(SeededFeatureName);
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_WithLayerDenial_ReturnsNoAttributes()
    {
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(DeniedPrincipal);

        var response = await client.GetAsync(GetFeatureInfoUrl());

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().NotBe(HttpStatusCode.OK, body);
        body.Should().NotContain(SeededFeatureName);
        body.Should().NotContain(SeededFeatureDescription);
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_WithReadAccess_ReturnsSeededAttributes()
    {
        // I=41 / J=74 over the worldwide CRS:84 bbox at 256x256 lands on seeded objectid 1
        // (-122.5, 37.5), so the granted principal really does receive attribute data - which
        // is exactly what the denials above must withhold.
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(GrantedPrincipal, ReaderRole);

        var response = await client.GetAsync(GetFeatureInfoUrl());

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain(SeededFeatureName);
    }

    #endregion

    #region WMTS GetTile

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Protocol(TestProtocols.Wmts10)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_WithServiceDenial_ReturnsNoTileBytes()
    {
        RestrictServiceToReaders();
        using var client = _fixture.CreateClientAs(DeniedPrincipal);

        var response = await client.GetAsync(GetTileUrl());

        await AssertNoImageryAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Protocol(TestProtocols.Wmts10)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_WithLayerDenial_ReturnsNoTileBytes()
    {
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(DeniedPrincipal);

        var response = await client.GetAsync(GetTileUrl());

        await AssertNoImageryAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Protocol(TestProtocols.Wmts10)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetTile_WithReadAccess_ReturnsPngTile()
    {
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(GrantedPrincipal, ReaderRole);

        var response = await client.GetAsync(GetTileUrl());

        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Take(PngMagic.Length).Should().Equal(PngMagic);
    }

    #endregion

    #region WCS GetCoverage / DescribeCoverage

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Protocol(TestProtocols.Wcs201)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_GetCoverage_WithLayerDenial_ReturnsNoCoverageBytes()
    {
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(DeniedPrincipal);

        var response = await client.GetAsync(GetCoverageUrl());

        await AssertNoImageryAsync(response);

        // The refusal preceded the read: the substituted store recorded no export at all, so
        // there were never any coverage bytes to withhold.
        _exportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Protocol(TestProtocols.Wcs201)]
    [InterfaceOperation(TestProtocols.Wcs201, "DescribeCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_DescribeCoverage_WithLayerDenial_ReturnsNoCoverageDescription()
    {
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(DeniedPrincipal);

        var response = await client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=2.0.1&COVERAGEID=coverage_0");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().NotBe(HttpStatusCode.OK, body);
        body.Should().NotContain("<wcs:CoverageDescription");
        body.Should().NotContain("gmlcov:rangeType");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Protocol(TestProtocols.Wcs201)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_GetCoverage_WithReadAccess_ReturnsCoverageBytes()
    {
        RestrictLayerToReaders();
        using var client = _fixture.CreateClientAs(GrantedPrincipal, ReaderRole);

        var response = await client.GetAsync(GetCoverageUrl());

        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Should().Equal(PngMagic);
        _exportQueries.Should().ContainSingle();
    }

    #endregion

    private void RestrictServiceToReaders()
        => _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [ReaderRole] });

    private void RestrictLayerToReaders()
        => _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [ReaderRole] });

    private static string GetMapUrl()
        => $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0" +
           $"&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&CRS=EPSG:4326&LAYERS={WebAppFixture.TestLayerId}" +
           "&STYLES=&FORMAT=image/png&TRANSPARENT=true";

    private static string GetFeatureInfoUrl()
        => $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0" +
           $"&BBOX=-180,-90,180,90&CRS=CRS:84&WIDTH=256&HEIGHT=256&LAYERS={WebAppFixture.TestLayerId}" +
           $"&QUERY_LAYERS={WebAppFixture.TestLayerId}&INFO_FORMAT=text/plain&I=41&J=74";

    private static string GetTileUrl()
        => $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
           $"&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png" +
           "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0";

    private static string GetCoverageUrl()
        => $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1" +
           "&COVERAGEID=coverage_0&FORMAT=image/png&SUBSET=Long(-122.4,-122.3)&SUBSET=Lat(37.7,37.8)";

    private static async Task AssertWmsAccessDeniedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        // WMS 1.3.0 7.3.3.4 defines the ServiceExceptionReport payload; an authenticated
        // principal that fails authorization is Forbidden (only an anonymous one is
        // challenged) - see WmsRequestHandlers and AccessPolicyHelpers.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        body.Should().Contain("ServiceExceptionReport");
        body.Should().Contain("code=\"AccessDenied\"");
    }

    private static async Task AssertNoImageryAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var body = Encoding.UTF8.GetString(bytes);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK, body);
        (response.Content.Headers.ContentType?.MediaType ?? string.Empty)
            .Should().NotStartWith("image/", body);
        bytes.Take(PngMagic.Length).Should().NotEqual(PngMagic);
    }

    private static void ConfigureRasterStore(IRasterStore rasterStore, List<RasterQuery> exportQueries)
    {
        var raster = new RasterInfo
        {
            Id = TestRasterId,
            LayerId = WebAppFixture.TestLayerId,
            Name = "authz-coverage",
            Width = 16,
            Height = 16,
            BandCount = 1,
            PixelType = "32BF",
            Srid = 4326,
            NoDataValue = -9999,
            // 16x16 cells across a 0.3 x 0.3 degree extent: 0.01875 degrees per pixel.
            GeoTransform = [-122.5, 0.01875, 0, 37.9, 0, -0.01875],
            Extent = new RasterExtent
            {
                XMin = -122.5,
                YMin = 37.6,
                XMax = -122.2,
                YMax = 37.9,
                Srid = 4326
            },
            AcquisitionDate = DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            CreatedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture)
        };

        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(null));
        rasterStore.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));

        rasterStore.GetExtentAsync(WebAppFixture.TestLayerId, TestRasterId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterExtent?>(raster.Extent));

        rasterStore.ExportImageAsync(
                WebAppFixture.TestLayerId,
                TestRasterId,
                Arg.Any<RasterQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                exportQueries.Add(call.ArgAt<RasterQuery>(2));
                return Task.FromResult(new RasterResult
                {
                    Data = [0x89, 0x50, 0x4E, 0x47],
                    ContentType = "image/png",
                    Width = 16,
                    Height = 16,
                    Srid = 4326,
                    Extent = raster.Extent,
                    BandCount = 1,
                    PixelType = "32BF"
                });
            });
    }
}
