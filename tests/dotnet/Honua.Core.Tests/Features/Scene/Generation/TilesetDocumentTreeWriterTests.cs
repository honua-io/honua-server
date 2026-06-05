// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Generation;

/// <summary>
/// Unit tests for the multi-level (quadtree LOD) tileset writer path (#1200).
/// </summary>
public sealed class TilesetDocumentTreeWriterTests
{
    private static readonly double[] Bounds = [-122.5, 37.7, -122.4, 37.8];

    [UnitTest]
    public void BuildTree_EmitsRefineReplaceAndNestedChildren()
    {
        var features = MakePointGrid(40, 40);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 200, MaxDepth = 8, InteriorSampleCount = 64 };
        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 4096.0, options);
        var content = AssignContent(tree);

        var doc = TilesetDocumentWriter.BuildTree(tree, content, "honua-test");

        doc.Asset.Version.Should().Be("1.1");
        doc.GeometricError.Should().Be(tree.RootGeometricError);
        doc.Root.Refine.Should().Be("REPLACE");
        doc.Root.Children.Should().NotBeNull();
        doc.Root.Children!.Count.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public void BuildTree_RegionMatchesNodeBoundsInRadians()
    {
        var features = MakePointGrid(40, 40);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 200, MaxDepth = 8, InteriorSampleCount = 64 };
        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 4096.0, options);
        var content = AssignContent(tree);

        var doc = TilesetDocumentWriter.BuildTree(tree, content, "honua-test");

        doc.Root.BoundingVolume.Region[0].Should().BeApproximately(tree.Root.West * Math.PI / 180.0, 1e-12);
        doc.Root.BoundingVolume.Region[3].Should().BeApproximately(tree.Root.North * Math.PI / 180.0, 1e-12);
    }

    [UnitTest]
    public void BuildTree_Serialize_IsByteIdenticalAcrossRuns()
    {
        var features = MakePointGrid(45, 45);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 120, MaxDepth = 9, InteriorSampleCount = 32 };

        var treeA = SceneQuadtreePartitioner.Partition(features, Bounds, 2048.0, options);
        var treeB = SceneQuadtreePartitioner.Partition(features, Bounds, 2048.0, options);

        var bytesA = TilesetDocumentWriter.Serialize(TilesetDocumentWriter.BuildTree(treeA, AssignContent(treeA), "honua"));
        var bytesB = TilesetDocumentWriter.Serialize(TilesetDocumentWriter.BuildTree(treeB, AssignContent(treeB), "honua"));

        bytesA.Should().Equal(bytesB);
    }

    [UnitTest]
    public void BuildTree_ChildGeometricError_DecreasesInSerializedJson()
    {
        var features = MakePointGrid(50, 50);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 100, MaxDepth = 10, InteriorSampleCount = 32 };
        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 4096.0, options);

        var bytes = TilesetDocumentWriter.Serialize(TilesetDocumentWriter.BuildTree(tree, AssignContent(tree), "honua"));
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));

        var root = json.RootElement.GetProperty("root");
        var rootError = root.GetProperty("geometricError").GetDouble();
        foreach (var child in root.GetProperty("children").EnumerateArray())
        {
            child.GetProperty("geometricError").GetDouble().Should().BeLessThan(rootError);
        }
    }

    [UnitTest]
    public void BuildTree_ContentLessInteriorNode_RegionEnclosesDescendantHeights()
    {
        // InteriorSampleCount = 0 disables interior content, so every interior
        // node is absent from the content map. Its region vertical extent must
        // still enclose the [min,max] of its content-bearing leaf descendants,
        // or a conformant 3D Tiles client can cull visible geometry.
        var features = MakePointGrid(40, 40);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 80, MaxDepth = 8, InteriorSampleCount = 0 };
        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 4096.0, options);

        // Assign each content-bearing (leaf) node a DISTINCT, nonzero height
        // span so a naive [0,0] interior region is detectably wrong.
        var content = new Dictionary<SceneTileNode, SceneTileContent>();
        var index = 0;
        void AssignLeafHeights(SceneTileNode node)
        {
            if (node.Features.Count > 0)
            {
                var min = 100.0 + (index * 10.0);
                var max = min + 25.0 + index;
                content[node] = new SceneTileContent($"tile_{index:0000}.glb", min, max);
                index++;
            }
            foreach (var child in node.Children)
            {
                AssignLeafHeights(child);
            }
        }
        AssignLeafHeights(tree.Root);

        // Sanity: the configuration actually produced content-less interior
        // nodes (otherwise the test would vacuously pass).
        CountInteriorContentLessNodes(tree.Root, content).Should().BeGreaterThan(0);

        var doc = TilesetDocumentWriter.BuildTree(tree, content, "honua-test");

        // Pair the domain tree with the emitted TileNode tree (same pre-order
        // structure) and assert the enclosure invariant on every node.
        AssertRegionEnclosesDescendants(tree.Root, doc.Root, content);
    }

    [UnitTest]
    public void BuildTree_ContentBearingInteriorNode_RegionEnclosesDescendantHeights()
    {
        // Interior nodes DO carry content here (InteriorSampleCount > 0), but a
        // content-bearing interior node's height comes from a thinned sample of
        // its subtree, so its own [min,max] can be narrower than a descendant
        // leaf's. The emitted region must union the whole subtree's extent rather
        // than trust the node's own sampled span — otherwise it under-encloses
        // and a conformant 3D Tiles client culls visible geometry.
        var features = MakePointGrid(40, 40);
        var options = new SceneLodOptions { MaxFeaturesPerTile = 80, MaxDepth = 8, InteriorSampleCount = 64 };
        var tree = SceneQuadtreePartitioner.Partition(features, Bounds, 4096.0, options);

        // Give every content-bearing node (interior samples included) a narrow,
        // distinct height span...
        var content = new Dictionary<SceneTileNode, SceneTileContent>();
        var index = 0;
        void AssignNarrow(SceneTileNode node)
        {
            if (node.Features.Count > 0)
            {
                var min = 100.0 + index;
                content[node] = new SceneTileContent($"tile_{index:0000}.glb", min, min + 5.0);
                index++;
            }
            foreach (var child in node.Children)
            {
                AssignNarrow(child);
            }
        }
        AssignNarrow(tree.Root);

        // ...then force the deepest content-bearing leaf to carry an extreme span
        // that none of its narrow-span ancestors enclose on their own.
        SceneTileNode? deepestLeaf = null;
        var deepestDepth = -1;
        void FindDeepestLeaf(SceneTileNode node, int depth)
        {
            if (node.Children.Count == 0 && content.ContainsKey(node) && depth > deepestDepth)
            {
                deepestLeaf = node;
                deepestDepth = depth;
            }
            foreach (var child in node.Children)
            {
                FindDeepestLeaf(child, depth + 1);
            }
        }
        FindDeepestLeaf(tree.Root, 0);
        deepestLeaf.Should().NotBeNull("the fixture must contain a content-bearing leaf");
        content[deepestLeaf!] = new SceneTileContent("tile_extreme.glb", -500.0, 1500.0);

        // Sanity: at least one content-bearing INTERIOR node now fails to enclose
        // its descendants on its own span — so the test exercises the
        // content-bearing path and would fail if the writer trusted that span.
        CountUnderEnclosingInteriorContentNodes(tree.Root, content).Should().BeGreaterThan(0);

        var doc = TilesetDocumentWriter.BuildTree(tree, content, "honua-test");

        AssertRegionEnclosesDescendants(tree.Root, doc.Root, content);
    }

    private static int CountUnderEnclosingInteriorContentNodes(
        SceneTileNode node,
        IReadOnlyDictionary<SceneTileNode, SceneTileContent> content)
    {
        var count = 0;
        if (node.Children.Count > 0 && content.TryGetValue(node, out var own))
        {
            var childMin = double.PositiveInfinity;
            var childMax = double.NegativeInfinity;
            var childFound = false;
            foreach (var child in node.Children)
            {
                var (found, min, max) = DescendantHeightExtent(child, content);
                if (found)
                {
                    childFound = true;
                    if (min < childMin) childMin = min;
                    if (max > childMax) childMax = max;
                }
            }
            if (childFound && (own.MinHeightMeters > childMin || own.MaxHeightMeters < childMax))
            {
                count = 1;
            }
        }
        foreach (var child in node.Children)
        {
            count += CountUnderEnclosingInteriorContentNodes(child, content);
        }
        return count;
    }

    private static int CountInteriorContentLessNodes(
        SceneTileNode node,
        IReadOnlyDictionary<SceneTileNode, SceneTileContent> content)
    {
        var count = !content.ContainsKey(node) && node.Children.Count > 0 ? 1 : 0;
        foreach (var child in node.Children)
        {
            count += CountInteriorContentLessNodes(child, content);
        }
        return count;
    }

    private static void AssertRegionEnclosesDescendants(
        SceneTileNode node,
        TileNode emitted,
        IReadOnlyDictionary<SceneTileNode, SceneTileContent> content)
    {
        var (hasDescendant, descMin, descMax) = DescendantHeightExtent(node, content);
        if (hasDescendant)
        {
            // Region layout is [west, south, east, north, minHeight, maxHeight].
            emitted.BoundingVolume.Region[4].Should().BeLessThanOrEqualTo(
                descMin,
                "the node region floor must enclose every content-bearing descendant");
            emitted.BoundingVolume.Region[5].Should().BeGreaterThanOrEqualTo(
                descMax,
                "the node region ceiling must enclose every content-bearing descendant");
        }

        if (node.Children.Count > 0)
        {
            emitted.Children.Should().NotBeNull();
            emitted.Children!.Count.Should().Be(node.Children.Count);
            for (var i = 0; i < node.Children.Count; i++)
            {
                AssertRegionEnclosesDescendants(node.Children[i], emitted.Children[i], content);
            }
        }
    }

    private static (bool Found, double Min, double Max) DescendantHeightExtent(
        SceneTileNode node,
        IReadOnlyDictionary<SceneTileNode, SceneTileContent> content)
    {
        var found = false;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        if (content.TryGetValue(node, out var c))
        {
            found = true;
            min = c.MinHeightMeters;
            max = c.MaxHeightMeters;
        }
        foreach (var child in node.Children)
        {
            var (childFound, childMin, childMax) = DescendantHeightExtent(child, content);
            if (childFound)
            {
                found = true;
                if (childMin < min) min = childMin;
                if (childMax > max) max = childMax;
            }
        }
        return (found, min, max);
    }

    private static Dictionary<SceneTileNode, SceneTileContent> AssignContent(SceneTileTree tree)
    {
        var content = new Dictionary<SceneTileNode, SceneTileContent>();
        var index = 0;
        void Walk(SceneTileNode node)
        {
            if (node.Features.Count > 0)
            {
                content[node] = new SceneTileContent($"tile_{index:0000}.glb", 0.0, 10.0);
                index++;
            }
            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }
        Walk(tree.Root);
        return content;
    }

    private static List<SceneFeature> MakePointGrid(int cols, int rows)
    {
        var features = new List<SceneFeature>(cols * rows);
        var lonStep = (Bounds[2] - Bounds[0]) / (cols + 1);
        var latStep = (Bounds[3] - Bounds[1]) / (rows + 1);
        var id = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var lon = Bounds[0] + lonStep * (c + 1);
                var lat = Bounds[1] + latStep * (r + 1);
                features.Add(new SceneFeature
                {
                    Id = id++,
                    Geometry = new SceneFeatureGeometry
                    {
                        Kind = SceneGeometryKind.Point,
                        Vertices = [new SceneVertex(lon, lat, 10.0)]
                    }
                });
            }
        }
        return features;
    }
}
