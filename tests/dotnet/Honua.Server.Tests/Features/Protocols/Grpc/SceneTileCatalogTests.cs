// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Scene.Grpc;
using Honua.TestKit.Attributes;
using Domain = Honua.Core.Features.Scene.Domain;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Unit tests for the pure tileset-to-gRPC catalog projection.
/// </summary>
public sealed class SceneTileCatalogTests
{
    private static Domain.TilesetDocument BuildTwoLevelTileset()
    {
        return new Domain.TilesetDocument
        {
            GeometricError = 100,
            Root = new Domain.TileNode
            {
                GeometricError = 100,
                BoundingVolume = new Domain.BoundingVolume { Region = [-1.31, 0.69, -1.30, 0.70, 0, 20] },
                Content = new Domain.TileContent { Uri = "tiles/0.b3dm" },
                Children =
                [
                    new Domain.TileNode
                    {
                        GeometricError = 0,
                        BoundingVolume = new Domain.BoundingVolume { Region = [-1.31, 0.69, -1.30, 0.70, 0, 20] },
                        Content = new Domain.TileContent { Uri = "nested/sub.json" },
                    },
                ],
            },
        };
    }

    [UnitTest]
    public void Build_AssignsDeterministicIdsAndLods()
    {
        var entries = SceneTileCatalog.Build(BuildTwoLevelTileset());

        entries.Should().HaveCount(2);
        entries[0].NodeId.Should().Be("0");
        entries[0].Lod.Should().Be(0);
        entries[0].ChildIds.Should().ContainSingle().Which.Should().Be("0-0");
        entries[1].NodeId.Should().Be("0-0");
        entries[1].Lod.Should().Be(1);
    }

    [UnitTest]
    public void ToProtoNode_CarriesChildIdsAndGeometricError()
    {
        var entries = SceneTileCatalog.Build(BuildTwoLevelTileset());

        var rootNode = SceneTileCatalog.ToProtoNode(entries[0]);

        rootNode.NodeId.Should().Be("0");
        rootNode.GeometricError.Should().Be(100);
        rootNode.ChildNodeIds.Should().ContainSingle().Which.Should().Be("0-0");
        rootNode.BoundingVolume.Should().NotBeNull();
    }

    [UnitTest]
    public void ToBoundingVolume_ConvertsRadiansToDegrees()
    {
        var volume = SceneTileCatalog.ToBoundingVolume(new Domain.BoundingVolume { Region = [0, 0, Math.PI, Math.PI / 2, 5, 25] });

        volume.Should().NotBeNull();
        volume!.Region.Extent.Xmin.Should().BeApproximately(0, 1e-9);
        volume.Region.Extent.Xmax.Should().BeApproximately(180, 1e-9);
        volume.Region.Extent.Ymax.Should().BeApproximately(90, 1e-9);
        volume.Region.MinHeight.Should().Be(5);
        volume.Region.MaxHeight.Should().Be(25);
    }

    [Theory]
    [InlineData("tiles/0.b3dm", Proto.TileContentType.B3Dm)]
    [InlineData("model.glb", Proto.TileContentType.Glb)]
    [InlineData("instanced.i3dm", Proto.TileContentType.I3Dm)]
    [InlineData("cloud.pnts", Proto.TileContentType.Pnts)]
    [InlineData("nested/sub.json", Proto.TileContentType.Unspecified)]
    [InlineData("", Proto.TileContentType.Unspecified)]
    [Trait("Category", "Unit")]
    public void ContentTypeFromUri_MapsByExtension(string uri, Proto.TileContentType expected)
    {
        SceneTileCatalog.ContentTypeFromUri(uri).Should().Be(expected);
    }

    [UnitTest]
    public void IntersectsExtent_WithNoFilter_ReturnsTrue()
    {
        var node = new Domain.TileNode { BoundingVolume = new Domain.BoundingVolume { Region = [0, 0, 0.1, 0.1, 0, 10] } };

        SceneTileCatalog.IntersectsExtent(node, requestExtent: null).Should().BeTrue();
    }

    [UnitTest]
    public void IntersectsExtent_WithDisjointFilter_ReturnsFalse()
    {
        // Node region spans ~0..5.7 degrees (0..0.1 rad); filter sits far east.
        var node = new Domain.TileNode { BoundingVolume = new Domain.BoundingVolume { Region = [0, 0, 0.1, 0.1, 0, 10] } };
        var filter = new Proto.Extent3D { Extent = new Proto.Extent { Xmin = 100, Ymin = 100, Xmax = 110, Ymax = 110 } };

        SceneTileCatalog.IntersectsExtent(node, filter).Should().BeFalse();
    }
}
