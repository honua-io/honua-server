// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// End-to-end demo coverage for the NVIDIA construction fixture (#899)
/// against the v1 generation pipeline (#842). Locks the determinism,
/// feature-id traceability, attribute round-trip, bounding-region, and
/// flat-Z=0 warning behavior that the demo client (CesiumJS plus identify)
/// relies on. Hermetic — uses in-memory stubs, no Postgres or HTTP host.
/// </summary>
public sealed class ConstructionDemoGenerationTests : IDisposable
{
    private readonly string _outputRoot;
    private readonly StubFeatureSource _featureSource;
    private readonly StubRegistrationService _registration;
    private readonly TestMetadataV2GraphProvider _metadataProvider;
    private readonly SceneTilesPublishExecutor _executor;

    public ConstructionDemoGenerationTests()
    {
        _outputRoot = Path.Combine(Path.GetTempPath(), $"honua-scene-899-{Guid.NewGuid():N}");
        _featureSource = new StubFeatureSource { Features = ConstructionDemoFixture.Features };
        _registration = new StubRegistrationService();
        _metadataProvider = new TestMetadataV2GraphProvider(ConstructionDemoFixture.BuildMetadataGraph());

        var options = Options.Create(new SceneGenerationServerOptions
        {
            OutputRoot = _outputRoot,
            MaxFeatureCount = 50_000,
            GeneratorTag = "honua-3dtiles-generator/1.0"
        });
        var environment = new TestHostEnvironment();

        _executor = new SceneTilesPublishExecutor(
            _featureSource,
            _metadataProvider,
            environment,
            options,
            NullLogger<SceneTilesPublishExecutor>.Instance,
            _registration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    [UnitTest]
    public async Task Generate_ConstructionFixture_ProducesByteIdenticalOutput()
    {
        var first = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "construction-demo-a"), CancellationToken.None);
        var second = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "construction-demo-b"), CancellationToken.None);

        var glb1 = await File.ReadAllBytesAsync(Path.Combine(first.Result.AssetRoot, "tile_0000.glb"));
        var glb2 = await File.ReadAllBytesAsync(Path.Combine(second.Result.AssetRoot, "tile_0000.glb"));
        glb1.Should().Equal(glb2,
            "two runs of the construction fixture must produce byte-identical GLB output for CI golden checks.");

        var tileset1 = await File.ReadAllBytesAsync(Path.Combine(first.Result.AssetRoot, "tileset.json"));
        var tileset2 = await File.ReadAllBytesAsync(Path.Combine(second.Result.AssetRoot, "tileset.json"));
        tileset1.Should().Equal(tileset2,
            "two runs of the construction fixture must produce byte-identical tileset.json output.");
    }

    [UnitTest]
    public async Task Generate_ConstructionFixture_PreservesFeatureIds()
    {
        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "construction-demo-ids"), CancellationToken.None);

        var glb = await File.ReadAllBytesAsync(Path.Combine(outcome.Result.AssetRoot, "tile_0000.glb"));
        using var doc = JsonDocument.Parse(ExtractJsonChunk(glb));

        var properties = doc.RootElement
            .GetProperty("extensions").GetProperty("EXT_structural_metadata")
            .GetProperty("schema").GetProperty("classes")
            .GetProperty("honua_feature_class").GetProperty("properties");

        properties.TryGetProperty("objectid", out var objectIdSchema).Should().BeTrue(
            "objectid must round-trip into EXT_structural_metadata so the demo client can resolve a picked feature back to the Honua catalog row.");
        objectIdSchema.GetProperty("type").GetString().Should().Be("SCALAR");
        objectIdSchema.GetProperty("componentType").GetString().Should().Be("INT32");

        var primitive = doc.RootElement.GetProperty("meshes")[0]
            .GetProperty("primitives")[0];
        primitive.GetProperty("attributes").TryGetProperty("_FEATURE_ID_0", out _).Should().BeTrue(
            "EXT_mesh_features must surface the per-vertex feature id attribute for picking.");
        var featureIds = primitive.GetProperty("extensions").GetProperty("EXT_mesh_features")
            .GetProperty("featureIds")[0];
        featureIds.GetProperty("featureCount").GetInt32().Should().Be(
            ConstructionDemoFixture.Features.Count,
            "the property table count must match the fixture feature count.");

        var objectIdValues = ReadInt32Column(glb, "objectid");
        var expected = ConstructionDemoFixture.Features
            .Select(f => Convert.ToInt32(f.Attributes["objectid"], CultureInfo.InvariantCulture)).ToArray();
        objectIdValues.Should().Equal(expected,
            "objectid column values must mirror the fixture's stable feature ids in declaration order.");
    }

    [UnitTest]
    public async Task Generate_ConstructionFixture_AttributesRoundTrip()
    {
        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "construction-demo-attrs"), CancellationToken.None);

        var glb = await File.ReadAllBytesAsync(Path.Combine(outcome.Result.AssetRoot, "tile_0000.glb"));
        using var doc = JsonDocument.Parse(ExtractJsonChunk(glb));

        var properties = doc.RootElement
            .GetProperty("extensions").GetProperty("EXT_structural_metadata")
            .GetProperty("schema").GetProperty("classes")
            .GetProperty("honua_feature_class").GetProperty("properties");

        properties.TryGetProperty("name", out var nameSchema).Should().BeTrue();
        nameSchema.GetProperty("type").GetString().Should().Be("STRING");

        properties.TryGetProperty("phase", out var phaseSchema).Should().BeTrue();
        phaseSchema.GetProperty("type").GetString().Should().Be("STRING");

        properties.TryGetProperty("work_package_id", out var wpSchema).Should().BeTrue();
        wpSchema.GetProperty("type").GetString().Should().Be("STRING");

        properties.TryGetProperty("height_m", out var heightSchema).Should().BeTrue();
        heightSchema.GetProperty("type").GetString().Should().Be("SCALAR");
        heightSchema.GetProperty("componentType").GetString().Should().Be("FLOAT32");

        // Phase strings must round-trip exactly so the demo client can drive
        // its work-package legend off the picked feature's metadata column.
        var phaseValues = ReadStringColumn(glb, "phase");
        var expectedPhases = ConstructionDemoFixture.Features
            .Select(f => (string)f.Attributes["phase"]!).ToArray();
        phaseValues.Should().Equal(expectedPhases,
            "phase strings must round-trip in declaration order for client-side legend keying.");

        var workPackageValues = ReadStringColumn(glb, "work_package_id");
        var expectedWorkPackages = ConstructionDemoFixture.Features
            .Select(f => (string)f.Attributes["work_package_id"]!).ToArray();
        workPackageValues.Should().Equal(expectedWorkPackages,
            "work_package_id must round-trip so a picked building maps back to its Honua project work package.");
    }

    [UnitTest]
    public async Task Generate_ConstructionFixture_BoundingRegionCoversFootprints()
    {
        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "construction-demo-bounds"), CancellationToken.None);

        var bounds = outcome.Result.Summary.BoundingRegionDegrees;
        bounds.Should().HaveCount(4);
        bounds[0].Should().BeGreaterThanOrEqualTo(ConstructionDemoFixture.WestLongitude);
        bounds[1].Should().BeGreaterThanOrEqualTo(ConstructionDemoFixture.SouthLatitude);
        bounds[2].Should().BeLessThanOrEqualTo(ConstructionDemoFixture.EastLongitude);
        bounds[3].Should().BeLessThanOrEqualTo(ConstructionDemoFixture.NorthLatitude);

        // The tileset.json bounding region must enclose every fixture vertex
        // (radians). A region tighter than the fixture footprints would let
        // CesiumJS cull the tile prematurely and lose buildings on demo zoom.
        var tilesetJson = await File.ReadAllBytesAsync(Path.Combine(outcome.Result.AssetRoot, "tileset.json"));
        using var tilesetDoc = JsonDocument.Parse(tilesetJson);
        var region = tilesetDoc.RootElement.GetProperty("root")
            .GetProperty("boundingVolume").GetProperty("region");

        var westRad = region[0].GetDouble();
        var southRad = region[1].GetDouble();
        var eastRad = region[2].GetDouble();
        var northRad = region[3].GetDouble();
        var minHeight = region[4].GetDouble();
        var maxHeight = region[5].GetDouble();

        const double DegreesToRadians = Math.PI / 180.0;
        foreach (var feature in ConstructionDemoFixture.Features)
        {
            foreach (var vertex in feature.Geometry.Vertices)
            {
                var lonRad = vertex.Longitude * DegreesToRadians;
                var latRad = vertex.Latitude * DegreesToRadians;
                lonRad.Should().BeInRange(westRad, eastRad,
                    $"vertex ({vertex.Longitude}, {vertex.Latitude}) must lie inside the published region.");
                latRad.Should().BeInRange(southRad, northRad,
                    $"vertex ({vertex.Longitude}, {vertex.Latitude}) must lie inside the published region.");
            }
        }

        minHeight.Should().BeApproximately(0.0, 1e-6,
            "the fixture's prism base sits at Z=0 (no BaseHeightField configured).");
        maxHeight.Should().BeApproximately(
            ConstructionDemoFixture.MaxExtrusionHeightMeters,
            1e-6,
            "the bounding region must enclose the tallest extruded building (80 m).");
    }

    [UnitTest]
    public async Task Generate_ConstructionFixture_FlatInputWithoutExtrusionEmitsWarning()
    {
        // Stripping extrusion from the Metadata v2 resource models the "footprints only,
        // no heights" path. Generation must still succeed but surface the documented
        // flat-Z=0 warning so operators know the demo lost vertical fidelity.
        _metadataProvider.SetGraph(ConstructionDemoFixture.BuildMetadataGraph(includeExtrusion: false));
        // Drop the per-vertex Z values so SawAnyHeight stays false; the
        // fixture vertices already sit at Z=0 but they are non-null and would
        // otherwise mask the warning.
        _featureSource.Features = ConstructionDemoFixture.Features
            .Select(f => f with
            {
                Geometry = new SceneFeatureGeometry
                {
                    Kind = f.Geometry.Kind,
                    Vertices = f.Geometry.Vertices
                        .Select(v => new SceneVertex(v.Longitude, v.Latitude, null))
                        .ToArray()
                }
            })
            .ToArray();

        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "construction-demo-flat"), CancellationToken.None);

        outcome.Result.Warnings.Should().Contain(
            "Layer has no Z values and no extrusion configured; output is flat at Z=0.",
            "the executor must surface the documented flat warning when extrusion is stripped from the demo fixture.");
    }

    private static PublishIntent BuildIntent(string sceneId)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SceneTilesPublishExecutor.TargetConfigSceneId] = sceneId
        };
        return PublishIntent.CreateDraft(
            intentId: Guid.NewGuid().ToString("N"),
            sourceKind: PublishSourceKind.FeatureLayer,
            sourceId: ConstructionDemoFixture.LayerId.ToString(CultureInfo.InvariantCulture),
            targetKind: PublishTargetKind.SceneService,
            targetConfig: config);
    }

    private static string ExtractJsonChunk(byte[] glb)
    {
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        return Encoding.UTF8.GetString(glb.AsSpan(20, jsonLength)).TrimEnd('\0', ' ');
    }

    private static byte[] ExtractBinChunk(byte[] glb)
    {
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        var binChunkOffset = 20 + jsonLength;
        var binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binChunkOffset, 4));
        return glb.AsSpan(binChunkOffset + 8, binLength).ToArray();
    }

    private static int[] ReadInt32Column(byte[] glb, string propertyId)
    {
        using var doc = JsonDocument.Parse(ExtractJsonChunk(glb));
        var viewIndex = ResolveValuesBufferView(doc, propertyId);
        var view = doc.RootElement.GetProperty("bufferViews")[viewIndex];
        var byteOffset = view.GetProperty("byteOffset").GetInt32();
        var byteLength = view.GetProperty("byteLength").GetInt32();
        var bin = ExtractBinChunk(glb);
        var count = byteLength / sizeof(int);
        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(bin.AsSpan(byteOffset + i * 4, 4));
        }
        return values;
    }

    private static string[] ReadStringColumn(byte[] glb, string propertyId)
    {
        using var doc = JsonDocument.Parse(ExtractJsonChunk(glb));
        var propertyTable = doc.RootElement.GetProperty("extensions")
            .GetProperty("EXT_structural_metadata")
            .GetProperty("propertyTables")[0];
        var count = propertyTable.GetProperty("count").GetInt32();
        var property = propertyTable.GetProperty("properties").GetProperty(propertyId);
        var valuesViewIndex = property.GetProperty("values").GetInt32();
        var offsetsViewIndex = property.GetProperty("stringOffsets").GetInt32();
        var bufferViews = doc.RootElement.GetProperty("bufferViews");
        var valuesView = bufferViews[valuesViewIndex];
        var offsetsView = bufferViews[offsetsViewIndex];
        var valuesOffset = valuesView.GetProperty("byteOffset").GetInt32();
        var offsetsOffset = offsetsView.GetProperty("byteOffset").GetInt32();
        var bin = ExtractBinChunk(glb);
        var values = new string[count];
        for (var i = 0; i < count; i++)
        {
            var startOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                bin.AsSpan(offsetsOffset + i * 4, 4));
            var endOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                bin.AsSpan(offsetsOffset + (i + 1) * 4, 4));
            values[i] = Encoding.UTF8.GetString(
                bin.AsSpan(valuesOffset + startOffset, endOffset - startOffset));
        }
        return values;
    }

    private static int ResolveValuesBufferView(JsonDocument doc, string propertyId)
    {
        return doc.RootElement.GetProperty("extensions")
            .GetProperty("EXT_structural_metadata")
            .GetProperty("propertyTables")[0]
            .GetProperty("properties").GetProperty(propertyId)
            .GetProperty("values").GetInt32();
    }

}
