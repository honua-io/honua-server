// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Generation;

/// <summary>
/// Unit tests for the deterministic quadtree LOD partitioner (#1200).
/// </summary>
public sealed class SceneQuadtreePartitionerTests
{
    private static readonly double[] Bounds = [-122.5, 37.7, -122.4, 37.8];

    [UnitTest]
    public void Partition_SmallSet_ProducesSingleLeaf()
    {
        var features = MakePointGrid(4, 4); // 16 features.
        var options = new SceneLodOptions { MaxFeaturesPerTile = 100, MaxDepth = 8, InteriorSampleCount = 8 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 1000.0, options);

        tree.Root.IsLeaf.Should().BeTrue();
        tree.Root.Features.Should().HaveCount(16);
        tree.MaxDepthReached.Should().Be(0);
    }

    [UnitTest]
    public void Partition_LargeSet_SubdividesIntoMultipleTiles()
    {
        var features = MakePointGrid(40, 40); // 1600 features.
        var options = new SceneLodOptions { MaxFeaturesPerTile = 200, MaxDepth = 8, InteriorSampleCount = 64 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 1000.0, options);

        tree.Root.IsLeaf.Should().BeFalse();
        tree.ContentNodeCount.Should().BeGreaterThan(1);
        tree.MaxDepthReached.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void Partition_GeometricError_DecreasesMonotonicallyWithDepth()
    {
        var features = MakePointGrid(60, 60);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 150, MaxDepth = 10, InteriorSampleCount = 64 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 4096.0, options);

        AssertErrorDecreases(tree.Root);
    }

    [UnitTest]
    public void Partition_EveryFeatureAssignedExactlyOnce()
    {
        var features = MakePointGrid(30, 30); // 900 features.
        var options = new SceneLodOptions { MaxFeaturesPerTile = 50, MaxDepth = 10, InteriorSampleCount = 0 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 1000.0, options);

        // With InteriorSampleCount = 0 interior nodes carry NO content (0 disables
        // interior content per SceneLodOptions.InteriorSampleCount), so the leaves
        // alone cover the dataset exactly once.
        var leafIds = new List<long>();
        CollectLeafFeatureIds(tree.Root, leafIds);

        leafIds.Should().HaveCount(900);
        leafIds.Distinct().Should().HaveCount(900);
    }

    [UnitTest]
    public void Partition_InteriorSampleCountZero_InteriorNodesCarryNoContent()
    {
        // Regression: InteriorSampleCount = 0 is documented to DISABLE interior
        // content (the tileset then carries only leaf geometry). Previously the
        // sampleCount<=0 case fell through to "return the full member set", so
        // every interior node carried the entire dataset beneath it — O(features x
        // depth) total emitted geometry and a root tile equal to the whole layer.
        // The grid is large enough to force subdivision so interior nodes exist.
        var features = MakePointGrid(40, 40); // 1600 features.
        var options = new SceneLodOptions { MaxFeaturesPerTile = 100, MaxDepth = 8, InteriorSampleCount = 0 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 1000.0, options);

        tree.Root.IsLeaf.Should().BeFalse("the large grid must subdivide so interior nodes exist.");

        // Every interior (non-leaf) node must carry zero content.
        AssertInteriorNodesEmpty(tree.Root);

        // Total emitted feature count across ALL content-bearing nodes equals the
        // input count exactly once: interior nodes contribute nothing, leaves
        // partition the dataset. (If interior nodes still carried the full set the
        // total would be a large multiple of 1600.)
        var totalEmitted = 0;
        SumEmittedFeatureCount(tree.Root, ref totalEmitted);
        totalEmitted.Should().Be(1600);

        // ContentNodeCount counts only nodes with content; with empty interiors it
        // equals the number of leaves, never the full node count.
        tree.ContentNodeCount.Should().Be(CountLeaves(tree.Root));
    }

    private static void AssertInteriorNodesEmpty(SceneTileNode node)
    {
        if (!node.IsLeaf)
        {
            node.Features.Should().BeEmpty(
                "InteriorSampleCount = 0 disables interior content, so an interior node carries no features.");
        }
        foreach (var child in node.Children)
        {
            AssertInteriorNodesEmpty(child);
        }
    }

    private static void SumEmittedFeatureCount(SceneTileNode node, ref int total)
    {
        total += node.Features.Count;
        foreach (var child in node.Children)
        {
            SumEmittedFeatureCount(child, ref total);
        }
    }

    private static int CountLeaves(SceneTileNode node)
    {
        if (node.IsLeaf)
        {
            return 1;
        }
        var count = 0;
        foreach (var child in node.Children)
        {
            count += CountLeaves(child);
        }
        return count;
    }

    [UnitTest]
    public void Partition_InteriorNodesCarryThinnedSample()
    {
        var features = MakePointGrid(50, 50); // 2500 features.
        var options = new SceneLodOptions { MaxFeaturesPerTile = 100, MaxDepth = 8, InteriorSampleCount = 64 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 1000.0, options);

        tree.Root.IsLeaf.Should().BeFalse();
        tree.Root.Features.Count.Should().BeLessThanOrEqualTo(64);
        tree.Root.Features.Should().NotBeEmpty();
    }

    [UnitTest]
    public void Partition_IsDeterministic_SameTreeShapeAndOrder()
    {
        var features = MakePointGrid(45, 45);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 120, MaxDepth = 9, InteriorSampleCount = 32 };

