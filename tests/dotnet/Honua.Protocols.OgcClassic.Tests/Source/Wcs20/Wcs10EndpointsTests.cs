// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// WCS 1.0.0 compatibility surface (honua-server#5020).
/// </summary>
/// <remarks>
/// These assertions are deliberately written against what the stock QGIS WCS provider
/// reads rather than only against the schema, because the whole point of serving 1.0.0 is
/// that QGIS - which ships a 1.0/1.1 client and rejects 2.0.1 outright - can open a
/// coverage. The element names asserted here (<c>WCS_Capabilities</c>,
/// <c>ContentMetadata/CoverageOfferingBrief</c> with <c>name</c>/<c>label</c>/
/// <c>description</c>, the GetCoverage <c>OnlineResource</c>, and the DescribeCoverage
/// <c>RectifiedGrid</c>/<c>GridEnvelope</c> limits plus <c>supportedCRSs</c> and
/// <c>supportedFormats</c>) are the exact ones the provider looks for; dropping any of
/// them makes the layer unopenable even though the document still validates.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Wcs10)]
public sealed class Wcs10EndpointsTests : IAsyncLifetime
{
    private const long TestRasterId = 377;
    private const string Wcs10Namespace = "http://www.opengis.net/wcs";
    private const string GmlNamespace = "http://www.opengis.net/gml";
    private const string OgcNamespace = "http://www.opengis.net/ogc";

    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly List<RasterQuery> _exportQueries = [];
    private readonly WebAppFixture _fixture;

