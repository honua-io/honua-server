// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Covers the batched feature-id resolution used by the OGC Features batch transaction
/// prepare phase (#1593): all update/delete targets resolve with at most two provider
/// round trips while ids that do not resolve are simply absent from the result, so a
/// missing feature fails only its own operation.
/// </summary>
public sealed class OgcFeatureIdentifierResolverResolveManyTests
{
    private const int LayerId = 5;

    [UnitTest]
    public async Task ResolveManyAsync_ObjectIdFastPath_AllFound_IssuesSingleQuery()
    {
        var (snapshot, publication, resource) = BuildGraph(objectIdField: true);
        var reader = new RecordingFeatureReader
        {
            QueryHandler = query => QueryResult<Feature>.Create(
                2,
                [CreateFeature(1), CreateFeature(3)])
        };
        var queryProcessor = new RecordingQueryProcessor();

        var resolved = await OgcFeatureIdentifierResolver.ResolveManyAsync(
            reader, queryProcessor, snapshot, publication, resource,
            ["1", "3"], CancellationToken.None);

        resolved.Should().ContainKeys("1", "3");
        resolved["1"].ObjectId.Should().Be(1);
        resolved["3"].ObjectId.Should().Be(3);
        reader.Queries.Should().HaveCount(1);
        reader.Queries[0].ObjectIds.Should().NotBeNull();
        reader.Queries[0].ObjectIds!.Value.Should().BeEquivalentTo(new long[] { 1, 3 });
        queryProcessor.ToFeatureQueryCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task ResolveManyAsync_ObjectIdFastPath_MissingId_IsAbsentAndFailsOnlyItself()
    {
        var (snapshot, publication, resource) = BuildGraph(objectIdField: true);
        var reader = new RecordingFeatureReader
        {
            // Fast-path ObjectIds query finds 1 and 3 but not 2; the follow-up
            // IN-filter query for the unresolved id finds nothing either.
            QueryHandler = query => query.ObjectIds is not null
                ? QueryResult<Feature>.Create(2, [CreateFeature(1), CreateFeature(3)])
                : QueryResult<Feature>.Empty()
        };
        var queryProcessor = new RecordingQueryProcessor();

        var resolved = await OgcFeatureIdentifierResolver.ResolveManyAsync(
            reader, queryProcessor, snapshot, publication, resource,
            ["1", "2", "3"], CancellationToken.None);

        resolved.Should().ContainKeys("1", "3");
        resolved.Should().NotContainKey("2");
        // Bounded round trips: one ObjectIds fast-path query plus one IN-filter
        // fallback for the unresolved id, regardless of batch size.
        reader.Queries.Should().HaveCount(2);
        queryProcessor.ToFeatureQueryCalls.Should().Be(1);
    }

    [UnitTest]
    public async Task ResolveManyAsync_NonCanonicalNumericToken_ResolvesThroughInFilter()
    {
        var (snapshot, publication, resource) = BuildGraph(objectIdField: true);
        var reader = new RecordingFeatureReader
        {
            QueryHandler = query => query.ObjectIds is not null
                ? QueryResult<Feature>.Empty()
                : QueryResult<Feature>.Create(1, [CreateFeature(7)])
        };
        var queryProcessor = new RecordingQueryProcessor();

        var resolved = await OgcFeatureIdentifierResolver.ResolveManyAsync(
            reader, queryProcessor, snapshot, publication, resource,
            ["007"], CancellationToken.None);

        resolved.Should().ContainKey("007");
        resolved["007"].ObjectId.Should().Be(7);
        queryProcessor.ToFeatureQueryCalls.Should().Be(1);
    }

    [UnitTest]
    public async Task ResolveManyAsync_StringPublicId_SingleInFilterQuery_MapsTokensBack()
    {
        var (snapshot, publication, resource) = BuildGraph(objectIdField: false);
        var reader = new RecordingFeatureReader
        {
            QueryHandler = _ => QueryResult<Feature>.Create(
                2,
                [
                    CreateFeature(11, ("id", "alpha")),
                    CreateFeature(12, ("id", "beta"))
                ])
        };
        var queryProcessor = new RecordingQueryProcessor();

        var resolved = await OgcFeatureIdentifierResolver.ResolveManyAsync(
            reader, queryProcessor, snapshot, publication, resource,
            ["alpha", "beta", "missing"], CancellationToken.None);

        resolved.Should().HaveCount(2);
        resolved["alpha"].ObjectId.Should().Be(11);
        resolved["beta"].ObjectId.Should().Be(12);
        resolved.Should().NotContainKey("missing");
        reader.Queries.Should().HaveCount(1);
        queryProcessor.ToFeatureQueryCalls.Should().Be(1);

        // The single round trip carries one IN list with every requested id.
        var expression = queryProcessor.LastUnifiedQuery!.Value.Filter!.Value.Expression
            .Should().BeOfType<BinaryExpression>().Subject;
        expression.Operator.Should().Be(BinaryOperator.In);
        expression.Right.Should().BeOfType<ValueList>()
            .Which.Values.Should().HaveCount(3);
    }

    [UnitTest]
    public async Task ResolveManyAsync_EmptyInput_IssuesNoQueries()
    {
        var (snapshot, publication, resource) = BuildGraph(objectIdField: true);
        var reader = new RecordingFeatureReader
        {
            QueryHandler = _ => QueryResult<Feature>.Empty()
        };
        var queryProcessor = new RecordingQueryProcessor();

        var resolved = await OgcFeatureIdentifierResolver.ResolveManyAsync(
            reader, queryProcessor, snapshot, publication, resource,
            [], CancellationToken.None);

        resolved.Should().BeEmpty();
        reader.Queries.Should().BeEmpty();
        queryProcessor.ToFeatureQueryCalls.Should().Be(0);
    }

    private static Feature CreateFeature(long objectId, params (string Key, object? Value)[] attributes)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in attributes)
        {
            builder[key] = value;
        }

