// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NSubstitute;

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

    [UnitTest]
    public async Task BufferAggregate_DissolvesLayerFeatures_ReachesSucceededWithCount()
    {
        var source = new FakeDagFeatureSource(HonuaLayerSourceId,
        [
            PointFeature(0, 0),
            PointFeature(1, 1),
        ]);

        var (status, uri, _) = await RunAsync(
            new LayerBufferAggregateExecutor(ScopeFactory(source), Options(), NullLogger<LayerBufferAggregateExecutor>.Instance),
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
        features[0].Geometry!.IsEmpty.Should().BeFalse();
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

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7),
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

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri, _lastRequestForAssertions);
    }

    private static DagSourceRequest? _lastRequestForAssertions;

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
}
