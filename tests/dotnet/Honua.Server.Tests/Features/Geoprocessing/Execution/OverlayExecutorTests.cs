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
/// In-memory unit coverage for the layer-aware overlay tool pack (#2206, #2139):
/// clip, intersect, union, erase, merge, split, and append. Pure managed
/// NetTopologySuite geometry over two inline FeatureCollections — no Docker.
/// </summary>
public sealed class OverlayExecutorTests
{
    private static readonly string[] ExpectedAppendedFields = { "id", "name" };

    [UnitTest]
    public async Task Clip_TruncatesInputGeometryToClipUnion()
    {
        var input = Uri(Feature(Box(0, 0, 10, 10), ("name", "tile")));
        var clip = Uri(Feature(Box(5, 0, 15, 10)));

        var (status, uri) = await RunAsync(
            new OverlayClipExecutor(Options()),
            OverlayClipExecutor.HandledProcessId,
            ("input", input),
            ("clip", clip));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        features[0].Attributes.GetOptionalValue("name").Should().Be("tile");
        features[0].Geometry.Area.Should().BeApproximately(50, 1e-6, "the eastern half (x:5..10) survives the clip");
    }

    [UnitTest]
    public async Task Clip_DropsFeaturesOutsideClipRegion()
    {
        var input = Uri(Feature(Box(0, 0, 1, 1)), Feature(Box(100, 100, 101, 101)));
        var clip = Uri(Feature(Box(0, 0, 10, 10)));

        var (status, uri) = await RunAsync(
            new OverlayClipExecutor(Options()),
            OverlayClipExecutor.HandledProcessId,
            ("input", input),
            ("clip", clip));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().ContainSingle("only the feature overlapping the clip survives");
    }

    [UnitTest]
    public async Task Erase_RemovesOverlapWithEraseUnion()
    {
        var input = Uri(Feature(Box(0, 0, 10, 10), ("name", "tile")));
        var erase = Uri(Feature(Box(5, 0, 15, 10)));

        var (status, uri) = await RunAsync(
            new OverlayEraseExecutor(Options()),
            OverlayEraseExecutor.HandledProcessId,
            ("input", input),
            ("erase", erase));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        features[0].Geometry.Area.Should().BeApproximately(50, 1e-6, "the western half (x:0..5) remains after erase");
    }