        return Feature.Create(objectId, geometry: null, builder.ToImmutable());
    }

    private static (MetadataV2GraphSnapshot Snapshot, MetadataV2Publication Publication, MetadataV2Resource Resource) BuildGraph(
        bool objectIdField)
    {
        MetadataV2Field[] fields = objectIdField
            ?
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false }
            ]
            :
            [
                new()
                {
                    Name = "id",
                    Type = MetadataV2FieldType.String,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                }
            ];

        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc-1", "test-service")
            .AddResource("res-1", "test-resource", fields: fields)
            .AddPublication("pub-1", "svc-1", "res-1", layerIndex: LayerId)
            .Build();

        var snapshot = new MetadataV2GraphSnapshot(graph, "\"etag\"", DateTimeOffset.UtcNow);
        return (snapshot, graph.Publications[0], graph.Resources[0]);
    }

    private sealed class RecordingFeatureReader : IFeatureReader
    {
        public required Func<FeatureQuery, QueryResult<Feature>> QueryHandler { get; init; }

        public List<FeatureQuery> Queries { get; } = [];

        public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(QueryHandler(query));
        }

        public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Batched resolution must not issue per-id reads.");

        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingQueryProcessor : IQueryProcessor
    {
        public int ToFeatureQueryCalls { get; private set; }

        public UnifiedQuery? LastUnifiedQuery { get; private set; }

        public FeatureQuery ToFeatureQuery(UnifiedQuery query, MetadataV2Resource resource)
        {
            ToFeatureQueryCalls++;
            LastUnifiedQuery = query;
            // The production processor translates the filter expression to SQL; for
            // these tests the reader fake only needs a non-ObjectIds marker query.
            return new FeatureQuery { Limit = query.Limit };
        }

        public QueryValidationResult ValidateQuery(UnifiedQuery query, MetadataV2Resource resource)
            => throw new NotSupportedException();

        public UnifiedQuery OptimizeQuery(UnifiedQuery query, MetadataV2Resource resource)
            => throw new NotSupportedException();

        public string BuildCacheKey(UnifiedQuery query, MetadataV2Resource resource, string protocol)
            => throw new NotSupportedException();

        public bool ShouldUseStreaming(UnifiedQuery query, MetadataV2Resource resource, string outputFormat)
            => throw new NotSupportedException();

        public Task<long> EstimateResultCountAsync(UnifiedQuery query, MetadataV2Resource resource, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
