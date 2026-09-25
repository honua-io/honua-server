// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.GeoServicesSql;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Exercises the post-publish data-reconciliation service (issue #1247): count/geometry/content/
/// extent probes and the pass/warn/fail/skipped classification bands that the parity gate (#1380)
/// consumes.
/// </summary>
public sealed class LayerReconciliationServiceTests
{
    [Theory]
    [InlineData(false, 100, true, "pass")]
    [InlineData(false, 10, true, "fail")]
    [InlineData(false, 100, false, "fail")]
    [InlineData(true, 100, true, "fail")]
    public async Task Reconcile_AttributeOnlySource_PreservesCountAndContentChecks(
        bool sourceHasGeometry, long targetCount, bool hasExpectedField, string expected)
    {
        var reader = new StubFeatureReader
        {
            Count = targetCount,
            Sample = BuildSampleInternal(hasExpectedField ? ["NAME"] : ["OTHER"], validGeometry: false, rows: 5)
        };
        var request = new LayerReconciliationRequest
        {
            RunId = "table-run",
            SourceKind = "arcgis-geoservices-rest",
            Layers = [new LayerReconciliationLayerInput
            {
                SourceLayerId = "svc#1", TargetHonuaLayerId = 1,
                SourceFeatureCount = 100, SourceHasGeometry = sourceHasGeometry,
                SourceFieldNames = ["NAME"]
            }]
        };

        var result = await NewService(reader).ReconcileAsync(request);

        result.Classification.Should().Be(expected);
        if (!sourceHasGeometry)
        {
            reader.ExtentQueries.Should().BeEmpty();
            result.Layers[0].Geometry.Sampled.Should().Be(0);
            result.Layers[0].Geometry.Reason.Should().Contain("not applicable");
            result.Layers[0].Extent.Reason.Should().Contain("not applicable");
        }
        else
        {
            result.Layers[0].Geometry.Classification.Should().Be("fail");
        }
    }

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

    [Theory]
    [InlineData(99, "fail")]
    [InlineData(100, "pass")]
    [InlineData(101, "fail")]
    public async Task Reconcile_DedicatedImportTarget_CountsEveryRowWithoutSourcePredicate(long targetCount, string expected)
    {
        const string sourceFilter = "DateOfFlight = DATE '2025-01-11' AND OBJECTID > 1000";
        var reader = new StubFeatureReader
        {
            Count = targetCount,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 5)
        };
        var request = BuildRequest(100, BoundingBox.Create(0, 0, 10, 10, 4326), ["OBJECTID", "NAME"]);
        request = request with
        {
            Layers = [request.Layers[0] with { FilterMirror = sourceFilter, TargetContainsOnlyImportedFeatures = true }]
        };

        var result = await NewService(reader).ReconcileAsync(request);

        // A 1% delta sits inside the default 5% pass band; a dedicated target must still fail it.
        result.Layers[0].Count.Classification.Should().Be(expected);
        result.Layers[0].Count.FilterMirror.Should().Be(sourceFilter);
        result.Layers[0].Count.TargetCount.Should().Be(targetCount);
        reader.CountQueries.Should().ContainSingle().Which.Where.Should().BeNull();
        reader.ExtentQueries.Should().ContainSingle().Which!.Value.Where.Should().BeNull();
        reader.SampleQueries.Should().ContainSingle().Which.Where.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_InheritedNullGeometry_IsNotTransferLoss()
    {
        var reader = new StubFeatureReader
        {
            Count = 2,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildGeometrySample((1, false), (2, true))
        };
        var request = BuildRequest(2, BoundingBox.Create(0, 0, 10, 10, 4326), ["NAME"]);
        request = request with
        {
            Layers =
            [
                request.Layers[0] with
                {
                    SourceGeometry = new SourceGeometryCensus
                    {
                        AbsentTargetFeatureIds = new HashSet<long> { 1 }
                    }
                }
            ]
        };

        var result = await NewService(reader).ReconcileAsync(request);

        var geometry = result.Layers[0].Geometry;
        geometry.Classification.Should().Be("pass");
        geometry.InheritedSourceDefects.Should().Be(1);
        geometry.MigrationLosses.Should().Be(0);
        geometry.Reason.Should().Contain("inherited a null source geometry");
    }

