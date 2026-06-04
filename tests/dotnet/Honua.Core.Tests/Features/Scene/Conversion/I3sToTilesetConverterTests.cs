// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Conversion;

/// <summary>
/// Unit tests for the I3S scene-layer to 3D Tiles tileset converter (#1268).
/// </summary>
public sealed class I3sToTilesetConverterTests
{
    private static I3sSceneLayerDocument BuildSampleLayer(string layerType = "3DObject") => new()
    {
        Id = 0,
        LayerType = layerType,
        Name = "Sample Buildings",
        Version = "1.7",
        SpatialReference = new I3sSpatialReference { Wkid = 4326, LatestWkid = 4326 },
        FullExtent = new I3sFullExtent
        {
            Xmin = -122.5,
            Ymin = 37.7,
            Xmax = -122.4,
            Ymax = 37.8,
            Zmin = 0.0,
            Zmax = 120.0,
            SpatialReference = new I3sSpatialReference { Wkid = 4326 },
        },
        Store = new I3sStore { Id = "store-1", Profile = "meshpyramids" },
    };

    [UnitTest]
    public void Convert_3dObjectLayer_ProducesTilesetRootedAtExtent()
    {
        var tileset = I3sToTilesetConverter.Convert(BuildSampleLayer());

        tileset.Asset.Version.Should().Be("1.1");
        tileset.Asset.Generator.Should().Be("honua-i3s-converter");

        // Root region is in radians (west, south, east, north, minH, maxH).
        var region = tileset.Root.BoundingVolume.Region;
        region.Should().HaveCount(6);
        region[0].Should().BeApproximately(-122.5 * Math.PI / 180.0, 1e-9);
        region[3].Should().BeApproximately(37.8 * Math.PI / 180.0, 1e-9);
        region[4].Should().Be(0.0);
        region[5].Should().Be(120.0);
        tileset.GeometricError.Should().BeGreaterThan(0.0);
    }

    [UnitTest]
    public void Convert_IntegratedMeshLayer_IsAccepted()
    {
        var tileset = I3sToTilesetConverter.Convert(BuildSampleLayer("IntegratedMesh"));

        tileset.Root.BoundingVolume.Region.Should().HaveCount(6);
    }

    [UnitTest]
    public void Convert_WithRootContentUri_AddsRootContentChild()
    {
        var tileset = I3sToTilesetConverter.Convert(BuildSampleLayer(), rootContentUri: "0/0.glb");

        tileset.Root.Children.Should().NotBeNull();
        tileset.Root.Children!.Single().Content!.Uri.Should().Be("0/0.glb");
    }

    [UnitTest]
    public void Convert_WithoutContentUri_EmitsExtentOnlyRoot()
    {
        var tileset = I3sToTilesetConverter.Convert(BuildSampleLayer());

        // No children when there is no content URI: an extent-only tileset.
        (tileset.Root.Children is null || tileset.Root.Children.Count == 0).Should().BeTrue();
    }

    [UnitTest]
    public void Convert_UnsupportedLayerType_Throws()
    {
        var layer = BuildSampleLayer("Voxel");

        var act = () => I3sToTilesetConverter.Convert(layer);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.UnsupportedLayerType);
    }

    [UnitTest]
    public void Convert_MissingExtent_Throws()
    {
        var layer = BuildSampleLayer();
        layer.FullExtent = null;

        var act = () => I3sToTilesetConverter.Convert(layer);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.MissingExtent);
    }

    [UnitTest]
    public void Convert_NonWgs84SpatialReference_Throws()
    {
        var layer = BuildSampleLayer();
        layer.FullExtent!.SpatialReference = new I3sSpatialReference { Wkid = 3857 };
        layer.SpatialReference = new I3sSpatialReference { Wkid = 3857 };

        var act = () => I3sToTilesetConverter.Convert(layer);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.UnsupportedSpatialReference);
    }

    [UnitTest]
    public void Convert_DegenerateExtent_Throws()
    {
        var layer = BuildSampleLayer();
        layer.FullExtent!.Xmin = 10.0;
        layer.FullExtent.Xmax = -10.0;

        var act = () => I3sToTilesetConverter.Convert(layer);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.MissingExtent);
    }

    [UnitTest]
    public void Convert_LongitudeOutsideWgs84Range_Throws()
    {
        // A WKID-4326 layer whose bounds are in a non-degree unit (e.g. metres)
        // would otherwise be placed far outside the globe. The converter
        // validates the WGS-84 range before treating the extent as degrees.
        var layer = BuildSampleLayer();
        layer.FullExtent!.Xmin = 0.0;
        layer.FullExtent.Xmax = 500_000.0; // > 180 degrees.

        var act = () => I3sToTilesetConverter.Convert(layer);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.MissingExtent);
    }

    [UnitTest]
    public void Convert_LatitudeOutsideWgs84Range_Throws()
    {
        var layer = BuildSampleLayer();
        layer.FullExtent!.Ymin = -95.0; // < -90 degrees.
        layer.FullExtent.Ymax = 10.0;

        var act = () => I3sToTilesetConverter.Convert(layer);

        act.Should().Throw<I3sConversionException>()
            .Which.Reason.Should().Be(I3sConversionErrorReason.MissingExtent);
    }
}
