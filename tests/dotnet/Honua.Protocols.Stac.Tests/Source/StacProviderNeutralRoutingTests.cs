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
    private const string FilterMatchId = "provider-neutral-filter-match";
    private const string FilterNonMatchId = "provider-neutral-filter-nonmatch";
    private const string FilterProjectionId = "provider-neutral-filter-projection";
    private const string SortZuluId = "provider-neutral-sort-zulu";
    private const string SortAlphaHighId = "provider-neutral-sort-alpha-high";
    private const string SortAlphaLowId = "provider-neutral-sort-alpha-low";
    private const string CrossLayerZeroAlphaId = "provider-neutral-cross-zero-alpha";
    private const string CrossLayerZeroCharlieId = "provider-neutral-cross-zero-charlie";
    private const string CrossLayerOneBravoId = "provider-neutral-cross-one-bravo";
    private const string CrossLayerOneDeltaId = "provider-neutral-cross-one-delta";
    private const string OverflowId = "provider-neutral-overflow";
    private const string CapIdPrefix = "qz";
    private const string AggregateCapIdPrefix = "aggregate-cap-";
    private const string AggregateOverflowIdPrefix = "aggregate-overflow-";
    private const string DetailId = "provider-neutral-detail";
    private readonly ConcurrentQueue<FeatureQuery> _capturedQueries = new();
    private readonly ConcurrentQueue<int> _capturedStorageLayerIds = new();
    private readonly WebAppFixture _fixture;
    private int _boundReaderCreations;

    public StacProviderNeutralRoutingTests()
    {
        var routedReader = Substitute.For<IFeatureReader>();
        routedReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _capturedStorageLayerIds.Enqueue(call.ArgAt<int>(0));
                var query = CaptureProviderNeutralQuery(call.ArgAt<FeatureQuery>(1));
                return Task.FromResult(BuildCandidateResult(call.ArgAt<int>(0), query));
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
            new MetadataV2Field { Name = "STAC_ID", Type = MetadataV2FieldType.String, Nullable = true });
        _fixture.UpdateV2ResourceSchemaField(
            WebAppFixture.TestLayerId,
            new MetadataV2Field { Name = "ITEM_ID", Type = MetadataV2FieldType.String, Nullable = true });
        _fixture.UpdateV2ResourceSchemaField(
            WebAppFixture.TestLayerId,
            new MetadataV2Field { Name = "ID", Type = MetadataV2FieldType.String, Nullable = true });
        _fixture.UpdateV2ResourceSchemaField(
            1,
            new MetadataV2Field { Name = "STAC_ID", Type = MetadataV2FieldType.String, Nullable = true });
        _fixture.UpdateV2ResourceSchemaField(
            1,
            new MetadataV2Field { Name = "ITEM_ID", Type = MetadataV2FieldType.String, Nullable = true });
        _fixture.UpdateV2ResourceSchemaField(
            1,
            new MetadataV2Field { Name = "ID", Type = MetadataV2FieldType.String, Nullable = true });

        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var primaryPublication = snapshot.Graph.Publications.First(candidate =>
            candidate.LayerIndex == WebAppFixture.TestLayerId);
        var primaryBinding = snapshot.Graph.StorageBindings.First(binding =>
            binding.ResourceId == primaryPublication.ResourceId && binding.StorageLayerId.HasValue);
        var secondaryPublication = snapshot.Graph.Publications.First(candidate => candidate.LayerIndex == 1);
        var secondaryBinding = primaryBinding with
        {
            Metadata = primaryBinding.Metadata with { Id = $"{primaryBinding.Metadata.Id}-secondary" },
            ResourceId = secondaryPublication.ResourceId,
            StorageLayerId = 1
        };
        var publications = snapshot.Graph.Publications
            .Select(candidate => candidate.LayerIndex is WebAppFixture.TestLayerId or 1
                ? candidate with
                {
                    StorageBindingId = candidate.LayerIndex == WebAppFixture.TestLayerId
                        ? primaryBinding.Metadata.Id
                        : secondaryBinding.Metadata.Id
                }
                : candidate)
            .ToArray();
        var graphProvider = _fixture.GetService<TestMetadataV2GraphProvider>();
        graphProvider.SetGraph(snapshot.Graph with
        {
            Publications = publications,
            StorageBindings = [.. snapshot.Graph.StorageBindings, secondaryBinding],
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
        queries.Select(query => query.Where).Should().Contain(where => where!.Contains("STAC_ID", StringComparison.Ordinal));
        queries.Select(query => query.Where).Should().Contain(where => where!.Contains("ITEM_ID", StringComparison.Ordinal));
        queries.Select(query => query.Where).Should().Contain(where => where!.Contains("ID", StringComparison.Ordinal));

        var returnedIds = System.Text.Json.JsonDocument.Parse(content).RootElement
            .GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .ToArray();
        returnedIds.Should().BeEquivalentTo(SearchFirstId, SearchSecondId);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WhenGraphActivatesAfterAuthorization_RoutesAuthorizedSnapshotBinding()
    {
        var authorizedSnapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = authorizedSnapshot.Graph.Publications.First(candidate =>
            candidate.LayerIndex == WebAppFixture.TestLayerId &&
            candidate.PublicationType == MetadataV2PublicationType.StacCollection);
        var authorizedBinding = authorizedSnapshot.Index.StorageBindingsById[publication.StorageBindingId!];
        var activatedBinding = authorizedBinding with
        {
            Metadata = authorizedBinding.Metadata with { Id = $"{authorizedBinding.Metadata.Id}-activated" },
            StorageLayerId = 777
        };
        var activatedGraph = authorizedSnapshot.Graph with
        {
            Revision = authorizedSnapshot.Graph.Revision + 1,
            StorageBindings = [.. authorizedSnapshot.Graph.StorageBindings, activatedBinding],
            Publications = authorizedSnapshot.Graph.Publications
                .Select(candidate => candidate.Metadata.Id == publication.Metadata.Id
                    ? candidate with { StorageBindingId = activatedBinding.Metadata.Id }
                    : candidate)
                .ToArray()
        };
        _capturedStorageLayerIds.Clear();
        var graphProvider = _fixture.GetService<TestMetadataV2GraphProvider>();
        graphProvider.ActivateAfterNextRead(activatedGraph);

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={WebAppFixture.TestLayerId}&ids={SearchFirstId},{SearchSecondId}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _capturedStorageLayerIds.Should().NotBeEmpty();
        _capturedStorageLayerIds.Should().OnlyContain(layerId => layerId == authorizedBinding.StorageLayerId);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithIdsAndSupportedFilter_EvaluatesMatchingAndNonMatchingCandidates()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var filter = Uri.EscapeDataString("properties.name = 'keep'");

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={FilterMatchId},{FilterNonMatchId}&filter-lang=cql2-text&filter={filter}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var json = System.Text.Json.JsonDocument.Parse(content);
        json.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .Should().Equal(FilterMatchId);
        json.RootElement.GetProperty("numberMatched").GetInt32().Should().Be(1);

        _capturedQueries.Should().NotBeEmpty();
        _capturedQueries.Should().OnlyContain(query => query.SqlFilter == null);
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithIdsAndMalformedFilter_ReturnsBadRequestBeforeProviderRouting()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var capturedBefore = _capturedQueries.Count;
        var filter = Uri.EscapeDataString("properties.name =");

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={FilterMatchId}&filter-lang=cql2-text&filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _capturedQueries.Count.Should().Be(capturedBefore, "malformed filters must fail before provider routing");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithIdsAndEvaluatorUnsupportedFilter_ReturnsBadRequestBeforeProviderRouting()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var capturedBefore = _capturedQueries.Count;
        var filter = Uri.EscapeDataString("UPPER(properties.name) = 'KEEP'");

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={FilterMatchId}&filter-lang=cql2-text&filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _capturedQueries.Count.Should().Be(capturedBefore, "unsupported in-memory filters must fail before provider routing");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithIdsAndFilterOnExcludedField_EvaluatesBeforeResponseProjection()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var filter = Uri.EscapeDataString("properties.name = 'keep'");

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={FilterProjectionId}&filter-lang=cql2-text&filter={filter}&fields=-properties.name");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var json = System.Text.Json.JsonDocument.Parse(content);
        var item = json.RootElement.GetProperty("features").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("id").GetString().Should().Be(FilterProjectionId);
        item.GetProperty("properties").TryGetProperty("name", out _).Should().BeFalse();

        _capturedQueries.Should().NotBeEmpty();
        _capturedQueries.Should().OnlyContain(query => query.OutFields == null,
            "candidate evaluation must fetch attributes excluded from the response projection");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithIdsAndSortBy_AppliesGlobalOrderBeforePagingAndDeduplicatesCandidates()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={SortZuluId},{SortAlphaHighId},{SortAlphaLowId}&sortby=name&limit=2");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var page1 = System.Text.Json.JsonDocument.Parse(content);
        page1.RootElement.GetProperty("numberMatched").GetInt32().Should().Be(3,
            "the same routed candidates returned by each canonical-field query must be deduplicated by Feature.Id");
        page1.RootElement.GetProperty("numberReturned").GetInt32().Should().Be(2);
        page1.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .Should().Equal([SortAlphaLowId, SortAlphaHighId],
                "sortby must be applied globally, with Feature.Id as the stable tie-breaker, before paging");

        var page1Queries = _capturedQueries.ToArray();
        page1Queries.Should().HaveCount(3);
        page1Queries.Should().OnlyContain(query => query.Limit == 4,
            "each canonical-field query may fetch at most distinct requested ids plus one overflow sentinel");

        var nextHref = page1.RootElement.GetProperty("links").EnumerateArray()
            .Single(link => link.GetProperty("rel").GetString() == "next")
            .GetProperty("href").GetString();
        var page2Response = await _fixture.Client.GetAsync(new Uri(nextHref!).PathAndQuery);
        var page2Content = await page2Response.Content.ReadAsStringAsync();
        page2Response.StatusCode.Should().Be(HttpStatusCode.OK, page2Content);

        using var page2 = System.Text.Json.JsonDocument.Parse(page2Content);
        page2.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .Should().Equal(SortZuluId);
        page2.RootElement.GetProperty("links").EnumerateArray()
            .Should().NotContain(link => link.GetProperty("rel").GetString() == "next");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithTwoBoundCollections_InterleavesGlobalSortAcrossPageBoundaries()
    {
        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections=0,1&ids={CrossLayerZeroAlphaId},{CrossLayerOneBravoId},{CrossLayerZeroCharlieId},{CrossLayerOneDeltaId}&sortby=name&limit=2");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var page1 = System.Text.Json.JsonDocument.Parse(content);
        page1.RootElement.GetProperty("numberMatched").GetInt32().Should().Be(4);
        page1.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => (
                Id: feature.GetProperty("id").GetString(),
                Collection: feature.GetProperty("collection").GetString()))
            .Should().Equal(
                (CrossLayerZeroAlphaId, "0"),
                (CrossLayerOneBravoId, "1"));

        var nextHref = page1.RootElement.GetProperty("links").EnumerateArray()
            .Single(link => link.GetProperty("rel").GetString() == "next")
            .GetProperty("href").GetString();
        var page2Response = await _fixture.Client.GetAsync(new Uri(nextHref!).PathAndQuery);
        var page2Content = await page2Response.Content.ReadAsStringAsync();
        page2Response.StatusCode.Should().Be(HttpStatusCode.OK, page2Content);

        using var page2 = System.Text.Json.JsonDocument.Parse(page2Content);
        page2.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => (
                Id: feature.GetProperty("id").GetString(),
                Collection: feature.GetProperty("collection").GetString()))
            .Should().Equal(
                (CrossLayerZeroCharlieId, "0"),
                (CrossLayerOneDeltaId, "1"));
        page2.RootElement.GetProperty("links").EnumerateArray()
            .Should().NotContain(link => link.GetProperty("rel").GetString() == "next");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WhenRoutedCandidateQueryExceedsSafetyCap_ReturnsInternalServerError()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={OverflowId}");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        _capturedQueries.Should().ContainSingle();
        _capturedQueries.Single().Limit.Should().Be(2,
            "one requested id permits one candidate plus one overflow sentinel");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_With512DistinctIds_UsesBoundedOverflowSentinelQuery()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var ids = string.Join(',', Enumerable.Range(0, 512).Select(index => $"{CapIdPrefix}{index:D3}"));

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={ids}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        _capturedQueries.Should().HaveCount(3);
        _capturedQueries.Should().OnlyContain(query => query.Limit == 513,
            "the maximum accepted distinct-id set still carries one overflow sentinel");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_With513DistinctIds_ReturnsBadRequestBeforeProviderRouting()
    {
        var collectionId = WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture);
        var ids = string.Join(',', Enumerable.Range(0, 513).Select(index => $"{CapIdPrefix}{index:D3}"));

        var response = await _fixture.Client.GetAsync(
            $"/stac/search?collections={collectionId}&ids={ids}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _capturedQueries.Should().BeEmpty("over-cap input must be rejected before any provider query");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithoutCollections_WhenAggregateCandidatesEqualCap_SucceedsAcrossBoundTargets()
    {
        var ids = string.Join(',', Enumerable.Range(0, 256).Select(index => $"{AggregateCapIdPrefix}{index:D3}"));

        var response = await _fixture.Client.GetAsync($"/stac/search?ids={ids}&sortby=name");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var json = System.Text.Json.JsonDocument.Parse(content);
        json.RootElement.GetProperty("numberMatched").GetInt32().Should().Be(512,
            "the global cap permits exactly 512 retained candidates across visible bound publications");
        _capturedQueries.Should().HaveCount(6,
            "each of the two visible bound publications queries its three canonical item-id fields");
    }

    [IntegrationTest]
    [Operation(Operations.StacSearch)]
    [Endpoint("GET /stac/search")]
    public async Task SearchGet_WithoutCollections_WhenAggregateCandidatesExceedCap_ReturnsBadRequestWithoutFurtherReads()
    {
        var ids = string.Join(',', Enumerable.Range(0, 257).Select(index => $"{AggregateOverflowIdPrefix}{index:D3}"));

        var response = await _fixture.Client.GetAsync($"/stac/search?ids={ids}&sortby=name");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _capturedQueries.Should().HaveCount(6,
            "routing must stop immediately after the second bound target breaches the aggregate cap");
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
        queries[0].Where.Should().Contain("STAC_ID");
        queries[1].Where.Should().Contain("ITEM_ID");
        queries[2].Where.Should().Contain("ID");
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

    private static QueryResult<Feature> BuildCandidateResult(int layerId, FeatureQuery query)
    {
        var where = query.Where ?? string.Empty;
        if (where.Contains(OverflowId, StringComparison.Ordinal))
        {
            return QueryResult<Feature>.Create(
                2,
                [
                    FeatureWithIds(501, stacId: OverflowId),
                    FeatureWithIds(502, stacId: OverflowId)
                ]);
        }

        if (where.Contains(CapIdPrefix, StringComparison.Ordinal))
        {
            return QueryResult<Feature>.Empty();
        }

        if (where.Contains(AggregateCapIdPrefix, StringComparison.Ordinal))
        {
            return Result(Enumerable.Range(0, 256)
                .Select(index => FeatureWithIds(
                    (layerId * 1_000L) + index,
                    stacId: $"{AggregateCapIdPrefix}{index:D3}",
                    name: $"Candidate {layerId:D2}-{index:D3}"))
                .ToArray());
        }

        if (where.Contains(AggregateOverflowIdPrefix, StringComparison.Ordinal))
        {
            var candidateCount = layerId == WebAppFixture.TestLayerId ? 256 : 257;
            return Result(Enumerable.Range(0, candidateCount)
                .Select(index => FeatureWithIds(
                    (layerId * 1_000L) + index,
                    stacId: $"{AggregateOverflowIdPrefix}{index:D3}",
                    name: $"Candidate {layerId:D2}-{index:D3}"))
                .ToArray());
        }

        if (where.Contains(SortZuluId, StringComparison.Ordinal))
        {
            return Result(
                FeatureWithIds(410, stacId: SortZuluId, name: "Zulu"),
                FeatureWithIds(420, stacId: SortAlphaHighId, name: "Alpha"),
                FeatureWithIds(415, stacId: SortAlphaLowId, name: "Alpha"));
        }

        if (where.Contains(CrossLayerZeroAlphaId, StringComparison.Ordinal))
        {
            return layerId == 0
                ? Result(
                    FeatureWithIds(710, stacId: CrossLayerZeroAlphaId, name: "Alpha"),
                    FeatureWithIds(730, stacId: CrossLayerZeroCharlieId, name: "Charlie"))
                : Result(
                    FeatureWithIds(720, stacId: CrossLayerOneBravoId, name: "Bravo"),
                    FeatureWithIds(740, stacId: CrossLayerOneDeltaId, name: "Delta"));
        }

        if (where.Contains(SearchFirstId, StringComparison.Ordinal))
        {
            if (where.Contains("STAC_ID", StringComparison.Ordinal))
            {
                return Result(
                    FeatureWithIds(101, stacId: SearchFirstId),
                    FeatureWithIds(102, stacId: SearchSecondId));
            }
            if (where.Contains("ITEM_ID", StringComparison.Ordinal))
            {
                return Result(FeatureWithIds(201, stacId: "different-effective-id", itemId: SearchFirstId));
            }
            if (where.Contains("id", StringComparison.Ordinal))
            {
                return Result(FeatureWithIds(202, stacId: "another-effective-id", id: SearchSecondId));
            }
        }

        if (where.Contains(FilterMatchId, StringComparison.Ordinal))
        {
            return Result(
                FeatureWithIds(601, stacId: FilterMatchId, name: "keep"),
                FeatureWithIds(602, stacId: FilterNonMatchId, name: "drop"));
        }

        if (where.Contains(FilterProjectionId, StringComparison.Ordinal))
        {
            return Result(FeatureWithIds(603, stacId: FilterProjectionId, name: "keep"));
        }

        if (where.Contains(DetailId, StringComparison.Ordinal))
        {
            return where.Contains("STAC_ID", StringComparison.Ordinal)
                ? Result(FeatureWithIds(301, stacId: "not-the-requested-id", itemId: DetailId))
                : Result(FeatureWithIds(302, stacId: " ", itemId: DetailId));
        }

        return QueryResult<Feature>.Empty();
    }

    private static QueryResult<Feature> Result(params Feature[] features)
        => QueryResult<Feature>.Create(features.Length, [.. features]);

    private static Feature FeatureWithIds(
        long objectId,
        string? stacId = null,
        string? itemId = null,
        string? id = null,
        string? name = null)
    {
        var attributes = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
            .Add("name", name ?? $"Feature {objectId}");
        if (stacId is not null) attributes = attributes.Add("STAC_ID", stacId);
        if (itemId is not null) attributes = attributes.Add("ITEM_ID", itemId);
        if (id is not null) attributes = attributes.Add("ID", id);
        return Feature.Create(objectId, geometry: null, attributes);
    }

}
