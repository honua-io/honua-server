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
