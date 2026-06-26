// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using static Honua.Server.Tests.Features.Geoprocessing.Execution.ManagedExecutorTestHarness;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the proximity tool pack (#2139): Near and
/// GenerateNearTable. Distances are planar in CRS units; no Docker.
/// </summary>
public sealed class ProximityExecutorTests
{
    private static double Dist(IFeature f) => Convert.ToDouble(f.Attributes.GetOptionalValue("NEAR_DIST"), CultureInfo.InvariantCulture);

    private static long Fid(IFeature f, string key) => Convert.ToInt64(f.Attributes.GetOptionalValue(key), CultureInfo.InvariantCulture);

    [UnitTest]
    public async Task Near_AppendsNearestFidAndDistance()
    {
        var input = Uri(Feature(Point(0, 0), ("id", 1)));
        // Two candidates: ordinal 0 at distance 5, ordinal 1 at distance 3 (the winner).
        var near = Uri(Feature(Point(5, 0)), Feature(Point(0, 3)));

        var (status, uri) = await RunAsync(
            new ProximityNearExecutor(Options()),
            ProximityNearExecutor.HandledProcessId,
            ("input", input),
            ("near", near));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        Fid(features[0], "NEAR_FID").Should().Be(1, "the closer candidate is ordinal 1");
        Dist(features[0]).Should().BeApproximately(3, 1e-6);
    }

    [UnitTest]
    public async Task Near_NoNeighbourWithinRadius_EmitsSentinel()
    {
        var input = Uri(Feature(Point(0, 0)));
        var near = Uri(Feature(Point(100, 100)));

        var (status, uri) = await RunAsync(
            new ProximityNearExecutor(Options()),
            ProximityNearExecutor.HandledProcessId,
            ("input", input),
            ("near", near),
            ("searchRadius", "10"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        Fid(features[0], "NEAR_FID").Should().Be(-1);
        Dist(features[0]).Should().Be(-1);
    }

    [UnitTest]
    public async Task Near_UsesNearIdFieldWhenSupplied()
    {
        var input = Uri(Feature(Point(0, 0)));
        var near = Uri(Feature(Point(1, 0), ("OID", 7L)));

        var (status, uri) = await RunAsync(
            new ProximityNearExecutor(Options()),
            ProximityNearExecutor.HandledProcessId,
            ("input", input),
            ("near", near),
            ("nearIdField", "OID"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        Fid(ReadFeatures(uri!)[0], "NEAR_FID").Should().Be(7);
    }

    [UnitTest]
    public async Task NearTable_EmitsOneRowPerInputWithNeighbour()
    {
        var input = Uri(Feature(Point(0, 0)), Feature(Point(10, 0)));
        var near = Uri(Feature(Point(1, 0)));

        var (status, uri) = await RunAsync(
            new ProximityNearTableExecutor(Options()),
            ProximityNearTableExecutor.HandledProcessId,
            ("input", input),
            ("near", near));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var rows = ReadFeatures(uri!);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Geometry == null, "table rows are null-geometry features");
        var first = rows.Single(r => Fid(r, "IN_FID") == 0);
        Dist(first).Should().BeApproximately(1, 1e-6);
        var second = rows.Single(r => Fid(r, "IN_FID") == 1);
        Dist(second).Should().BeApproximately(9, 1e-6);
    }

    [UnitTest]
    public async Task NearTable_EmptyNearLayer_ProducesNoRows()
    {
        var input = Uri(Feature(Point(0, 0)));

        var (status, uri) = await RunAsync(
            new ProximityNearTableExecutor(Options()),
            ProximityNearTableExecutor.HandledProcessId,
            ("input", input),
            ("near", Uri()));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().BeEmpty();
    }
}
