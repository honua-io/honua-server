// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
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

    private static GeoServerImportService CreateService(GeoServerRestClient restClient)
    {
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);

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
                "/geoserver/rest/workspaces/demo/datastores.json" => ("application/json", """{"dataStores":{"dataStore":""}}"""),
                "/geoserver/rest/workspaces/demo/coveragestores.json" => ("application/json", """{"coverageStores":{"coverageStore":""}}"""),
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
                    <StyledLayerDescriptor>
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
                    <StyledLayerDescriptor>
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
}
