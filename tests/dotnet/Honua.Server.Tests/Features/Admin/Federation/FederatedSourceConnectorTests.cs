// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Admin.Federation.Connectors;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Server.Tests.Features.Admin.Federation;

/// <summary>
/// Unit tests for the HTTP federated-source connectors (issue #341). Remote I/O is faked with a
/// capturing <see cref="HttpMessageHandler"/>, so these tests exercise the real push-down URI
/// construction and GeoJSON-to-canonical-feature mapping without contacting a live source.
/// </summary>
public sealed class FederatedSourceConnectorTests
{
    private const string GeoJsonResponse = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "id": 42,
              "geometry": { "type": "Point", "coordinates": [1.5, 2.5] },
              "properties": { "objectid": 42, "name": "Alpha" }
            },
            {
              "type": "Feature",
              "geometry": { "type": "Point", "coordinates": [3.0, 4.0] },
              "properties": { "name": "Bravo" }
            }
          ]
        }
        """;

    [UnitTest]
    public async Task EsriRest_PushesDownWhereEnvelopeAndPaging_AndMapsFeatures()
    {
        var (handler, factory) = CreateClient();
        var connector = new EsriRestFederatedSourceConnector(factory, NullLogger<EsriRestFederatedSourceConnector>.Instance);

        var query = new FeatureQuery
        {
            Where = "state = 'HI'",
            SpatialFilter = Envelope(-160, 18, -154, 23, srid: 4326),
            Limit = 25,
            Offset = 50,
        };

        var features = await Fetch(connector, EsriSource(), query);

        var uri = handler.LastRequestUri!;
        uri.AbsolutePath.Should().EndWith("/0/query");
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        q["f"].ToString().Should().Be("geojson");
        q["where"].ToString().Should().Be("state = 'HI'");
        q["geometryType"].ToString().Should().Be("esriGeometryEnvelope");
        q["geometry"].ToString().Should().Be("-160,18,-154,23");
        q["inSR"].ToString().Should().Be("4326");
        q["resultRecordCount"].ToString().Should().Be("25");
        q["resultOffset"].ToString().Should().Be("50");

        AssertMappedFeatures(features);
    }

    [UnitTest]
    public async Task EsriRest_NoAttributeFilter_SendsSelectAllWhere()
    {
        var (handler, factory) = CreateClient();
        var connector = new EsriRestFederatedSourceConnector(factory, NullLogger<EsriRestFederatedSourceConnector>.Instance);

        await Fetch(connector, EsriSource(), new FeatureQuery());

        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(handler.LastRequestUri!.Query);
        q["where"].ToString().Should().Be("1=1");
    }

    [UnitTest]
    public async Task OgcFeatures_PushesDownFilterBboxAndPaging_AndMapsFeatures()
    {
        var (handler, factory) = CreateClient();
        var connector = new OgcFeaturesFederatedSourceConnector(factory, NullLogger<OgcFeaturesFederatedSourceConnector>.Instance);

        var query = new FeatureQuery
        {
            Where = "pop > 1000",
            SpatialFilter = Envelope(-10, -20, 30, 40),
            Limit = 100,
            Offset = 200,
        };

        var features = await Fetch(connector, OgcSource(), query);

        var uri = handler.LastRequestUri!;
        uri.AbsolutePath.Should().EndWith("/collections/places/items");
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        q["f"].ToString().Should().Be("json");
        q["filter"].ToString().Should().Be("pop > 1000");
        q["filter-lang"].ToString().Should().Be("cql2-text");
        q["bbox"].ToString().Should().Be("-10,-20,30,40");
        q["limit"].ToString().Should().Be("100");
        q["offset"].ToString().Should().Be("200");

        AssertMappedFeatures(features);
    }

    [UnitTest]
    public async Task OgcFeatures_OrderByPresent_DoesNotPushPagingOrSort()
    {
        var (handler, factory) = CreateClient();
        var connector = new OgcFeaturesFederatedSourceConnector(factory, NullLogger<OgcFeaturesFederatedSourceConnector>.Instance);

        // OGC sources do not push ordering down, which forces paging to be applied locally too,
        // so the remote request must not carry limit/offset.
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("name")),
            Limit = 10,
        };

        await Fetch(connector, OgcSource(), query);

        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(handler.LastRequestUri!.Query);
        q.ContainsKey("limit").Should().BeFalse();
        q.ContainsKey("offset").Should().BeFalse();
    }

    [UnitTest]
    public async Task FetchAsync_RemoteErrorStatus_Throws()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var factory = new StubHttpClientFactory(handler);
        var connector = new EsriRestFederatedSourceConnector(factory, NullLogger<EsriRestFederatedSourceConnector>.Instance);

        var act = async () => await Fetch(connector, EsriSource(), new FeatureQuery());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static void AssertMappedFeatures(ImmutableArray<Feature> features)
    {
        features.Should().HaveCount(2);

        // First feature carries an explicit object id; geometry maps to WKB.
        features[0].Id.Should().Be(42);
        features[0].Geometry.Should().NotBeNull();
        features[0].Attributes["name"].Should().Be("Alpha");

        // Second feature has no id attribute, so the connector falls back to the ordinal index.
        features[1].Id.Should().Be(1);
        features[1].Attributes["name"].Should().Be("Bravo");
    }

    private static async Task<ImmutableArray<Feature>> Fetch(
        IFederatedSourceConnector connector,
        FederatedSourceDescriptor source,
        FeatureQuery query)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Honua.Core.Features.Federation.ServiceCollectionExtensions.AddFederationCore(services);
        var planner = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IFederationQueryPlanner>(
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));

        var plan = planner.Plan(source, in query, joinsLocalLayer: false);
        var request = new FederatedFetchRequest(source, query, plan);
        return await connector.FetchAsync(request, CancellationToken.None);
    }

    private static (CapturingHandler Handler, StubHttpClientFactory Factory) CreateClient()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GeoJsonResponse),
        });
        return (handler, new StubHttpClientFactory(handler));
    }

    private static FederatedSourceDescriptor EsriSource() => new()
    {
        Id = "esri",
        DisplayName = "Esri",
        Kind = FederatedSourceKind.EsriRest,
        Endpoint = new Uri("https://gis.example.gov/arcgis/rest/services/Parcels/FeatureServer"),
        RemoteLayer = "0",
        RequestTimeout = TimeSpan.FromSeconds(15),
    };

    private static FederatedSourceDescriptor OgcSource() => new()
    {
        Id = "ogc",
        DisplayName = "OGC",
        Kind = FederatedSourceKind.OgcWfs,
        Endpoint = new Uri("https://ogc.example.org/api"),
        RemoteLayer = "places",
        RequestTimeout = TimeSpan.FromSeconds(15),
    };

    private static SpatialFilter Envelope(double minX, double minY, double maxX, double maxY, int? srid = null) => new()
    {
        Geometry = Array.Empty<byte>(),
        SpatialRelationship = SpatialRelationship.EnvelopeIntersects,
        Srid = srid,
        IsSimpleEnvelope = true,
        EnvelopeMinX = minX,
        EnvelopeMinY = minY,
        EnvelopeMaxX = maxX,
        EnvelopeMaxY = maxY,
    };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
