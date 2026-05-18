// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Core.Tests.Features.Import;

public sealed class OgcServiceMigrationScannerTests
{
    [Fact]
    public async Task ScanSourceAsync_Wfs20Capabilities_ProducesFeatureInventory()
    {
        using var httpClient = new HttpClient(new OgcFixtureHandler());
        var crsRegistry = CreateCrsRegistry();
        var scanner = CreateScanner(httpClient, crsRegistry);

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WFS",
            ServiceUrl = "https://example.com/geoserver/wfs",
            Version = "2.0.0",
            TimeoutSeconds = 10
        });

        artifact.SourceKind.Should().Be("ogc-wfs");
        artifact.Source.ServiceType.Should().Be("WFS");
        artifact.ScanCompleteness.Status.Should().Be("complete");
        artifact.Resources.Should().ContainSingle(resource => resource.Id == "feature-type:topp-states");
        var resource = artifact.Resources.Single();
        resource.Kind.Should().Be("feature-type");
        resource.Capabilities.Should().Contain("wfs:GetFeature");
        resource.GeometryType.Should().Be("Point");
        resource.Fields.Should().Contain(field => field.Name == "STATE_NAME" && field.FieldType == "xsd:string");
        resource.SpatialReferences.Should().ContainSingle(reference => reference.Srid == 4326);
        resource.Compatibility.Code.Should().Be(ImportCompatibilityCodes.OgcWfsFeatureSource);
    }

    [Fact]
    public async Task ScanSourceAsync_CapabilitiesFailure_ReturnsSanitizedFailureArtifact()
    {
        using var httpClient = new HttpClient(new ThrowingHandler(new HttpRequestException("secret=/var/private/provider-token")));
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WFS",
            ServiceUrl = "https://example.com/geoserver/wfs",
            Version = "2.0.0",
            TimeoutSeconds = 10
        });

        const string expectedReason = "OGC service endpoint could not be reached or returned an unsupported response.";
        artifact.ScanCompleteness.Status.Should().Be("failed");
        artifact.AuthPosture.Notes.Should().ContainSingle().Which.Should().Be(expectedReason);
        artifact.ScanCompleteness.Warnings.Should().ContainSingle().Which.Should().Be(expectedReason);
        artifact.OverallCompatibility.Warnings.Should().ContainSingle().Which.Should().Be(expectedReason);
        artifact.AuthPosture.Notes
            .Concat(artifact.ScanCompleteness.Warnings)
            .Concat(artifact.OverallCompatibility.Warnings)
            .Should().NotContain(value => value.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanSourceAsync_DescribeFeatureTypeFailure_UsesSanitizedManualReviewWarning()
    {
        using var httpClient = new HttpClient(new DescribeFeatureTypeFailureHandler());
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WFS",
            ServiceUrl = "https://example.com/geoserver/wfs",
            Version = "2.0.0",
            TimeoutSeconds = 10
        });

        artifact.ScanCompleteness.Status.Should().Be("partial");
        artifact.ScanCompleteness.Warnings.Should().ContainSingle()
            .Which.Should().Be("DescribeFeatureType metadata was unavailable for topp:states: DescribeFeatureType metadata could not be retrieved.");
        artifact.Resources.Should().ContainSingle().Which.Compatibility.Warnings
            .Should().ContainSingle().Which.Should().Be("DescribeFeatureType metadata could not be retrieved.");
        artifact.ScanCompleteness.Warnings
            .Concat(artifact.Resources.SelectMany(resource => resource.Compatibility.Warnings))
            .Should().NotContain(value => value.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanSourceAsync_WmsCapabilities_ReportsRenderOnlyUnsupportedResource()
    {
        using var httpClient = new HttpClient(new OgcFixtureHandler());
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WMS",
            ServiceUrl = "https://example.com/geoserver/wms",
            Version = "1.3.0",
            TimeoutSeconds = 10
        });

        artifact.SourceKind.Should().Be("ogc-wms");
        artifact.Resources.Should().ContainSingle(resource => resource.Name == "topp:states")
            .Which.Compatibility.Should().Match<MigrationCompatibilityAssessment>(compatibility =>
                compatibility.Level == "incompatible" &&
                compatibility.Code == ImportCompatibilityCodes.OgcWmsRenderOnlySource);
        artifact.Styles.Should().ContainSingle(style => style.Kind == "wms-style");
    }

    [Fact]
    public async Task ScanSourceAsync_WmtsCapabilities_ReportsTileOnlyUnsupportedResourceAndTileMatrixSet()
    {
        using var httpClient = new HttpClient(new OgcFixtureHandler());
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WMTS",
            ServiceUrl = "https://example.com/geoserver/gwc/service/wmts",
            Version = "1.0.0",
            TimeoutSeconds = 10
        });

        artifact.SourceKind.Should().Be("ogc-wmts");
        artifact.Resources.Should().ContainSingle(resource => resource.Name == "topp:states")
            .Which.Compatibility.Code.Should().Be(ImportCompatibilityCodes.OgcWmtsTileOnlySource);
        artifact.ExternalDependencies.Should().Contain(dependency => dependency.Kind == "tile-matrix-set" && dependency.Name == "EPSG:3857");
    }

    private static OgcServiceMigrationScanner CreateScanner(HttpClient httpClient, Mock<ICrsRegistry> crsRegistry)
        => new(
            httpClient,
            crsRegistry.Object,
            NullLogger<OgcServiceMigrationScanner>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

    private static Mock<ICrsRegistry> CreateCrsRegistry()
    {
        var registry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        registry.Setup(item => item.ResolveBySridAsync(4326, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<CrsDefinition?>((CrsDefinition?)null));
        return registry;
    }

    private sealed class OgcFixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var payload = (path, query) switch
            {
                ("/geoserver/wfs", var q) when q.Contains("DescribeFeatureType", StringComparison.OrdinalIgnoreCase) => WfsSchema,
                ("/geoserver/wfs", _) => WfsCapabilities,
                ("/geoserver/wms", _) => WmsCapabilities,
                ("/geoserver/gwc/service/wmts", _) => WmtsCapabilities,
                _ => throw new HttpRequestException("Unexpected test URL.")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_exception);
    }

    private sealed class DescribeFeatureTypeFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("DescribeFeatureType", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("secret=/var/private/provider-token"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(WfsCapabilities)
            });
        }
    }

    private const string WfsCapabilities = """
        <wfs:WFS_Capabilities xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:ows="http://www.opengis.net/ows/1.1" version="2.0.0">
          <ows:ServiceIdentification>
            <ows:Title>Reference WFS</ows:Title>
          </ows:ServiceIdentification>
          <wfs:FeatureTypeList>
            <wfs:FeatureType>
              <wfs:Name>topp:states</wfs:Name>
              <wfs:Title>States</wfs:Title>
              <wfs:DefaultCRS>EPSG:4326</wfs:DefaultCRS>
            </wfs:FeatureType>
          </wfs:FeatureTypeList>
        </wfs:WFS_Capabilities>
        """;

    private const string WfsSchema = """
        <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:gml="http://www.opengis.net/gml/3.2" targetNamespace="http://www.openplans.org/topp">
          <xsd:complexType name="statesType">
            <xsd:complexContent>
              <xsd:extension base="gml:AbstractFeatureType">
                <xsd:sequence>
                  <xsd:element name="the_geom" minOccurs="0" nillable="true" type="gml:PointPropertyType" />
                  <xsd:element name="STATE_NAME" minOccurs="0" nillable="true" type="xsd:string" />
                </xsd:sequence>
              </xsd:extension>
            </xsd:complexContent>
          </xsd:complexType>
          <xsd:element name="states" substitutionGroup="gml:AbstractFeature" type="topp:statesType" />
        </xsd:schema>
        """;

    private const string WmsCapabilities = """
        <WMS_Capabilities version="1.3.0">
          <Service><Title>Reference WMS</Title></Service>
          <Capability>
            <Layer>
              <Title>Root</Title>
              <Layer>
                <Name>topp:states</Name>
                <Title>States</Title>
                <Style><Name>polygon</Name><Title>Polygon</Title></Style>
              </Layer>
            </Layer>
          </Capability>
        </WMS_Capabilities>
        """;

    private const string WmtsCapabilities = """
        <Capabilities xmlns="http://www.opengis.net/wmts/1.0" xmlns:ows="http://www.opengis.net/ows/1.1" version="1.0.0">
          <ows:ServiceIdentification><ows:Title>Reference WMTS</ows:Title></ows:ServiceIdentification>
          <Contents>
            <Layer>
              <ows:Title>States</ows:Title>
              <ows:Identifier>topp:states</ows:Identifier>
              <Style><ows:Identifier>default</ows:Identifier></Style>
              <TileMatrixSetLink><TileMatrixSet>EPSG:3857</TileMatrixSet></TileMatrixSetLink>
            </Layer>
            <TileMatrixSet><ows:Identifier>EPSG:3857</ows:Identifier></TileMatrixSet>
          </Contents>
        </Capabilities>
        """;
}
