// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Exercises the post-publish data-reconciliation service (issue #1247): count/geometry/content/
/// extent probes and the pass/warn/fail/skipped classification bands that the parity gate (#1380)
/// consumes.
/// </summary>
public sealed class LayerReconciliationServiceTests
{
    [Fact]
    public async Task Reconcile_WhenTargetMatchesSourceSnapshot_ClassifiesPass()
    {
        var reader = new StubFeatureReader
        {
            Count = 100,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 5)
        };
        var service = NewService(reader);

        var artifact = await service.ReconcileAsync(BuildRequest(
            sourceCount: 100,
            sourceExtent: BoundingBox.Create(0, 0, 10, 10, 4326),
            sourceFields: ["OBJECTID", "NAME"]));

        artifact.Classification.Should().Be(MigrationReconciliationClassifications.Pass);
        artifact.Summary.PassCount.Should().Be(1);
        artifact.Layers.Should().ContainSingle();
    }

    [Fact]
    public async Task Reconcile_WhenTargetCountFarBelowSource_ClassifiesFail()
    {
        // 100 source vs 10 target = 90% delta, well past the 20% fail band → fail (NeedsReview).
        var reader = new StubFeatureReader
        {
            Count = 10,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 5)
        };
        var service = NewService(reader);

        var artifact = await service.ReconcileAsync(BuildRequest(
            sourceCount: 100,
            sourceExtent: BoundingBox.Create(0, 0, 10, 10, 4326),
            sourceFields: ["OBJECTID", "NAME"]));

        artifact.Classification.Should().Be(MigrationReconciliationClassifications.Fail);
        artifact.Summary.FailCount.Should().Be(1);
        artifact.Reasons.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenSourceFieldMissingOnTarget_ClassifiesFail()
    {
        var reader = new StubFeatureReader
        {
            Count = 100,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildSampleInternal(["OBJECTID"], validGeometry: true, rows: 5) // NAME dropped on target
        };
        var service = NewService(reader);

        var artifact = await service.ReconcileAsync(BuildRequest(
            sourceCount: 100,
            sourceExtent: BoundingBox.Create(0, 0, 10, 10, 4326),
            sourceFields: ["OBJECTID", "NAME"]));

        artifact.Classification.Should().Be(MigrationReconciliationClassifications.Fail);
        artifact.Layers[0].Content.Classification.Should().Be(MigrationReconciliationClassifications.Fail);
        artifact.Layers[0].Content.MissingOnTarget.Should().Contain("NAME");
    }

    [Fact]
    public async Task Reconcile_WhenNoTargetLayerPublished_ClassifiesSkipped()
    {
        var service = NewService(new StubFeatureReader());

        var artifact = await service.ReconcileAsync(new LayerReconciliationRequest
        {
            RunId = "run",
            SourceKind = "arcgis-geoservices-rest",
            Layers =
            [
                new LayerReconciliationLayerInput
                {
                    SourceLayerId = "svc#0",
                    TargetHonuaLayerId = null,
                    SourceFeatureCount = 100
                }
            ]
        });

        artifact.Summary.SkippedCount.Should().Be(1);
        artifact.Layers[0].Classification.Should().Be(MigrationReconciliationClassifications.Skipped);
    }

    private static LayerReconciliationService NewService(IFeatureReader reader)
        => new(reader, TimeProvider.System, NullLogger<LayerReconciliationService>.Instance);

    private static LayerReconciliationRequest BuildRequest(
        long sourceCount,
        BoundingBox sourceExtent,
        string[] sourceFields)
        => new()
        {
            RunId = "run",
            SourceKind = "arcgis-geoservices-rest",
            Layers =
            [
                new LayerReconciliationLayerInput
                {
                    SourceLayerId = "svc#0",
                    SourceLayerName = "Layer",
                    TargetHonuaLayerId = 1,
                    SourceFeatureCount = sourceCount,
                    SourceExtent = sourceExtent,
                    SourceFieldNames = sourceFields
                }
            ]
        };

    private static QueryResult<Feature> BuildSample(
        (string, string) fieldsTwo,
        bool validGeometry,
        int rows)
        => BuildSampleInternal([fieldsTwo.Item1, fieldsTwo.Item2], validGeometry, rows);

    private static QueryResult<Feature> BuildSampleInternal(string[] fields, bool validGeometry, int rows)
    {
        var attrs = fields.ToImmutableDictionary(f => f, _ => (object?)"x");
        // WKB byte-order byte of 1 (little-endian) marks a well-formed geometry per the probe.
        byte[]? geom = validGeometry ? [1, 1, 0, 0, 0] : null;
        var items = Enumerable.Range(0, rows)
            .Select(i => Feature.Create(i, geom, attrs))
            .ToImmutableArray();
        return QueryResult<Feature>.Create(rows, items);
    }

    private sealed class StubFeatureReader : IFeatureReader
    {
        public long Count { get; init; }
        public FeatureExtent? Extent { get; init; }
        public QueryResult<Feature> Sample { get; init; } = QueryResult<Feature>.Empty();

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(Count);

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Extent);

        public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(Sample);

        public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<TemporalExtentResult?> GetTemporalExtentAsync(int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
