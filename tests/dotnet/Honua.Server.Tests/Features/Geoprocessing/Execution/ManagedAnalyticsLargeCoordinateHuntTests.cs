// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Regression coverage for finite coordinates whose squared distance overflows.
/// </summary>
public sealed class ManagedAnalyticsLargeCoordinateHuntTests
{
    [UnitTest]
    public async Task Cluster_DoesNotTreatSeparatedFiniteCoordinatesAsNeighborsWhenSquaredDistanceOverflows()
    {
        var input = ManagedExecutorTestHarness.Uri(
            ManagedExecutorTestHarness.Feature(ManagedExecutorTestHarness.Point(-1e200, 0), ("id", 1)),
            ManagedExecutorTestHarness.Feature(ManagedExecutorTestHarness.Point(1e200, 0), ("id", 2)));

        var (status, uri) = await ManagedExecutorTestHarness.RunAsync(
            new ManagedClusterExecutor(ManagedExecutorTestHarness.Options()),
            ManagedClusterExecutor.HandledProcessId,
            ("input", input), ("algorithm", "dbscan"), ("eps", "1e200"), ("minPoints", "2"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var output = ManagedExecutorTestHarness.ReadFeatures(uri!);
        output.Select(feature => Convert.ToInt32(
                feature.Attributes.GetOptionalValue(ManagedClusterExecutor.ClusterIdAttribute),
                CultureInfo.InvariantCulture))
            .Should().OnlyContain(clusterId => clusterId == ManagedClusterExecutor.NoiseClusterId);
    }

    [UnitTest]
    public async Task HotSpot_DoesNotTreatSeparatedFiniteCoordinatesAsNeighborsWhenSquaredDistanceOverflows()
    {
        var input = ManagedExecutorTestHarness.Uri(
            ManagedExecutorTestHarness.Feature(ManagedExecutorTestHarness.Point(-1e200, 0), ("value", 1)),
            ManagedExecutorTestHarness.Feature(ManagedExecutorTestHarness.Point(1e200, 0), ("value", 2)));

        var (status, uri) = await ManagedExecutorTestHarness.RunAsync(
            new ManagedHotSpotExecutor(ManagedExecutorTestHarness.Options()),
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", input), ("field", "value"), ("distanceBand", "1e200"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var zScores = ManagedExecutorTestHarness.ReadFeatures(uri!)
            .Select(feature => Convert.ToDouble(
                feature.Attributes.GetOptionalValue(ManagedHotSpotExecutor.ZScoreAttribute),
                CultureInfo.InvariantCulture));
        zScores.Should().Contain(z => z < -0.9);
        zScores.Should().Contain(z => z > 0.9);
    }
}
