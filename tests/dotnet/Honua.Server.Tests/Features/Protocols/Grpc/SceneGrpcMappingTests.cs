// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Grpc;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Unit tests for the pure scene/elevation gRPC projection helpers.
/// </summary>
public sealed class SceneGrpcMappingTests
{
    [UnitTest]
    public void ToSceneMetadata_FromRecord_MapsCoreFieldsAndTilesetUrl()
    {
        var record = new SceneDatasetRecord
        {
            Id = "downtown",
            Name = "Downtown",
            Description = "A scene",
            AssetRoot = "/data/downtown",
            CreatedBy = "tester",
            Extent = new SceneExtent(-158.0, 21.0, -157.0, 22.0),
        };

        var metadata = SceneGrpcMapping.ToSceneMetadata(record);

        metadata.SceneId.Should().Be("downtown");
        metadata.Title.Should().Be("Downtown");
        metadata.Description.Should().Be("A scene");
        metadata.TilesetUrl.Should().Be("/scenes/downtown/tileset.json");
        metadata.Capabilities.Should().Contain("3d-tiles");
        metadata.Extent.Should().NotBeNull();
        metadata.InitialCamera.Should().NotBeNull();
    }

    [UnitTest]
    public void ToExtent3D_MapsEnvelopeAndDefaultsToWgs84()
    {
        var extent3d = SceneGrpcMapping.ToExtent3D(new SceneExtent(-10, -20, 30, 40));

        extent3d.Extent.Xmin.Should().Be(-10);
        extent3d.Extent.Ymin.Should().Be(-20);
        extent3d.Extent.Xmax.Should().Be(30);
        extent3d.Extent.Ymax.Should().Be(40);
        extent3d.Extent.SpatialReference.Wkid.Should().Be(4326);
    }

    [UnitTest]
    public void ToInitialCamera_CentersOverExtent()
    {
        var camera = SceneGrpcMapping.ToInitialCamera(new SceneExtent(-158, 21, -156, 23));

        camera.Longitude.Should().BeApproximately(-157, 1e-9);
        camera.Latitude.Should().BeApproximately(22, 1e-9);
        camera.Height.Should().BeGreaterThan(0);
        camera.Pitch.Should().Be(-45);
    }

    [Theory]
    [InlineData("newest", RasterMergeStrategy.Newest)]
    [InlineData("oldest", RasterMergeStrategy.Oldest)]
    [InlineData("average", RasterMergeStrategy.Average)]
    [InlineData("max", RasterMergeStrategy.Max)]
    [InlineData("min", RasterMergeStrategy.Min)]
    [InlineData("", RasterMergeStrategy.Newest)]
    [InlineData("unrecognized", RasterMergeStrategy.Newest)]
    [Trait("Category", "Unit")]
    public void ParseMergeStrategy_MapsKnownRulesAndDefaultsToNewest(string rule, RasterMergeStrategy expected)
    {
        SceneGrpcMapping.ParseMergeStrategy(rule).Should().Be(expected);
    }

    [UnitTest]
    public void FormatMergeStrategy_RoundTripsWithParse()
    {
        foreach (var strategy in new[]
        {
            RasterMergeStrategy.Newest,
            RasterMergeStrategy.Oldest,
            RasterMergeStrategy.Average,
            RasterMergeStrategy.Max,
            RasterMergeStrategy.Min,
        })
        {
            var formatted = SceneGrpcMapping.FormatMergeStrategy(strategy);
            SceneGrpcMapping.ParseMergeStrategy(formatted).Should().Be(strategy);
        }
    }
}
