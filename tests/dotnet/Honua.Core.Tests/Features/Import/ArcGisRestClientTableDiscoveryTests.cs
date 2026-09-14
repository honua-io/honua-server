// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class ArcGisRestClientTableDiscoveryTests
{
    [Theory]
    [InlineData("FeatureServer")]
    [InlineData("MapServer")]
    public async Task DiscoverServiceAsync_IncludesSpatialLayersAndStandaloneTables(string serviceType)
    {
        using var handler = new DiscoveryHandler("""
            {"layers":[{"id":0,"name":"Tracts"}],"tables":[{"id":1,"name":"Summary"}]}
            """);
        using var http = new HttpClient(handler);
        var service = await CreateClient(http).DiscoverServiceAsync(
            $"https://example.com/arcgis/rest/services/Summary/{serviceType}", 5, 0, CancellationToken.None);

        service.Layers.Select(layer => layer.Id).Should().Equal(0, 1);
        service.Layers[0].GeometryType.Should().Be("esriGeometryPolygon");
        var table = service.Layers[1];
        table.Type.Should().Be("Table");
        table.GeometryType.Should().BeNull();
        table.FeatureCount.Should().Be(877);
        table.Fields.Should().Contain(field => field.Name == "Join_ID");
        handler.MetadataRequests.Should().Equal(0, 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"layers\":null,")]
    [InlineData("\"layers\":[],")]
    public async Task DiscoverServiceAsync_DiscoversTableOnlyService(string layersProperty)
    {
        using var handler = new DiscoveryHandler($$"""
            { {{layersProperty}} "tables":[{"id":1,"name":"Summary"}] }
            """);
        using var http = new HttpClient(handler);
        var service = await DiscoverAsync(http);

        service.Layers.Should().ContainSingle().Which.Type.Should().Be("Table");
        handler.MetadataRequests.Should().Equal(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"tables\":null,")]
    [InlineData("\"tables\":[],")]
    public async Task DiscoverServiceAsync_PreservesLayerOnlyService(string tablesProperty)
    {
        using var handler = new DiscoveryHandler($$"""
            { {{tablesProperty}} "layers":[{"id":0,"name":"Tracts"}] }
            """);
        using var http = new HttpClient(handler);
        var service = await DiscoverAsync(http);

        service.Layers.Should().ContainSingle().Which.Id.Should().Be(0);
        handler.MetadataRequests.Should().Equal(0);
    }

    [Fact]
    public async Task DiscoverServiceAsync_DoesNotDuplicateResourceListedInBothCollections()
    {
        using var handler = new DiscoveryHandler("""
            {"layers":[{"id":0},{"id":1}],"tables":[{"id":1}]}
            """);
        using var http = new HttpClient(handler);
        var service = await DiscoverAsync(http);

        service.Layers.Select(layer => layer.Id).Should().Equal(0, 1);
        handler.MetadataRequests.Should().Equal(0, 1);
    }

    [Fact]
    public async Task DiscoverServiceAsync_ContinuesToTableAfterLayerMetadataFailure()
    {
        using var handler = new DiscoveryHandler("""
            {"layers":[{"id":2}],"tables":[{"id":1}]}
            """);
        using var http = new HttpClient(handler);
        var service = await DiscoverAsync(http);

        service.Layers.Should().ContainSingle().Which.Id.Should().Be(1);
        handler.MetadataRequests.Should().Equal(2, 1);
    }

    [Fact]
    public async Task DiscoverServiceAsync_PropagatesCancellationDuringTableDiscovery()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new DiscoveryHandler("""{"tables":[{"id":1}]}""", cancellation);
        using var http = new HttpClient(handler);
        var action = () => DiscoverAsync(http, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.MetadataRequests.Should().Equal(1);
    }

    private static ArcGisRestClient CreateClient(HttpClient http)
        => new(http, NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

    private static Task<Honua.Core.Features.Migration.Domain.GeoservicesServiceInfo> DiscoverAsync(
        HttpClient http, CancellationToken cancellationToken = default)
        => CreateClient(http).DiscoverServiceAsync(
            "https://example.com/arcgis/rest/services/Summary/FeatureServer", 5, 0, cancellationToken);

    private sealed class DiscoveryHandler(string serviceMetadata, CancellationTokenSource? cancellation = null)
        : HttpMessageHandler
    {
        public List<int> MetadataRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string response;
            if (path.EndsWith("/query", StringComparison.Ordinal))
            {
                response = """{"count":877}""";
            }
            else if (int.TryParse(path[(path.LastIndexOf('/') + 1)..], out var id))
            {
                MetadataRequests.Add(id);
                if (cancellation != null)
                {
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                response = id switch
                {
                    0 => """{"id":0,"name":"Tracts","type":"Feature Layer","geometryType":"esriGeometryPolygon"}""",
                    1 => """{"id":1,"name":"Summary","type":"Table","fields":[{"name":"Join_ID","type":"esriFieldTypeInteger"}]}""",
                    _ => """{"error":{"code":404,"message":"Resource not found"}}"""
                };
            }
            else
            {
                response = serviceMetadata;
            }

            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
