// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Contract tests for provider-neutral STAC item-id filters after provider routing.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacProviderNeutralRoutingTests : IAsyncLifetime
{
    private const string SearchFirstId = "provider-neutral-a";
    private const string SearchSecondId = "provider-neutral-b";
    private const string DetailId = "provider-neutral-detail";
    private readonly ConcurrentQueue<FeatureQuery> _capturedQueries = new();
    private readonly WebAppFixture _fixture;
    private int _boundReaderCreations;

    public StacProviderNeutralRoutingTests()
    {
        var routedReader = Substitute.For<IFeatureReader>();
        routedReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = CaptureProviderNeutralQuery(call.ArgAt<FeatureQuery>(1));
                return Task.FromResult(BuildCandidateResult(query));
            });
        routedReader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                CaptureProviderNeutralQuery(call.ArgAt<FeatureQuery>(1));
                return Task.FromResult(0L);
            });

        var unboundReader = Substitute.For<IFeatureReader>();
        var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider>();
        provider.ProviderName.Returns("postgis");
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        provider.Reader.Returns(unboundReader);
        provider.Writer.Returns((IFeatureWriter?)null);
        ((IBindableFeatureDataProvider)provider)
            .CreateReaderForBinding(Arg.Any<FeatureProviderBinding>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref _boundReaderCreations);
                return routedReader;
            });

        _fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IFeatureDataProvider>();
            services.AddSingleton(provider);
        });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceSchemaField(
            WebAppFixture.TestLayerId,
            new MetadataV2Field { Name = "stac_id", Type = MetadataV2FieldType.String, Nullable = true });
        _fixture.UpdateV2ResourceSchemaField(
            WebAppFixture.TestLayerId,
            new MetadataV2Field { Name = "item_id", Type = MetadataV2FieldType.String, Nullable = true });
        _fixture.UpdateV2ResourceSchemaField(
            WebAppFixture.TestLayerId,
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.String, Nullable = true });

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(candidate =>
            candidate.LayerIndex == WebAppFixture.TestLayerId);
        var storageBinding = snapshot.Graph.StorageBindings.First(candidate =>
            candidate.ResourceId == publication.ResourceId && candidate.StorageLayerId.HasValue);
        var publications = snapshot.Graph.Publications
            .Select(candidate => candidate.LayerIndex == WebAppFixture.TestLayerId
                ? candidate with { StorageBindingId = storageBinding.Metadata.Id }
                : candidate)
            .ToArray();
        var graphProvider = _fixture.GetService<IMetadataV2GraphProvider>() as TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException("Test metadata graph provider is unavailable.");
        graphProvider.SetGraph(snapshot.Graph with
        {
            Publications = publications,
            Revision = snapshot.Graph.Revision + 1
        });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithIds_RoutesCanonicalWhereWithoutSqlFilter()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var bindingsBefore = Volatile.Read(ref _boundReaderCreations);

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={SearchFirstId},{SearchSecondId}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        Volatile.Read(ref _boundReaderCreations).Should().BeGreaterThan(bindingsBefore);

        var queries = _capturedQueries.ToArray();
        queries.Should().NotBeEmpty();
        queries.Should().OnlyContain(query => query.SqlFilter == null);
        queries.Should().OnlyContain(query => !string.IsNullOrWhiteSpace(query.Where));
        queries.Should().OnlyContain(query =>
            query.Where!.Contains(SearchFirstId, StringComparison.Ordinal) &&
            query.Where.Contains(SearchSecondId, StringComparison.Ordinal));
        queries.Should().OnlyContain(query =>
            !query.Where!.Contains(" OR ", StringComparison.Ordinal) &&
            !query.Where.Contains("TRIM", StringComparison.OrdinalIgnoreCase) &&
            !query.Where.Contains("IS NULL", StringComparison.OrdinalIgnoreCase));
        queries.Select(query => query.Where).Should().Contain(where => where!.Contains("stac_id", StringComparison.Ordinal));
        queries.Select(query => query.Where).Should().Contain(where => where!.Contains("item_id", StringComparison.Ordinal));
        queries.Select(query => query.Where).Should().Contain(where => where!.Contains("id", StringComparison.Ordinal));

        var returnedIds = System.Text.Json.JsonDocument.Parse(content).RootElement
            .GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .ToArray();
        returnedIds.Should().BeEquivalentTo(SearchFirstId, SearchSecondId);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /stac/collections/{collectionId}/items/{itemId}")]
    public async Task GetItem_RoutesCanonicalEqualityWhereInPrecedenceOrderWithoutSqlFilter()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var bindingsBefore = Volatile.Read(ref _boundReaderCreations);

        var response = await _fixture.Client.GetAsync(
            $"/stac/collections/{collectionId}/items/{DetailId}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        Volatile.Read(ref _boundReaderCreations).Should().BeGreaterThan(bindingsBefore);

        var queries = _capturedQueries.ToArray();
        queries.Should().HaveCount(3, "bounded fanout gathers each declared canonical field before effective-id selection");
        queries.Should().OnlyContain(query => query.SqlFilter == null);
        queries.Should().OnlyContain(query =>
            !string.IsNullOrWhiteSpace(query.Where) && query.Where.Contains(DetailId, StringComparison.Ordinal));
        queries[0].Where.Should().Contain("stac_id");
        queries[1].Where.Should().Contain("item_id");
        queries[2].Where.Should().Contain("id");
        System.Text.Json.JsonDocument.Parse(content).RootElement.GetProperty("id").GetString().Should().Be(DetailId);
    }

    private FeatureQuery CaptureProviderNeutralQuery(FeatureQuery query)
    {
        if (query.SqlFilter is not null)
        {
            throw new InvalidOperationException(
                "The routed secondary-provider reader rejects process-wide translated SqlFilter fragments.");
        }

        _capturedQueries.Enqueue(query);
        return query;
    }

    private static QueryResult<Feature> BuildCandidateResult(FeatureQuery query)
    {
        var where = query.Where ?? string.Empty;
        if (where.Contains(SearchFirstId, StringComparison.Ordinal))
        {
            if (where.Contains("stac_id", StringComparison.Ordinal))
            {
                return Result(
                    FeatureWithIds(101, stacId: SearchFirstId),
                    FeatureWithIds(102, stacId: SearchSecondId));
            }
            if (where.Contains("item_id", StringComparison.Ordinal))
            {
                return Result(FeatureWithIds(201, stacId: "different-effective-id", itemId: SearchFirstId));
            }
            if (where.Contains("id", StringComparison.Ordinal))
            {
                return Result(FeatureWithIds(202, stacId: "another-effective-id", id: SearchSecondId));
            }
        }

        if (where.Contains(DetailId, StringComparison.Ordinal))
        {
            return where.Contains("stac_id", StringComparison.Ordinal)
                ? Result(FeatureWithIds(301, stacId: "not-the-requested-id", itemId: DetailId))
                : Result(FeatureWithIds(302, stacId: " ", itemId: DetailId));
        }

        return QueryResult<Feature>.Empty();
    }

    private static QueryResult<Feature> Result(params Feature[] features)
        => QueryResult<Feature>.Create(features.Length, [.. features]);

    private static Feature FeatureWithIds(long objectId, string? stacId = null, string? itemId = null, string? id = null)
    {
        var attributes = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("name", $"Feature {objectId}");
        if (stacId is not null) attributes = attributes.Add("stac_id", stacId);
        if (itemId is not null) attributes = attributes.Add("item_id", itemId);
        if (id is not null) attributes = attributes.Add("id", id);
        return Feature.Create(objectId, geometry: null, attributes);
    }
}
