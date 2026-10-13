// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the managed (NetTopologySuite) spatial-join
/// executor — the job-dispatchable counterpart to trunk's PostGIS-protocol
/// analytics.spatial-join. Aggregate mode summarizes every matched join feature
/// into per-target JOIN_COUNT + numeric SUM/MEAN/MIN/MAX statistics, preserving
/// zero-match targets one-to-one. Pure managed geometry; no native dependency,
/// no Docker.
/// </summary>
public sealed class ManagedSpatialJoinExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task Aggregate_CountsAndSumsMatchedJoinFeaturesPerTarget()
    {
        var executor = new ManagedSpatialJoinExecutor(Options());

        // Two target zones: zone A (0..10) and zone B (20..30).
        var target = BuildUri(
            Feature(BuildBox(0, 0, 10, 10), ("zone", "A")),
            Feature(BuildBox(20, 20, 30, 30), ("zone", "B")));

        // Three join points carrying a numeric 'pop' field. Two fall in zone A,
        // one in zone B.
        var join = BuildUri(
            Feature(Point(1, 1), ("pop", 10)),
            Feature(Point(2, 2), ("pop", 30)),
            Feature(Point(25, 25), ("pop", 5)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedSpatialJoinExecutor.HandledProcessId,
            ("input", target),
            ("join", join),
            ("predicate", "intersects"),
            ("statistics", "pop:sum;pop:mean"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "every target is preserved one-to-one");

        var zoneA = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "A"));
        ToLong(zoneA.Attributes.GetOptionalValue("JOIN_COUNT")).Should().Be(2);
        ToDouble(zoneA.Attributes.GetOptionalValue("SUM_pop")).Should().BeApproximately(40, 1e-6);
        ToDouble(zoneA.Attributes.GetOptionalValue("MEAN_pop")).Should().BeApproximately(20, 1e-6);

        var zoneB = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "B"));
        ToLong(zoneB.Attributes.GetOptionalValue("JOIN_COUNT")).Should().Be(1);
        ToDouble(zoneB.Attributes.GetOptionalValue("SUM_pop")).Should().BeApproximately(5, 1e-6);
    }

    [UnitTest]
    public async Task Aggregate_ZeroMatchTarget_PreservedWithCountZeroAndNullStat()
    {
        var executor = new ManagedSpatialJoinExecutor(Options());

        var target = BuildUri(Feature(BuildBox(0, 0, 10, 10), ("zone", "lonely")));
        // Join feature is far outside the target — no match.
        var join = BuildUri(Feature(Point(100, 100), ("pop", 99)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedSpatialJoinExecutor.HandledProcessId,
            ("input", target),
            ("join", join),
            ("statistics", "pop:sum"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1, "zero-match targets are still emitted");
        ToLong(features[0].Attributes.GetOptionalValue("JOIN_COUNT")).Should().Be(0);
        features[0].Attributes.GetOptionalValue("SUM_pop").Should().BeNull();
    }

    [UnitTest]
    public async Task Predicate_Contains_PointInPolygonMatchesContainingJoin()
    {
        var executor = new ManagedSpatialJoinExecutor(Options());

        // Target is a point; join is the polygon that contains it.
        var target = BuildUri(Feature(Point(5, 5), ("id", 1)));
        var join = BuildUri(Feature(BuildBox(0, 0, 10, 10), ("pop", 7)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedSpatialJoinExecutor.HandledProcessId,
            ("input", target),
            ("join", join),
            ("predicate", "contains"),
            ("statistics", "pop:max"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        ToLong(features[0].Attributes.GetOptionalValue("JOIN_COUNT")).Should().Be(1);
        ToDouble(features[0].Attributes.GetOptionalValue("MAX_pop")).Should().BeApproximately(7, 1e-6);
    }

    [UnitTest]
    public async Task MissingJoin_FailsCleanly()
    {
        var executor = new ManagedSpatialJoinExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedSpatialJoinExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(1, 1)))));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task UnsupportedPredicate_FailsCleanly()
    {
        var executor = new ManagedSpatialJoinExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedSpatialJoinExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(1, 1)))),
            ("join", BuildUri(Feature(BuildBox(0, 0, 10, 10)))),
            ("predicate", "dwithin"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task UnsupportedStatistic_FailsCleanly()
    {
        var executor = new ManagedSpatialJoinExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedSpatialJoinExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(1, 1)))),
            ("join", BuildUri(Feature(BuildBox(0, 0, 10, 10), ("pop", 1)))),
            ("statistics", "pop:median"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static long ToLong(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static double ToDouble(object? value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

    private static Polygon BuildBox(double minX, double minY, double maxX, double maxY)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        });
    }

    private static Feature Feature(Geometry geometry, params (string Name, object Value)[] attributes)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in attributes)
        {
            table.Add(name, value);
        }

        return new Feature(geometry, table);
    }

    private static string BuildUri(params IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        return new GeoJsonReader().Read<FeatureCollection>(json).ToList();
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        ManagedSpatialJoinExecutor executor,
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
                Parameters = parameters
            }
        };

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri);
    }
}
