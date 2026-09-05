// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Protocols.Stac;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Stac;

[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacPagingRegressionTests : IAsyncLifetime
{
    private static readonly string[] SearchCollections = ["0", "1"];
    private readonly IFeatureReader _reader = Substitute.For<IFeatureReader, IPagedFeatureReader>();
    private readonly WebAppFixture _fixture;

    public StacPagingRegressionTests()
    {
        _reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var page = Page(call.ArgAt<int>(0), call.ArgAt<FeatureQuery>(1));
                return QueryResult<Feature>.Create(3, page.Items, page.HasMoreResults);
            });
        _reader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>()).Returns(3L);
        ((IPagedFeatureReader)_reader).QueryPageAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => Page(call.ArgAt<int>(0), call.ArgAt<FeatureQuery>(1)));
        var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider>();
        provider.ProviderName.Returns("postgis");
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        provider.Reader.Returns(_reader);
        ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(Arg.Any<FeatureProviderBinding>()).Returns(_reader);
        _fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IFeatureDataProvider>();
            services.AddSingleton(provider);
            services.PostConfigure<StacOptions>(options => options.NumberMatchedPolicy = StacNumberMatchedPolicy.OmitWhenExpensive);
        });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var primaryPublication = snapshot.Graph.Publications.First(p => p.LayerIndex == 0);
        var primaryBinding = snapshot.Graph.StorageBindings.First(b => b.ResourceId == primaryPublication.ResourceId && b.StorageLayerId.HasValue);
        var secondaryPublication = snapshot.Graph.Publications.First(p => p.LayerIndex == 1);
        var secondaryBinding = primaryBinding with
        {
            Metadata = primaryBinding.Metadata with { Id = $"{primaryBinding.Metadata.Id}-paging-secondary" },
            ResourceId = secondaryPublication.ResourceId,
            StorageLayerId = 1
        };
        _fixture.GetService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Publications = snapshot.Graph.Publications.Select(p => p.LayerIndex is 0 or 1
                ? p with { StorageBindingId = p.LayerIndex == 0 ? primaryBinding.Metadata.Id : secondaryBinding.Metadata.Id }
                : p).ToArray(),
            StorageBindings = [.. snapshot.Graph.StorageBindings, secondaryBinding],
            Revision = snapshot.Graph.Revision + 1
        }, schema: _fixture.CurrentSchema);
        _reader.ClearReceivedCalls();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /stac/search")]
    [Operation(Operations.StacSearch)]
    public async Task ExactCounts_CrossCollectionOffset_DoesNotCountThePageCollectionTwice()
    {
        _fixture.GetService<IOptions<StacOptions>>().Value.NumberMatchedPolicy = StacNumberMatchedPolicy.Exact;
        var response = await _fixture.Client.GetAsync("/stac/search?collections=0,1&limit=1&offset=4");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("numberMatched").GetInt64().Should().Be(6);
        json.RootElement.GetProperty("features")[0].GetProperty("id").GetString().Should().Be("1-1");
        await _reader.Received(1).CountAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        await _reader.Received(1).CountAsync(1, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        await _reader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
        await ((IPagedFeatureReader)_reader).Received(1).QueryPageAsync(
            1, Arg.Is<FeatureQuery>(query => query.Offset == 1 && query.Limit == 1), Arg.Any<CancellationToken>());
    }

    [IntegrationTheory]
    [InlineData("get", 0, 4, true)]
    [InlineData("get", 2, 2, true)]
    [InlineData("get", 3, 2, true)]
    [InlineData("get", 4, 2, false)]
    [InlineData("get", 8, 0, false)]
    [InlineData("post", 2, 2, true)]
    [InlineData("post", 4, 2, false)]
    [InlineData("items", 0, 2, true)]
    [InlineData("items", 2, 1, false)]
    [Endpoint("GET /stac/search")]
    [Endpoint("POST /stac/search")]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    [Operation(Operations.StacSearch)]
    public async Task OptionalCounts_PageAcrossCollectionsWithoutCounting(string route, int offset, int returned, bool hasNext)
    {
        var limit = returned == 4 ? 4 : 2;
        var response = route switch
        {
            "items" => await _fixture.Client.GetAsync($"/stac/collections/0/items?limit={limit}&offset={offset}"),
            "get" => await _fixture.Client.GetAsync($"/stac/search?collections=0,1&limit={limit}&offset={offset}"),
            _ => await _fixture.Client.PostAsJsonAsync("/stac/search", new { collections = SearchCollections, limit, token = $"offset:{offset}" })
        };
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;
        root.GetProperty("numberReturned").GetInt32().Should().Be(returned);
        root.TryGetProperty("numberMatched", out _).Should().BeFalse();
        root.GetProperty("context").TryGetProperty("matched", out _).Should().BeFalse();
        root.GetProperty("links").EnumerateArray().Any(link => link.GetProperty("rel").GetString() == "next")
            .Should().Be(hasNext);
        root.GetProperty("features").EnumerateArray().Select(item => item.GetProperty("id").GetString())
            .Should().Equal(Enumerable.Range(0, route == "items" ? 3 : 6).Skip(offset).Take(limit)
                .Select(i => $"{i / 3}-{i % 3}"));
        await _reader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
        await _reader.DidNotReceiveWithAnyArgs().CountAsync(default, default!, default);
    }

    private static PagedQueryResult<Feature> Page(int layerId, FeatureQuery query)
    {
        var offset = query.Offset ?? 0;
        var limit = query.Limit ?? 2;
        var features = Enumerable.Range(0, 3).Skip(offset).Take(limit)
            .Select(i => Feature.Create(i, null, ImmutableDictionary<string, object?>.Empty
                .Add("stac_id", $"{layerId}-{i}").Add("timestamp", "2024-01-01T00:00:00Z"))).ToImmutableArray();
        return PagedQueryResult<Feature>.Create(features, offset + features.Length < 3);
    }
}
