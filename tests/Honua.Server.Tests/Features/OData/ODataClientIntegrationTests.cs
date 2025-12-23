// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.OData.Client;
using Xunit;

namespace Honua.Server.Tests.Features.OData;

[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataClientIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        _fixture.ReplaceService<ILayerCatalog>(new TestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(new TestFeatureStore());
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers")]
    public async Task LayersQuery_ReturnsExpectedLayer()
    {
        var context = CreateODataContext(_fixture.Client);

        var query = context.CreateQuery<ODataLayer>("Layers");
        var response = await query.ExecuteAsync();
        var layers = response.ToList();

        layers.Should().NotBeEmpty();
        layers.Should().ContainSingle(layer => layer.Id == TestLayerId);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task FeaturesQuery_ReturnsLayerFeatures()
    {
        var context = CreateODataContext(_fixture.Client);

        var response = await context.ExecuteAsync<ODataFeature>(
            new Uri($"Features({TestLayerId})", UriKind.Relative));
        var features = response.ToList();

        features.Should().NotBeEmpty();
        features.Should().OnlyContain(feature => feature.LayerId == TestLayerId);
    }

    private static DataServiceContext CreateODataContext(HttpClient client)
    {
        var serviceRoot = new Uri(client.BaseAddress!, "odata/");
        var context = new DataServiceContext(serviceRoot, ODataProtocolVersion.V4)
        {
            HttpClientFactory = new TestHttpClientFactory(client)
        };
        context.Format.UseJson();

        return context;
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Microsoft.OData.Client.Key("Id")]
    private sealed class ODataLayer
    {
        public int Id { get; init; }

        public string? Name { get; init; }

        public string? Description { get; init; }
    }

    [Microsoft.OData.Client.Key("ObjectId")]
    private sealed class ODataFeature
    {
        public long ObjectId { get; init; }

        public int LayerId { get; init; }

        public byte[]? Geometry { get; init; }

        public string? Attributes { get; init; }
    }
}