        var a = SceneQuadtreePartitioner.Partition(features, Bounds, 2048.0, options);
        var b = SceneQuadtreePartitioner.Partition(features, Bounds, 2048.0, options);

        Flatten(a.Root).Should().Equal(Flatten(b.Root));
    }

    [UnitTest]
    public void Partition_ClusteredFeatures_RespectsDepthCapWithoutInfiniteRecursion()
    {
        // 500 features all at the same coordinate: subdivision can never
        // separate them, so the partitioner must fall back to a leaf.
        var features = new List<SceneFeature>();
        for (var i = 0; i < 500; i++)
        {
            features.Add(MakePoint(i, -122.45, 37.75));
        }
        var options = new SceneLodOptions { MaxFeaturesPerTile = 10, MaxDepth = 12, InteriorSampleCount = 8 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 1000.0, options);

        // Degenerate split collapses to a single leaf at the root.
        tree.Root.IsLeaf.Should().BeTrue();
        tree.Root.Features.Should().HaveCount(500);
    }

    [UnitTest]
    public void Partition_RootIsLeaf_RootKeepsPositiveGeometricError()
    {
        // When the whole dataset fits one leaf, the root tile is also a leaf. It
        // must still carry its (positive) root geometric error rather than 0.0: a
        // zero root error gives the root a zero screen-space error and can leave a
        // client never scheduling the root tile, defeating root SSE refinement.
        var features = MakePointGrid(2, 2); // 4 features, well under the threshold.
        var options = new SceneLodOptions { MaxFeaturesPerTile = 100, MaxDepth = 8, InteriorSampleCount = 8 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 1234.0, options);

        tree.Root.IsLeaf.Should().BeTrue();
        tree.Root.GeometricError.Should().Be(1234.0,
            "a root-leaf must retain the floored, positive root geometric error.");
    }

    [UnitTest]
    public void Partition_DegenerateRootLeaf_RootKeepsPositiveGeometricError()
    {
        // All features at the same coordinate collapse the split into a single
        // root-leaf via the degenerate-quadrant path; that path must also preserve
        // the positive root geometric error.
        var features = new List<SceneFeature>();
        for (var i = 0; i < 50; i++)
        {
            features.Add(MakePoint(i, -122.45, 37.75));
        }
        var options = new SceneLodOptions { MaxFeaturesPerTile = 10, MaxDepth = 12, InteriorSampleCount = 8 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 777.0, options);

        tree.Root.IsLeaf.Should().BeTrue();
        tree.Root.GeometricError.Should().Be(777.0);
    }

    [UnitTest]
    public void Partition_NonRootLeaves_HaveZeroGeometricError()
    {
        // Every non-root leaf (a childless tile below the root) must report
        // geometric error 0.0, so a 3D Tiles client's SSE refinement converges at
        // the deepest LOD instead of believing finer detail still exists.
        var features = MakePointGrid(40, 40);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 50, MaxDepth = 10, InteriorSampleCount = 16 };

        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 2048.0, options);

        tree.Root.IsLeaf.Should().BeFalse("the large grid must subdivide so non-root leaves exist.");
        AssertNonRootLeavesAreZero(tree.Root, isRoot: true);
    }

    private static void AssertNonRootLeavesAreZero(SceneTileNode node, bool isRoot)
    {
        if (node.IsLeaf && !isRoot)
        {
            node.GeometricError.Should().Be(0.0,
                "a non-root leaf must have zero geometric error so SSE refinement converges.");
            return;
        }
        foreach (var child in node.Children)
        {
            AssertNonRootLeavesAreZero(child, isRoot: false);
        }
    }

    private static void AssertErrorDecreases(SceneTileNode node)
    {
        foreach (var child in node.Children)
        {
            child.GeometricError.Should().BeLessThan(node.GeometricError,
                "child geometric error must be strictly less than its parent's so a client refines correctly");
            AssertErrorDecreases(child);
        }
    }

    private static void CollectLeafFeatureIds(SceneTileNode node, List<long> sink)
    {
        if (node.IsLeaf)
        {
            foreach (var f in node.Features)
            {
                sink.Add(f.Id);
            }
            return;
        }
        foreach (var child in node.Children)
        {
            CollectLeafFeatureIds(child, sink);
        }
    }

    private static List<string> Flatten(SceneTileNode node)
    {
        var lines = new List<string>();
        void Walk(SceneTileNode n)
        {
            lines.Add($"d{n.Depth}:ge{n.GeometricError:F4}:fc{n.Features.Count}:ch{n.Children.Count}:[{n.West:F4},{n.South:F4},{n.East:F4},{n.North:F4}]");
            foreach (var c in n.Children)
            {
                Walk(c);
            }
        }
        Walk(node);
        return lines;
    }

    private static List<SceneFeature> MakePointGrid(int cols, int rows)
    {
        var features = new List<SceneFeature>(cols * rows);
        var west = Bounds[0];
        var south = Bounds[1];
        var lonStep = (Bounds[2] - Bounds[0]) / (cols + 1);
        var latStep = (Bounds[3] - Bounds[1]) / (rows + 1);
        var id = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var lon = west + lonStep * (c + 1);
                var lat = south + latStep * (r + 1);
                features.Add(MakePoint(id++, lon, lat));
            }
        }
        return features;
    }

    private static SceneFeature MakePoint(int id, double lon, double lat) => new()
    {
        Id = id,
        Geometry = new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.Point,
            Vertices = [new SceneVertex(lon, lat, 10.0)]
        }
    };
}
