// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Protocols.Scene.I3s;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Unit tests for the I3S SceneServer descriptor builder (#1202). The builder
/// is a pure mapping from a hosted scene + extent to I3S service/layer JSON.
/// </summary>
[Protocol(TestProtocols.Scene)]
public sealed class I3sSceneServiceBuilderTests
{
    private static readonly SceneDataset Scene = new()
    {
        Id = "downtown",
        Name = "Downtown",
        Description = "A hosted city scene",
        AssetRoot = "/srv/scenes/downtown",
    };

    private static readonly SceneExtent Extent = new(-122.5, 37.7, -122.4, 37.8);

    [UnitTest]
    public void BuildLayer_WithExtent_MapsToWgs84ThreeDObjectLayer()
    {
        var layer = I3sSceneServiceBuilder.BuildLayer(Scene, Extent, minHeightMeters: 0.0, maxHeightMeters: 100.0);

        layer.Id.Should().Be(0);
        layer.LayerType.Should().Be("3DObject");
        layer.Name.Should().Be("Downtown");
        layer.SpatialReference!.Wkid.Should().Be(4326);
        layer.FullExtent!.Xmin.Should().Be(-122.5);
        layer.FullExtent.Ymax.Should().Be(37.8);
        layer.FullExtent.Zmax.Should().Be(100.0);
        layer.Store!.Id.Should().Be("downtown");
        layer.Store.Profile.Should().Be("meshpyramids");

        // The descriptor carries the spec-required heightModelInfo block that is
        // honestly knowable for a hosted WGS-84 scene.
        layer.HeightModelInfo.Should().NotBeNull();
        layer.HeightModelInfo!.HeightModel.Should().Be("ellipsoidal");
        layer.HeightModelInfo.HeightUnit.Should().Be("meter");
    }

    [UnitTest]
    public void BuildLayer_DoesNotAdvertiseUnservableRootNode()
    {
        // This slice is a descriptor preview: per-node geometry (the nodes/*
        // store) is a tracked follow-up (#1202) and no node routes are mapped, so
        // the descriptor must NOT advertise a fetchable rootNode that would 404
        // for a conformant I3S/ArcGIS client.
        var layer = I3sSceneServiceBuilder.BuildLayer(Scene, Extent);

        layer.Store!.RootNode.Should().BeNull();
    }

    [UnitTest]
    public void BuildLayer_WithExtentButNoHeights_LeavesVerticalExtentNull()
    {
        // The served descriptor path (I3sSceneServerEndpoints.ResolveExtentAsync)
        // has no height source — the persisted SceneDatasetRecord carries only a 2D
        // SceneExtent — so it passes null heights. The fullExtent must then advertise
        // a horizontal-only extent (zmin/zmax omitted) rather than a fabricated 0..0
        // vertical range; authoritative vertical bounds live on the gRPC TileService
        // bounding volumes.
        var layer = I3sSceneServiceBuilder.BuildLayer(Scene, Extent);

        layer.FullExtent.Should().NotBeNull();
        layer.FullExtent!.Xmin.Should().Be(-122.5);
        layer.FullExtent.Zmin.Should().BeNull();
        layer.FullExtent.Zmax.Should().BeNull();
    }

    [UnitTest]
    public void BuildLayer_WithoutExtent_OmitsFullExtent()
    {
        var layer = I3sSceneServiceBuilder.BuildLayer(Scene, extent: null);

        layer.FullExtent.Should().BeNull();
        layer.LayerType.Should().Be("3DObject");
    }

    [UnitTest]
    public void BuildService_WrapsSingleLayer()
    {
        var service = I3sSceneServiceBuilder.BuildService(Scene, Extent);

        service.ServiceName.Should().Be("Downtown");
        service.ServiceVersion.Should().Be("1.7");
        service.SupportedBindings.Should().Contain("REST");
        service.Layers.Should().ContainSingle();
        service.Layers[0].Id.Should().Be(0);
    }
}
