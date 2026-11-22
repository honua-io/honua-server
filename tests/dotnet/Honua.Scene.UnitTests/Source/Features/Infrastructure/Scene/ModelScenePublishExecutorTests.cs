// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Infrastructure.Scene;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class ModelScenePublishExecutorTests : IDisposable
{
    private const string SampleCubeObj = """
        # Simple georeferenced test box
        v -5 0 -5
        v 5 0 -5
        v 5 0 5
        v -5 0 5
        v -5 10 -5
        v 5 10 -5
        v 5 10 5
        v -5 10 5
        vt 0 0
        vt 1 0
        vt 1 1
        vt 0 1
        f 1/1 2/2 3/3 4/4
        f 5/1 8/2 7/3 6/4
        f 1/1 5/2 6/3 2/4
        f 2/1 6/2 7/3 3/4
        f 3/1 7/2 8/3 4/4
        f 4/1 8/2 5/3 1/4
        """;

    private readonly string _outputRoot;
    private readonly StubRegistrationService _registration;
    private readonly ModelScenePublishExecutor _executor;

    public ModelScenePublishExecutorTests()
    {
        _outputRoot = Path.Join(Path.GetTempPath(), $"honua-model-{Guid.NewGuid():N}");
        _registration = new StubRegistrationService();
        var options = Options.Create(new SceneGenerationServerOptions
        {
            OutputRoot = _outputRoot,
            GeneratorTag = "honua-test-generator/1.0",
        });

        _executor = new ModelScenePublishExecutor(
            new TestHostEnvironment(),
            options,
            NullLogger<ModelScenePublishExecutor>.Instance,
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
    public void ObjToGlbConverter_QuadMeshWithUvs_ProducesValidGlb()
    {
        var result = ObjToGlbConverter.Convert(Encoding.UTF8.GetBytes(SampleCubeObj), "honua-test");

        result.TriangleCount.Should().Be(12);
        result.VertexCount.Should().Be(36);
        result.Bounds.MinX.Should().Be(-5);
        result.Bounds.MaxX.Should().Be(5);
        result.Bounds.MinY.Should().Be(0);
        result.Bounds.MaxY.Should().Be(10);

        result.GlbBytes.AsSpan(0, 4).SequenceEqual("glTF"u8).Should().BeTrue();
        BinaryPrimitives.ReadUInt32LittleEndian(result.GlbBytes.AsSpan(4)).Should().Be(2u);
        BinaryPrimitives.ReadUInt32LittleEndian(result.GlbBytes.AsSpan(8)).Should().Be((uint)result.GlbBytes.Length);
    }

    [UnitTest]
    public void Convert_ConcaveObjFace_RequiresTriangulation()
    {
        var bytes = """
            v 0 0 0
            v 3 0 0
            v 3 3 0
            v 2 3 0
            v 2 1 0
            v 1 1 0
            v 1 3 0
            v 0 3 0
            f 1 2 3 4 5 6 7 8
            """u8.ToArray();

        var convert = () => ObjToGlbConverter.Convert(bytes);

        convert.Should().Throw<ValidationException>().WithMessage("*triangulated before upload*");
    }

    [UnitTest]
    public async Task IngestAsync_ObjWithPlacement_ProducesValidated3DTiles11Scene()
    {
        var placement = new ModelPlacement(
            Longitude: -157.8581,
            Latitude: 21.3069,
            Height: 15.0,
            HeadingDegrees: 30.0,
            ScaleX: 2.0,
            ScaleY: 2.0,
            ScaleZ: 2.0);

        var outcome = await _executor.IngestAsync(
            new ModelSceneIngestRequest(
                Encoding.UTF8.GetBytes(SampleCubeObj),
                "tower.obj",
                placement,
                SceneId: "obj-tower",
                DisplayName: "OBJ Tower"),
            CancellationToken.None);

        outcome.SceneId.Should().Be("obj-tower");
        outcome.SourceFormat.Should().Be("obj");
        outcome.BoundsDegrees[0].Should().BeLessThan(-157.8581);
        outcome.BoundsDegrees[2].Should().BeGreaterThan(-157.8581);
        outcome.MinHeightMeters.Should().BeApproximately(15.0, 0.5);
        outcome.MaxHeightMeters.Should().BeApproximately(35.0, 0.5);

        var tilesetPath = Path.Join(outcome.AssetRoot, "tileset.json");
        var modelPath = Path.Join(outcome.AssetRoot, "model_0000.glb");
        File.Exists(tilesetPath).Should().BeTrue();
        File.Exists(modelPath).Should().BeTrue();

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(tilesetPath));
        doc.RootElement.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
        var child = doc.RootElement.GetProperty("root").GetProperty("children")[0];
        child.GetProperty("transform").GetArrayLength().Should().Be(16);
        child.GetProperty("content").GetProperty("uri").GetString().Should().Be("model_0000.glb");

        _registration.Records.Should().ContainSingle();
        _registration.Records[0].Id.Should().Be("obj-tower");
        _registration.Records[0].Crs.Should().Be("EPSG:4979");
    }

    [UnitTest]
    public async Task IngestAsync_GlbModel_PublishesImmutableScene()
    {
        var glb = ObjToGlbConverter.Convert(Encoding.UTF8.GetBytes(SampleCubeObj)).GlbBytes;
        var outcome = await _executor.IngestAsync(
            new ModelSceneIngestRequest(
                glb,
                "building.glb",
                new ModelPlacement(-122.4194, 37.7749, 5.0),
                SceneId: "glb-building"),
            CancellationToken.None);

        outcome.SourceFormat.Should().Be("glb");
        File.Exists(Path.Join(outcome.AssetRoot, "model_0000.glb")).Should().BeTrue();
        _registration.Records.Should().ContainSingle(r => r.Id == "glb-building");
    }

    [UnitTest]
    public async Task IngestAsync_EmbeddedGltfModel_ConvertsToSelfContainedGlb()
    {
        var gltfBytes = BuildEmbeddedGltf();
        var outcome = await _executor.IngestAsync(
            new ModelSceneIngestRequest(
                gltfBytes,
                "embedded.gltf",
                new ModelPlacement(-122.4194, 37.7749, 10.0),
                SceneId: "gltf-embedded"),
            CancellationToken.None);

        outcome.SourceFormat.Should().Be("gltf");
        var writtenGlb = await File.ReadAllBytesAsync(Path.Join(outcome.AssetRoot, "model_0000.glb"));
        writtenGlb.AsSpan(0, 4).SequenceEqual("glTF"u8).Should().BeTrue();
    }

    [Theory]
    [InlineData("\"external.bin\"")]
    [InlineData("42")]
    public async Task IngestAsync_GltfWithInvalidBufferUri_RejectsWithModelAssetInvalid(string uriJson)
    {
        var externalGltf = Encoding.UTF8.GetBytes($$"""
            {
              "asset": { "version": "2.0" },
              "buffers": [ { "uri": {{uriJson}}, "byteLength": 36 } ]
            }
            """);

        var act = async () => await _executor.IngestAsync(
            new ModelSceneIngestRequest(
                externalGltf,
                "external.gltf",
                new ModelPlacement(-122.4, 37.7),
                SceneId: "external-gltf"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.ModelAssetInvalid);
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task IngestAsync_InvalidPlacementCoordinates_ThrowsOptionsInvalid()
    {
        var act = async () => await _executor.IngestAsync(
            new ModelSceneIngestRequest(
                Encoding.UTF8.GetBytes(SampleCubeObj),
                "bad-place.obj",
                new ModelPlacement(Longitude: 250.0, Latitude: 21.0),
                SceneId: "bad-place"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
    }

    private static byte[] BuildEmbeddedGltf()
    {
        var positions = new float[] { 0, 0, 0, 10, 0, 0, 0, 10, 0 };
        var bin = new byte[positions.Length * 4];
        for (var i = 0; i < positions.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 4, 4), positions[i]);
        }

        var dataUri = "data:application/octet-stream;base64," + Convert.ToBase64String(bin);
        var json = $$"""
            {
              "asset": { "version": "2.0" },
              "scene": 0,
              "scenes": [ { "nodes": [0] } ],
              "nodes": [ { "mesh": 0 } ],
              "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0 }, "mode": 4 } ] } ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0,0,0], "max": [10,10,0] }
              ],
              "bufferViews": [ { "buffer": 0, "byteOffset": 0, "byteLength": 36 } ],
              "buffers": [ { "uri": "{{dataUri}}", "byteLength": 36 } ]
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }
}