    [Fact]
    public async Task Reconcile_UnconvertedSourceGeometry_IsTransferLoss()
    {
        var reader = new StubFeatureReader
        {
            Count = 2,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildGeometrySample((1, false), (2, true))
        };
        var request = BuildRequest(2, BoundingBox.Create(0, 0, 10, 10, 4326), ["NAME"]);
        request = request with
        {
            Layers =
            [
                request.Layers[0] with
                {
                    SourceGeometry = new SourceGeometryCensus
                    {
                        UnconvertedTargetFeatureIds = new HashSet<long> { 1 }
                    }
                }
            ]
        };

        var result = await NewService(reader).ReconcileAsync(request);

        var geometry = result.Layers[0].Geometry;
        geometry.Classification.Should().Be("fail");
        geometry.MigrationLosses.Should().Be(1);
        geometry.InheritedSourceDefects.Should().Be(0);
        geometry.Reason.Should().Contain("lost in transfer");
        result.Classification.Should().Be("fail");
    }

    [Fact]
    public async Task Reconcile_NewlyNullTargetGeometry_IsTransferLossBesideInheritedNulls()
    {
        var reader = new StubFeatureReader
        {
            Count = 3,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildGeometrySample((1, false), (2, false), (3, true))
        };
        var request = BuildRequest(3, BoundingBox.Create(0, 0, 10, 10, 4326), ["NAME"]);
        request = request with
        {
            Layers =
            [
                request.Layers[0] with
                {
                    SourceGeometry = new SourceGeometryCensus
                    {
                        AbsentTargetFeatureIds = new HashSet<long> { 1 }
                    }
                }
            ]
        };

        var geometry = (await NewService(reader).ReconcileAsync(request)).Layers[0].Geometry;

        geometry.Classification.Should().Be("fail");
        geometry.InheritedSourceDefects.Should().Be(1);
        geometry.MigrationLosses.Should().Be(1);
        geometry.Reason.Should().Contain("lost in transfer").And.Contain("inherited");
    }

    [Fact]
    public async Task Reconcile_StaleAdvertisedExtent_ComparesTargetWithQueriedExtent()
    {
        var reader = new StubFeatureReader
        {
            Count = 2,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildGeometrySample((1, true), (2, true))
        };
        var request = BuildRequest(2, BoundingBox.Create(-100, -100, -90, -90, 4326), ["NAME"]);
        request = request with
        {
            Layers =
            [
                request.Layers[0] with
                {
                    QueriedSourceExtent = BoundingBox.Create(0, 0, 10, 10, 4326)
                }
            ]
        };

        var extent = (await NewService(reader).ReconcileAsync(request)).Layers[0].Extent;

        extent.AdvertisedExtentStale.Should().BeTrue();
        extent.Classification.Should().Be("warn");
        extent.QueriedSource!.Value.MinX.Should().Be(0);
        extent.Source!.Value.MinX.Should().Be(-100);
        extent.Reason.Should().Contain("queried");
        extent.MaxDimensionDelta.Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_StaleAdvertisedExtent_StillFailsWhenTargetMissesQueriedExtent()
    {
        var reader = new StubFeatureReader
        {
            Count = 2,
            Extent = FeatureExtent.Create(50, 50, 60, 60, 4326),
            Sample = BuildGeometrySample((1, true), (2, true))
        };
        var request = BuildRequest(2, BoundingBox.Create(-100, -100, -90, -90, 4326), ["NAME"]);
        request = request with
        {
            Layers =
            [
                request.Layers[0] with
                {
                    QueriedSourceExtent = BoundingBox.Create(0, 0, 10, 10, 4326)
                }
            ]
        };

        var extent = (await NewService(reader).ReconcileAsync(request)).Layers[0].Extent;

        extent.AdvertisedExtentStale.Should().BeTrue();
        extent.Classification.Should().Be("fail");
        extent.Reason.Should().Contain("queried extent");
    }

    [Fact]
    public async Task Reconcile_SharedTarget_RewritesFilterFieldsAndPreservesLiterals()
    {
        const string sourceFilter = "\"DateOfFlight\" = DATE '2026-01-01' AND UPPER(STATUS) = 'DateOfFlight'";
        var reader = new StubFeatureReader
        {
            Count = 100,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 5)
        };
        var request = BuildRequest(100, BoundingBox.Create(0, 0, 10, 10, 4326), ["OBJECTID", "NAME"]);
        request = request with
        {
            Layers =
            [
                request.Layers[0] with
                {
                    FilterMirror = sourceFilter,
                    FilterFieldMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["DateOfFlight"] = "flight_date",
                        ["STATUS"] = "status",
                        ["UPPER"] = "must_not_replace_function"
                    }
                }
            ]
        };

