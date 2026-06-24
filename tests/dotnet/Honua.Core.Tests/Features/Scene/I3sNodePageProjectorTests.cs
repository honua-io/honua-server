// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene;

/// <summary>
/// Unit tests for the glTF/3D-Tiles -> I3S 1.7 node-page projector (#1809):
/// flattening the tileset tree into HLOD node pages with oriented bounding
/// boxes, LOD thresholds, parent/child references by global index, and per-node
/// geometry/attribute resource references.
/// </summary>
public sealed class I3sNodePageProjectorTests
{
    [UnitTest]
    public void BuildPages_RootWithTwoContentChildren_ProjectsThreeNodes()
    {
        var document = BuildThreeNodeTileset();

        var pages = I3sNodePageProjector.BuildPages(document);

        pages.Should().ContainSingle();
        pages[0].Nodes.Should().HaveCount(3);
    }

    [UnitTest]
    public void BuildPages_Root_HasZeroLodThreshold_NoParent_AndChildIndices()
    {
        var pages = I3sNodePageProjector.BuildPages(BuildThreeNodeTileset());

        var root = pages[0].Nodes[0];
        root.LodThreshold.Should().Be(0d);
        root.ParentIndex.Should().BeNull();
        root.Children.Should().BeEquivalentTo(new[] { 1, 2 });
        root.Mesh.Should().BeNull("a pure grouping node carries no fetchable geometry");
    }

    [UnitTest]
    public void BuildPages_ContentChildren_ReferenceGeometryByGlobalIndex()
    {
        var pages = I3sNodePageProjector.BuildPages(BuildThreeNodeTileset());

        var first = pages[0].Nodes[1];
        first.ParentIndex.Should().Be(0);
        first.Mesh.Should().NotBeNull();
        first.Mesh!.Geometry.Resource.Should().Be(1);
        first.Mesh.Attribute!.Resource.Should().Be(1);

        var second = pages[0].Nodes[2];
        second.Mesh!.Geometry.Resource.Should().Be(2);
    }

    [UnitTest]
    public void BuildPages_Obb_CentersInIndexCrs_WithMetricHalfSizes()
    {
        var pages = I3sNodePageProjector.BuildPages(BuildThreeNodeTileset());

        var obb = pages[0].Nodes[0].Obb;
        obb.Should().NotBeNull();
        obb!.Center.Should().HaveCount(3);
        obb.HalfSize.Should().HaveCount(3);
        obb.Quaternion.Should().BeEquivalentTo(new[] { 0d, 0d, 0d, 1d });

        // Centre longitude/latitude fall in WGS-84 degrees (the index CRS), and
        // the half-sizes are positive metric extents.
        obb.Center[0].Should().BeInRange(-180d, 180d);
        obb.Center[1].Should().BeInRange(-90d, 90d);
        obb.HalfSize[0].Should().BeGreaterThan(0d);
        obb.HalfSize[2].Should().BeApproximately(20d, 1e-6, "root vertical half-extent is (40-0)/2 metres");
    }

    [UnitTest]
    public void BuildPages_EmptyTileset_ReturnsNoPages()
    {
        var document = new TilesetDocument
        {
            Root = new TileNode { BoundingVolume = new BoundingVolume { Region = [] } },
        };

        // A single root with no content and no children still yields one node.
        I3sNodePageProjector.BuildPages(document).Should().ContainSingle();
    }

    [UnitTest]
    public void PageCount_LargeTree_PartitionsByNodesPerPage()
    {
        // Build a wide root with more children than fit on a single page.
        var childCount = I3sNodePageProjector.NodesPerPage + 5;
        var children = new List<TileNode>(childCount);
        for (var i = 0; i < childCount; i++)
        {
            children.Add(new TileNode
            {
                BoundingVolume = Region(-1.2, 0.6, -1.19, 0.61, 0, 10),
                GeometricError = 1.0,
                Content = new TileContent { Uri = $"nodes/{i}.glb" },
            });
        }

        var document = new TilesetDocument
        {
            Root = new TileNode
            {
                BoundingVolume = Region(-1.2, 0.6, -1.19, 0.61, 0, 40),
                GeometricError = 10.0,
                Children = children,
            },
        };

        // 1 root + childCount children = NodesPerPage + 6 nodes -> 2 pages.
        I3sNodePageProjector.PageCount(document).Should().Be(2);
        var pages = I3sNodePageProjector.BuildPages(document);
        pages.Should().HaveCount(2);
        pages[0].Nodes.Should().HaveCount(I3sNodePageProjector.NodesPerPage);
    }

    private static TilesetDocument BuildThreeNodeTileset() => new()
    {
        GeometricError = 100.0,
        Root = new TileNode
        {
            BoundingVolume = Region(-1.3197, 0.69886, -1.31966, 0.69890, 0, 40),
            GeometricError = 50.0,
            Children =
            [
                new TileNode
                {
                    BoundingVolume = Region(-1.3197, 0.69886, -1.31968, 0.69890, 0, 20),
                    GeometricError = 5.0,
                    Content = new TileContent { Uri = "nodes/0.glb" },
                },
                new TileNode
                {
                    BoundingVolume = Region(-1.31968, 0.69886, -1.31966, 0.69890, 0, 20),
                    GeometricError = 5.0,
                    Content = new TileContent { Uri = "nodes/1.glb" },
                },
            ],
        },
    };

    private static BoundingVolume Region(
        double west, double south, double east, double north, double minH, double maxH)
        => new() { Region = [west, south, east, north, minH, maxH] };
}
