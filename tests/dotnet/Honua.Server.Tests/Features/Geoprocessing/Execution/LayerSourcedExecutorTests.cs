// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// End-to-end coverage for the layer-aware, layer-SOURCED managed executors (#2322,
/// #2325): <c>analytics.buffer-aggregate</c>, <c>conversion.feature-project</c>,
/// <c>generalization.dissolve</c>, and <c>generalization.simplify-layer</c>. Each is
/// the job-executable counterpart of a per-operation OGC API - Processes projection
/// (#1382) that advertises a <c>layerId</c> input: the executor streams a Honua
/// catalog layer through <c>source.honua-layer</c> (faked here so no Postgres runs),
/// applies the managed NetTopologySuite op in one dispatched job, and publishes the
/// canonical FeatureCollection artifact — reaching a terminal SUCCEEDED state that the
/// bare projected ids previously never reached (they were refused at dispatch).
/// </summary>
public sealed class LayerSourcedExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";
    private const string HonuaLayerSourceId = "source.honua-layer";

    [Theory]
    [InlineData(100, true)]
    [InlineData(99, false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Dissolve_TopologyWorkBoundary_ProducesExactUnionOrRejectsBeforeCompute(long budget, bool succeeds)
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
            [BoxFeature(0, 0, 2, 2), BoxFeature(1, 1, 3, 3)]);
        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(source), Options(maxTopologyWork: budget),
                NullLogger<LayerDissolveExecutor>.Instance),
            LayerDissolveExecutor.HandledProcessId, ("layerId", "9"));
        if (succeeds)
        {
            status.Should().Be(ExecutionJobStatus.Succeeded);
            var result = ReadFeatures(uri!).Single();
            // Two 2x2 squares overlap in one 1x1 square: union area = 4 + 4 - 1.
            result.Geometry.Area.Should().Be(7);
            Convert.ToInt64(result.Attributes["COUNT"], CultureInfo.InvariantCulture).Should().Be(2);
        }
        else
        {
            status.Should().Be(ExecutionJobStatus.Failed);
            uri.Should().BeNull();
            _lastErrorForAssertions.Should().Contain("MaxTopologyWork=99").And.Contain("stopped before computation");
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Dissolve_CumulativeVertexBoundary_IsChargedAcrossFeatures(int vertices, bool succeeds)
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(1, 2), PointFeature(3, 4)]);
        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(source), Options(maxLayerVertices: vertices),
                NullLogger<LayerDissolveExecutor>.Instance),
            LayerDissolveExecutor.HandledProcessId, ("layerId", "9"), ("dissolve", "false"));
        status.Should().Be(succeeds ? ExecutionJobStatus.Succeeded : ExecutionJobStatus.Failed);
        if (succeeds)
        {
            ReadFeatures(uri!).Select(f => f.Geometry.Coordinate.X).Should().Equal(1, 3);
        }
        else
        {
            uri.Should().BeNull();
            _lastErrorForAssertions.Should().Contain("MaxLayerVertices=1").And.Contain("while streaming");
        }
    }

    [UnitTest]
    public async Task Dissolve_ElapsedReadLimit_DisposesSourceAndPublishesNothing()
    {
        var source = new WaitingLayerSource();
        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(source), Options(maxExecutionSeconds: 1),
                NullLogger<LayerDissolveExecutor>.Instance),
            LayerDissolveExecutor.HandledProcessId, ("layerId", "9"));
        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        source.Disposed.Should().BeTrue();
        _lastErrorForAssertions.Should().Contain("MaxLayerExecutionSeconds=1").And.Contain("resubmit");
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(9, false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task SpatialJoin_TopologyBudgetChargesBothSides(long budget, bool succeeds)
    {
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [1] = [PointFeature(0, 0), PointFeature(10, 10)],
            [2] = [BoxFeature(-1, -1, 1, 1)]
        });
        var (status, uri, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(source), Options(maxTopologyWork: budget),
                NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId, ("layerId", "1"), ("joinLayerId", "2"));
        status.Should().Be(succeeds ? ExecutionJobStatus.Succeeded : ExecutionJobStatus.Failed);
        if (succeeds)
        {
            ReadFeatures(uri!).Select(f => Convert.ToInt64(f.Attributes["JOIN_COUNT"], CultureInfo.InvariantCulture))
                .Should().Equal(1, 0);
        }
        else
        {
            uri.Should().BeNull();
            _lastErrorForAssertions.Should().Contain("MaxTopologyWork=9").And.Contain("stopped before computation");
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task BufferAggregate_IntermediateVerticesBoundedEvenWhenDissolving(string dissolve)
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0), PointFeature(1, 1)]);
        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, 7, 3857, false), Options(maxLayerVertices: 32),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId, ("layerId", "7"), ("distance", "1"), ("dissolve", dissolve));
        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain("MaxLayerVertices").And.Contain("stopped during buffering");
    }

    [UnitTest]
    public async Task BufferAggregate_OversizedTopology_IsRejectedBeforeGeometryService()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [BoxFeature(0, 0, 2, 2)]);
        var geometryOps = Substitute.For<IGeometryOperationService>();
        var services = new ServiceCollection();
        services.AddSingleton<IDagFeatureSource>(source);
        services.AddSingleton(geometryOps);
        services.AddSingleton<IMetadataV2GraphProvider>(new FakeMetadataV2GraphProvider(7, 3857));
        using var provider = services.BuildServiceProvider();
        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(provider.GetRequiredService<IServiceScopeFactory>(),
                Options(maxTopologyWork: 24), NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId, ("layerId", "7"), ("distance", "1"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain("MaxTopologyWork=24").And.Contain("stopped before computation");
        geometryOps.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task Dissolve_NestedUnicodeAttributes_AreChargedToInputBudget()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"items\":[\"" + new string('界', 200) + "\"]}");
        var feature = PointFeature(1, 2) with
        {
            Attributes = new Dictionary<string, object?> { ["tree"] = document.RootElement }
        };
        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(new FakeDagFeatureSource(HonuaLayerSourceId, [feature])),
                Options(), NullLogger<LayerDissolveExecutor>.Instance, LimitsOptions(maxInputBytes: 500)),
            LayerDissolveExecutor.HandledProcessId, ("layerId", "9"), ("dissolve", "false"));
        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain("MaxInputBytes");
    }

    private sealed class WaitingLayerSource : IDagFeatureSource
    {
        public string SourceId => HonuaLayerSourceId;
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
            DagSourceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return PointFeature(1, 2);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Disposed = true;
            }
        }
    }

    [UnitTest]
    public async Task BufferAggregate_DissolvesLayerFeatures_ReachesSucceededWithCount()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
        [
            PointFeature(0, 0),
            PointFeature(1, 1),
        ]);

        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false),
                Options(),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "1000"),
            ("unit", "meters"),
            ("dissolve", "true"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle("dissolve=true unions all buffers into one feature");
        Convert.ToInt64(features[0].Attributes.GetOptionalValue(LayerBufferAggregateExecutor.CountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(2);
        var geometry = features[0].Geometry!;
        geometry.GeometryType.Should().Be("Polygon");
        geometry.EnvelopeInternal.MinX.Should().BeApproximately(-1000, 0.001);
        geometry.EnvelopeInternal.MinY.Should().BeApproximately(-1000, 0.001);
        geometry.EnvelopeInternal.MaxX.Should().BeApproximately(1001, 0.001);
        geometry.EnvelopeInternal.MaxY.Should().BeApproximately(1001, 0.001);
        geometry.Area.Should().BeInRange(3_120_000, 3_130_000,
            "the dissolved output must be the union of two overlapping 1000-unit point buffers");
    }

    [UnitTest]
    public async Task Dissolve_AtConfiguredFeatureLimit_Succeeds()
    {
        // #4629 threshold boundary: exactly the configured cap must be admitted.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0), PointFeature(1, 1)]);

        var (status, _, _) = await RunAsync(
            new LayerDissolveExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance,
                LimitsOptions(maxInputFeatures: 2)),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task Dissolve_ExceedsConfiguredFeatureLimit_FailsBeforeComputation()
    {
        // #4629: one feature over the configured cap must fail closed WHILE STREAMING,
        // with an actionable message — never silently truncate or compute over a partial read.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
            [PointFeature(0, 0), PointFeature(1, 1), PointFeature(2, 2)]);

        var (status, _, _) = await RunAsync(
            new LayerDissolveExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance,
                LimitsOptions(maxInputFeatures: 2)),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Failed);
        _lastErrorForAssertions.Should().Contain("exceeds the configured limit of 2 features");
    }

    [UnitTest]
    public async Task Dissolve_SingleGeometryExceedsVertexLimit_FailsBeforeComputation()
    {
        // #4629: a feature-count cap alone does not bound one deliberately oversized
        // geometry; the per-geometry vertex ceiling must charge independently.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [LineFeature(vertexCount: 100)]);

        var (status, _, _) = await RunAsync(
            new LayerDissolveExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance,
                LimitsOptions(maxVerticesPerGeometry: 50)),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Failed);
        _lastErrorForAssertions.Should().Contain("100 vertices").And.Contain("exceeding the configured limit of 50");
    }

    [UnitTest]
    public async Task Dissolve_SingleGeometryAtVertexLimit_Succeeds()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [LineFeature(vertexCount: 50)]);

        var (status, _, _) = await RunAsync(
            new LayerDissolveExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance,
                LimitsOptions(maxVerticesPerGeometry: 50)),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task SpatialJoin_JoinLayerExceedsConfiguredFeatureLimit_FailsBeforeComputation()
    {
        // #4629: the join (second) layer must be bounded by the SAME admission limits as
        // the target layer — a two-layer op that only bounded one side would let the
        // unbounded side alone destabilize the worker.
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [1] = [PointFeature(0, 0)],
            [2] = [PointFeature(0, 0), PointFeature(1, 1), PointFeature(2, 2)],
        });

        var (status, _, _) = await RunAsync(
            new LayerSpatialJoinExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance,
                LimitsOptions(maxInputFeatures: 2)),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "1"),
            ("joinLayerId", "2"));

        status.Should().Be(ExecutionJobStatus.Failed);
        _lastErrorForAssertions.Should().Contain("join layer 2").And.Contain("exceeds the configured limit of 2 features");
    }

    [UnitTest]
    public async Task Dissolve_SingleGeometryAtByteLimit_Succeeds()
    {
        // #4629 threshold boundary for the per-geometry serialized-size cap
        // (Limits:Geometry:MaxGeometrySize): a geometry of exactly the cap is admitted.
        var feature = LineFeature(vertexCount: 10);
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [feature]);

        var (status, _, _) = await RunAsync(
            new LayerDissolveExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance,
                LimitsOptions(maxGeometryBytes: feature.GeometryGeoJson!.Length)),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Succeeded, _lastErrorForAssertions);
    }

    [UnitTest]
    public async Task Dissolve_SingleGeometryExceedsByteLimit_FailsBeforeComputation()
    {
        // One byte over the cap fails while streaming, before the geometry is parsed.
        var feature = LineFeature(vertexCount: 10);
        var bytes = feature.GeometryGeoJson!.Length;
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [feature]);

        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance,
                LimitsOptions(maxGeometryBytes: bytes - 1)),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull("no partial result may be published");
        _lastErrorForAssertions.Should().Contain($"{bytes} serialized bytes")
            .And.Contain($"limit of {bytes - 1} bytes").And.Contain("MaxGeometrySize");
    }

    [UnitTest]
    public async Task Dissolve_CumulativeInputAtByteBudget_Succeeds()
    {
        // #4629: the cumulative input budget (Limits:Analytics:MaxInputBytes). Attribute-free
        // features make the charge exactly the sum of the serialized geometries.
        DagSourceFeature[] features = [LineFeature(10), LineFeature(10), LineFeature(10)];
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, features);

        var (status, _, _) = await RunAsync(
            new LayerDissolveExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance,
                LimitsOptions(maxInputBytes: features.Sum(f => (long)f.GeometryGeoJson!.Length))),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Succeeded, _lastErrorForAssertions);
    }

    [UnitTest]
    public async Task Dissolve_CumulativeInputExceedsByteBudget_FailsWhileStreaming()
    {
        // Three features of the same size, one byte of budget short: the third feature trips it.
        DagSourceFeature[] features = [LineFeature(10), LineFeature(10), LineFeature(10)];
        var budget = features.Sum(f => (long)f.GeometryGeoJson!.Length) - 1;
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, features);

        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance,
                LimitsOptions(maxInputBytes: budget)),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain($"input budget of {budget} bytes")
            .And.Contain("at feature 3").And.Contain("MaxInputBytes");
    }

    [UnitTest]
    public async Task SpatialJoin_JoinLayerExceedsByteBudget_FailsBeforeComputation()
    {
        // The join side is charged against the same byte budget as the target side.
        DagSourceFeature[] joinFeatures = [LineFeature(10), LineFeature(10), LineFeature(10)];
        var budget = 2L * joinFeatures[0].GeometryGeoJson!.Length;
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [1] = [PointFeature(0, 0)],
            [2] = joinFeatures,
        });

        var (status, _, _) = await RunAsync(
            new LayerSpatialJoinExecutor(
                ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance,
                LimitsOptions(maxInputBytes: budget)),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "1"),
            ("joinLayerId", "2"));

        status.Should().Be(ExecutionJobStatus.Failed);
        _lastErrorForAssertions.Should().Contain("join layer 2").And.Contain($"input budget of {budget} bytes");
    }

    [UnitTest]
    public async Task Dissolve_OutputBeyondArtifactLowerBound_FailsBeforeSerialization()
    {
        // #4629: a 100-vertex output needs at least 100 x 5 = 500 bytes of GeoJSON ("[x,y]" per
        // vertex), so a 499-byte artifact budget is refused before serialization allocates it.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [LineFeature(vertexCount: 100)]);

        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(source), Options(maxArtifactBytes: 499), NullLogger<LayerDissolveExecutor>.Instance),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain("100 vertices").And.Contain("at least 500 bytes")
            .And.Contain("stopped before serialization");
    }

    [UnitTest]
    public async Task Dissolve_OutputAtArtifactLowerBound_ReachesTheSerializedSizeCheck()
    {
        // At exactly the lower bound the pre-check must not reject (the output might still fit);
        // the real serialized size is then checked, and this payload is larger than 500 bytes.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [LineFeature(vertexCount: 100)]);

        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(source), Options(maxArtifactBytes: 500), NullLogger<LayerDissolveExecutor>.Instance),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain("artifact size").And.Contain("MaxArtifactBytes=500")
            .And.NotContain("stopped before serialization");
    }

    [UnitTest]
    public async Task BufferAggregate_UndissolvedExpansionBeyondArtifactBudget_FailsDuringBuffering()
    {
        // Without dissolve every buffer is emitted, so the running vertex count is charged as
        // each feature is buffered: a 5-byte artifact budget holds one vertex, and the first
        // buffered polygon alone has more.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0), PointFeature(10, 0), PointFeature(20, 0)]);

        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false),
                Options(maxArtifactBytes: 5),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "1"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain("after 1 of 3 features").And.Contain("stopped during buffering");
    }

    [UnitTest]
    public async Task BufferAggregate_DissolvedExpansionBeyondArtifactBudget_FailsBeforeSerialization()
    {
        // With dissolve the union may shrink the output, so buffering is not charged; the
        // dissolved result is bounded before serialization instead.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0), PointFeature(10, 0), PointFeature(20, 0)]);

        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false),
                Options(maxArtifactBytes: 5),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "1"),
            ("dissolve", "true"));

        status.Should().Be(ExecutionJobStatus.Failed);
        uri.Should().BeNull();
        _lastErrorForAssertions.Should().Contain("stopped before serialization");
    }

    [UnitTest]
    public async Task BufferAggregate_MissingLayerId_FailsWithClassifiedError()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0)]);

        var (status, _, _) = await RunAsync(
            new LayerBufferAggregateExecutor(ScopeFactory(source), Options(), NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("distance", "10"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task BufferAggregate_GeographicLayer_BuffersByGroundMeters_NotDegrees()
    {
        // #4623 regression: a geographic (EPSG:4326) layer buffered by a metric distance
        // must NOT apply that distance as degrees (the prior bug) — the resulting buffer
        // must be a few thousandths of a degree wide, not ~1000 degrees wide. The expected
        // envelope is computed from an INDEPENDENT closed-form approximation (the standard
        // ~111,320 m/degree-of-latitude constant and its cos(latitude) correction for
        // longitude), a different code path from the executor's own Web-Mercator buffer.
        const double lon = -122.4194;
        const double lat = 37.7749; // San Francisco
        const double distanceMeters = 1000;
        const double metersPerDegreeLatitude = 111_320.0;

        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(lon, lat)]);

        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, layerId: 7, storageSrid: 4326, isGeographic: true),
                Options(),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", distanceMeters.ToString(CultureInfo.InvariantCulture)),
            ("unit", "meters"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        var envelope = features[0].Geometry!.EnvelopeInternal;

        var expectedLatHalfExtent = distanceMeters / metersPerDegreeLatitude;
        var expectedLonHalfExtent = distanceMeters / (metersPerDegreeLatitude * Math.Cos(lat * Math.PI / 180.0));

        envelope.MinY.Should().BeApproximately(lat - expectedLatHalfExtent, 0.0005,
            "1000 m at this latitude is roughly 0.009 degrees of latitude, not 1000 degrees");
        envelope.MaxY.Should().BeApproximately(lat + expectedLatHalfExtent, 0.0005);
        envelope.MinX.Should().BeApproximately(lon - expectedLonHalfExtent, 0.0005);
        envelope.MaxX.Should().BeApproximately(lon + expectedLonHalfExtent, 0.0005);
    }

    [UnitTest]
    public async Task BufferAggregate_NonMetricProjectedLayer_ConvertsDistanceToNativeUnit()
    {
        // #4623 regression: a projected layer whose native linear unit is NOT meters (US
        // survey feet here) must convert the metric input distance into that native unit
        // before buffering — never assume every projected CRS is metric. The expected
        // buffer radius (in native units) is computed independently via plain division,
        // a different code path from the executor/service's own conversion.
        const double metersPerUsSurveyFoot = 0.3048006096012192;
        const double distanceMeters = 100;
        const double centerX = 1000;
        const double centerY = 2000;
        var expectedNativeRadius = distanceMeters / metersPerUsSurveyFoot;

        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(centerX, centerY)]);

        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(
                    source, layerId: 9, storageSrid: 2229, isGeographic: false, metersPerUnit: metersPerUsSurveyFoot),
                Options(),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "9"),
            ("distance", distanceMeters.ToString(CultureInfo.InvariantCulture)),
            ("unit", "meters"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        var envelope = features[0].Geometry!.EnvelopeInternal;

        (envelope.MaxX - centerX).Should().BeApproximately(expectedNativeRadius, 0.01,
            "100 meters over a 0.3048-m survey foot is ~328.08 native units, not 100");
        (centerX - envelope.MinX).Should().BeApproximately(expectedNativeRadius, 0.01);
        (envelope.MaxY - centerY).Should().BeApproximately(expectedNativeRadius, 0.01);
        (centerY - envelope.MinY).Should().BeApproximately(expectedNativeRadius, 0.01);
    }

    [UnitTest]
    public async Task BufferAggregate_NoGeometryOperationServiceConfigured_FailsClosed()
    {
        // #4623: when the CRS-aware geometry service / metadata provider are unavailable,
        // the executor must refuse rather than silently fall back to the old CRS-blind math.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0)]);

        var (status, _, _) = await RunAsync(
            new LayerBufferAggregateExecutor(ScopeFactory(source), Options(), NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "10"),
            ("unit", "meters"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task BufferAggregate_ObjectIdsAndWhereAndEnvelope_AllPropagateToSourceRequest()
    {
        // #4624: 'where' already propagated; 'objectIds' and the GeoServices
        // geometry/geometryType/inSR/spatialRel envelope family did not — the catalog
        // advertised them but LayerSourcedFeatureExecutor silently dropped them. Assert
        // the built DagSourceRequest carries the EXACT values for every selector together,
        // not a subset. The geometry family reaches source.honua-layer verbatim so the
        // canonical selection translator (the synchronous analytics interpretation) owns it;
        // LayerSourceExecutionProofTests proves the real rows it selects.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0)]);

        var (status, _, request) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false),
                Options(),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "10"),
            ("where", "pop > 100"),
            ("objectIds", "3, 8, 21"),
            ("geometry", """{"xmin":-10,"ymin":-20,"xmax":30,"ymax":40}"""),
            ("geometryType", "esriGeometryEnvelope"),
            ("inSR", "4326"),
            ("spatialRel", "esriSpatialRelIntersects"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        request.Should().NotBeNull();
        request!.Where.Should().Be("pop > 100");
        request.ObjectIds.Should().Be("3, 8, 21");
        request.Geometry.Should().Be("""{"xmin":-10,"ymin":-20,"xmax":30,"ymax":40}""");
        request.GeometryType.Should().Be("esriGeometryEnvelope");
        request.InSr.Should().Be("4326");
        request.SpatialRel.Should().Be("esriSpatialRelIntersects");
        request.Bbox.Should().BeNull("the geometry filter is interpreted canonically by the source, not approximated as a bbox");
    }

    [UnitTest]
    public async Task BufferAggregate_TimeFilter_PropagatesToCanonicalSource()
    {
        // #4624: time/timeRelation used to be rejected outright; they now reach
        // source.honua-layer verbatim, which evaluates them against the layer's
        // configured temporal fields through the canonical translator.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0)]);

        var (status, _, request) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false),
                Options(),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "10"),
            ("time", "1700000000000,1700086400000"),
            ("timeRelation", "esriTimeRelationOverlaps"));

        status.Should().Be(ExecutionJobStatus.Succeeded, _lastErrorForAssertions);
        request!.Time.Should().Be("1700000000000,1700086400000");
        request.TimeRelation.Should().Be("esriTimeRelationOverlaps");
    }

    [UnitTest]
    public async Task BufferAggregate_UnsupportedGeometryType_IsRejected()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0)]);

        var (status, _, _) = await RunAsync(
            new LayerBufferAggregateExecutor(ScopeFactory(source), Options(), NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "10"),
            ("geometry", """{"rings":[[[0,0],[0,1],[1,1],[0,0]]]}"""),
            ("geometryType", "esriGeometryBogus"));

        status.Should().Be(ExecutionJobStatus.Failed,
            "a broadened, unsupported geometry filter must be rejected rather than silently widening the selection");
        _allRequestsForAssertions.Should().BeEmpty("the selector is rejected before any layer read");
    }

    [UnitTest]
    public async Task BufferAggregate_UnsupportedSpatialRel_IsRejected()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0)]);

        var (status, _, _) = await RunAsync(
            new LayerBufferAggregateExecutor(ScopeFactory(source), Options(), NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "10"),
            ("geometry", """{"xmin":0,"ymin":0,"xmax":1,"ymax":1}"""),
            ("geometryType", "esriGeometryEnvelope"),
            ("spatialRel", "esriSpatialRelWithinDistance"));

        status.Should().Be(ExecutionJobStatus.Failed,
            "distance-based relationships collide with the operation's own 'distance' and are rejected, as on the synchronous surface");
        _allRequestsForAssertions.Should().BeEmpty();
    }

    [UnitTest]
    public async Task BufferAggregate_InvalidObjectIds_IsRejected()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0)]);

        var (status, _, _) = await RunAsync(
            new LayerBufferAggregateExecutor(ScopeFactory(source), Options(), NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "10"),
            ("objectIds", "3,not-a-number"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task BufferAggregate_OutStatistics_ComputesGroupAggregatesOnDissolve()
    {
        // Independent oracle: zone "a" has pop values 5 and 7 -> SUM=12, MEAN=6, MAX=7.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
        [
            NamedPointWithNumericField(0, 0, "zone", "a", "pop", 5),
            NamedPointWithNumericField(1, 1, "zone", "a", "pop", 7),
            NamedPointWithNumericField(50, 50, "zone", "b", "pop", 100),
        ]);

        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false),
                Options(),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "1"),
            ("unit", "meters"),
            ("dissolve", "true"),
            ("groupByFields", "zone"),
            ("outStatistics", "pop:sum;pop:mean;pop:max"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        var zoneA = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "a"));
        Convert.ToDouble(zoneA.Attributes.GetOptionalValue("SUM_pop"), CultureInfo.InvariantCulture).Should().Be(12);
        Convert.ToDouble(zoneA.Attributes.GetOptionalValue("MEAN_pop"), CultureInfo.InvariantCulture).Should().Be(6);
        Convert.ToDouble(zoneA.Attributes.GetOptionalValue("MAX_pop"), CultureInfo.InvariantCulture).Should().Be(7);

        var zoneB = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "b"));
        Convert.ToDouble(zoneB.Attributes.GetOptionalValue("SUM_pop"), CultureInfo.InvariantCulture).Should().Be(100);
    }

    [UnitTest]
    public async Task BufferAggregate_OutStatisticsWithoutDissolve_IsRejected()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(0, 0)]);

        var (status, _, _) = await RunAsync(
            new LayerBufferAggregateExecutor(ScopeFactory(source), Options(), NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "10"),
            ("dissolve", "false"),
            ("outStatistics", "pop:sum"));

        status.Should().Be(ExecutionJobStatus.Failed,
            "per-feature output cannot carry aggregate columns, matching generalization.dissolve's identical guard");
    }

    [UnitTest]
    public async Task FeatureProject_RequestsServerSideReprojection_ReachesSucceeded()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(1, 2)]);

        var (status, uri, request) = await RunAsync(
            new LayerFeatureProjectExecutor(ScopeFactory(source), Options(), NullLogger<LayerFeatureProjectExecutor>.Instance),
            LayerFeatureProjectExecutor.HandledProcessId,
            ("layerId", "3"),
            ("targetSrid", "3857"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().ContainSingle();

        // Reprojection is delegated to the shared query pipeline: the executor asks
        // source.honua-layer to stream the layer already in the target CRS.
        request.Should().NotBeNull();
        request!.LayerId.Should().Be(3);
        request.OutputSrid.Should().Be(3857);
    }

    [UnitTest]
    public async Task FeatureProject_MissingTargetSrid_Fails()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId, [PointFeature(1, 2)]);

        var (status, _, _) = await RunAsync(
            new LayerFeatureProjectExecutor(ScopeFactory(source), Options(), NullLogger<LayerFeatureProjectExecutor>.Instance),
            LayerFeatureProjectExecutor.HandledProcessId,
            ("layerId", "3"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task Dissolve_GroupsByField_EmitsOneFeaturePerGroupWithStatistics()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
        [
            BoxFeature(0, 0, 10, 10, ("zone", "a"), ("pop", 5)),
            BoxFeature(10, 0, 20, 10, ("zone", "a"), ("pop", 7)),
            BoxFeature(0, 20, 10, 30, ("zone", "b"), ("pop", 3)),
        ]);

        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("groupByFields", "zone"),
            ("outStatistics", "pop:sum"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "one dissolved feature per zone group");

        var zoneA = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "a"));
        Convert.ToInt64(zoneA.Attributes.GetOptionalValue(LayerDissolveExecutor.CountAttribute), CultureInfo.InvariantCulture).Should().Be(2);
        Convert.ToDouble(zoneA.Attributes.GetOptionalValue("SUM_pop"), CultureInfo.InvariantCulture).Should().Be(12);
    }

    [UnitTest]
    public async Task Dissolve_MultipleStatisticsOnSameField_DoNotDoubleCountSamples()
    {
        // #4624 regression: requesting more than one aggregate on the SAME field
        // (e.g. "pop:sum;pop:mean") previously shared one FieldAccumulator keyed by field
        // name but added the sample once per co-requested stat, silently multiplying
        // SUM/MEAN by the stat count. Independent oracle: zone "a" has pop values 5 and
        // 7 -> SUM=12, MEAN=6 regardless of how many stats target "pop".
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
        [
            BoxFeature(0, 0, 10, 10, ("zone", "a"), ("pop", 5)),
            BoxFeature(10, 0, 20, 10, ("zone", "a"), ("pop", 7)),
        ]);

        var (status, uri, _) = await RunAsync(
            new LayerDissolveExecutor(ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance),
            LayerDissolveExecutor.HandledProcessId,
            ("layerId", "9"),
            ("groupByFields", "zone"),
            ("outStatistics", "pop:sum;pop:mean;pop:max"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        Convert.ToDouble(features[0].Attributes.GetOptionalValue("SUM_pop"), CultureInfo.InvariantCulture).Should().Be(12);
        Convert.ToDouble(features[0].Attributes.GetOptionalValue("MEAN_pop"), CultureInfo.InvariantCulture).Should().Be(6);
        Convert.ToDouble(features[0].Attributes.GetOptionalValue("MAX_pop"), CultureInfo.InvariantCulture).Should().Be(7);
    }

    [UnitTest]
    public async Task SimplifyLayer_ReducesVertices_AndCarriesAttributes()
    {
        // A near-collinear vertex on the bottom edge is removed by Douglas-Peucker.
        var wkt = new WKTReader();
        var polygon = wkt.Read("POLYGON ((0 0, 5 0.0001, 10 0, 10 10, 0 10, 0 0))");
        var feature = new Feature(polygon, new AttributesTable { { "name", "poly" } });
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
        [
            new DagSourceFeature
            {
                GeometryGeoJson = new GeoJsonWriter().Write(feature.Geometry),
                Attributes = new Dictionary<string, object?> { ["name"] = "poly" },
            },
        ]);

        var (status, uri, _) = await RunAsync(
            new LayerSimplifyExecutor(ScopeFactory(source), Options(), NullLogger<LayerSimplifyExecutor>.Instance),
            LayerSimplifyExecutor.HandledProcessId,
            ("layerId", "2"),
            ("tolerance", "0.5"),
            ("preserveTopology", "true"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        features[0].Attributes.GetOptionalValue("name").Should().Be("poly");
        features[0].Geometry!.Coordinates.Length.Should().BeLessThan(6, "the near-collinear vertex is dropped");
    }

    [UnitTest]
    public async Task LayerSourced_NoHonuaLayerConnectorRegistered_FailsClosed()
    {
        // Register a source with a different id so resolution finds no honua-layer source.
        var source = new FakeDagFeatureSource("source.wfs", []);

        var (status, _, _) = await RunAsync(
            new LayerSimplifyExecutor(ScopeFactory(source), Options(), NullLogger<LayerSimplifyExecutor>.Instance),
            LayerSimplifyExecutor.HandledProcessId,
            ("layerId", "2"),
            ("tolerance", "0.5"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task SpatialJoin_IntersectsCarriesJoinFields_ReachesSucceededWithJoinCount()
    {
        // Target layer 7: two disjoint zone polygons. Join layer 8: three named points,
        // two inside zone A and one inside zone B.
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [7] =
            [
                BoxFeature(0, 0, 10, 10, ("zone", "a")),
                BoxFeature(20, 20, 30, 30, ("zone", "b")),
            ],
            [8] =
            [
                NamedPoint(5, 5, "p1"),
                NamedPoint(6, 6, "p2"),
                NamedPoint(25, 25, "p3"),
            ],
        });

        var (status, uri, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "7"),
            ("joinLayerId", "8"),
            ("predicate", "intersects"),
            ("carryFields", "name"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "each target zone is preserved one-to-one");

        var zoneA = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "a"));
        Convert.ToInt64(zoneA.Attributes.GetOptionalValue(LayerSpatialJoinExecutor.JoinCountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(2);
        CarriedValues(zoneA, "name").Should().BeEquivalentTo(new[] { "p1", "p2" });

        var zoneB = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "b"));
        Convert.ToInt64(zoneB.Attributes.GetOptionalValue(LayerSpatialJoinExecutor.JoinCountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(1);
        CarriedValues(zoneB, "name").Should().BeEquivalentTo(new[] { "p3" });
    }

    [UnitTest]
    public async Task SpatialJoin_Dwithin_MatchesNeighboursWithinDistance()
    {
        // Target point at origin; join layer has one point 3 units away and one 30 away.
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [1] = [NamedPoint(0, 0, "target")],
            [2] =
            [
                NamedPoint(3, 0, "near"),
                NamedPoint(30, 0, "far"),
            ],
        });

        var (status, uri, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "1"),
            ("joinLayerId", "2"),
            ("predicate", "dwithin"),
            ("distance", "5"),
            ("carryFields", "name"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        Convert.ToInt64(features[0].Attributes.GetOptionalValue(LayerSpatialJoinExecutor.JoinCountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(1, "only the point within 5 units matches");
        CarriedValues(features[0], "name").Should().BeEquivalentTo(new[] { "near" });
    }

    [UnitTest]
    public async Task SpatialJoin_MissingJoinLayerId_Fails()
    {
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [7] = [BoxFeature(0, 0, 10, 10, ("zone", "a"))],
        });

        var (status, _, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "7"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task BufferAggregate_MultiFieldGroupBy_AmbiguousValues_ProducesSeparateGroups()
    {
        // BH2-017 regression: BuildGroupKey previously used string.Join("", parts) with no
        // separator, so {"San","Jose"} and {"Sa","nJose"} both produced key "SanJose" and
        // were dissolved into the same geometry/group. With the fix they must dissolve separately.
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
        [
            new DagSourceFeature
            {
                GeometryGeoJson = """{"type":"Point","coordinates":[0,0]}""",
                Attributes = new Dictionary<string, object?> { ["first"] = "San", ["last"] = "Jose" },
            },
            new DagSourceFeature
            {
                GeometryGeoJson = """{"type":"Point","coordinates":[50,50]}""",
                Attributes = new Dictionary<string, object?> { ["first"] = "Sa", ["last"] = "nJose" },
            },
        ]);

        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(
                BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false),
                Options(),
                NullLogger<LayerBufferAggregateExecutor>.Instance),
            LayerBufferAggregateExecutor.HandledProcessId,
            ("layerId", "7"),
            ("distance", "1"),
            ("unit", "meters"),
            ("dissolve", "true"),
            ("groupByFields", "first,last"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "distinct (first,last) combinations must produce separate dissolved groups even when the concatenated values would collide");
        features.Should().AllSatisfy(f =>
            Convert.ToInt64(f.Attributes.GetOptionalValue(LayerBufferAggregateExecutor.CountAttribute), CultureInfo.InvariantCulture)
                .Should().Be(1, "each group has exactly one member"));
    }

    // -------------------------------------------------------------------------
    // #4624 catalog-to-execution parameter fidelity inventory
    // -------------------------------------------------------------------------

    private static readonly string[] SharedSelectors =
        ["where", "objectIds", "geometry", "geometryType", "inSR", "spatialRel", "time", "timeRelation"];

    /// <summary>
    /// Every parameter the catalog advertises for a layer-sourced executor, each backed by an
    /// execution proof: the shared selectors by
    /// <see cref="LayerSourced_EveryAdvertisedSelector_ReachesTheCanonicalSource"/> (propagation)
    /// plus LayerSourceExecutionProofTests (the PostGIS rows the canonical translation selects);
    /// <c>outStatistics</c> by the GeoServices statistics oracles below; the op-specific inputs by
    /// the per-operation tests above.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> ProvenLayerParameters =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["analytics.buffer-aggregate"] = ["layerId", "distance", "unit", "dissolve", "groupByFields", "outStatistics", .. SharedSelectors],
            ["analytics.spatial-join"] = ["layerId", "joinLayerId", "predicate", "distance", "carryFields", "outStatistics", .. SharedSelectors],
            ["generalization.dissolve"] = ["layerId", "groupByFields", "dissolve", "outStatistics", .. SharedSelectors],
            ["generalization.simplify-layer"] = ["layerId", "tolerance", "preserveTopology", .. SharedSelectors],
        };

    // Advertise the shared selectors but execute through the synchronous analytics handlers,
    // which build their query with the same canonical translation.
    private static readonly string[] ProtocolOnlySelectorProcesses = ["analytics.cluster", "analytics.density"];

    private const string AllAggregatesPayload = """
        [{"statisticType":"sum","onStatisticField":"pop","outStatisticFieldName":"total_pop"},
         {"statisticType":"avg","onStatisticField":"pop","outStatisticFieldName":"mean_pop"},
         {"statisticType":"min","onStatisticField":"pop","outStatisticFieldName":"min_pop"},
         {"statisticType":"max","onStatisticField":"pop","outStatisticFieldName":"max_pop"},
         {"statisticType":"count","onStatisticField":"pop","outStatisticFieldName":"n_pop"},
         {"statisticType":"stddev","onStatisticField":"pop","outStatisticFieldName":"sd_pop"},
         {"statisticType":"var","onStatisticField":"pop","outStatisticFieldName":"var_pop"}]
        """;

    [UnitTest]
    public void CatalogToExecution_LayerSourcedParameterInventory_MatchesTheCatalogExactly()
    {
        var catalog = new BuiltInProcessCatalog();
        foreach (var (processId, proven) in ProvenLayerParameters)
        {
            catalog.GetProcess(processId)!.Parameters.Select(p => p.Name).Should().BeEquivalentTo(proven,
                $"every parameter {processId} advertises needs an execution proof, and every proven parameter must still be advertised");
        }

        // A new operation that advertises the shared selector family must join this inventory
        // (with proofs) or be served by the synchronous canonical handlers.
        catalog.ListProcesses()
            .Where(p => p.Parameters.Any(parameter => parameter.Name == "timeRelation"))
            .Select(p => p.ProcessId)
            .Should().BeSubsetOf(ProvenLayerParameters.Keys.Concat(ProtocolOnlySelectorProcesses));
    }

    [UnitTest]
    public async Task LayerSourced_EveryAdvertisedSelector_ReachesTheCanonicalSource()
    {
        // Mutation guard: an executor that drops (or rewrites) any advertised selector fails here
        // with the offending process and selector named.
        const string polygon = """{"rings":[[[0,0],[0,5],[5,5],[5,0],[0,0]]]}""";
        (string Name, string Value)[] selectors =
        [
            ("where", "pop > 1"),
            ("objectIds", "3,8"),
            ("geometry", polygon),
            ("geometryType", "esriGeometryPolygon"),
            ("inSR", "3857"),
            ("spatialRel", "esriSpatialRelContains"),
            ("time", "1700000000000,1700086400000"),
            ("timeRelation", "esriTimeRelationOverlaps"),
        ];
        selectors.Select(s => s.Name).Should().BeEquivalentTo(SharedSelectors);

        foreach (var processId in ProvenLayerParameters.Keys)
        {
            var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
            {
                [7] = [BoxFeature(0, 0, 1, 1)],
                [8] = [BoxFeature(0, 0, 1, 1)],
            });
            var (executor, operationInputs) = CreateLayerExecutor(processId, source);

            var (status, _, _) = await RunAsync(executor, processId, [("layerId", "7"), .. operationInputs, .. selectors]);

            status.Should().Be(ExecutionJobStatus.Succeeded, $"{processId}: {_lastErrorForAssertions}");
            var target = _allRequestsForAssertions.Single(r => r.LayerId == 7);
            target.Where.Should().Be("pop > 1", processId);
            target.ObjectIds.Should().Be("3,8", processId);
            target.Geometry.Should().Be(polygon, processId);
            target.GeometryType.Should().Be("esriGeometryPolygon", processId);
            target.InSr.Should().Be("3857", processId);
            target.SpatialRel.Should().Be("esriSpatialRelContains", processId);
            target.Time.Should().Be("1700000000000,1700086400000", processId);
            target.TimeRelation.Should().Be("esriTimeRelationOverlaps", processId);

            // The shared selectors narrow the TARGET layer only, exactly as the synchronous
            // spatial join applies them; the join layer is read in full.
            foreach (var other in _allRequestsForAssertions.Where(r => r.LayerId != 7))
            {
                other.Where.Should().BeNull(processId);
                other.ObjectIds.Should().BeNull(processId);
                other.HasCanonicalSelectors.Should().BeFalse(processId);
            }
        }
    }

    [UnitTest]
    public async Task LayerStatistics_GeoServicesPayload_HonorsEveryAggregateAndOutputName()
    {
        // #4624: ProcessPlanValidator admits outStatistics ONLY as this GeoServices payload, yet the
        // layer executors parsed only 'field:stat', so every statistics request submitted through
        // the canonical job API failed. Independent oracle — zone "a" pop {5, 7, null}: SUM 12,
        // AVG 6, MIN 5, MAX 7, COUNT(pop) 2 (the null row joins the group but not COUNT(pop)),
        // sample VAR ((5-6)^2 + (7-6)^2) / (2-1) = 2, STDDEV sqrt(2). Zone "b" pop {100}: sample
        // VAR/STDDEV are undefined for n = 1 and must be null, not 0.
        foreach (var processId in new[] { "analytics.buffer-aggregate", "generalization.dissolve" })
        {
            var source = new FakeDagFeatureSource(HonuaLayerSourceId,
            [
                NamedPointWithNumericField(0, 0, "zone", "a", "pop", 5),
                NamedPointWithNumericField(1, 1, "zone", "a", "pop", 7),
                new DagSourceFeature
                {
                    GeometryGeoJson = """{"type":"Point","coordinates":[2,2]}""",
                    Attributes = new Dictionary<string, object?> { ["zone"] = "a", ["pop"] = null },
                },
                NamedPointWithNumericField(50, 50, "zone", "b", "pop", 100),
            ]);
            var (executor, operationInputs) = CreateLayerExecutor(processId, source);

            var (status, uri, _) = await RunAsync(executor, processId,
                [("layerId", "7"), .. operationInputs, ("groupByFields", "zone"), ("outStatistics", AllAggregatesPayload)]);

            status.Should().Be(ExecutionJobStatus.Succeeded, $"{processId}: {_lastErrorForAssertions}");
            var features = ReadFeatures(uri!);
            var zoneA = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "a"));
            Number(zoneA, "COUNT").Should().Be(3, processId);
            Number(zoneA, "total_pop").Should().Be(12, processId);
            Number(zoneA, "mean_pop").Should().Be(6, processId);
            Number(zoneA, "min_pop").Should().Be(5, processId);
            Number(zoneA, "max_pop").Should().Be(7, processId);
            Number(zoneA, "n_pop").Should().Be(2, processId);
            Number(zoneA, "var_pop").Should().BeApproximately(2, 1e-12, processId);
            Number(zoneA, "sd_pop").Should().BeApproximately(Math.Sqrt(2), 1e-12, processId);
            zoneA.Attributes.GetNames().Should().NotContain(name => name.StartsWith("SUM_", StringComparison.Ordinal),
                "the requested outStatisticFieldName replaces the legacy default column name");

            var zoneB = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "b"));
            Number(zoneB, "total_pop").Should().Be(100, processId);
            Number(zoneB, "n_pop").Should().Be(1, processId);
            zoneB.Attributes.GetOptionalValue("var_pop").Should().BeNull(processId);
            zoneB.Attributes.GetOptionalValue("sd_pop").Should().BeNull(processId);
        }
    }

    [UnitTest]
    public async Task SpatialJoin_GeoServicesOutStatistics_AggregatesMatchedJoinRowsUnderRequestedNames()
    {
        // The target box [0,10]^2 intersects the join points with pop 5 and 7; the join point at
        // (50,50) with pop 100 matches nothing. Oracle: SUM 12, COUNT(pop) 2, JOIN_COUNT 2.
        var source = new FakeTwoLayerDagFeatureSource(HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>
        {
            [7] = [BoxFeature(0, 0, 10, 10, ("name", "target"))],
            [8] =
            [
                NamedPointWithNumericField(2, 2, "zone", "a", "pop", 5),
                NamedPointWithNumericField(3, 3, "zone", "a", "pop", 7),
                NamedPointWithNumericField(50, 50, "zone", "b", "pop", 100),
            ],
        });

        var (status, uri, _) = await RunAsync(
            new LayerSpatialJoinExecutor(ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance),
            LayerSpatialJoinExecutor.HandledProcessId,
            ("layerId", "7"),
            ("joinLayerId", "8"),
            ("outStatistics", """
                [{"statisticType":"sum","onStatisticField":"pop","outStatisticFieldName":"joined_pop"},
                 {"statisticType":"count","onStatisticField":"pop","outStatisticFieldName":"joined_n"}]
                """));

        status.Should().Be(ExecutionJobStatus.Succeeded, _lastErrorForAssertions);
        var feature = ReadFeatures(uri!).Should().ContainSingle().Which;
        Number(feature, "joined_pop").Should().Be(12);
        Number(feature, "joined_n").Should().Be(2);
        Number(feature, LayerSpatialJoinExecutor.JoinCountAttribute).Should().Be(2);
    }

    [UnitTest]
    public async Task LayerStatistics_InvalidCombinations_AreRejectedBeforeComputation()
    {
        var schema = new[]
        {
            new MetadataV2Field { Name = "zone", Type = MetadataV2FieldType.Integer },
            new MetadataV2Field { Name = "pop", Type = MetadataV2FieldType.Integer },
        };
        (string Payload, string Error, bool RejectedBeforeRead)[] cases =
        [
            ("""[{"statisticType":"sum","onStatisticField":"pop","outStatisticFieldName":"x"},{"statisticType":"max","onStatisticField":"pop","outStatisticFieldName":"X"}]""",
                "more than once", true),
            ("""[{"statisticType":"median","onStatisticField":"pop","outStatisticFieldName":"m"}]""", "not supported", true),
            ("""[{"statisticType":"sum","onStatisticField":"pop"}]""", "outStatisticFieldName", true),
            ("""[{"statisticType":"sum","onStatisticField":"popl","outStatisticFieldName":"t"}]""", "not a field of layer 7", true),
            ("""[{"statisticType":"sum","onStatisticField":"POP","outStatisticFieldName":"t"}]""", "did you mean 'pop'", true),
            ("""[{"statisticType":"sum","onStatisticField":"pop","outStatisticFieldName":"zone"}]""", "collides", false),
            ("""[{"statisticType":"sum","onStatisticField":"pop","outStatisticFieldName":"COUNT"}]""", "collides", false),
        ];

        foreach (var (payload, error, rejectedBeforeRead) in cases)
        {
            var source = new FakeDagFeatureSource(HonuaLayerSourceId, [NamedPointWithNumericField(0, 0, "zone", "a", "pop", 5)]);
            var (status, _, _) = await RunAsync(
                new LayerBufferAggregateExecutor(
                    BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false, schemaFields: schema),
                    Options(),
                    NullLogger<LayerBufferAggregateExecutor>.Instance),
                LayerBufferAggregateExecutor.HandledProcessId,
                ("layerId", "7"),
                ("distance", "1"),
                ("groupByFields", "zone"),
                ("outStatistics", payload));

            status.Should().Be(ExecutionJobStatus.Failed, payload);
            _lastErrorForAssertions.Should().Contain(error, payload);
            if (rejectedBeforeRead)
            {
                _allRequestsForAssertions.Should().BeEmpty($"'{payload}' is rejected before the layer is read");
            }
        }
    }

    private static (LayerSourcedFeatureExecutor Executor, (string Name, string Value)[] Inputs) CreateLayerExecutor(
        string processId,
        IDagFeatureSource source)
        => processId switch
        {
            "analytics.buffer-aggregate" => (
                new LayerBufferAggregateExecutor(
                    BufferScopeFactory(source, layerId: 7, storageSrid: 3857, isGeographic: false),
                    Options(),
                    NullLogger<LayerBufferAggregateExecutor>.Instance),
                [("distance", "1")]),
            "analytics.spatial-join" => (
                new LayerSpatialJoinExecutor(ScopeFactory(source), Options(), NullLogger<LayerSpatialJoinExecutor>.Instance),
                [("joinLayerId", "8")]),
            "generalization.dissolve" => (
                new LayerDissolveExecutor(ScopeFactory(source), Options(), NullLogger<LayerDissolveExecutor>.Instance),
                []),
            "generalization.simplify-layer" => (
                new LayerSimplifyExecutor(ScopeFactory(source), Options(), NullLogger<LayerSimplifyExecutor>.Instance),
                [("tolerance", "0.1")]),
            _ => throw new ArgumentOutOfRangeException(nameof(processId), processId, null),
        };

    private static double Number(IFeature feature, string attribute)
        => Convert.ToDouble(feature.Attributes.GetOptionalValue(attribute), CultureInfo.InvariantCulture);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static List<string?> CarriedValues(IFeature feature, string field)
    {
        var raw = feature.Attributes.GetOptionalValue(field);
        raw.Should().BeAssignableTo<System.Collections.IEnumerable>("carried join fields are emitted as arrays");
        var values = new List<string?>();
        foreach (var item in (System.Collections.IEnumerable)raw!)
        {
            values.Add(item?.ToString());
        }

        return values;
    }

    private static DagSourceFeature NamedPoint(double x, double y, string name)
        => new()
        {
            GeometryGeoJson = $$"""{"type":"Point","coordinates":[{{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}},{{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}}]}""",
            Attributes = new Dictionary<string, object?> { ["name"] = name },
        };

    private static DagSourceFeature NamedPointWithNumericField(
        double x, double y, string groupField, string groupValue, string numericField, double numericValue)
        => new()
        {
            GeometryGeoJson = $$"""{"type":"Point","coordinates":[{{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}},{{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}}]}""",
            Attributes = new Dictionary<string, object?> { [groupField] = groupValue, [numericField] = numericValue },
        };

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options(long? maxArtifactBytes = null, long maxTopologyWork = 4_000_000, int maxLayerVertices = 100_000, int maxExecutionSeconds = 300)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes ?? 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7),
            MaxTopologyWork = maxTopologyWork,
            MaxLayerVertices = maxLayerVertices,
            MaxLayerExecutionSeconds = maxExecutionSeconds,
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static IServiceScopeFactory ScopeFactory(IDagFeatureSource source)
    {
        var services = new ServiceCollection();
        services.AddSingleton(source);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// A <see cref="LimitsOptions"/> with a tightened <see cref="AnalyticsLimits.MaxInputFeatures"/>
    /// and/or <see cref="GeometryLimits.MaxVerticesPerGeometry"/> (#4629), so admission tests can
    /// force the bound without depending on the production defaults (100,000 / 50,000).
    /// </summary>
    private static IOptions<LimitsOptions> LimitsOptions(
        int? maxInputFeatures = null,
        int? maxVerticesPerGeometry = null,
        long? maxGeometryBytes = null,
        long? maxInputBytes = null)
    {
        var limits = new LimitsOptions();
        if (maxGeometryBytes is { } geometryBytes)
        {
            limits.Geometry.MaxGeometrySize = geometryBytes;
        }

        if (maxInputBytes is { } inputBytes)
        {
            limits.Analytics.MaxInputBytes = inputBytes;
        }

        if (maxInputFeatures is { } features)
        {
            limits.Analytics.MaxInputFeatures = features;
        }

        if (maxVerticesPerGeometry is { } vertices)
        {
            limits.Geometry.MaxVerticesPerGeometry = vertices;
        }

        return Microsoft.Extensions.Options.Options.Create(limits);
    }

    /// <summary>A LineString feature with exactly <paramref name="vertexCount"/> vertices.</summary>
    private static DagSourceFeature LineFeature(int vertexCount)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var coordinates = string.Join(",", Enumerable.Range(0, vertexCount).Select(i => $"[{i.ToString(ci)},0]"));
        return new DagSourceFeature
        {
            GeometryGeoJson = $$"""{"type":"LineString","coordinates":[{{coordinates}}]}""",
            Attributes = new Dictionary<string, object?>(),
        };
    }

    /// <summary>
    /// Scope factory for <c>analytics.buffer-aggregate</c> tests (#4623): registers the
    /// CRS-aware <see cref="IGeometryOperationService"/> (a self-contained, independently
    /// coded Web-Mercator/native-unit buffer implementation — NOT a delegate to production
    /// PostGIS code) plus an <see cref="IMetadataV2GraphProvider"/> that resolves
    /// <paramref name="layerId"/> to <paramref name="storageSrid"/>.
    /// </summary>
    private static IServiceScopeFactory BufferScopeFactory(
        IDagFeatureSource source,
        int layerId,
        int storageSrid,
        bool isGeographic,
        double metersPerUnit = 1.0,
        IReadOnlyList<MetadataV2Field>? schemaFields = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(source);
        services.AddSingleton<IGeometryOperationService>(
            new FakeGeometryOperationService(new Dictionary<int, (bool IsGeographic, double MetersPerUnit)>
            {
                [storageSrid] = (isGeographic, metersPerUnit),
            }));
        services.AddSingleton<IMetadataV2GraphProvider>(new FakeMetadataV2GraphProvider(layerId, storageSrid, schemaFields));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static DagSourceFeature PointFeature(double x, double y)
        => new()
        {
            GeometryGeoJson = $$"""{"type":"Point","coordinates":[{{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}},{{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}}]}""",
            Attributes = new Dictionary<string, object?>(),
        };

    private static DagSourceFeature BoxFeature(
        double minX,
        double minY,
        double maxX,
        double maxY,
        params (string Name, object Value)[] attributes)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string P(double x, double y) => $"[{x.ToString(ci)},{y.ToString(ci)}]";
        var ring = string.Join(",", P(minX, minY), P(maxX, minY), P(maxX, maxY), P(minX, maxY), P(minX, minY));
        var attrs = new Dictionary<string, object?>();
        foreach (var (name, value) in attributes)
        {
            attrs[name] = value;
        }

        return new DagSourceFeature
        {
            GeometryGeoJson = $$"""{"type":"Polygon","coordinates":[[{{ring}}]]}""",
            Attributes = attrs,
        };
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri, DagSourceRequest? Request)> RunAsync(
        LayerSourcedFeatureExecutor executor,
        string processId,
        params (string Name, string Value)[] inputs)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");
        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        foreach (var (name, value) in inputs)
        {
            parameters[prefix + name] = value;
        }

        var record = new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters,
            },
        };

        _lastRequestForAssertions = null;
        _allRequestsForAssertions.Clear();
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        _lastErrorForAssertions = result.ErrorMessage;
        return (result.Status, publishedUri, _lastRequestForAssertions);
    }

    private static DagSourceRequest? _lastRequestForAssertions;
    private static readonly List<DagSourceRequest> _allRequestsForAssertions = [];
    private static string? _lastErrorForAssertions;

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        return new GeoJsonReader().Read<FeatureCollection>(json).ToList();
    }

    private sealed class FakeDagFeatureSource : IDagFeatureSource
    {
        private readonly IReadOnlyList<DagSourceFeature> _features;

        public FakeDagFeatureSource(string sourceId, IReadOnlyList<DagSourceFeature> features)
        {
            SourceId = sourceId;
            _features = features;
        }

        public string SourceId { get; }

        public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
            DagSourceRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _lastRequestForAssertions = request;
            _allRequestsForAssertions.Add(request);
            foreach (var feature in _features)
            {
                yield return feature;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake <see cref="IDagFeatureSource"/> that returns a distinct feature set per
    /// catalog layer id, so a two-layer op such as <c>analytics.spatial-join</c> reads
    /// its target (<c>layerId</c>) and join (<c>joinLayerId</c>) layers from the same
    /// connector without a Postgres catalog.
    /// </summary>
    private sealed class FakeTwoLayerDagFeatureSource : IDagFeatureSource
    {
        private readonly IReadOnlyDictionary<int, IReadOnlyList<DagSourceFeature>> _byLayer;

        public FakeTwoLayerDagFeatureSource(
            string sourceId,
            IReadOnlyDictionary<int, IReadOnlyList<DagSourceFeature>> byLayer)
        {
            SourceId = sourceId;
            _byLayer = byLayer;
        }

        public string SourceId { get; }

        public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
            DagSourceRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _lastRequestForAssertions = request;
            _allRequestsForAssertions.Add(request);
            if (request.LayerId is int layerId && _byLayer.TryGetValue(layerId, out var features))
            {
                foreach (var feature in features)
                {
                    yield return feature;
                }
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Resolves a single catalog layer id to a relational storage binding with a fixed SRID
    /// (#4623), matching the shape <c>MetadataV2GraphSnapshotExtensions.ResolveStorageSrid</c>
    /// expects: a resource plus a <see cref="MetadataV2StorageBinding"/> whose
    /// <see cref="MetadataV2StorageBinding.StorageLayerId"/> matches the layer id.
    /// </summary>
    private sealed class FakeMetadataV2GraphProvider : IMetadataV2GraphProvider
    {
        private readonly MetadataV2GraphSnapshot _snapshot;

        public FakeMetadataV2GraphProvider(int layerId, int storageSrid, IReadOnlyList<MetadataV2Field>? schemaFields = null)
        {
            var resource = new MetadataV2Resource
            {
                SchemaFields = schemaFields ?? [],
                Metadata = new MetadataV2ObjectMetadata { Id = $"res-{layerId}", Name = $"layer-{layerId}" },
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                Spatial = new MetadataV2ResourceSpatial
                {
                    StorageCrs = new MetadataV2SpatialReference { Srid = storageSrid },
                },
            };
            var binding = new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = $"binding-{layerId}", Name = $"binding-{layerId}" },
                ResourceId = resource.Metadata.Id,
                StorageType = MetadataV2StorageType.RelationalTable,
                Locator = $"public.layer_{layerId}",
                StorageLayerId = layerId,
            };

            var graph = new MetadataV2Graph
            {
                Revision = 1,
                Resources = [resource],
                StorageBindings = [binding],
            };
            _snapshot = new MetadataV2GraphSnapshot(graph, "\"buffer-tests\"", DateTimeOffset.UnixEpoch);
        }

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(revision == _snapshot.Revision ? _snapshot : null);
    }

    /// <summary>
    /// Independently coded (not a delegate to <c>PostgresGeometryOperationService</c>)
    /// CRS-aware buffer implementation used as the test oracle for #4623: a geographic SRID
    /// buffers through a plain Web Mercator forward/inverse projection with a latitude-scale
    /// correction; a projected SRID converts the metric distance into its declared native
    /// linear unit before buffering directly.
    /// </summary>
    private sealed class FakeGeometryOperationService : IGeometryOperationService
    {
        private const double EarthRadiusMeters = 6378137.0;
        private readonly IReadOnlyDictionary<int, (bool IsGeographic, double MetersPerUnit)> _crsMetrics;

        public FakeGeometryOperationService(IReadOnlyDictionary<int, (bool IsGeographic, double MetersPerUnit)> crsMetrics)
            => _crsMetrics = crsMetrics;

        public Task<byte[]> BufferAsync(byte[] wkb, int srid, double distance, bool geodesic, CancellationToken ct = default)
        {
            var (isGeographic, metersPerUnit) = _crsMetrics.TryGetValue(srid, out var metrics) ? metrics : (false, 1.0);
            var geometry = new WKBReader().Read(wkb);

            NtsGeometry buffered;
            if (isGeographic)
            {
                var midLatitudeRadians =
                    (geometry.EnvelopeInternal.MinY + geometry.EnvelopeInternal.MaxY) / 2.0 * Math.PI / 180.0;
                var mercator = geometry.Copy();
                mercator.Apply(new WebMercatorForwardFilter());
                var scaledDistance = distance / Math.Cos(midLatitudeRadians);
                var bufferedMercator = mercator.Buffer(scaledDistance);
                bufferedMercator.Apply(new WebMercatorInverseFilter());
                buffered = bufferedMercator;
            }
            else
            {
                buffered = geometry.Buffer(distance / metersPerUnit);
            }

            return Task.FromResult(new WKBWriter().Write(buffered));
        }

        public Task<byte[]> SimplifyAsync(byte[] wkb, double tolerance, bool preserveTopology, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task<byte[]> ProjectAsync(byte[] wkb, int fromSrid, int toSrid, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task<byte[]> MakeValidAsync(byte[] wkb, int srid, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task<byte[]> UnionAsync(byte[][] wkbs, int srid, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task<byte[]> IntersectAsync(byte[] targetWkb, byte[] intersectorWkb, int srid, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task<byte[]> ClipAsync(byte[] targetWkb, byte[] clipEnvelopeWkb, int srid, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task<byte[]> DifferenceAsync(byte[] targetWkb, byte[] eraserWkb, int srid, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task<double> AreaAsync(byte[] wkb, int srid, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task<double> LengthAsync(byte[] wkb, int srid, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by these tests.");

        private sealed class WebMercatorForwardFilter : ICoordinateSequenceFilter
        {
            public bool Done => false;

            public bool GeometryChanged => true;

            public void Filter(CoordinateSequence seq, int i)
            {
                var lonRadians = seq.GetX(i) * Math.PI / 180.0;
                var latRadians = seq.GetY(i) * Math.PI / 180.0;
                seq.SetOrdinate(i, Ordinate.X, EarthRadiusMeters * lonRadians);
                seq.SetOrdinate(i, Ordinate.Y, EarthRadiusMeters * Math.Log(Math.Tan(Math.PI / 4 + latRadians / 2)));
            }
        }

        private sealed class WebMercatorInverseFilter : ICoordinateSequenceFilter
        {
            public bool Done => false;

            public bool GeometryChanged => true;

            public void Filter(CoordinateSequence seq, int i)
            {
                var x = seq.GetX(i);
                var y = seq.GetY(i);
                var lonRadians = x / EarthRadiusMeters;
                var latRadians = 2 * Math.Atan(Math.Exp(y / EarthRadiusMeters)) - Math.PI / 2;
                seq.SetOrdinate(i, Ordinate.X, lonRadians * 180.0 / Math.PI);
                seq.SetOrdinate(i, Ordinate.Y, latRadians * 180.0 / Math.PI);
            }
        }
    }
}