    public Wcs10EndpointsTests()
    {
        ConfigureRasterStore(_rasterStore, _exportQueries);
        _fixture = new WebAppFixture().ReplaceService(_rasterStore);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_GetCapabilities_ReturnsTheElementsTheQgisProviderReads()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=1.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");

        var root = XDocument.Parse(content).Root!;
        root.Name.Should().Be(XName.Get("WCS_Capabilities", Wcs10Namespace),
            "the provider rejects the document unless the root element is WCS_Capabilities");
        root.Attribute("version")!.Value.Should().Be("1.0.0");

        // The provider sends GetCoverage to this href, so its absence makes the
        // coverage unreadable even though the capabilities parse.
        var getCoverageHref = root
            .Element(XName.Get("Capability", Wcs10Namespace))!
            .Element(XName.Get("Request", Wcs10Namespace))!
            .Element(XName.Get("GetCoverage", Wcs10Namespace))!
            .Descendants(XName.Get("OnlineResource", Wcs10Namespace))
            .Single()
            .Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))!.Value;
        getCoverageHref.Should().Contain($"/ogc/services/{WebAppFixture.TestServiceId}/wcs");

        var brief = root
            .Element(XName.Get("ContentMetadata", Wcs10Namespace))!
            .Elements(XName.Get("CoverageOfferingBrief", Wcs10Namespace))
            .Single();
        brief.Element(XName.Get("name", Wcs10Namespace))!.Value.Should().Be("coverage_0");
        brief.Element(XName.Get("label", Wcs10Namespace))!.Should().NotBeNull();
        brief.Element(XName.Get("description", Wcs10Namespace))!.Should().NotBeNull();

        // Exactly two gml:pos children: a different count is skipped by the provider.
        brief.Element(XName.Get("lonLatEnvelope", Wcs10Namespace))!
            .Elements(XName.Get("pos", GmlNamespace))
            .Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCapabilities")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs10_GetCapabilities_ImageServerRoute_ServesTheLegacyEncoding()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=1.0.0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        // The 2.0.1 encoding must not leak into a 1.0.0 response.
        content.Should().Contain("WCS_Capabilities");
        content.Should().NotContain("ows:ServiceIdentification");
        content.Should().NotContain("wcs:Contents");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "DescribeCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_DescribeCoverage_ExposesGridLimitsCrsAndFormats()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=1.0.0&COVERAGE=coverage_0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var root = XDocument.Parse(content).Root!;
        root.Name.Should().Be(XName.Get("CoverageDescription", Wcs10Namespace));

        var offering = root.Elements(XName.Get("CoverageOffering", Wcs10Namespace)).Single();
        offering.Element(XName.Get("name", Wcs10Namespace))!.Value.Should().Be("coverage_0");

        // The provider derives raster width and height from high - low, so these must
        // describe the real raster size (64x64 in the fixture).
        var grid = offering
            .Element(XName.Get("domainSet", Wcs10Namespace))!
            .Element(XName.Get("spatialDomain", Wcs10Namespace))!
            .Element(XName.Get("RectifiedGrid", GmlNamespace))!;
        var limits = grid
            .Element(XName.Get("limits", GmlNamespace))!
            .Element(XName.Get("GridEnvelope", GmlNamespace))!;
        limits.Element(XName.Get("low", GmlNamespace))!.Value.Should().Be("0 0");
        limits.Element(XName.Get("high", GmlNamespace))!.Value.Should().Be("63 63");

        // Without a resolvable CRS the provider refuses to build a layer.
        offering
            .Element(XName.Get("supportedCRSs", Wcs10Namespace))!
            .Element(XName.Get("requestResponseCRSs", Wcs10Namespace))!
            .Value.Should().Contain("4326");

        // The provider picks the first format containing "tif" and gives up when none
        // is advertised, so GeoTIFF has to be present.
        offering
            .Element(XName.Get("supportedFormats", Wcs10Namespace))!
            .Elements(XName.Get("formats", Wcs10Namespace))
            .Select(static element => element.Value)
            .Should().Contain("GeoTIFF");

        // Native extent carries its real srsName rather than being relabelled.
        offering
            .Element(XName.Get("domainSet", Wcs10Namespace))!
            .Element(XName.Get("spatialDomain", Wcs10Namespace))!
            .Element(XName.Get("Envelope", GmlNamespace))!
            .Attribute("srsName")!.Value.Should().Contain("4326");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_GetCoverage_HonoursTheBboxAndPixelSizeFormQgisSends()
    {
        // Exactly the parameter set the QGIS provider builds for 1.0.0: no SUBSET, no
        // SCALESIZE, and WIDTH/HEIGHT rather than RESX/RESY.
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
            "&COVERAGE=coverage_0&FORMAT=GeoTIFF&BBOX=-122.5,37.7,-122.35,37.84" +
            "&CRS=EPSG:4326&RESPONSE_CRS=EPSG:4326&WIDTH=32&HEIGHT=32");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        _exportQueries.Should().HaveCount(1);
        var query = _exportQueries[0];
        query.OutputWidth.Should().Be(32);
        query.OutputHeight.Should().Be(32);
        query.OutputSrid.Should().Be(4326);
        query.OutputFormat.Should().Be(RasterFormat.TIFF);
        query.ClipRegion.Should().NotBeNull();
        query.ClipRegion!.Value.Srid.Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_GetCoverage_UnknownFormat_ReturnsLegacyServiceExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
            "&COVERAGE=coverage_0&FORMAT=application/x-nonsense&BBOX=-122.5,37.7,-122.35,37.84" +
            "&CRS=EPSG:4326&WIDTH=8&HEIGHT=8");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);

        // WCS 1.0.0 predates OWS. A 1.0.0 client parses ServiceException/@code and
        // cannot read the ows:ExceptionReport that 2.0.1 emits, so the error would be
        // invisible in the UI if this regressed.
        var root = XDocument.Parse(content).Root!;
        root.Name.Should().Be(XName.Get("ServiceExceptionReport", OgcNamespace));
        root.Elements(XName.Get("ServiceException", OgcNamespace)).Single()
            .Attribute("code")!.Value.Should().Be("InvalidFormat");
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/vnd.ogc.se_xml");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_GetCoverage_WithoutBboxRejectsNonnativeRequestCrs()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
            "&COVERAGE=coverage_0&FORMAT=GeoTIFF&CRS=EPSG:3857&WIDTH=16&HEIGHT=16");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("BBOX is required");
        _exportQueries.Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("CRS")]
    [InlineData("RESPONSE_CRS")]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_GetCoverage_UnsupportedCrsIsRejectedBeforeExport(string parameter)
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
            $"&COVERAGE=coverage_0&FORMAT=GeoTIFF&BBOX=-122.5,37.7,-122.35,37.84" +
            $"&{parameter}=EPSG:999999&WIDTH=16&HEIGHT=16");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("InvalidParameterValue");
        content.Should().Contain($"locator=\"{parameter}\"");
        _exportQueries.Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("WIDTH=16", 16, 64)]
    [InlineData("HEIGHT=16", 64, 16)]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_GetCoverage_OneExplicitDimensionPreservesIt(
        string dimensionParameter, int expectedWidth, int expectedHeight)
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
            $"&COVERAGE=coverage_0&FORMAT=GeoTIFF&{dimensionParameter}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _exportQueries.Should().ContainSingle();
        _exportQueries[0].OutputWidth.Should().Be(expectedWidth);
        _exportQueries[0].OutputHeight.Should().Be(expectedHeight);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_GetCoverage_TinyResolutionIsRejectedBeforeIntegerConversion()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
            "&COVERAGE=coverage_0&FORMAT=GeoTIFF&BBOX=-122.5,37.7,-122.35,37.84" +
            "&CRS=EPSG:4326&RESX=0.000000000001&RESY=0.01");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("limit per dimension");
        _exportQueries.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs10, "DescribeCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs10_DescribeCoverage_UnknownCoverage_ReturnsCoverageNotDefined()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&VERSION=1.0.0" +
            "&REQUEST=DescribeCoverage&COVERAGE=coverage_9999");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, content);
        XDocument.Parse(content).Root!
            .Elements(XName.Get("ServiceException", OgcNamespace)).Single()
            .Attribute("code")!.Value.Should().Be("CoverageNotDefined");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_VersionNegotiation_KeepsTwoPointZeroAsTheDefaultAndIsolatesTheEncodings()
    {
        // No VERSION: negotiate to the highest supported, which stays 2.0.1 so existing
        // clients are unaffected by 1.0.0 being served.
        var negotiated = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=GetCapabilities");
        var negotiatedContent = await negotiated.Content.ReadAsStringAsync();
        negotiated.StatusCode.Should().Be(HttpStatusCode.OK, negotiatedContent);
        negotiatedContent.Should().Contain("wcs:Capabilities");
        negotiatedContent.Should().NotContain("WCS_Capabilities");

        // An explicit 2.0.1 request is untouched.
        var modern = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1");
        var modernContent = await modern.Content.ReadAsStringAsync();
        modern.StatusCode.Should().Be(HttpStatusCode.OK, modernContent);
        modernContent.Should().Contain("wcs:Capabilities");

        // A version we do not serve is still refused rather than silently downgraded.
        var unsupported = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=9.9.9");
        unsupported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unsupported.Content.ReadAsStringAsync())
            .Should().Contain("VersionNegotiationFailed");
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
                    Data = [0x49, 0x49, 0x2A, 0x00],
                    ContentType = "image/tiff",
                    Width = 32,
                    Height = 32,
                    Srid = 4326,
                    Extent = raster.Extent,
                    BandCount = 1,
                    PixelType = "32BF"
                });
            });
    }

    private static RasterInfo CreateRasterInfo()
        => new()
        {
            Id = TestRasterId,
            LayerId = WebAppFixture.TestLayerId,
            Name = "wcs10-test-raster",
            Width = 64,
            Height = 64,
            BandCount = 1,
            Srid = 4326,
            PixelType = "32BF",
            NoDataValue = -9999,
            GeoTransform = [-122.5, 0.00234375, 0, 37.84, 0, -0.0021875],
            Extent = new RasterExtent
            {
                XMin = -122.5,
                YMin = 37.7,
                XMax = -122.35,
                YMax = 37.84,
                Srid = 4326
            },
            AcquisitionDate = DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            CreatedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture)
        };
}
