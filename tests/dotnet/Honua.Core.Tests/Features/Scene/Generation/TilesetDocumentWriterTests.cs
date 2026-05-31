// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Generation;

/// <summary>
/// Unit tests for the deterministic <c>tileset.json</c> writer.
/// </summary>
public sealed class TilesetDocumentWriterTests
{
    [UnitTest]
    public void Build_ProducesOgc31TilesetWithRegionAndChild()
    {
        var doc = TilesetDocumentWriter.Build(
            boundingRegionDegrees: [-122.5, 37.7, -122.4, 37.8],
            minHeightMeters: 0.0,
            maxHeightMeters: 50.0,
            geometricError: 1234.5,
            tileContentUris: ["tile_0000.glb"],
            generatorTag: "honua-test");

        doc.Asset.Version.Should().Be("1.1");
        doc.Asset.Generator.Should().Be("honua-test");
        doc.GeometricError.Should().Be(1234.5);
        doc.Root.Refine.Should().Be("ADD");
        doc.Root.BoundingVolume.Region.Should().HaveCount(6);
        doc.Root.Children.Should().NotBeNull();
        doc.Root.Children!.Single().Content!.Uri.Should().Be("tile_0000.glb");
    }

    [UnitTest]
    public void Build_ConvertsRegionDegreesToRadians()
    {
        var doc = TilesetDocumentWriter.Build(
            boundingRegionDegrees: [-180.0, -90.0, 180.0, 90.0],
            minHeightMeters: -10.0,
            maxHeightMeters: 10.0,
            geometricError: 1.0,
            tileContentUris: ["a.glb"]);

        doc.Root.BoundingVolume.Region![0].Should().BeApproximately(-Math.PI, 1e-12);
        doc.Root.BoundingVolume.Region![1].Should().BeApproximately(-Math.PI / 2, 1e-12);
        doc.Root.BoundingVolume.Region![2].Should().BeApproximately(Math.PI, 1e-12);
        doc.Root.BoundingVolume.Region![3].Should().BeApproximately(Math.PI / 2, 1e-12);
        doc.Root.BoundingVolume.Region![4].Should().Be(-10.0);
        doc.Root.BoundingVolume.Region![5].Should().Be(10.0);
    }

    [UnitTest]
    public void Serialize_IsByteIdenticalAcrossRuns()
    {
        var bounds = new[] { -122.5, 37.7, -122.4, 37.8 };
        var doc1 = TilesetDocumentWriter.Build(bounds, 0.0, 12.0, 100.0, ["tile_0000.glb"], "honua");
        var doc2 = TilesetDocumentWriter.Build(bounds, 0.0, 12.0, 100.0, ["tile_0000.glb"], "honua");

        var bytes1 = TilesetDocumentWriter.Serialize(doc1);
        var bytes2 = TilesetDocumentWriter.Serialize(doc2);

        bytes1.Should().Equal(bytes2);
    }

    [UnitTest]
    public void Serialize_ProducesValidJsonWithExpectedTopLevelKeys()
    {
        var doc = TilesetDocumentWriter.Build(
            [-1, -1, 1, 1], 0.0, 1.0, 100.0, ["tile_0000.glb"], "honua");
        var bytes = TilesetDocumentWriter.Serialize(doc);

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        json.RootElement.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
        json.RootElement.GetProperty("geometricError").GetDouble().Should().Be(100.0);
        json.RootElement.GetProperty("root").GetProperty("refine").GetString().Should().Be("ADD");
        json.RootElement.GetProperty("root").GetProperty("children").GetArrayLength().Should().Be(1);
    }

    [UnitTest]
    public void Build_NoStyleReference_OmitsExtras()
    {
        var doc = TilesetDocumentWriter.Build(
            [-1, -1, 1, 1], 0.0, 1.0, 100.0, ["tile_0000.glb"], "honua");

        doc.Extras.Should().BeNull();

        var bytes = TilesetDocumentWriter.Serialize(doc);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        json.RootElement.TryGetProperty("extras", out _).Should().BeFalse(
            "a tileset without symbology must not emit an empty extras block.");
    }

    [UnitTest]
    public void Build_WithStyleReference_AdvertisesStyleMetadataContract()
    {
        var styleRef = new TilesetStyleReference
        {
            Encoding = "3d-tiles-styling",
            Version = "1.0",
            Uri = "style.json"
        };

        var doc = TilesetDocumentWriter.Build(
            [-1, -1, 1, 1], 0.0, 1.0, 100.0, ["tile_0000.glb"], "honua", styleRef);

        var bytes = TilesetDocumentWriter.Serialize(doc);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var style = json.RootElement.GetProperty("extras").GetProperty("honua_style");
        style.GetProperty("encoding").GetString().Should().Be("3d-tiles-styling");
        style.GetProperty("version").GetString().Should().Be("1.0");
        style.GetProperty("uri").GetString().Should().Be("style.json");
    }
}
