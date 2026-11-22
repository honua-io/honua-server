// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Infrastructure.Scene;
using Honua.Scene.Assets;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class SceneRoundTripAndI3sConformanceTests : IDisposable
{
    private readonly string _outputRoot;
    private readonly StubRegistrationService _registration;
    private readonly IOptions<SceneGenerationServerOptions> _options;
    private readonly HostedI3sSceneResourceProvider _i3sProvider;

    public SceneRoundTripAndI3sConformanceTests()
    {
        _outputRoot = Path.Join(Path.GetTempPath(), $"honua-rt-{Guid.NewGuid():N}");
        _registration = new StubRegistrationService();
        _options = Options.Create(new SceneGenerationServerOptions
        {
            OutputRoot = _outputRoot,
            GeneratorTag = "honua-roundtrip/1.0",
        });
        _i3sProvider = new HostedI3sSceneResourceProvider(TimeProvider.System);
    }

    public void Dispose()
    {
        _i3sProvider.Dispose();
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    [UnitTest]
    public async Task RoundTrip_CityGmlIngest_Serves3DTilesAndConformantI3sRootGeometry()
    {
        var executor = new CityGmlScenePublishExecutor(
            new TestHostEnvironment(), _options, NullLogger<CityGmlScenePublishExecutor>.Instance, _registration);

        var outcome = await executor.IngestAsync(
            new CityGmlSceneIngestRequest(CityGmlSceneFixtures.SingleBuildingGeographic(), SceneId: "rt-citygml"),
            CancellationToken.None);

        var scene = ToSceneDataset(outcome.SceneId, outcome.AssetRoot);
        SceneAssetResolver.TryResolve(scene, "tileset.json", out var tilesetAsset, out _).Should().BeTrue();
        tilesetAsset.File.Length.Should().BeGreaterThan(0);

        var resources = await _i3sProvider.GetResourcesAsync(scene, CancellationToken.None);
        resources.Should().NotBeNull();
        AssertValidI3sRootGeometry(resources!);
    }

    [UnitTest]
    public async Task RoundTrip_FeatureExtrudedScene_Serves3DTilesAndConformantI3sRootGeometry()
    {
        var featureSource = new StubFeatureSource
        {
            Features =
            [
                new SceneFeature
                {
                    Id = 101,
                    Geometry = new SceneFeatureGeometry
                    {
                        Kind = SceneGeometryKind.Polygon,
                        Vertices =
                        [
                            new SceneVertex(-157.8581, 21.3069, 0.0),
                            new SceneVertex(-157.8579, 21.3069, 0.0),
                            new SceneVertex(-157.8579, 21.3071, 0.0),
                            new SceneVertex(-157.8581, 21.3071, 0.0),
                            new SceneVertex(-157.8581, 21.3069, 0.0),
                        ],
                    },
                    Attributes = new Dictionary<string, object?>
                    {
                        ["height_m"] = 45.0,
                        ["name"] = "Honolulu Tower",
                    },
                },
            ],
        };

        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-layer-1", Name = "Buildings" },
                    Type = MetadataV2ObjectType.FeatureDataset,
                    StorageBindingIds = ["binding-layer-1"],
                    SchemaFields =
                    [
                        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                        new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false },
                        new MetadataV2Field { Name = "height_m", Type = MetadataV2FieldType.Double, Nullable = true },
                        new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 64, Nullable = true },
                    ],
                    Spatial = new MetadataV2ObjectSpatial
                    {
                        SpatialReference = new MetadataV2SpatialReference
                        {
                            Srid = 4326,
                            Crs = "EPSG:4326",
                            IsGeographic = true,
                        },
                        GeometryType = MetadataV2GeometryType.Polygon,
                        PrimaryGeometryField = "shape",
                    },
                    Extrusion = new MetadataV2ExtrusionInfo
                    {
                        HeightField = "height_m",
                    },
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "binding-layer-1", Name = "binding-layer-1" },
                    ResourceId = "res-layer-1",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "features:1",
                    StorageLayerId = 1,
                },
            ],
        };
        var metadata = new TestMetadataV2GraphProvider(graph);

        var executor = new SceneTilesPublishExecutor(
            featureSource,
            metadata,
            new TestHostEnvironment(),
            _options,
            NullLogger<SceneTilesPublishExecutor>.Instance,
            _registration);

        var intent = PublishIntent.CreateDraft(
            intentId: Guid.NewGuid().ToString("N"),
            sourceKind: PublishSourceKind.FeatureLayer,
            sourceId: "1",
            targetKind: PublishTargetKind.SceneService,
            targetConfig: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SceneTilesPublishExecutor.TargetConfigSceneId] = "rt-extruded",
                [SceneTilesPublishExecutor.TargetConfigIncludeAttributes] = "height_m,name",
            });

        var outcome = await executor.RunDirectAsync(intent, CancellationToken.None);
        var scene = ToSceneDataset(outcome.Result.SceneId, outcome.Result.AssetRoot);

        var resources = await _i3sProvider.GetResourcesAsync(scene, CancellationToken.None);
        resources.Should().NotBeNull();
        AssertValidI3sRootGeometry(resources!);
        resources!.Extent.ZMax.Should().BeApproximately(45.0, 0.5);
    }

    [UnitTest]
    public async Task RoundTrip_ObjAndGlbModelIngest_Serves3DTilesAndConformantI3sRootGeometry()
    {
        var executor = new ModelScenePublishExecutor(
            new TestHostEnvironment(), _options, NullLogger<ModelScenePublishExecutor>.Instance, _registration);

        const string obj = "v -10 0 -10\nv 10 0 -10\nv 10 0 10\nv -10 0 10\nv 0 20 0\nf 1 2 5\nf 2 3 5\nf 3 4 5\nf 4 1 5\n";
        var placement = new ModelPlacement(-157.8581, 21.3069, 12.0, HeadingDegrees: 45.0);

        var outcome = await executor.IngestAsync(
            new ModelSceneIngestRequest(Encoding.UTF8.GetBytes(obj), "pyramid.obj", placement, SceneId: "rt-model"),
            CancellationToken.None);

        var scene = ToSceneDataset(outcome.SceneId, outcome.AssetRoot);
        var resources = await _i3sProvider.GetResourcesAsync(scene, CancellationToken.None);
        resources.Should().NotBeNull();
        AssertValidI3sRootGeometry(resources!);
        resources!.Extent.XMin.Should().BeApproximately(-157.8581, 0.01);
        resources.Extent.YMin.Should().BeApproximately(21.3069, 0.01);
        resources.Extent.ZMin.Should().BeApproximately(12.0, 0.5);
        resources.Extent.ZMax.Should().BeApproximately(32.0, 0.5);
    }

    [UnitTest]
    public async Task RoundTrip_PreTiledZipArchive_Serves3DTilesAndConformantI3sRootGeometry()
    {
        var modelExecutor = new ModelScenePublishExecutor(
            new TestHostEnvironment(), _options, NullLogger<ModelScenePublishExecutor>.Instance);
        const string obj = "v 0 0 0\nv 15 0 0\nv 0 15 0\nf 1 2 3\n";
        var staged = await modelExecutor.IngestAsync(
            new ModelSceneIngestRequest(Encoding.UTF8.GetBytes(obj), "seed.obj", new ModelPlacement(-122.4194, 37.7749, 5.0), SceneId: "rt-archive-seed"),
            CancellationToken.None);

        byte[] archiveBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(staged.AssetRoot, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(staged.AssetRoot, file).Replace('\\', '/');
                    var entry = zip.CreateEntry(rel);
                    await using var es = entry.Open();
                    var bytes = await File.ReadAllBytesAsync(file);
                    await es.WriteAsync(bytes);
                }
            }
            archiveBytes = ms.ToArray();
        }

        var importer = new SceneTilesetArchiveImporter(
            new TestHostEnvironment(), _options, NullLogger<SceneTilesetArchiveImporter>.Instance, _registration);
        var imported = await importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(archiveBytes, SceneId: "rt-archive"),
            CancellationToken.None);

        var scene = ToSceneDataset(imported.SceneId, imported.AssetRoot);
        var resources = await _i3sProvider.GetResourcesAsync(scene, CancellationToken.None);
        resources.Should().NotBeNull();
        AssertValidI3sRootGeometry(resources!);
    }

    [UnitTest]
    public async Task RoundTrip_LasPointCloudIngest_ServesValid3DTilesAndPntsAsset()
    {
        var executor = new PointCloudScenePublishExecutor(
            new TestHostEnvironment(), _options, NullLogger<PointCloudScenePublishExecutor>.Instance, _registration);

        var las = PointCloudSceneFixtures.ColoredGridGeographic();
        var outcome = await executor.IngestAsync(
            new PointCloudSceneIngestRequest(las, SceneId: "rt-las"),
            CancellationToken.None);

        outcome.PointCount.Should().BeGreaterThan(0);
        outcome.TileCount.Should().BeGreaterThan(0);

        var scene = ToSceneDataset(outcome.SceneId, outcome.AssetRoot);
        SceneAssetResolver.TryResolve(scene, "tileset.json", out var tilesetAsset, out _).Should().BeTrue();
        tilesetAsset.File.Length.Should().BeGreaterThan(0);

        var pntsFile = Directory.EnumerateFiles(outcome.AssetRoot, "*.pnts").Single();
        var pntsBytes = await File.ReadAllBytesAsync(pntsFile);
        pntsBytes.AsSpan(0, 4).SequenceEqual("pnts"u8).Should().BeTrue();
    }

    [UnitTest]
    public async Task AssetLifecycleProjector_ProjectsRegisteredSceneAndJobMetadata()
    {
        var executor = new ModelScenePublishExecutor(
            new TestHostEnvironment(), _options, NullLogger<ModelScenePublishExecutor>.Instance, _registration);
        const string obj = "v 0 0 0\nv 5 0 0\nv 0 5 0\nf 1 2 3\n";
        await executor.IngestAsync(
            new ModelSceneIngestRequest(Encoding.UTF8.GetBytes(obj), "asset.obj", new ModelPlacement(-157.85, 21.30), SceneId: "lifecycle-scene"),
            CancellationToken.None);

        var record = _registration.Records.Single(r => r.Id == "lifecycle-scene");
        var job = new SceneJobLifecycleSummary(
            JobId: "job-123",
            SourceKind: "model",
            Status: SceneJobStatusNames.Succeeded,
            SceneId: "lifecycle-scene",
            DisplayName: record.Name,
            ByteSize: 1024,
            FileCount: 2,
            Warnings: ["model normalized from obj"],
            CreatedAt: DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt: DateTimeOffset.UtcNow);

        var view = SceneAssetLifecycleProjector.Project(record, job, "https://gis.example.test");

        view.SceneId.Should().Be("lifecycle-scene");
        view.SourceKind.Should().Be("model");
        view.JobStatus.Should().Be("succeeded");
        view.PublishedRevision.Should().Be(1);
        view.TilesetUrl.Should().Be("https://gis.example.test/scenes/lifecycle-scene/tileset.json");
        view.I3sUrl.Should().Be("https://gis.example.test/rest/services/lifecycle-scene/SceneServer");
        view.ByteSize.Should().BeGreaterThan(0);
        view.FileCount.Should().Be(2);
        view.Warnings.Should().Contain("model normalized from obj");
    }

    private static SceneDataset ToSceneDataset(string sceneId, string assetRoot) => new()
    {
        Id = sceneId,
        Name = sceneId,
        AssetRoot = assetRoot,
        TilesetFileName = "tileset.json",
    };

    private static void AssertValidI3sRootGeometry(I3sSceneResources resources)
    {
        resources.Pages.Should().NotBeEmpty();
        resources.RootGeometry.Should().NotBeNull("root-node geometry must be populated for I3S 1.7/1.8 renderability");

        var rootGeom = resources.RootGeometry!;
        rootGeom.VertexCount.Should().BeGreaterThan(0);
        rootGeom.FeatureCount.Should().BeGreaterThan(0);

        var bytes = rootGeom.Buffer;
        var expectedLength = 8 + rootGeom.VertexCount * 44 + rootGeom.FeatureCount * 16;
        bytes.Length.Should().Be(expectedLength);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)).Should().Be((uint)rootGeom.VertexCount);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)).Should().Be((uint)rootGeom.FeatureCount);

        var rootObb = resources.Pages[0].Nodes[0].Obb;
        rootObb.Should().NotBeNull();
        rootObb!.Center.Should().HaveCount(3);
        rootObb.HalfSize.Should().HaveCount(3);
        rootObb.Quaternion.Should().HaveCount(4);

        // Verify all vertex positions and normals are finite Float32 values.
        for (var v = 0; v < rootGeom.VertexCount; v++)
        {
            var posOffset = 8 + v * 12;
            float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(posOffset))).Should().BeTrue();
            float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(posOffset + 4))).Should().BeTrue();
            float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(posOffset + 8))).Should().BeTrue();
        }
    }
}