    [UnitTest]
    public async Task Erase_FullyCoveredFeatureIsDropped()
    {
        var input = Uri(Feature(Box(2, 2, 4, 4)));
        var erase = Uri(Feature(Box(0, 0, 10, 10)));

        var (status, uri) = await RunAsync(
            new OverlayEraseExecutor(Options()),
            OverlayEraseExecutor.HandledProcessId,
            ("input", input),
            ("erase", erase));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Intersect_EmitsPairwiseIntersectionWithMergedAttributes()
    {
        var input = Uri(Feature(Box(0, 0, 10, 10), ("zone", "A")));
        var overlay = Uri(Feature(Box(5, 5, 15, 15), ("cls", "X")));

        var (status, uri) = await RunAsync(
            new OverlayIntersectExecutor(Options()),
            OverlayIntersectExecutor.HandledProcessId,
            ("input", input),
            ("overlay", overlay));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        features[0].Geometry.Area.Should().BeApproximately(25, 1e-6);
        features[0].Attributes.GetOptionalValue("zone").Should().Be("A");
        features[0].Attributes.GetOptionalValue("cls").Should().Be("X");
    }

    [UnitTest]
    public async Task Union_EmitsInputOnlyOverlayOnlyAndIntersectionPieces()
    {
        var input = Uri(Feature(Box(0, 0, 10, 10), ("zone", "A")));
        var overlay = Uri(Feature(Box(5, 0, 15, 10), ("cls", "X")));

        var (status, uri) = await RunAsync(
            new OverlayUnionExecutor(Options()),
            OverlayUnionExecutor.HandledProcessId,
            ("input", input),
            ("overlay", overlay));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(3, "input-only, overlay-only, and the intersection piece");
        features.Sum(f => f.Geometry.Area).Should().BeApproximately(150, 1e-6, "the three disjoint pieces tile the full union area");
        features.Should().Contain(f => Equals(f.Attributes.GetOptionalValue("cls"), "X"));
    }

    [UnitTest]
    public async Task Merge_CombinesBothLayers()
    {
        var input = Uri(Feature(Point(0, 0), ("src", "a")));
        var merge = Uri(Feature(Point(1, 1), ("src", "b")), Feature(Point(2, 2), ("src", "c")));

        var (status, uri) = await RunAsync(
            new OverlayMergeExecutor(Options()),
            OverlayMergeExecutor.HandledProcessId,
            ("input", input),
            ("merge", merge));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().HaveCount(3);
    }

    [UnitTest]
    public async Task Merge_EmptyInputs_ProduceEmptyOutput()
    {
        var (status, uri) = await RunAsync(
            new OverlayMergeExecutor(Options()),
            OverlayMergeExecutor.HandledProcessId,
            ("input", Uri()),
            ("merge", Uri()));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Append_ProjectsSourceOntoTargetSchema()
    {
        var target = Uri(Feature(Point(0, 0), ("id", 1), ("name", "t")));
        // Source has an extra field 'extra' (must be dropped) and is missing 'name'.
        var append = Uri(Feature(Point(5, 5), ("id", 2), ("extra", "drop-me")));

        var (status, uri) = await RunAsync(
            new DataManagementAppendExecutor(Options()),
            DataManagementAppendExecutor.HandledProcessId,
            ("input", target),
            ("append", append));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
        var appended = features[1];
        appended.Attributes.GetNames().Should().BeEquivalentTo(ExpectedAppendedFields, "only target fields are kept");
        appended.Attributes.GetOptionalValue("name").Should().BeNull("the source lacks 'name'");
        appended.Attributes.Exists("extra").Should().BeFalse("the non-target field is dropped");
    }

    [UnitTest]
    public async Task Append_FieldMapRemapsSourceFieldNames()
    {
        var target = Uri(Feature(Point(0, 0), ("population", 0L)));
        var append = Uri(Feature(Point(5, 5), ("pop", 42L)));

        var (status, uri) = await RunAsync(
            new DataManagementAppendExecutor(Options()),
            DataManagementAppendExecutor.HandledProcessId,
            ("input", target),
            ("append", append),
            ("fieldMap", "pop:population"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        Convert.ToInt64(features[1].Attributes.GetOptionalValue("population"), CultureInfo.InvariantCulture)
            .Should().Be(42);
    }

    [UnitTest]
    public async Task Split_ByFeatureClipsAndTagsPerZone()
    {
        var input = Uri(Feature(Box(0, 0, 10, 10), ("name", "tile")));
        var split = Uri(
            Feature(Box(0, 0, 5, 10), ("region", "west")),
            Feature(Box(5, 0, 10, 10), ("region", "east")));

        var (status, uri) = await RunAsync(
            new OverlaySplitExecutor(Options()),
            OverlaySplitExecutor.HandledProcessId,
            ("input", input),
            ("split", split),
            ("splitField", "region"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
        features.Select(f => f.Attributes.GetOptionalValue("SPLIT_TARGET"))
            .Should().BeEquivalentTo(new object[] { "west", "east" });
        features.Should().OnlyContain(f => Math.Abs(f.Geometry.Area - 50) < 1e-6);
    }

    [UnitTest]
    public async Task Split_ByFieldTagsWithoutClipping()
    {
        var input = Uri(
            Feature(Point(0, 0), ("cat", "a")),
            Feature(Point(1, 1), ("cat", "b")),
            Feature(Point(2, 2), ("cat", "a")));

        var (status, uri) = await RunAsync(
            new OverlaySplitExecutor(Options()),
            OverlaySplitExecutor.HandledProcessId,
            ("input", input),
            ("splitField", "cat"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(3);
        features.Count(f => Equals(f.Attributes.GetOptionalValue("SPLIT_TARGET"), "a")).Should().Be(2);
    }

    [UnitTest]
    public async Task Clip_MissingClipLayer_FailsCleanly()
    {
        var (status, _) = await RunAsync(
            new OverlayClipExecutor(Options()),
            OverlayClipExecutor.HandledProcessId,
            ("input", Uri(Feature(Box(0, 0, 1, 1)))));

        status.Should().Be(ExecutionJobStatus.Failed);
    }
}
