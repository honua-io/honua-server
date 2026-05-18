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
    public async Task DiscoverAsync_WithOgcApiFeaturesUrl_ReturnsCollectionCandidates()
    {
        using var factory = new StubHttpClientFactory(OgcApiFeaturesResponses());
        var service = new ExternalServiceDiscoveryService(
            factory,
            new AllowingNetworkGuard(),
            NullLogger<ExternalServiceDiscoveryService>.Instance);

        var response = await service.DiscoverAsync(new ExternalServiceDiscoveryRequest
        {
            Url = "https://ogc.example.test/api",
            TimeoutSeconds = 5
        });

        response.SourceKind.Should().Be("ogc-api-features");
        response.ServiceType.Should().Be("OGC API Features");
        response.ServiceName.Should().Be("Honolulu OGC");
        response.NormalizedUrl.Should().Be("https://ogc.example.test/api/collections");
        response.Srid.Should().Be(4326);
        response.Candidates.Should().ContainSingle();

        var candidate = response.Candidates[0];
        candidate.LayerId.Should().BeNull();
        candidate.ExternalId.Should().Be("zoning");
        candidate.Name.Should().Be("Zoning");
        candidate.LayerType.Should().Be("collection");
        candidate.GeometryType.Should().Be("feature");
        candidate.Srid.Should().Be(4326);
        candidate.FeatureCount.Should().Be(7);
        candidate.ServiceUrl.Should().Be("https://ogc.example.test/api/collections/zoning");
        candidate.Extent.Should().BeEquivalentTo(new ExternalServiceExtent
        {
            XMin = -158.3,
            YMin = 21.2,
            XMax = -157.6,
            YMax = 21.8,
            Srid = 4326
        });

        factory.RequestedUrls.Should().BeEquivalentTo([
            "https://ogc.example.test/api",
            "https://ogc.example.test/api/collections"
        ], options => options.WithStrictOrdering());
    }

    [UnitTest]
    public async Task DiscoverAsync_WithWfsGetCapabilitiesFixture_ReturnsFeatureTypeCandidates()
    {
        using var factory = new StubHttpClientFactory(WfsGetCapabilitiesResponses());
        var service = new ExternalServiceDiscoveryService(
            factory,
            new AllowingNetworkGuard(),
            NullLogger<ExternalServiceDiscoveryService>.Instance);

        var response = await service.DiscoverAsync(new ExternalServiceDiscoveryRequest
        {
            Url = "https://wfs.example.test/geoserver/wfs?SERVICE=WFS&REQUEST=GetCapabilities",
            TimeoutSeconds = 5
        });

        response.SourceKind.Should().Be("wfs");
        response.ServiceType.Should().Be("WFS 2.0.0");
        response.ServiceName.Should().Be("Honolulu WFS");
        response.NormalizedUrl.Should().Be("https://wfs.example.test/geoserver/wfs?service=WFS&request=GetCapabilities");
        response.Candidates.Should().ContainSingle();

        var candidate = response.Candidates[0];
        candidate.LayerId.Should().BeNull();
        candidate.ExternalId.Should().Be("honua:parcels");
        candidate.Name.Should().Be("honua:parcels");
        candidate.Title.Should().Be("Parcels");
        candidate.Description.Should().Be("Parcel polygons");
        candidate.LayerType.Should().Be("feature-type");
        candidate.Srid.Should().Be(4326);
        candidate.Fields.Should().BeEmpty();
        candidate.Extent.Should().BeEquivalentTo(new ExternalServiceExtent
        {
            XMin = -158.3,
            YMin = 21.2,
            XMax = -157.6,
            YMax = 21.8,
            Srid = 4326
        });

        factory.RequestedUrls.Should().BeEquivalentTo([
            "https://wfs.example.test/geoserver/wfs?service=WFS&request=GetCapabilities"
        ], options => options.WithStrictOrdering());
    }

    internal static Dictionary<string, string> AllDiscoveryResponses()
    {
        var responses = ArcGisFeatureServerResponses();
        foreach (var (url, body) in OgcApiFeaturesResponses())
        {
            responses[url] = body;
        }

        foreach (var (url, body) in WfsGetCapabilitiesResponses())
        {
            responses[url] = body;
        }

        return responses;
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

    internal static Dictionary<string, string> OgcApiFeaturesResponses()
        => new(StringComparer.Ordinal)
        {
            ["https://ogc.example.test/api"] = """
            {
              "title": "Honolulu OGC",
              "description": "OGC API Features fixture",
              "links": [
                {
                  "href": "https://ogc.example.test/api/collections",
                  "rel": "data",
                  "type": "application/json",
                  "title": "Collections"
                }
              ]
            }
            """,
            ["https://ogc.example.test/api/collections"] = """
            {
              "title": "Honolulu Collections",
              "collections": [
                {
                  "id": "zoning",
                  "title": "Zoning",
                  "description": "Zoning district polygons",
                  "itemType": "feature",
                  "storageCrs": "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
                  "crs": [
                    "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
                  ],
                  "itemCount": 7,
                  "extent": {
                    "spatial": {
                      "bbox": [
                        [-158.3, 21.2, -157.6, 21.8]
                      ],
                      "crs": "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
                    }
                  },
                  "links": [
                    {
                      "href": "https://ogc.example.test/api/collections/zoning",
                      "rel": "self",
                      "type": "application/json"
                    }
                  ]
                }
              ]
            }
            """
        };

    internal static Dictionary<string, string> WfsGetCapabilitiesResponses()
        => new(StringComparer.Ordinal)
        {
            ["https://wfs.example.test/geoserver/wfs?service=WFS&request=GetCapabilities"] = """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:WFS_Capabilities
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:ows="http://www.opengis.net/ows/1.1"
                version="2.0.0">
              <ows:ServiceIdentification>
                <ows:Title>Honolulu WFS</ows:Title>
                <ows:Abstract>WFS discovery fixture</ows:Abstract>
              </ows:ServiceIdentification>
              <wfs:FeatureTypeList>
                <wfs:FeatureType>
                  <wfs:Name>honua:parcels</wfs:Name>
                  <wfs:Title>Parcels</wfs:Title>
                  <wfs:Abstract>Parcel polygons</wfs:Abstract>
                  <wfs:DefaultCRS>urn:ogc:def:crs:EPSG::4326</wfs:DefaultCRS>
                  <wfs:OtherCRS>urn:ogc:def:crs:EPSG::3857</wfs:OtherCRS>
                  <ows:WGS84BoundingBox>
                    <ows:LowerCorner>-158.3 21.2</ows:LowerCorner>
                    <ows:UpperCorner>-157.6 21.8</ows:UpperCorner>
                  </ows:WGS84BoundingBox>
                </wfs:FeatureType>
              </wfs:FeatureTypeList>
            </wfs:WFS_Capabilities>
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
