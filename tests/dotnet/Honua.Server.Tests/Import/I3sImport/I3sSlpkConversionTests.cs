// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Import.Features.I3sImport;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Import.I3sSlpk;

/// <summary>
/// End-to-end conversion tests for the Esri I3S/.slpk → 3D Tiles importer
/// (#1268). Synthesizes a minimal in-memory .slpk archive so the test is
/// hermetic, then verifies the converter produces a valid tileset hierarchy
/// and one GLB per geometry-bearing node.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class I3sSlpkConversionTests
{
    private const double SampleLongitude = -122.4194;
    private const double SampleLatitude = 37.7749;
    private const double SampleHeight = 50.0;
    private const double SampleMbsRadius = 25.0;

    [UnitTest]
    public void GeometryConverter_DecodesPerAttributeArray_ToEcefGlb()
    {
        var schema = BuildTriangleSchema();
        var geometryBuffer = BuildSimpleTriangleBuffer();

        var glb = I3sGeometryConverter.BuildGlbFromI3sGeometry(
            geometryBuffer,
            schema,
            SampleLongitude,
            SampleLatitude,
            SampleHeight,
            generatorTag: "honua-test");

        glb.Should().NotBeNullOrEmpty();
        BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0, 4)).Should().Be(0x46546C67u, "GLB header magic must be 'glTF'.");
        BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4)).Should().Be(2u, "GLB version must be 2.");
        BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8, 4)).Should().Be((uint)glb.Length, "GLB total length field must match the buffer length.");

        var (_, jsonChunk) = ReadFirstChunk(glb, 12);
        var jsonText = Encoding.UTF8.GetString(jsonChunk.TrimEnd((byte)0x20));
        using var json = JsonDocument.Parse(jsonText);
        json.RootElement.GetProperty("asset").GetProperty("version").GetString().Should().Be("2.0");
        json.RootElement.GetProperty("asset").GetProperty("generator").GetString().Should().Be("honua-test");
        json.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes")
            .GetProperty("POSITION").GetInt32().Should().Be(0);
        var positionAccessor = json.RootElement.GetProperty("accessors")[0];
        positionAccessor.GetProperty("count").GetInt32().Should().Be(3);
        positionAccessor.GetProperty("type").GetString().Should().Be("VEC3");
    }

    [UnitTest]
    public void GeometryConverter_RejectsIndexedTopology()
    {
        var schema = BuildTriangleSchema() with { Topology = "Indexed" };
        var geometryBuffer = BuildSimpleTriangleBuffer();

        var act = () => I3sGeometryConverter.BuildGlbFromI3sGeometry(
            geometryBuffer, schema, SampleLongitude, SampleLatitude, SampleHeight);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*PerAttributeArray*");
    }

    [UnitTest]
    public async Task ImportService_ConvertsMinimalSlpkAndRegistersScene()
    {
        // arrange — synthesize a hermetic .slpk on disk and a stub registration service.
        var tempDir = Directory.CreateTempSubdirectory("honua-i3s-import-test-");
        var outputBase = Path.Combine(tempDir.FullName, "scenes");
        try
        {
            var slpkPath = Path.Combine(tempDir.FullName, "sample.slpk");
            WriteMinimalSlpk(slpkPath);

            var registration = new StubSceneRegistrationService();
            var options = Microsoft.Extensions.Options.Options.Create(new I3sImportOptions
            {
                AssetRootBase = outputBase,
                AllowExplicitAssetRoot = false
            });
            var service = new I3sSlpkImportService(
                registration,
                options,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<I3sSlpkImportService>.Instance);

            // act
            var result = await service.ImportAsync(
                new I3sSlpkImportRequest { SlpkPath = slpkPath, DatasetId = "sample-i3s" },
                createdBy: "test",
                CancellationToken.None);

            // assert — service registers the scene and writes the expected on-disk layout.
            result.Success.Should().BeTrue(result.ErrorMessage);
            result.NodeCount.Should().Be(1);
            result.TileBytesWritten.Should().BeGreaterThan(0);
            result.AssetRoot.Should().Be(Path.Combine(outputBase, "sample-i3s"));

            registration.RegisteredRecord.Should().NotBeNull();
            registration.RegisteredRecord!.Id.Should().Be("sample-i3s");
            registration.RegisteredRecord.Crs.Should().Be("EPSG:4979");
            registration.RegisteredRecord.DatasetType.Should().Be(SceneDatasetType.HostedTiles);

            var tilesetPath = Path.Combine(result.AssetRoot!, "tileset.json");
            File.Exists(tilesetPath).Should().BeTrue("tileset.json must be written under the asset root.");
            var tileset = JsonSerializer.Deserialize(
                await File.ReadAllBytesAsync(tilesetPath),
                TilesetJsonContext.Default.TilesetDocument)!;
            tileset.Asset.Version.Should().Be("1.1");
            tileset.Root.Refine.Should().Be("REPLACE");
            tileset.Root.BoundingVolume.Sphere.Should().NotBeNull();
            tileset.Root.BoundingVolume.Sphere!.Should().HaveCount(4);
            tileset.Root.Content!.Uri.Should().Be("nodes/0.glb");

            var glbPath = Path.Combine(result.AssetRoot!, "nodes", "0.glb");
            File.Exists(glbPath).Should().BeTrue("the converted node GLB must be written under nodes/.");
            var glbBytes = await File.ReadAllBytesAsync(glbPath);
            BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.AsSpan(0, 4))
                .Should().Be(0x46546C67u, "GLB magic must be 'glTF'.");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [UnitTest]
    public async Task ImportService_RejectsNonWgs84SpatialReference()
    {
        var tempDir = Directory.CreateTempSubdirectory("honua-i3s-import-bad-crs-");
        try
        {
            var slpkPath = Path.Combine(tempDir.FullName, "bad-crs.slpk");
            WriteMinimalSlpk(slpkPath, wkid: 3857, latestWkid: 3857);

            var service = new I3sSlpkImportService(
                new StubSceneRegistrationService(),
                Microsoft.Extensions.Options.Options.Create(new I3sImportOptions
                {
                    AssetRootBase = Path.Combine(tempDir.FullName, "scenes"),
                    AllowExplicitAssetRoot = false
                }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<I3sSlpkImportService>.Instance);

            var result = await service.ImportAsync(
                new I3sSlpkImportRequest { SlpkPath = slpkPath, DatasetId = "bad-crs" },
                createdBy: "test",
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("WGS-84");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [UnitTest]
    public async Task ImportService_RejectsUnsupportedLayerType()
    {
        var tempDir = Directory.CreateTempSubdirectory("honua-i3s-import-bad-layer-");
        try
        {
            var slpkPath = Path.Combine(tempDir.FullName, "bad-layer.slpk");
            WriteMinimalSlpk(slpkPath, layerType: "PointCloud");

            var service = new I3sSlpkImportService(
                new StubSceneRegistrationService(),
                Microsoft.Extensions.Options.Options.Create(new I3sImportOptions
                {
                    AssetRootBase = Path.Combine(tempDir.FullName, "scenes"),
                    AllowExplicitAssetRoot = false
                }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<I3sSlpkImportService>.Instance);

            var result = await service.ImportAsync(
                new I3sSlpkImportRequest { SlpkPath = slpkPath, DatasetId = "bad-layer" },
                createdBy: "test",
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("3D Object");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [UnitTest]
    public async Task ImportService_DryRunDoesNotWriteFilesOrRegister()
    {
        var tempDir = Directory.CreateTempSubdirectory("honua-i3s-import-dryrun-");
        var outputBase = Path.Combine(tempDir.FullName, "scenes");
        try
        {
            var slpkPath = Path.Combine(tempDir.FullName, "dryrun.slpk");
            WriteMinimalSlpk(slpkPath);

            var registration = new StubSceneRegistrationService();
            var service = new I3sSlpkImportService(
                registration,
                Microsoft.Extensions.Options.Options.Create(new I3sImportOptions
                {
                    AssetRootBase = outputBase,
                    AllowExplicitAssetRoot = false
                }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<I3sSlpkImportService>.Instance);

            var result = await service.ImportAsync(
                new I3sSlpkImportRequest { SlpkPath = slpkPath, DatasetId = "dryrun", DryRun = true },
                createdBy: "test",
                CancellationToken.None);

            result.Success.Should().BeTrue(result.ErrorMessage);
            registration.RegisteredRecord.Should().BeNull("dry run must not register a scene dataset.");
            Directory.Exists(outputBase).Should().BeFalse("dry run must not create any output files.");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static (int ChunkLength, byte[] ChunkBytes) ReadFirstChunk(byte[] glb, int headerOffset)
    {
        var chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(headerOffset, 4));
        var chunkBytes = new byte[chunkLength];
        Array.Copy(glb, headerOffset + 8, chunkBytes, 0, chunkLength);
        return (chunkLength, chunkBytes);
    }

    private static I3sGeometrySchema BuildTriangleSchema() => new()
    {
        GeometryType = "triangles",
        Topology = "PerAttributeArray",
        Header = [new() { Property = "vertexCount", Type = "UInt32" }],
        Ordering = ["position"],
        VertexAttributes = new Dictionary<string, I3sVertexAttribute>
        {
            ["position"] = new() { ValueType = "Float32", ValuesPerElement = 3 }
        }
    };

    private static byte[] BuildSimpleTriangleBuffer()
    {
        // One triangle: three FLOAT32 VEC3 positions in the node-local ENU frame.
        // The values themselves are irrelevant for the codec test — we assert the
        // GLB shape, not the world placement.
        var buffer = new byte[4 + 3 * 3 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 3u);

        var floats = new[] { 0f, 0f, 0f, 10f, 0f, 0f, 0f, 10f, 0f };
        for (var i = 0; i < floats.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4 + i * 4, 4), floats[i]);
        }
        return buffer;
    }

    private static void WriteMinimalSlpk(string path, int wkid = 4326, int latestWkid = 4326, string layerType = "3DObject")
    {
        using var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        AddJsonEntry(archive, "3dSceneLayer.json", BuildSceneLayerJson(wkid, latestWkid, layerType));
        AddJsonEntry(archive, "nodepages/0.json", BuildNodePageJson());
        AddBinaryEntry(archive, "nodes/0/geometries/0.bin", BuildSimpleTriangleBuffer());
    }

    private static string BuildSceneLayerJson(int wkid, int latestWkid, string layerType) => $$"""
        {
          "layerType": "{{layerType}}",
          "name": "Sample I3S Scene",
          "description": "Synthetic fixture for Honua import tests.",
          "spatialReference": { "wkid": {{wkid}}, "latestWkid": {{latestWkid}}, "vcsWkid": 5703 },
          "fullExtent": { "xmin": -122.5, "ymin": 37.7, "xmax": -122.4, "ymax": 37.8, "zmin": 0, "zmax": 100 },
          "store": {
            "profile": "meshpyramids",
            "nodePages": { "nodesPerPage": 64, "lodSelectionMetricType": "maxScreenThresholdSQ" },
            "defaultGeometrySchema": {
              "geometryType": "triangles",
              "topology": "PerAttributeArray",
              "header": [ { "property": "vertexCount", "type": "UInt32" } ],
              "ordering": [ "position" ],
              "vertexAttributes": {
                "position": { "valueType": "Float32", "valuesPerElement": 3 }
              }
            }
          }
        }
        """;

    private static string BuildNodePageJson() => $$"""
        {
          "nodes": [
            {
              "index": 0,
              "lodThreshold": 1000,
              "mbs": [ {{SampleLongitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{SampleLatitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{SampleHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{SampleMbsRadius.ToString(System.Globalization.CultureInfo.InvariantCulture)}} ],
              "mesh": { "geometry": { "resource": 0 } }
            }
          ]
        }
        """;

    private static void AddJsonEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void AddBinaryEntry(ZipArchive archive, string entryName, byte[] content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private sealed class StubSceneRegistrationService : Honua.Core.Features.Scene.Abstractions.ISceneRegistrationService
    {
        public SceneDatasetRecord? RegisteredRecord { get; private set; }

        public Task<SceneDatasetRecord> RegisterAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
        {
            RegisteredRecord = record;
            return Task.FromResult(record);
        }

        public Task<SceneDatasetRecord?> GetAsync(Guid datasetId, CancellationToken cancellationToken = default) => Task.FromResult<SceneDatasetRecord?>(null);
        public Task<SceneDatasetRecord?> GetBySceneIdAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<SceneDatasetRecord?>(null);
        public Task<IReadOnlyList<SceneDatasetRecord>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SceneDatasetRecord>>(Array.Empty<SceneDatasetRecord>());
        public Task<SceneDatasetRecord> UpdateAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default) => Task.FromResult(record);
        public Task<bool> DeactivateAsync(Guid datasetId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
