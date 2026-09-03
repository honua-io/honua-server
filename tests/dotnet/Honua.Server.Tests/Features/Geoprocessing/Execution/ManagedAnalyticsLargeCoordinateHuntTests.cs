// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Bug-hunt reproductions for finite, very large coordinate values. These are
/// intentionally failing tests: the managed analytics executors compare squared
/// distances without guarding the intermediate multiplication against overflow.
/// </summary>
public sealed class ManagedAnalyticsLargeCoordinateHuntTests
{
    [UnitTest]
    public async Task Cluster_DoesNotTreatSeparatedFiniteCoordinatesAsNeighborsWhenSquaredDistanceOverflows()
    {
        var input = ManagedExecutorTestHarness.Uri(
            ManagedExecutorTestHarness.Feature(
                ManagedExecutorTestHarness.Point(-1e200, 0),
                ("id", 1)),
            ManagedExecutorTestHarness.Feature(
                ManagedExecutorTestHarness.Point(1e200, 0),
                ("id", 2)));

        var (status, uri) = await ManagedExecutorTestHarness.RunAsync(
            new ManagedClusterExecutor(ManagedExecutorTestHarness.Options()),
            ManagedClusterExecutor.HandledProcessId,
            ("input", input),
            ("algorithm", "dbscan"),
            ("eps", "1e200"),
            ("minPoints", "2"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var output = ManagedExecutorTestHarness.ReadFeatures(uri!);
        output.Should().HaveCount(2);
        output.Select(feature => Convert.ToInt32(
                feature.Attributes.GetOptionalValue(ManagedClusterExecutor.ClusterIdAttribute),
                CultureInfo.InvariantCulture))
            .Should().OnlyContain(clusterId => clusterId == ManagedClusterExecutor.NoiseClusterId,
                "the points at x=-1e200 and x=1e200 are 2e200 apart and must not be DBSCAN neighbors for eps=1e200");
    }

    [UnitTest]
    public async Task HotSpot_DoesNotTreatSeparatedFiniteCoordinatesAsNeighborsWhenSquaredDistanceOverflows()
    {
        var input = ManagedExecutorTestHarness.Uri(
            ManagedExecutorTestHarness.Feature(
                ManagedExecutorTestHarness.Point(-1e200, 0),
                ("value", 1)),
            ManagedExecutorTestHarness.Feature(
                ManagedExecutorTestHarness.Point(1e200, 0),
                ("value", 2)));

        var (status, uri) = await ManagedExecutorTestHarness.RunAsync(
            new ManagedHotSpotExecutor(ManagedExecutorTestHarness.Options()),
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", input),
            ("field", "value"),
            ("distanceBand", "1e200"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var output = ManagedExecutorTestHarness.ReadFeatures(uri!);
        output.Should().HaveCount(2);

        var zScores = output.Select(feature => Convert.ToDouble(
            feature.Attributes.GetOptionalValue(ManagedHotSpotExecutor.ZScoreAttribute),
            CultureInfo.InvariantCulture)).ToList();
        zScores.Should().Contain(z => z < -0.9,
            "the x=-1e200 feature has only itself as a neighbor and should have a negative Gi* z-score");
        zScores.Should().Contain(z => z > 0.9,
            "the x=1e200 feature has only itself as a neighbor and should have a positive Gi* z-score");
    }
}
