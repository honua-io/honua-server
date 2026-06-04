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
