// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Covers the batched response reload used by the OGC Features batch transaction mapping
/// phase (#1593): created rows are reloaded with one ObjectIds query (projected when the
/// response CRS differs from storage) and missing rows are simply absent, preserving the
/// per-operation fallback to payload-derived ids.
/// </summary>
public sealed class OgcFeaturesResponseHelpersBatchLoadTests
{
    private const int LayerId = 9;

    private static readonly CrsDefinition StorageCrs = new(
        "http://www.opengis.net/def/crs/OGC/1.3/CRS84", 4326, AxisOrder.EastNorth, true);

    private static readonly CrsDefinition ProjectedCrs = new(
        "http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false);

    [UnitTest]
    public async Task LoadFeaturesForResponseAsync_SameCrs_SingleQueryKeyedByObjectId()
    {
        var reader = new RecordingFeatureReader
        {
            QueryHandler = _ => QueryResult<Feature>.Create(
                2,
                [Feature.Create(5, geometry: null), Feature.Create(6, geometry: null)])
        };

        var loaded = await OgcFeaturesResponseHelpers.LoadFeaturesForResponseAsync(
            reader, LayerId, CreateResource(), [5, 5, 6, 7], StorageCrs, CancellationToken.None);

        loaded.Keys.Should().BeEquivalentTo(new long[] { 5, 6 });
        loaded.Should().NotContainKey(7);
        reader.Queries.Should().HaveCount(1);
        var query = reader.Queries[0];
        // Duplicate object ids are collapsed before the round trip.
        query.ObjectIds!.Value.Should().BeEquivalentTo(new long[] { 5, 6, 7 });
        query.OutputSrid.Should().BeNull();
    }

    [UnitTest]
    public async Task LoadFeaturesForResponseAsync_DifferentCrs_ProjectsTheSingleQuery()
    {
        var reader = new RecordingFeatureReader
        {
            QueryHandler = _ => QueryResult<Feature>.Create(1, [Feature.Create(5, geometry: null)])
        };

        var loaded = await OgcFeaturesResponseHelpers.LoadFeaturesForResponseAsync(
            reader, LayerId, CreateResource(), [5], ProjectedCrs, CancellationToken.None);

        loaded.Keys.Should().BeEquivalentTo(new long[] { 5 });
        reader.Queries.Should().HaveCount(1);
        var query = reader.Queries[0];
        query.SpatialReferenceSrid.Should().Be(4326);
        query.OutputSrid.Should().Be(3857);
        query.OutputAxisOrder.Should().Be(ProjectedCrs.AxisOrder);
    }

    [UnitTest]
    public async Task LoadFeaturesForResponseAsync_EmptyInput_IssuesNoQuery()
    {
        var reader = new RecordingFeatureReader
        {
            QueryHandler = _ => QueryResult<Feature>.Empty()
        };

        var loaded = await OgcFeaturesResponseHelpers.LoadFeaturesForResponseAsync(
            reader, LayerId, CreateResource(), [], StorageCrs, CancellationToken.None);

        loaded.Should().BeEmpty();
        reader.Queries.Should().BeEmpty();
    }

    private static MetadataV2Resource CreateResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-1", Name = "test-resource" },
        Type = MetadataV2ResourceType.FeatureDataset,
        SchemaFields =
        [
            new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false }
        ]
    };

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
            => throw new NotSupportedException("Batched response loading must not issue per-id reads.");

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
}
