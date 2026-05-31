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
/// In-memory unit coverage for the managed (NetTopologySuite) clustering
/// executor — the job-dispatchable counterpart to trunk's PostGIS-protocol
/// <c>analytics.cluster</c>. DBSCAN and K-Means each emit one feature per
/// input feature with a <c>CLUSTER_ID</c> attribute appended; DBSCAN noise
/// points get <c>CLUSTER_ID = -1</c>. Pure managed geometry; no native
/// dependency, no Docker.
/// </summary>
public sealed class ManagedClusterExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task Dbscan_GroupsDenseNeighbors_AndMarksOutliersAsNoise()
    {
        var executor = new ManagedClusterExecutor(Options());

        // Two dense clusters at (0,0) and (50,50), and a single outlier at (200,200).
        var input = BuildUri(
            Feature(Point(0, 0), ("id", 1)),
            Feature(Point(0.5, 0), ("id", 2)),
            Feature(Point(0, 0.5), ("id", 3)),
            Feature(Point(50, 50), ("id", 4)),
            Feature(Point(50.5, 50), ("id", 5)),
            Feature(Point(50, 50.5), ("id", 6)),
            Feature(Point(200, 200), ("id", 7)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedClusterExecutor.HandledProcessId,
            ("input", input),
            ("algorithm", "dbscan"),
            ("eps", "5"),
            ("minPoints", "3"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(7, "every input feature is preserved one-to-one");

        var ids = features.Select(f =>
            ((long)Convert.ToInt64(f.Attributes.GetOptionalValue("id"), CultureInfo.InvariantCulture),
             ClusterId(f))).ToDictionary(t => t.Item1, t => t.Item2);

        // The two dense neighborhoods get distinct, non-negative cluster ids; the
        // outlier is noise.
        ids[1].Should().BeGreaterOrEqualTo(0);
        ids[2].Should().Be(ids[1]);
        ids[3].Should().Be(ids[1]);

        ids[4].Should().BeGreaterOrEqualTo(0);
        ids[4].Should().NotBe(ids[1]);
        ids[5].Should().Be(ids[4]);
        ids[6].Should().Be(ids[4]);

        ids[7].Should().Be(ManagedClusterExecutor.NoiseClusterId);
    }

    [UnitTest]
    public async Task Dbscan_NoisePointReachableFromLaterCorePoint_IsReclaimedAsBorderMember()
    {
        var executor = new ManagedClusterExecutor(Options());

        // Border point at (4,0) sits between two core neighborhoods: the visit
        // order labels it noise first (only 2 self+neighbour points within eps=1),
        // but it is within eps of (5,0), (5,1), (5,-1) which form a dense core
        // neighbourhood. After the refactor it must STILL be reclaimed into the
        // (5,*) cluster instead of staying noise.
        var input = BuildUri(
            Feature(Point(4, 0), ("id", 1)),
            Feature(Point(5, 0), ("id", 2)),
            Feature(Point(5, 1), ("id", 3)),
            Feature(Point(5, -1), ("id", 4)),
            Feature(Point(100, 100), ("id", 5)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedClusterExecutor.HandledProcessId,
            ("input", input),
            ("algorithm", "dbscan"),
            ("eps", "1.5"),
            ("minPoints", "3"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        var ids = features.Select(f => (
            Convert.ToInt64(f.Attributes.GetOptionalValue("id"), CultureInfo.InvariantCulture),
            ClusterId(f))).ToDictionary(t => t.Item1, t => t.Item2);

        // Core cluster covers (5,0)/(5,1)/(5,-1); the (4,0) border point joins it.
        ids[2].Should().BeGreaterOrEqualTo(0);
        ids[3].Should().Be(ids[2]);
        ids[4].Should().Be(ids[2]);
        ids[1].Should().Be(ids[2], "border point reachable from a core neighbourhood must be reclaimed, not stay noise");

        // The far outlier is still noise.
        ids[5].Should().Be(ManagedClusterExecutor.NoiseClusterId);
    }

    [UnitTest]
    public async Task KMeans_AssignsEveryFeatureToOneOfKClusters()
    {
        var executor = new ManagedClusterExecutor(Options());

        // Two well-separated clusters; k=2 should perfectly partition them.
        var input = BuildUri(
            Feature(Point(0, 0), ("id", 1)),
            Feature(Point(0, 1), ("id", 2)),
            Feature(Point(1, 0), ("id", 3)),
            Feature(Point(100, 100), ("id", 4)),
            Feature(Point(100, 101), ("id", 5)),
            Feature(Point(101, 100), ("id", 6)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedClusterExecutor.HandledProcessId,
            ("input", input),
            ("algorithm", "kmeans"),
            ("k", "2"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(6);

        var clusterIds = features.Select(ClusterId).ToList();
        clusterIds.Should().AllSatisfy(c => c.Should().BeInRange(0, 1));
        clusterIds.Distinct().Should().HaveCount(2, "k=2 produces two distinct cluster labels for separable input");

        // The first three features form one cluster; the last three form the other.
        var ids = features.Select(f => (
            (long)Convert.ToInt64(f.Attributes.GetOptionalValue("id"), CultureInfo.InvariantCulture),
            ClusterId(f))).ToDictionary(t => t.Item1, t => t.Item2);
        ids[1].Should().Be(ids[2]).And.Be(ids[3]);
        ids[4].Should().Be(ids[5]).And.Be(ids[6]);
        ids[1].Should().NotBe(ids[4]);
    }

    [UnitTest]
    public async Task MissingInput_FailsCleanly()
    {
        var executor = new ManagedClusterExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedClusterExecutor.HandledProcessId,
            ("algorithm", "dbscan"),
            ("eps", "1"),
            ("minPoints", "1"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task UnsupportedAlgorithm_FailsCleanly()
    {
        var executor = new ManagedClusterExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedClusterExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(0, 0)))),
            ("algorithm", "fancy"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task Dbscan_MissingEps_FailsCleanly()
    {
        var executor = new ManagedClusterExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedClusterExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(0, 0)))),
            ("algorithm", "dbscan"),
            ("minPoints", "2"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task KMeans_MissingK_FailsCleanly()
    {
        var executor = new ManagedClusterExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedClusterExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(0, 0)))),
            ("algorithm", "kmeans"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static int ClusterId(IFeature feature)
        => Convert.ToInt32(
            feature.Attributes.GetOptionalValue(ManagedClusterExecutor.ClusterIdAttribute),
            CultureInfo.InvariantCulture);

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

    private static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

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
        ManagedClusterExecutor executor,
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
