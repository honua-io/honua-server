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
        artifact.FidelityClassifications.Should().ContainSingle(record =>
                record.Id == "classification:feature-type:topp-states:feature-schema")
            .Which.Should().Match<MigrationFidelityClassificationRecord>(record =>
                record.AutomationStatus == MigrationFidelityAutomationStatuses.Automated &&
                record.Code == ImportCompatibilityCodes.OgcWfsFeatureSource &&
                record.TargetKind == "feature-layer");
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
    public async Task ScanSourceAsync_ServiceUrlWithSecretQuery_RedactsArtifactUrls()
    {
        using var httpClient = new HttpClient(new OgcFixtureHandler());
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WFS",
            ServiceUrl = "https://example.com/geoserver/wfs?token=super-secret&request=GetCapabilities&service=WFS&version=2.0.0",
            Version = "2.0.0",
            TimeoutSeconds = 10
        });

        artifact.Source.BaseUrl.Should().Be("https://example.com/geoserver/wfs");
        artifact.ExternalDependencies.Should().ContainSingle(dependency => dependency.Kind == "ogc-endpoint")
            .Which.Address.Should().Be("https://example.com/geoserver/wfs?request=GetCapabilities&service=WFS&version=2.0.0");
        artifact.Source.BaseUrl.Should().NotContain("super-secret");
        artifact.ExternalDependencies.Select(dependency => dependency.Address)
            .Should().OnlyContain(address => address == null || !address.Contains("super-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanSourceAsync_ServiceUrlWithRoutingQuery_PreservesSafeDependencyAddress()
    {
        using var httpClient = new HttpClient(new OgcFixtureHandler());
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WFS",
            ServiceUrl = "https://example.com/geoserver/wfs?workspace=topp&map=city&token=super-secret&accessToken=super-secret&id_token_hint=super-secret&x-amz-security-token=super-secret",
            Version = "2.0.0",
            TimeoutSeconds = 10
        });

        artifact.Source.BaseUrl.Should().Be("https://example.com/geoserver/wfs");
        artifact.ExternalDependencies.Should().ContainSingle(dependency => dependency.Kind == "ogc-endpoint")
            .Which.Address.Should().Be("https://example.com/geoserver/wfs?map=city&request=GetCapabilities&service=WFS&version=2.0.0&workspace=topp");
        artifact.ExternalDependencies.Select(dependency => dependency.Address)
            .Should().OnlyContain(address => address == null || !address.Contains("super-secret", StringComparison.Ordinal));
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
        artifact.FidelityClassifications.Should().Contain(record =>
            record.SourceId == "feature-type:topp-states" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.ManualReview &&
            record.Code == ImportCompatibilityCodes.OgcFeatureSchemaManualReview);
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
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Id == "endpoint:wms:get-map" &&
            dependency.DependencyType == "render" &&
            dependency.Metadata["formats"] == "image/jpeg,image/png" &&
            dependency.Address == "https://example.com/geoserver/wms");
        artifact.ExternalDependencies.Select(dependency => dependency.Address)
            .Should().OnlyContain(address => address == null || !address.Contains("secret", StringComparison.OrdinalIgnoreCase));
        artifact.FidelityClassifications.Should().Contain(record =>
            record.SourceId == "wms-layer:topp-states" &&
            record.Category == "render-data-copy" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.Unsupported);
        artifact.FidelityClassifications.Should().Contain(record =>
            record.SourceId == "style:topp-states:polygon" &&
            record.Category == "style" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.ManualReview);
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
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Id == "endpoint:wmts:get-tile" &&
            dependency.DependencyType == "tile" &&
            dependency.Address == "https://example.com/geoserver/gwc/service/wmts");
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Id == "endpoint:wmts:tile:topp-states:image-png" &&
            dependency.ResourceId == "wmts-layer:topp-states" &&
            dependency.DependencyType == "resource-url:tile" &&
            dependency.Address != null &&
            dependency.Address.Contains("format=image%2Fpng", StringComparison.Ordinal));
        artifact.ExternalDependencies.Select(dependency => dependency.Address)
            .Should().OnlyContain(address => address == null || !address.Contains("secret", StringComparison.OrdinalIgnoreCase));
        artifact.FidelityClassifications.Should().Contain(record =>
            record.SourceId == "wmts-layer:topp-states" &&
            record.Category == "tile-data-copy" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.Unsupported);
        artifact.FidelityClassifications.Should().Contain(record =>
            record.SourceId == "tile-matrix-set:epsg-3857" &&
            record.Category == "tile-matrix-set" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.ManualReview);
    }

    [Fact]
    public async Task ScanSourceAsync_WcsCapabilitiesAndDescribeCoverage_ProducesCoverageInventory()
    {
        using var httpClient = new HttpClient(new OgcFixtureHandler());
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WCS",
            ServiceUrl = "https://example.com/geoserver/wcs",
            Version = "2.0.1",
            TimeoutSeconds = 10
        });

        artifact.SourceKind.Should().Be("ogc-wcs");
        artifact.Resources.Should().ContainSingle(resource => resource.Kind == "coverage" && resource.Name == "nurc:temperature");
        var resource = artifact.Resources.Single();
        resource.Fields.Should().ContainSingle(field => field.Name == "temperature" && field.FieldType == "Float32");
        resource.SpatialReferences.Should().ContainSingle(reference => reference.Srid == 4326);
        resource.Compatibility.Code.Should().Be(OgcCoverageMigrationCompatibilityCodes.CogSupported);
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Kind == "coverage-output-format" &&
            dependency.DependencyType == "cog");
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Kind == "coverage-axis" &&
            dependency.Name == "Lat");
        artifact.FidelityClassifications.Should().Contain(record =>
            record.Category == "coverage-metadata" &&
            record.AutomationStatus == MigrationFidelityAutomationStatuses.Assisted);
    }

    [Fact]
    public async Task ScanSourceAsync_WcsDescribeCoverageFailure_LeavesAccessConstraintsEmptyAndFlagsManualReview()
    {
        using var httpClient = new HttpClient(new DescribeCoverageFailureHandler());
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "WCS",
            ServiceUrl = "https://example.com/geoserver/wcs",
            Version = "2.0.1",
            TimeoutSeconds = 10
        });

        artifact.ScanCompleteness.Status.Should().Be("partial");
        artifact.ScanCompleteness.Warnings.Should().ContainSingle()
            .Which.Should().Be("Coverage nurc:temperature did not advertise output formats.");
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Kind == "coverage-service-metadata" &&
            !dependency.Metadata.ContainsKey("accessConstraints"));
        artifact.ExternalDependencies.Should().NotContain(dependency =>
            dependency.Kind == "coverage-service-metadata" &&
            dependency.Compatibility.Warnings.Any(warning =>
                warning.Contains("DescribeCoverage", StringComparison.OrdinalIgnoreCase)));
        artifact.Resources.Should().ContainSingle().Which.Compatibility.Code
            .Should().Be(OgcCoverageMigrationCompatibilityCodes.OutputFormatMissing);
    }

    [Fact]
    public async Task ScanSourceAsync_OgcApiCoveragesCollections_UsesOnlyCoverageOutputLinksForFormats()
    {
        using var httpClient = new HttpClient(new OgcFixtureHandler());
        var scanner = CreateScanner(httpClient, CreateCrsRegistry());

        var artifact = await scanner.ScanSourceAsync(new OgcServiceScanRequest
        {
            ServiceType = "OGC API Coverages",
            ServiceUrl = "https://coverages.example.com",
            TimeoutSeconds = 10
        });

        artifact.SourceKind.Should().Be("ogc-api-coverages");
        artifact.Resources.Should().ContainSingle(resource => resource.Kind == "coverage" && resource.Name == "ocean-forecast");
        artifact.Resources.Single().Compatibility.Code.Should().Be(OgcCoverageMigrationCompatibilityCodes.ScientificFormatUnsupported);
        artifact.ExternalDependencies.Should().Contain(dependency =>
            dependency.Kind == "coverage-output-format" &&
            dependency.Compatibility.Code == OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported);
        artifact.ExternalDependencies
            .Where(static dependency => dependency.Kind == "coverage-output-format")
            .Should().ContainSingle()
            .Which.Name.Should().Be("application/x-netcdf");
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
                ("/geoserver/wcs", var q) when q.Contains("DescribeCoverage", StringComparison.OrdinalIgnoreCase) => WcsDescribeCoverage,
                ("/geoserver/wcs", _) => WcsCapabilities,
                ("/collections", _) => OgcApiCoveragesCollections,
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

    private sealed class DescribeCoverageFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("DescribeCoverage", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("secret=/var/private/provider-token"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(WcsCapabilities)
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
        <WMS_Capabilities xmlns:xlink="http://www.w3.org/1999/xlink" version="1.3.0">
          <Service><Title>Reference WMS</Title></Service>
          <Capability>
            <Request>
              <GetMap>
                <Format>image/png</Format>
                <Format>image/jpeg</Format>
                <DCPType><HTTP><Get><OnlineResource xlink:href="https://example.com/geoserver/wms?token=secret" /></Get></HTTP></DCPType>
              </GetMap>
              <GetFeatureInfo>
                <Format>text/plain</Format>
                <DCPType><HTTP><Get><OnlineResource xlink:href="https://example.com/geoserver/wms?token=secret" /></Get></HTTP></DCPType>
              </GetFeatureInfo>
            </Request>
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
        <Capabilities xmlns="http://www.opengis.net/wmts/1.0" xmlns:ows="http://www.opengis.net/ows/1.1" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.0.0">
          <ows:ServiceIdentification><ows:Title>Reference WMTS</ows:Title></ows:ServiceIdentification>
          <ows:OperationsMetadata>
            <ows:Operation name="GetTile">
              <ows:DCP><ows:HTTP><ows:Get xlink:href="https://example.com/geoserver/gwc/service/wmts?token=secret" /></ows:HTTP></ows:DCP>
            </ows:Operation>
          </ows:OperationsMetadata>
          <Contents>
            <Layer>
              <ows:Title>States</ows:Title>
              <ows:Identifier>topp:states</ows:Identifier>
              <Style><ows:Identifier>default</ows:Identifier></Style>
              <ResourceURL format="image/png" resourceType="tile" template="https://example.com/geoserver/gwc/service/wmts/rest/topp:states/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png?token=secret&amp;format=image/png" />
              <TileMatrixSetLink><TileMatrixSet>EPSG:3857</TileMatrixSet></TileMatrixSetLink>
            </Layer>
            <TileMatrixSet><ows:Identifier>EPSG:3857</ows:Identifier></TileMatrixSet>
          </Contents>
        </Capabilities>
        """;

    private const string WcsCapabilities = """
        <wcs:Capabilities xmlns:wcs="http://www.opengis.net/wcs/2.0" xmlns:ows="http://www.opengis.net/ows/2.0" version="2.0.1">
          <ows:ServiceIdentification>
            <ows:Title>Reference WCS</ows:Title>
            <ows:Abstract>Coverage migration fixture</ows:Abstract>
          </ows:ServiceIdentification>
          <wcs:ServiceMetadata>
            <wcs:formatSupported>image/tiff; application=geotiff; profile=cloud-optimized-geotiff</wcs:formatSupported>
          </wcs:ServiceMetadata>
          <wcs:Contents>
            <wcs:CoverageSummary>
              <wcs:CoverageId>nurc:temperature</wcs:CoverageId>
            </wcs:CoverageSummary>
          </wcs:Contents>
        </wcs:Capabilities>
        """;

    private const string WcsDescribeCoverage = """
        <wcs:CoverageDescriptions xmlns:wcs="http://www.opengis.net/wcs/2.0" xmlns:gml="http://www.opengis.net/gml/3.2" xmlns:swe="http://www.opengis.net/swe/2.0">
          <wcs:CoverageDescription gml:id="nurc_temperature">
            <wcs:CoverageId>nurc:temperature</wcs:CoverageId>
            <gml:boundedBy>
              <gml:Envelope srsName="EPSG:4326" axisLabels="Lat Long">
                <gml:lowerCorner>-90 -180</gml:lowerCorner>
                <gml:upperCorner>90 180</gml:upperCorner>
              </gml:Envelope>
            </gml:boundedBy>
            <gml:domainSet>
              <gml:RectifiedGrid dimension="2">
                <gml:axisLabels>Lat Long</gml:axisLabels>
              </gml:RectifiedGrid>
            </gml:domainSet>
            <gml:rangeType>
              <swe:DataRecord>
                <swe:field name="temperature">
                  <swe:Quantity definition="temperature">
                    <swe:dataType>Float32</swe:dataType>
                    <swe:uom code="K" />
                    <swe:nilValues><swe:nilValue>-9999</swe:nilValue></swe:nilValues>
                    <swe:constraint><swe:AllowedValues><swe:interval>200 330</swe:interval></swe:AllowedValues></swe:constraint>
                  </swe:Quantity>
                </swe:field>
              </swe:DataRecord>
            </gml:rangeType>
            <wcs:format>image/tiff; application=geotiff; profile=cloud-optimized-geotiff</wcs:format>
          </wcs:CoverageDescription>
        </wcs:CoverageDescriptions>
        """;

    private const string OgcApiCoveragesCollections = """
        {
          "title": "Reference Coverages",
          "collections": [
            {
              "id": "ocean-forecast",
              "title": "Ocean forecast",
              "itemType": "coverage",
              "extent": {
                "spatial": {
                  "bbox": [[-180, -90, 180, 90]],
                  "crs": "EPSG:4326"
                }
              },
              "links": [
                { "rel": "self", "href": "https://coverages.example.com/collections/ocean-forecast", "type": "application/json" },
                { "rel": "alternate", "href": "https://coverages.example.com/collections/ocean-forecast?f=html", "type": "text/html" },
                { "rel": "items", "href": "https://coverages.example.com/collections/ocean-forecast/items", "type": "application/geo+json" },
                { "rel": "coverage", "href": "https://coverages.example.com/collections/ocean-forecast/coverage", "type": "application/x-netcdf" }
              ]
            }
          ]
        }
        """;
}
