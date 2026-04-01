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
                "/geoserver/rest/workspaces/demo/styles.json" => ("application/json", """{"styles":{"style":[{"name":"polygon"}]}}"""),
                "/geoserver/rest/workspaces/demo/styles/polygon.json" => ("application/json", """{"style":{"name":"polygon","format":"sld"}}"""),
                _ => throw new InvalidOperationException($"Unexpected GeoServer request path: {path}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, contentType)
            });
        }
    }
}
