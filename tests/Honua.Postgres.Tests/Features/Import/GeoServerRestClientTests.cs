// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoServerRestClientTests
{
    [Fact]
    public async Task DiscoverServiceAsync_WithVersionXml_ParsesGeoServerVersionMetadata()
    {
        using var httpClient = new HttpClient(new StubGeoServerHandler());
        var client = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);

        var result = await client.DiscoverServiceAsync(
            "https://example.com/geoserver/rest",
            username: null,
            password: null,
            includeCompatibilityAnalysis: false,
            includeStyleContent: false,
            timeoutSeconds: 5,
            maxRetryAttempts: 0,
            CancellationToken.None);

        result.Version.Should().Be("2.28.0");
        result.BuildTimestamp.Should().Be("13-Oct-2025 05:02");
        result.GitRevision.Should().Be("16064e8d72a06ac36b373d8cd7cf328bde2be7cd");
    }

    [Fact]
    public async Task DiscoverServiceAsync_ConcurrentCallsWithDifferentCredentials_UsesPerRequestAuthorizationHeaders()
    {
        using var handler = new CoordinatedGeoServerHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);

        var alphaTask = client.DiscoverServiceAsync(
            "https://alpha.example/geoserver/rest",
            username: "alpha-user",
            password: "alpha-pass",
            includeCompatibilityAnalysis: false,
            includeStyleContent: false,
            timeoutSeconds: 5,
            maxRetryAttempts: 0,
            CancellationToken.None);

        await handler.WaitForAlphaVersionRequestAsync();

        var betaTask = client.DiscoverServiceAsync(
            "https://beta.example/geoserver/rest",
            username: "beta-user",
            password: "beta-pass",
            includeCompatibilityAnalysis: false,
            includeStyleContent: false,
            timeoutSeconds: 5,
            maxRetryAttempts: 0,
            CancellationToken.None);

        await Task.WhenAll(alphaTask, betaTask);

        handler.GetAuthorizations("alpha.example").Should().OnlyContain(value => value == CreateBasicAuthorization("alpha-user", "alpha-pass"));
        handler.GetAuthorizations("beta.example").Should().OnlyContain(value => value == CreateBasicAuthorization("beta-user", "beta-pass"));
    }

    [Fact]
    public async Task DiscoverServiceAsync_WithLayerGroupStylesAndBounds_ParsesLayerGroupMetadata()
    {
        using var httpClient = new HttpClient(new LayerGroupMetadataGeoServerHandler());
        var client = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);

        var result = await client.DiscoverServiceAsync(
            "https://example.com/geoserver/rest",
            username: null,
            password: null,
            includeCompatibilityAnalysis: false,
            includeStyleContent: false,
            timeoutSeconds: 5,
            maxRetryAttempts: 0,
            CancellationToken.None);

        var layerGroup = result.LayerGroups.Should().ContainSingle(group => group.Name == "transport").Subject;
        layerGroup.Styles.Should().ContainSingle(style => style.Name == "group-style" && style.WorkspaceName == "demo");
        layerGroup.Bounds.Should().NotBeNull();
        layerGroup.Bounds!.CRS.Should().Be("EPSG:3857");
        layerGroup.Bounds.MinX.Should().Be(-10.5);
        layerGroup.Bounds.MaxY.Should().Be(45.75);
    }

    private static string CreateBasicAuthorization(string username, string password)
        => $"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"))}";

    private sealed class StubGeoServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var (contentType, payload) = path switch
            {
                "/geoserver/rest/about/version.xml" => ("application/xml", """
                    <about>
                      <resource name="GeoServer">
                        <Build-Timestamp>13-Oct-2025 05:02</Build-Timestamp>
                        <Version>2.28.0</Version>
                        <Git-Revision>16064e8d72a06ac36b373d8cd7cf328bde2be7cd</Git-Revision>
                      </resource>
                      <resource name="GeoTools">
                        <Build-Timestamp>12-Oct-2025 00:03</Build-Timestamp>
                        <Version>34.0</Version>
                        <Git-Revision>b07b3543dba73763574d6e1749dfd72e4b14fe90</Git-Revision>
                      </resource>
                    </about>
                    """),
                "/geoserver/rest/settings.json" => ("application/json", """{"global":{}}"""),
                "/geoserver/rest/workspaces.json" => ("application/json", """{"workspaces":{"workspace":""}}"""),
                "/geoserver/rest/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/styles.json" => ("application/json", """{"styles":{"style":""}}"""),
                _ => throw new InvalidOperationException($"Unexpected GeoServer request path: {path}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, contentType)
            });
        }
    }

    private sealed class CoordinatedGeoServerHandler : HttpMessageHandler, IDisposable
    {
        private readonly TaskCompletionSource _alphaVersionRequestSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _betaRequestSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string?>> _authorizations = new(StringComparer.OrdinalIgnoreCase);

        public Task WaitForAlphaVersionRequestAsync()
            => _alphaVersionRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public string?[] GetAuthorizations(string host)
            => _authorizations.TryGetValue(host, out var values)
                ? values.ToArray()
                : Array.Empty<string?>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            _authorizations.GetOrAdd(host, _ => new ConcurrentQueue<string?>())
                .Enqueue(request.Headers.Authorization?.ToString());

            if (string.Equals(host, "alpha.example", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, "/geoserver/rest/about/version.xml", StringComparison.Ordinal))
            {
                _alphaVersionRequestSeen.TrySetResult();
                await _betaRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            else if (string.Equals(host, "beta.example", StringComparison.OrdinalIgnoreCase))
            {
                _betaRequestSeen.TrySetResult();
            }

            var (contentType, payload) = path switch
            {
                "/geoserver/rest/about/version.xml" => ("application/xml", """
                    <about>
                      <resource name="GeoServer">
                        <Version>2.28.0</Version>
                      </resource>
                    </about>
                    """),
                "/geoserver/rest/settings.json" => ("application/json", """{"global":{}}"""),
                "/geoserver/rest/workspaces.json" => ("application/json", """{"workspaces":{"workspace":""}}"""),
                "/geoserver/rest/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/styles.json" => ("application/json", """{"styles":{"style":""}}"""),
                _ => throw new InvalidOperationException($"Unexpected GeoServer request path: {path}")
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, contentType)
            };
        }
    }

    private sealed class LayerGroupMetadataGeoServerHandler : HttpMessageHandler
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
                "/geoserver/rest/settings.json" => ("application/json", """{"global":{}}"""),
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
}