        FilterExpression? translated = null;
        var service = NewFilteredService(reader, expression => translated = expression);
        await service.ReconcileAsync(request);

        var expected = new GeoServicesSqlParser().Parse("flight_date = DATE '2026-01-01' AND UPPER(status) = 'DateOfFlight'");
        translated.Should().BeEquivalentTo(expected);
        reader.CountQueries.Should().ContainSingle().Which.Where.Should().BeNull();
        reader.CountQueries[0].SqlFilter.Should().NotBeNull();
        reader.SampleQueries.Should().ContainSingle().Which.SqlFilter.Should().BeSameAs(reader.CountQueries[0].SqlFilter);
    }

    [Fact]
    public async Task Reconcile_SharedTargetWithFilterMirror_KeepsMirroredPredicateAndTolerance()
    {
        const string sourceFilter = "STATUS = 'open'";
        var reader = new StubFeatureReader
        {
            Count = 101,
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 5)
        };
        var request = BuildRequest(100, BoundingBox.Create(0, 0, 10, 10, 4326), ["OBJECTID", "NAME"]);
        request = request with { Layers = [request.Layers[0] with { FilterMirror = sourceFilter }] };

        var result = await NewFilteredService(reader).ReconcileAsync(request);

        result.Layers[0].Count.Classification.Should().Be("pass");
        reader.CountQueries.Should().ContainSingle().Which.SqlFilter.Should().NotBeNull();
        reader.CountQueries[0].Where.Should().BeNull();
        reader.SampleQueries.Should().ContainSingle().Which.SqlFilter.Should().BeSameAs(reader.CountQueries[0].SqlFilter);
    }

    [Fact]
    public async Task Reconcile_SharedFilterWithoutCompiler_FailsWithoutReadingUnfilteredRows()
    {
        var reader = new StubFeatureReader();
        var request = BuildRequest(1, BoundingBox.Create(0, 0, 1, 1, 4326), ["NAME"]);
        request = request with { Layers = [request.Layers[0] with { FilterMirror = "STATUS = 'open'" }] };

        var result = await NewService(reader).ReconcileAsync(request);

        result.Layers[0].Count.Classification.Should().Be("fail");
        reader.CountQueries.Should().BeEmpty();
        reader.SampleQueries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, "STATUS = 'open'", false)]
    [InlineData(false, "STATUS = 'open'", true)]
    public async Task Reconcile_ProviderWithoutFilterService_OnlyFilteredSharedTargetFails(bool dedicated, string? filter, bool fails)
    {
        var reader = new StubFeatureReader
        {
            Count = 1,
            Extent = FeatureExtent.Create(0, 0, 1, 1, 4326),
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 1)
        };
        var request = BuildRequest(1, BoundingBox.Create(0, 0, 1, 1, 4326), ["OBJECTID", "NAME"]);
        request = request with
        {
            Layers = [request.Layers[0] with { FilterMirror = filter, TargetContainsOnlyImportedFeatures = dedicated }]
        };
        var metadata = new Mock<IMetadataV2GraphProvider>(MockBehavior.Strict);
        var service = new LayerReconciliationService(reader, TimeProvider.System, NullLogger<LayerReconciliationService>.Instance,
            new ReconciliationQueryBuilder(metadata.Object));

        var result = await service.ReconcileAsync(request);

        result.Layers[0].Count.Classification.Should().Be(fails ? "fail" : "pass");
        reader.CountQueries.Should().HaveCount(fails ? 0 : 1);
        reader.SampleQueries.Should().HaveCount(fails ? 0 : 1);
        metadata.VerifyNoOtherCalls();
    }

    private static LayerReconciliationService NewFilteredService(StubFeatureReader reader, Action<FilterExpression>? translated = null)
    {
        var resource = new MetadataV2Resource { Metadata = new() { Id = "target" } };
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Resources = [resource],
            StorageBindings = [new() { Metadata = new() { Id = "binding" }, ResourceId = "target", StorageLayerId = 1 }]
        }, "test", DateTimeOffset.UtcNow);
        var graph = new Mock<IMetadataV2GraphProvider>();
        graph.Setup(provider => provider.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var filters = new Mock<IFilterExpressionService>();
        filters.Setup(service => service.Parse(FilterLanguage.ArcGisSql, It.IsAny<string>()))
            .Returns((FilterLanguage _, string filter) => FilterParseResult.Success(new GeoServicesSqlParser().Parse(filter)));
        filters.Setup(service => service.Translate(It.IsAny<FilterExpression>(), resource))
            .Returns((FilterExpression expression, MetadataV2Resource _) =>
            {
                translated?.Invoke(expression);
                return FilterTranslationResult.Success(expression, new SqlFragment("status = @p0", ["open"]));
            });
        return new LayerReconciliationService(reader, TimeProvider.System, NullLogger<LayerReconciliationService>.Instance,
            new ReconciliationQueryBuilder(graph.Object, filters.Object));
    }

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

    private static QueryResult<Feature> BuildGeometrySample(params (long Id, bool Valid)[] rows)
    {
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("NAME", "x");
        var items = rows
            .Select(row => Feature.Create(row.Id, row.Valid ? [1, 1, 0, 0, 0] : null, attributes))
            .ToImmutableArray();
        return QueryResult<Feature>.Create(rows.Length, items);
    }

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

    [Theory]
    [InlineData(0, "pass")]
    [InlineData(5000, "fail")]
    public async Task Reconcile_PlannedReprojection_ComparesTargetRowsInSourceCrs(double offset, string expected)
    {
        var source = BoundingBox.Create(361431.356, 3736037.969, 483556.921, 3783589.624, 26911);
        var reader = new StubFeatureReader
        {
            Count = 26,
            Extent = FeatureExtent.Create(-118.5013, 33.7591, -117.1778, 34.1842, 4326),
            ComparisonExtent = FeatureExtent.Create(source.MinX + offset, source.MinY, source.MaxX + offset, source.MaxY, 26911),
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 26)
        };
        var request = BuildRequest(26, source, ["OBJECTID", "NAME"]);
        request = request with { Layers = [request.Layers[0] with { PlannedTargetSrid = 4326 }] };

        var result = await NewService(reader).ReconcileAsync(request);

        result.Layers[0].Extent.Classification.Should().Be(expected);
        result.Layers[0].Extent.Source!.Value.Srid.Should().Be(26911);
        result.Layers[0].Extent.Target!.Value.Srid.Should().Be(4326);
        result.Layers[0].Extent.ComparisonTarget!.Value.Srid.Should().Be(26911);
        reader.ExtentQueries.Should().HaveCount(2);
        reader.ExtentQueries[1]!.Value.OutputSrid.Should().Be(26911);
    }

    [Theory]
    [InlineData(4326, 0, "warn")]
    [InlineData(3857, 0, "warn")]
    [InlineData(null, 0, "pass")]
    [InlineData(4326, 5000, "fail")]
    public async Task Reconcile_QueriedExtentCrs_UsesSelectedBaselineForTargetObservation(
        int? advertisedSrid, double offset, string expected)
    {
        var queried = BoundingBox.Create(361431.356, 3736037.969, 483556.921, 3783589.624, 26911);
        var reader = new StubFeatureReader
        {
            Count = 26,
            Extent = FeatureExtent.Create(-118.5013, 33.7591, -117.1778, 34.1842, 4326),
            ComparisonExtent = FeatureExtent.Create(
                queried.MinX + offset, queried.MinY, queried.MaxX + offset, queried.MaxY, 26911),
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 26)
        };
        var request = BuildRequest(26, queried, ["OBJECTID", "NAME"]);
        request = request with
        {
            Layers =
            [
                request.Layers[0] with
                {
                    SourceExtent = advertisedSrid is { } srid ? BoundingBox.Create(0, 0, 10, 10, srid) : null,
                    QueriedSourceExtent = queried,
                    PlannedTargetSrid = 4326
                }
            ]
        };

        var extent = (await NewService(reader).ReconcileAsync(request)).Layers[0].Extent;

        extent.Classification.Should().Be(expected);
        (extent.Source?.Srid).Should().Be(advertisedSrid);
        extent.QueriedSource!.Value.Srid.Should().Be(26911);
        extent.ComparisonTarget!.Value.Srid.Should().Be(26911);
        extent.AdvertisedExtentStale.Should().Be(advertisedSrid.HasValue);
        reader.ExtentQueries.Should().HaveCount(2);
        reader.ExtentQueries[1]!.Value.OutputSrid.Should().Be(26911);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(4326, null)]
    [InlineData(4326, 3857)]
    [InlineData(3857, 26911)]
    public async Task Reconcile_UnplannedOrUnobservedReprojection_CannotPass(int? plannedSrid, int? comparisonSrid)
    {
        var reader = new StubFeatureReader
        {
            Count = 26,
            // Identical coordinate numbers do not prove parity when their CRSs differ.
            Extent = FeatureExtent.Create(0, 0, 10, 10, 4326),
            ComparisonExtent = comparisonSrid is { } srid ? FeatureExtent.Create(0, 0, 10, 10, srid) : null,
            Sample = BuildSample(("OBJECTID", "NAME"), validGeometry: true, rows: 26)
        };
        var request = BuildRequest(26, BoundingBox.Create(0, 0, 10, 10, 26911), ["OBJECTID", "NAME"]);
        request = request with { Layers = [request.Layers[0] with { PlannedTargetSrid = plannedSrid }] };

        var result = await NewService(reader).ReconcileAsync(request);

        result.Layers[0].Extent.Classification.Should().Be("fail");
        result.Layers[0].Extent.ComparisonTarget.Should().BeNull();
    }

    private sealed class StubFeatureReader : IFeatureReader
    {
        public long Count { get; init; }
        public FeatureExtent? Extent { get; init; }
        public FeatureExtent? ComparisonExtent { get; init; }
        public List<FeatureQuery?> ExtentQueries { get; } = [];
        public List<FeatureQuery> CountQueries { get; } = [];
        public List<FeatureQuery> SampleQueries { get; } = [];
        public QueryResult<Feature> Sample { get; init; } = QueryResult<Feature>.Empty();

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            CountQueries.Add(query);
            return Task.FromResult(Count);
        }

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
        {
            ExtentQueries.Add(query);
            return Task.FromResult(query?.OutputSrid is not null ? ComparisonExtent : Extent);
        }

        public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            SampleQueries.Add(query);
            return Task.FromResult(Sample);
        }

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
