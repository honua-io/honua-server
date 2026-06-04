// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.Bim;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.Bim;

/// <summary>
/// End-to-end unit tests for the CityGML -> 3D Tiles Building Scene Layer
/// pipeline (#1207): reader -> BSL builder -> tileset + GLB with per-feature
/// BSL attributes carried through EXT_structural_metadata.
/// </summary>
public sealed class BuildingSceneLayerBuilderTests
{
    // Treat CityGML X/Y as longitude/latitude degrees directly so the test does
    // not depend on a projected-CRS transform.
    private static (double, double, double) IdentityGeo(double x, double y, double z) => (x, y, z);

    [UnitTest]
    public void Build_FixtureBuilding_ProducesServableTileset()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());

        var result = BuildingSceneLayerBuilder.Build(model, IdentityGeo, "honua-test");

        result.BuildingCount.Should().Be(1);
        result.SurfaceCount.Should().Be(4);
        result.Disciplines.Should().BeEquivalentTo(["Architectural", "Structural"]);
        result.Tiles.Should().ContainKey("building_0000.glb");

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(result.TilesetJsonBytes));
        json.RootElement.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
        var uri = json.RootElement.GetProperty("root")
            .GetProperty("children")[0].GetProperty("content").GetProperty("uri").GetString();
        uri.Should().Be("building_0000.glb");
        result.Tiles.Should().ContainKey(uri!);
    }

    [UnitTest]
    public void Build_Glb_CarriesBslAttributeColumnsInStructuralMetadata()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());
        var result = BuildingSceneLayerBuilder.Build(model, IdentityGeo, "honua-test");

        var schema = ExtractStructuralMetadataSchema(result.Tiles["building_0000.glb"]);
        var properties = schema.GetProperty("classes")
            .GetProperty("honua_feature_class")
            .GetProperty("properties");

        // Fixed BSL columns.
        properties.TryGetProperty(BuildingSceneLayerBuilder.AttributeBuildingId, out _).Should().BeTrue();
        properties.TryGetProperty(BuildingSceneLayerBuilder.AttributeStoreyId, out _).Should().BeTrue();
        properties.TryGetProperty(BuildingSceneLayerBuilder.AttributeSurfaceType, out _).Should().BeTrue();
        properties.TryGetProperty(BuildingSceneLayerBuilder.AttributeDiscipline, out _).Should().BeTrue();
        properties.TryGetProperty(BuildingSceneLayerBuilder.AttributeComponentId, out _).Should().BeTrue();

        // Discovered generic attributes are surfaced as attr_-prefixed columns.
        properties.TryGetProperty("attr_usage", out _).Should().BeTrue();
        properties.TryGetProperty("attr_material", out _).Should().BeTrue();
    }

    [UnitTest]
    public void Build_FeatureCount_MatchesBoundarySurfaceCount()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());
        var result = BuildingSceneLayerBuilder.Build(model, IdentityGeo);

        var propertyTable = ExtractFirstPropertyTable(result.Tiles["building_0000.glb"]);
        propertyTable.GetProperty("count").GetInt32().Should().Be(result.SurfaceCount);
    }

    [UnitTest]
    public void Build_IsDeterministic_ByteIdenticalAcrossRuns()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());

        var a = BuildingSceneLayerBuilder.Build(model, IdentityGeo, "honua");
        var b = BuildingSceneLayerBuilder.Build(model, IdentityGeo, "honua");

        a.TilesetJsonBytes.Should().Equal(b.TilesetJsonBytes);
        a.Tiles["building_0000.glb"].Should().Equal(b.Tiles["building_0000.glb"]);
    }

    [UnitTest]
    public void Build_GlbHeader_IsValidGlb2()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());
        var result = BuildingSceneLayerBuilder.Build(model, IdentityGeo);

        var glb = result.Tiles["building_0000.glb"];
        glb.Length.Should().BeGreaterThan(12);
        Encoding.ASCII.GetString(glb, 0, 4).Should().Be("glTF");
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4)).Should().Be(2u);
    }

    [UnitTest]
    public void Build_BoundsDegrees_EncloseTheBuilding()
    {
        var model = CityGmlReader.Read(CityGmlFixtures.SingleBuilding());
        var result = BuildingSceneLayerBuilder.Build(model, IdentityGeo);

        result.BoundsDegrees.Should().Equal(0.0, 0.0, 0.001, 0.001);
    }

    // ---- GLB parsing helpers (JSON chunk only) ----

    private static JsonDocument ParseGlbJsonChunk(byte[] glb)
    {
        // GLB: 12-byte header, then chunk header (4 length + 4 type) + chunk body.
        var jsonLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        var json = Encoding.UTF8.GetString(glb, 20, jsonLen).TrimEnd('\0', ' ');
        return JsonDocument.Parse(json);
    }

    private static JsonElement ExtractStructuralMetadataSchema(byte[] glb)
    {
        using var doc = ParseGlbJsonChunk(glb);
        return doc.RootElement
            .GetProperty("extensions")
            .GetProperty("EXT_structural_metadata")
            .GetProperty("schema")
            .Clone();
    }

    private static JsonElement ExtractFirstPropertyTable(byte[] glb)
    {
        using var doc = ParseGlbJsonChunk(glb);
        return doc.RootElement
            .GetProperty("extensions")
            .GetProperty("EXT_structural_metadata")
            .GetProperty("propertyTables")[0]
            .Clone();
    }
}
