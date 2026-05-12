// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ExternalServiceDiscoveryServiceTests
{
    [UnitTest]
    public async Task DiscoverAsync_WithArcGisFeatureServerFixture_ReturnsLayerCandidateMetadata()
    {
        using var factory = new StubHttpClientFactory(ArcGisFeatureServerResponses());
        var service = new ExternalServiceDiscoveryService(
            factory,
            new AllowingNetworkGuard(),
            NullLogger<ExternalServiceDiscoveryService>.Instance);

        var response = await service.DiscoverAsync(new ExternalServiceDiscoveryRequest
        {
            Url = "https://services.example.test/arcgis/rest/services/Planning/FeatureServer",
            TimeoutSeconds = 5
        });

        response.SourceKind.Should().Be("arcgis-feature-server");
        response.ServiceType.Should().Be("FeatureServer");
        response.ServiceName.Should().Be("Planning");
        response.Srid.Should().Be(4326);
        response.Candidates.Should().ContainSingle();

        var candidate = response.Candidates[0];
        candidate.LayerId.Should().Be(0);
        candidate.Name.Should().Be("Parcels");
        candidate.GeometryType.Should().Be("esriGeometryPolygon");
        candidate.Srid.Should().Be(4326);
        candidate.FeatureCount.Should().Be(42);
        candidate.Extent.Should().BeEquivalentTo(new ExternalServiceExtent
        {
            XMin = -158.3,
            YMin = 21.2,
            XMax = -157.6,
            YMax = 21.8,
            Srid = 4326
        });
        candidate.Fields.Should().Contain(field => field.Name == "OBJECTID" && field.Type == "esriFieldTypeOID");
        candidate.Fields.Should().Contain(field => field.Name == "zone" && field.Length == 16 && field.Nullable == true);

        factory.RequestedUrls.Should().BeEquivalentTo([
            "https://services.example.test/arcgis/rest/services/Planning/FeatureServer?f=json",
            "https://services.example.test/arcgis/rest/services/Planning/FeatureServer/0?f=json",
            "https://services.example.test/arcgis/rest/services/Planning/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json"
        ], options => options.WithStrictOrdering());
    }

    [UnitTest]
    public async Task DiscoverAsync_WithOgcApiFeaturesUrl_ReturnsExplicitBacklogError()
    {
        using var factory = new StubHttpClientFactory(new Dictionary<string, string>());
        var service = new ExternalServiceDiscoveryService(
            factory,
            new AllowingNetworkGuard(),
            NullLogger<ExternalServiceDiscoveryService>.Instance);

        var act = async () => await service.DiscoverAsync(new ExternalServiceDiscoveryRequest
        {
            Url = "https://ogc.example.test/collections"
        });

        await act.Should().ThrowAsync<ExternalServiceDiscoveryRequestException>()
            .WithMessage("*honua-server#977*");
        factory.RequestedUrls.Should().BeEmpty();
    }

    internal static Dictionary<string, string> ArcGisFeatureServerResponses()
        => new(StringComparer.Ordinal)
        {
            ["https://services.example.test/arcgis/rest/services/Planning/FeatureServer?f=json"] = """
            {
              "currentVersion": 11.2,
              "serviceDescription": "Planning",
              "description": "Planning service",
              "spatialReference": { "wkid": 4326, "latestWkid": 4326 },
              "layers": [
                { "id": 0, "name": "Parcels" }
              ]
            }
            """,
            ["https://services.example.test/arcgis/rest/services/Planning/FeatureServer/0?f=json"] = """
            {
              "id": 0,
              "name": "Parcels",
              "type": "Feature Layer",
              "geometryType": "esriGeometryPolygon",
              "extent": {
                "xmin": -158.3,
                "ymin": 21.2,
                "xmax": -157.6,
                "ymax": 21.8,
                "spatialReference": { "wkid": 4326 }
              },
              "fields": [
                { "name": "OBJECTID", "type": "esriFieldTypeOID", "alias": "OBJECTID", "nullable": false },
                { "name": "zone", "type": "esriFieldTypeString", "alias": "Zone", "length": 16, "nullable": true }
              ]
            }
            """,
            ["https://services.example.test/arcgis/rest/services/Planning/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json"] = """
            {
              "count": 42
            }
            """
        };

    internal sealed class AllowingNetworkGuard : IExternalServiceDiscoveryNetworkGuard
    {
        public Task<bool> IsDisallowedAsync(Uri uri, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    internal sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly StubHttpMessageHandler _handler;
        private readonly HttpClient _client;

        public StubHttpClientFactory(IReadOnlyDictionary<string, string> responses)
        {
            _handler = new StubHttpMessageHandler(responses);
            _client = new HttpClient(_handler);
        }

        public IReadOnlyList<string> RequestedUrls => _handler.RequestedUrls;

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
            _handler.Dispose();
        }
    }

    private sealed class StubHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        private readonly List<string> _requestedUrls = [];

        public IReadOnlyList<string> RequestedUrls => _requestedUrls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUrl = request.RequestUri?.ToString() ?? string.Empty;
            _requestedUrls.Add(requestUrl);

            if (!responses.TryGetValue(requestUrl, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
