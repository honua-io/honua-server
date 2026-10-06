// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
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

    // A z-bearing extent: the vertical range now lives on SceneExtent itself
    // (read from the tileset's root bounding volume) rather than being passed as
    // separate height parameters.
    private static readonly SceneExtent ExtentWithZ = new(-122.5, 37.7, -122.4, 37.8, ZMin: 0.0, ZMax: 100.0);

    [UnitTest]
    public void Issue5443_LayerDocument_ConformsToI3s17ThreeDSceneLayer()
    {
        var layer = I3sSceneServiceBuilder.BuildLayer(Scene, ExtentWithZ, advertiseNodePages: true);
        var document = JsonSerializer.SerializeToElement(
            layer,
            I3sServingJsonContext.Default.I3sSceneLayerDocument);

        document.GetProperty("capabilities").EnumerateArray()
            .Select(value => value.GetString()).Should().Contain("View");

        var nodePages = document.GetProperty("nodePages");
        nodePages.GetProperty("nodesPerPage").GetInt32().Should().BePositive();
        document.GetProperty("store").TryGetProperty("nodePages", out _).Should().BeFalse();

        var schema = document.GetProperty("store").GetProperty("defaultGeometrySchema");
        schema.GetProperty("topology").GetString().Should().Be("PerAttributeArray");
        schema.GetProperty("header").GetArrayLength().Should().BePositive();
        schema.GetProperty("ordering").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("position", "normal", "uv0", "color");
        schema.GetProperty("vertexAttributes").GetProperty("position")
            .GetProperty("valueType").GetString().Should().Be("Float32");
        schema.GetProperty("vertexAttributes").GetProperty("color")
            .GetProperty("valueType").GetString().Should().Be("UInt8");
        schema.GetProperty("vertexAttributes").GetProperty("color")
            .GetProperty("valuesPerElement").GetInt32().Should().Be(4);
        schema.GetProperty("featureAttributeOrder").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("id", "faceRange");
        schema.GetProperty("featureAttributes").GetProperty("faceRange")
            .GetProperty("valuesPerElement").GetInt32().Should().Be(2);

        document.GetProperty("attributeStorageInfo")[0].GetProperty("header")
            .GetArrayLength().Should().BePositive();

        var geometry = document.GetProperty("geometryDefinitions")[0];
        geometry.GetProperty("topology").GetString().Should().Be("triangle");
        var geometryBuffer = geometry.GetProperty("geometryBuffers")[0];
        geometryBuffer.GetProperty("offset").GetInt32().Should().Be(8);
        geometryBuffer.GetProperty("color").GetProperty("type").GetString().Should().Be("UInt8");
        geometryBuffer.GetProperty("color").GetProperty("component").GetInt32().Should().Be(4);
        geometryBuffer.GetProperty("featureId").GetProperty("binding")
            .GetString().Should().Be("per-feature");
        geometryBuffer.GetProperty("faceRange").GetProperty("binding")
            .GetString().Should().Be("per-feature");

        document.GetProperty("materialDefinitions")[0].GetProperty("alphaMode")
            .GetString().Should().Be("opaque");
        document.GetProperty("textureSetDefinitions")[0].GetProperty("formats")[0]
            .GetProperty("format").GetString().Should().Be("jpg");
    }

    [UnitTest]
    public void BuildLayer_WithExtent_MapsToWgs84ThreeDObjectLayer()
    {
        var layer = I3sSceneServiceBuilder.BuildLayer(Scene, ExtentWithZ);

        layer.Id.Should().Be(0);
        layer.LayerType.Should().Be("3DObject");
        layer.Name.Should().Be("Downtown");
        layer.SpatialReference!.Wkid.Should().Be(4326);
        // The layer advertises the WGS-84 ellipsoidal vertical CRS so a client
        // knows the z values are ellipsoidal metres.
        layer.SpatialReference.VcsWkid.Should().Be(115700);
        layer.FullExtent!.Xmin.Should().Be(-122.5);
        layer.FullExtent.Ymax.Should().Be(37.8);
        layer.FullExtent.Zmin.Should().Be(0.0);
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
    public void BuildLayer_DescribesFormatDefinitions()
    {
        // The enriched 1.7 descriptor (#1808) carries the format
        // definitions/schema that DESCRIBE the served 3D Object format —
        // geometry schema, materials, texture sets, and attribute storage —
        // without advertising any fetchable node store.
        var layer = I3sSceneServiceBuilder.BuildLayer(Scene, ExtentWithZ);

        layer.GeometryDefinitions.Should().NotBeNullOrEmpty();
        layer.GeometryDefinitions![0].GeometryBuffers.Should().NotBeNullOrEmpty();
        layer.GeometryDefinitions[0].GeometryBuffers![0].Position!.Component.Should().Be(3);

        layer.MaterialDefinitions.Should().NotBeNullOrEmpty();
        layer.MaterialDefinitions![0].PbrMetallicRoughness.Should().NotBeNull();

        layer.TextureSetDefinitions.Should().NotBeNullOrEmpty();
        layer.TextureSetDefinitions![0].Formats.Should().NotBeNullOrEmpty();

        layer.AttributeStorageInfo.Should().NotBeNullOrEmpty();
        layer.AttributeStorageInfo![0].Name.Should().Be("OBJECTID");
    }

    [UnitTest]
    public void BuildLayer_WithZBearingExtent_PopulatesVerticalExtent()
    {
        var layer = I3sSceneServiceBuilder.BuildLayer(Scene, ExtentWithZ);

        layer.FullExtent.Should().NotBeNull();
        layer.FullExtent!.Zmin.Should().Be(0.0);
        layer.FullExtent.Zmax.Should().Be(100.0);
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
