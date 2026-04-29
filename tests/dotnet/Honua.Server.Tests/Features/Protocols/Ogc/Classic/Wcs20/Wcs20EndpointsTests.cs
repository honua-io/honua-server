// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

[Collection("Database")]
[Protocol(TestProtocols.Wcs201)]
public sealed class Wcs20EndpointsTests : IAsyncLifetime
{
    private const long TestRasterId = 377;
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly List<RasterQuery> _exportQueries = [];
    private readonly WebAppFixture _fixture;

    public Wcs20EndpointsTests()
    {
        ConfigureRasterStore(_rasterStore, _exportQueries);
        _fixture = new WebAppFixture().ReplaceService(_rasterStore);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCapabilities")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCapabilities_ImageServerRoute_ReturnsCoverageXml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("<wcs:Capabilities");
        content.Should().Contain("<ows:ServiceIdentification>");
        content.Should().Contain("Operation name=\"DescribeCoverage\"");
        content.Should().Contain("<wcs:ServiceMetadata>");
        content.Should().Contain("<wcs:CoverageId>0</wcs:CoverageId>");
        content.Should().Contain("<wcs:formatSupported>image/tiff</wcs:formatSupported>");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_GetCapabilities_ServiceRoute_ReturnsAccessibleRasterCoverage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("<wcs:Contents>");
        content.Should().Contain("<wcs:CoverageId>0</wcs:CoverageId>");
        content.Should().Contain($"/ogc/services/{WebAppFixture.TestServiceId}/wcs?SERVICE=WCS&amp;REQUEST=GetCoverage");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.Wcs201, "DescribeCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_DescribeCoverage_WithBareIntegerCoverageId_ReturnsCoverageDescription()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=2.0.1&COVERAGEID=0,0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("<wcs:CoverageDescriptions");
        content.Should().Contain("<wcs:CoverageId>0</wcs:CoverageId>");
        content.Should().Contain("<gml:RectifiedGrid");
        content.Should().Contain("<gmlcov:rangeType>");
        content.Should().Contain("name=\"band1\"");
        content.Should().Contain("<wcs:nativeFormat>image/tiff</wcs:nativeFormat>");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_WithSubsetFormatAndOutputCrs_ReturnsRasterBytes()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=image/png&SUBSET=Long(-122.4,-122.3)&SUBSET=Lat(37.7,37.8)&OUTPUTCRS=EPSG:3857");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Should().Equal([0x89, 0x50, 0x4E, 0x47]);

        _exportQueries.Should().ContainSingle();
        var query = _exportQueries.Single();
        query.OutputFormat.Should().Be(RasterFormat.PNG);
        query.OutputSrid.Should().Be(3857);
        query.ClipRegion.Should().NotBeNull();
        query.ClipRegion!.Value.Srid.Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "DescribeCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_DescribeCoverage_UnknownCoverage_ReturnsNoSuchCoverageException()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=2.0.1&COVERAGEID=999");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("<ows:ExceptionReport");
        content.Should().Contain("exceptionCode=\"NoSuchCoverage\"");
        content.Should().NotContain("Npgsql");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "DescribeCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_DescribeCoverage_ServiceRouteUnknownService_ReturnsServiceNotFoundException()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/services/missing/wcs?SERVICE=WCS&REQUEST=DescribeCoverage&VERSION=2.0.1&COVERAGEID=0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("<ows:ExceptionReport");
        content.Should().Contain("exceptionCode=\"NoApplicableCode\"");
        content.Should().Contain("Service 'missing' was not found.");
        content.Should().NotContain("exceptionCode=\"NoSuchCoverage\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /ogc/services/{serviceId}/wcs")]
    public async Task Wcs_GetCoverage_ServiceRouteUnknownService_ReturnsServiceNotFoundException()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/services/missing/wcs?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("<ows:ExceptionReport");
        content.Should().Contain("exceptionCode=\"NoApplicableCode\"");
        content.Should().Contain("Service 'missing' was not found.");
        content.Should().NotContain("exceptionCode=\"NoSuchCoverage\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_UnsupportedFormat_ReturnsOwsException()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&FORMAT=application/netcdf");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidParameterValue\"");
        content.Should().Contain("locator=\"FORMAT\"");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCoverage")]
    [Endpoint("GET /rest/services/{id}/ImageServer/WCS")]
    public async Task Wcs_GetCoverage_MalformedSubset_ReturnsInvalidSubsettingException()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/WCS?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=0&SUBSET=Long(-122.4)");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("exceptionCode=\"InvalidSubsetting\"");
        content.Should().Contain("locator=\"SUBSET\"");
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
                    Data = [0x89, 0x50, 0x4E, 0x47],
                    ContentType = "image/png",
                    Width = 16,
                    Height = 16,
                    Srid = 3857,
                    Extent = new RasterExtent
                    {
                        XMin = -122.4,
                        YMin = 37.7,
                        XMax = -122.3,
                        YMax = 37.8,
                        Srid = 4326
                    },
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
            Name = "wcs-test-raster",
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
