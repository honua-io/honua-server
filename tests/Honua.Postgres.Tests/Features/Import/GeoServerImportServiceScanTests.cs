// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoServerImportServiceScanTests
{
    [Fact]
    public async Task ScanSourceAsync_WorkspaceQualifiedStyle_UsesScopedStyleAndContainerCompatibility()
    {
        using var httpClient = new HttpClient(new ScopedStyleGeoServerHandler());
        var restClient = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);
        var service = CreateService(restClient);

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            IncludeCompatibilityAnalysis = true
        });

        artifact.Resources.Should().ContainSingle(resource => resource.Id == "layer:demo:states")
            .Which.StyleIds.Should().Equal("style:demo:polygon");
        artifact.Styles.Should().Contain(style => style.Id == "style:global:polygon");
        artifact.Containers.Should().ContainSingle(container => container.Id == "workspace:demo")
            .Which.Compatibility.Level.Should().Be("incompatible");
    }

    [Fact]
    public async Task ScanSourceAsync_WithPartialCredentials_ReportsAnonymousAuthPosture()
    {
        using var httpClient = new HttpClient(new ScopedStyleGeoServerHandler());
        var restClient = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);
        var service = CreateService(restClient);

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            Username = "admin",
            IncludeCompatibilityAnalysis = true
        });

        artifact.AuthPosture.Mode.Should().Be("anonymous");
        artifact.AuthPosture.CredentialsSupplied.Should().BeFalse();
        artifact.AuthPosture.AccessConfirmed.Should().BeTrue();
        artifact.AuthPosture.Notes.Should().ContainSingle()
            .Which.Should().Contain("requires both username and password");
    }

    [Fact]
    public async Task ScanSourceAsync_WithLayerGroupStylesAndBounds_PopulatesArtifactLinksAndSpatialReferences()
    {
        using var httpClient = new HttpClient(new LayerGroupGeoServerHandler());
        var restClient = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        crsRegistry.Setup(registry => registry.ResolveBySridAsync(3857, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<CrsDefinition?>((CrsDefinition?)null));
        var service = CreateService(restClient, crsRegistry);

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            IncludeCompatibilityAnalysis = true
        });

        var layerGroup = artifact.Resources.Should().ContainSingle(resource => resource.Id == "layer-group:demo:transport").Subject;
        layerGroup.StyleIds.Should().Equal("style:demo:group-style");
        layerGroup.Capabilities.Should().BeEmpty();
        layerGroup.SpatialReferences.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new MigrationSpatialReferenceInfo
            {
                Role = "bounds",
                SourceValue = "EPSG:3857",
                Srid = 3857,
                CrsUri = "http://www.opengis.net/def/crs/EPSG/0/3857",
                IsGeographic = false
            }, options => options.Excluding(info => info.Datum).Excluding(info => info.Unit).Excluding(info => info.AxisOrder));

        artifact.Styles.Should().ContainSingle(style => style.Id == "style:demo:group-style")
            .Which.ResourceIds.Should().Equal("layer-group:demo:transport");
    }

    [Fact]
    public async Task ScanSourceAsync_WithExternalGraphicUrls_SanitizesDependencyAddressesAndIds()
    {
        using var httpClient = new HttpClient(new ScopedStyleGeoServerHandler());
        var restClient = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);
        var service = CreateService(restClient);

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            IncludeStyleContent = true,
            IncludeCompatibilityAnalysis = true
        });

        var dependency = artifact.ExternalDependencies.Should()
            .ContainSingle(item => item.Kind == "external-graphic" && item.ResourceId == "style:demo:polygon")
            .Subject;
        dependency.Address.Should().Be("https://example.com/styles/marker.svg");
        dependency.Id.Should().MatchRegex("^style:demo:polygon:external:[0-9a-f]{16}$");
        dependency.Id.Should().NotContain("https://");
        dependency.Id.Should().NotContain("secret");
        dependency.Id.Should().NotContain("token");
        artifact.Styles.Should().ContainSingle(style => style.Id == "style:demo:polygon")
            .Which.ExternalDependencyIds.Should().ContainSingle(dependency.Id);
    }

    [Fact]
    public async Task ScanSourceAsync_WithSldNamespaceDeclarations_DoesNotTreatSchemaUrisAsExternalDependencies()
    {
        using var httpClient = new HttpClient(new ScopedStyleGeoServerHandler());
        var restClient = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);
        var service = CreateService(restClient);

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            IncludeStyleContent = true,
            IncludeCompatibilityAnalysis = true
        });

        artifact.ExternalDependencies
            .Where(item => item.Kind == "external-graphic")
            .Select(item => item.Address)
            .Should()
            .OnlyContain(address => address == "https://example.com/styles/marker.svg");
    }

    [Fact]
    public async Task ScanSourceAsync_WithCredentialBearingStoreUrls_RedactsDependencyMetadataAndAddress()
    {
        using var httpClient = new HttpClient(new ScopedStyleGeoServerHandler());
        var restClient = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);
        var service = CreateService(restClient);

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            IncludeCompatibilityAnalysis = true
        });

        var dataStoreDependency = artifact.ExternalDependencies.Should().ContainSingle(item => item.Id == "datastore:demo:pg").Subject;
        dataStoreDependency.Address.Should().BeNull();
        dataStoreDependency.Metadata.Should().ContainKey("url").WhoseValue.Should().Be("[redacted]");

        var coverageStoreDependency = artifact.ExternalDependencies.Should().ContainSingle(item => item.Id == "coverage-store:demo:imagery").Subject;
        coverageStoreDependency.Address.Should().BeNull();
        coverageStoreDependency.Metadata.Should().ContainKey("url").WhoseValue.Should().Be("[redacted]");
    }

    [Fact]
    public async Task ScanSourceAsync_WithoutCrsMetadata_OmitsPlaceholderSpatialReferences()
    {
        using var httpClient = new HttpClient(new ScopedStyleGeoServerHandler());
        var restClient = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);
        var service = CreateService(restClient);

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            IncludeCompatibilityAnalysis = true
        });

        artifact.Resources.Should().ContainSingle(resource => resource.Id == "layer:demo:states")
            .Which.SpatialReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanSourceAsync_WithLayerBounds_PopulatesLayerBoundsSpatialReferences()
    {
        using var httpClient = new HttpClient(new LayerBoundsGeoServerHandler());
        var restClient = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);
        crsRegistry.Setup(registry => registry.ResolveBySridAsync(4326, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<CrsDefinition?>((CrsDefinition?)null));
        crsRegistry.Setup(registry => registry.ResolveBySridAsync(3857, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<CrsDefinition?>((CrsDefinition?)null));
        var service = CreateService(restClient, crsRegistry);

        var artifact = await service.ScanSourceAsync(new GeoServerDiscoveryRequest
        {
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            IncludeCompatibilityAnalysis = true
        });

        artifact.Resources.Should().ContainSingle(resource => resource.Id == "layer:demo:roads")
            .Which.SpatialReferences.Should().BeEquivalentTo(
                new[]
                {
                    new MigrationSpatialReferenceInfo
                    {
                        Role = "latlon-bounds",
                        SourceValue = "EPSG:4326",
                        Srid = 4326,
                        CrsUri = "http://www.opengis.net/def/crs/EPSG/0/4326",
                        IsGeographic = true
                    },
                    new MigrationSpatialReferenceInfo
                    {
                        Role = "native-bounds",
                        SourceValue = "EPSG:3857",
                        Srid = 3857,
                        CrsUri = "http://www.opengis.net/def/crs/EPSG/0/3857",
                        IsGeographic = false
                    }
                },
                options => options
                    .Excluding(info => info.Datum)
                    .Excluding(info => info.Unit)
                    .Excluding(info => info.AxisOrder));
    }

    private static GeoServerImportService CreateService(GeoServerRestClient restClient, Mock<ICrsRegistry>? crsRegistry = null)
    {
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        crsRegistry ??= new Mock<ICrsRegistry>(MockBehavior.Strict);

        return new GeoServerImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            NullLogger<GeoServerImportService>.Instance);
    }

    private sealed class ScopedStyleGeoServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var (contentType, payload) = path switch
            {
                "/geoserver/rest/about/version.xml" => ("application/xml", """
                    <about>
                      <resource name="GeoServer">
                        <Version>2.28.0</Version>
                      </resource>
                    </about>
                    """),
                "/geoserver/rest/settings.json" => ("application/json", """{"global":{"title":"Demo GeoServer"}}"""),
                "/geoserver/rest/workspaces.json" => ("application/json", """{"workspaces":{"workspace":[{"name":"demo"}]}}"""),
                "/geoserver/rest/workspaces/demo.json" => ("application/json", """{"workspace":{"name":"demo"}}"""),
                "/geoserver/rest/workspaces/demo/datastores.json" => ("application/json", """{"dataStores":{"dataStore":[{"name":"pg"}]}}"""),
                "/geoserver/rest/workspaces/demo/datastores/pg.json" => ("application/json", """
                    {
                      "dataStore": {
                        "name": "pg",
                        "type": "PostGIS",
                        "connectionParameters": {
                          "entry": [
                            { "@key": "url", "$": "https://user:secret@example.com/postgis?token=abc#frag" },
                            { "@key": "dbtype", "$": "postgis" }
                          ]
                        }
                      }
                    }
                    """),
                "/geoserver/rest/workspaces/demo/coveragestores.json" => ("application/json", """{"coverageStores":{"coverageStore":[{"name":"imagery"}]}}"""),
                "/geoserver/rest/workspaces/demo/coveragestores/imagery.json" => ("application/json", """
                    {
                      "coverageStore": {
                        "name": "imagery",
                        "type": "GeoTIFF",
                        "url": "https://user:secret@example.com/imagery.tif?token=abc#frag"
                      }
                    }
                    """),
                "/geoserver/rest/workspaces/demo/layers.json" => ("application/json", """{"layers":{"layer":[{"name":"states"}]}}"""),
                "/geoserver/rest/workspaces/demo/layers/states.json" => ("application/json", """
                    {
                      "layer": {
                        "name": "states",
                        "defaultStyle": {
                          "name": "polygon",
                          "workspace": "demo"
                        }
                      }
                    }
                    """),
                "/geoserver/rest/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/workspaces/demo/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/styles.json" => ("application/json", """{"styles":{"style":[{"name":"polygon"}]}}"""),
                "/geoserver/rest/styles/polygon.json" => ("application/json", """{"style":{"name":"polygon","format":"sld"}}"""),
                "/geoserver/rest/styles/polygon.sld" => ("application/vnd.ogc.sld+xml", """
                    <StyledLayerDescriptor
                        xmlns="http://www.opengis.net/sld"
                        xmlns:ogc="http://www.opengis.net/ogc"
                        xmlns:xlink="http://www.w3.org/1999/xlink"
                        xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                        xsi:schemaLocation="http://www.opengis.net/sld StyledLayerDescriptor.xsd">
                      <NamedLayer>
                        <UserStyle>
                          <FeatureTypeStyle>
                            <Rule>
                              <PointSymbolizer>
                                <Graphic>
                                  <ExternalGraphic>
                                    <OnlineResource xlink:href="https://user:secret@example.com/styles/marker.svg?token=abc#frag" />
                                  </ExternalGraphic>
                                </Graphic>
                              </PointSymbolizer>
                            </Rule>
                          </FeatureTypeStyle>
                        </UserStyle>
                      </NamedLayer>
                    </StyledLayerDescriptor>
                    """),
                "/geoserver/rest/workspaces/demo/styles.json" => ("application/json", """{"styles":{"style":[{"name":"polygon"}]}}"""),
                "/geoserver/rest/workspaces/demo/styles/polygon.json" => ("application/json", """{"style":{"name":"polygon","format":"sld"}}"""),
                "/geoserver/rest/workspaces/demo/styles/polygon.sld" => ("application/vnd.ogc.sld+xml", """
                    <StyledLayerDescriptor
                        xmlns="http://www.opengis.net/sld"
                        xmlns:ogc="http://www.opengis.net/ogc"
                        xmlns:xlink="http://www.w3.org/1999/xlink"
                        xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                        xsi:schemaLocation="http://www.opengis.net/sld StyledLayerDescriptor.xsd">
                      <NamedLayer>
                        <UserStyle>
                          <FeatureTypeStyle>
                            <Rule>
                              <PointSymbolizer>
                                <Graphic>
                                  <ExternalGraphic>
                                    <OnlineResource xlink:href="https://user:secret@example.com/styles/marker.svg?token=abc#frag" />
                                  </ExternalGraphic>
                                </Graphic>
                              </PointSymbolizer>
                            </Rule>
                          </FeatureTypeStyle>
                        </UserStyle>
                      </NamedLayer>
                    </StyledLayerDescriptor>
                    """),
                _ => throw new InvalidOperationException($"Unexpected GeoServer request path: {path}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, contentType)
            });
        }
    }

    private sealed class LayerGroupGeoServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var (contentType, payload) = path switch
            {
                "/geoserver/rest/about/version.xml" => ("application/xml", """
                    <about>
                      <resource name="GeoServer">
                        <Version>2.28.0</Version>
                      </resource>
                    </about>
                    """),
                "/geoserver/rest/settings.json" => ("application/json", """{"global":{"title":"Demo GeoServer"}}"""),
                "/geoserver/rest/workspaces.json" => ("application/json", """{"workspaces":{"workspace":[{"name":"demo"}]}}"""),
                "/geoserver/rest/workspaces/demo.json" => ("application/json", """{"workspace":{"name":"demo"}}"""),
                "/geoserver/rest/workspaces/demo/datastores.json" => ("application/json", """{"dataStores":{"dataStore":""}}"""),
                "/geoserver/rest/workspaces/demo/coveragestores.json" => ("application/json", """{"coverageStores":{"coverageStore":""}}"""),
                "/geoserver/rest/workspaces/demo/layers.json" => ("application/json", """{"layers":{"layer":""}}"""),
                "/geoserver/rest/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/workspaces/demo/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":[{"name":"transport"}]}}"""),
                "/geoserver/rest/workspaces/demo/layergroups/transport.json" => ("application/json", """
                    {
                      "layerGroup": {
                        "name": "transport",
                        "title": "Transport",
                        "mode": "SINGLE",
                        "publishables": {
                          "published": [
                            { "@type": "layer", "name": "roads", "workspace": "demo" }
                          ]
                        },
                        "styles": {
                          "style": [
                            { "name": "group-style", "workspace": "demo" }
                          ]
                        },
                        "bounds": {
                          "minx": -10.5,
                          "miny": 20.25,
                          "maxx": 11.5,
                          "maxy": 45.75,
                          "crs": "EPSG:3857"
                        }
                      }
                    }
                    """),
                "/geoserver/rest/styles.json" => ("application/json", """{"styles":{"style":""}}"""),
                "/geoserver/rest/workspaces/demo/styles.json" => ("application/json", """{"styles":{"style":[{"name":"group-style"}]}}"""),
                "/geoserver/rest/workspaces/demo/styles/group-style.json" => ("application/json", """{"style":{"name":"group-style","format":"sld"}}"""),
                _ => throw new InvalidOperationException($"Unexpected GeoServer request path: {path}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, contentType)
            });
        }
    }

    private sealed class LayerBoundsGeoServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var (contentType, payload) = path switch
            {
                "/geoserver/rest/about/version.xml" => ("application/xml", """
                    <about>
                      <resource name="GeoServer">
                        <Version>2.28.0</Version>
                      </resource>
                    </about>
                    """),
                "/geoserver/rest/settings.json" => ("application/json", """{"global":{"title":"Demo GeoServer"}}"""),
                "/geoserver/rest/workspaces.json" => ("application/json", """{"workspaces":{"workspace":[{"name":"demo"}]}}"""),
                "/geoserver/rest/workspaces/demo.json" => ("application/json", """{"workspace":{"name":"demo"}}"""),
                "/geoserver/rest/workspaces/demo/datastores.json" => ("application/json", """{"dataStores":{"dataStore":""}}"""),
                "/geoserver/rest/workspaces/demo/coveragestores.json" => ("application/json", """{"coverageStores":{"coverageStore":""}}"""),
                "/geoserver/rest/workspaces/demo/layers.json" => ("application/json", """{"layers":{"layer":[{"name":"roads"}]}}"""),
                "/geoserver/rest/workspaces/demo/layers/roads.json" => ("application/json", """
                    {
                      "layer": {
                        "name": "roads",
                        "latLonBoundingBox": {
                          "minx": -123.5,
                          "miny": 45.0,
                          "maxx": -122.0,
                          "maxy": 46.5,
                          "crs": "EPSG:4326"
                        },
                        "nativeBoundingBox": {
                          "minx": 1000,
                          "miny": 1000,
                          "maxx": 4000,
                          "maxy": 6000,
                          "crs": "EPSG:3857"
                        }
                      }
                    }
                    """),
                "/geoserver/rest/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/workspaces/demo/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/styles.json" => ("application/json", """{"styles":{"style":""}}"""),
                "/geoserver/rest/workspaces/demo/styles.json" => ("application/json", """{"styles":{"style":""}}"""),
                _ => throw new InvalidOperationException($"Unexpected GeoServer request path: {path}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, contentType)
            });
        }
    }
}
