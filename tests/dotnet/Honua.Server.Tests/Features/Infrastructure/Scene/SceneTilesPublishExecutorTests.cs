// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Scene;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// Unit tests for the v1 3D Tiles generation executor (#842). The tests use
/// in-memory stubs for the catalog and feature source so the pipeline can be
/// exercised end-to-end without standing up Postgres.
/// </summary>
public sealed class SceneTilesPublishExecutorTests : IDisposable
{
    private const int LayerId = 7;
    private readonly string _outputRoot;
    private readonly StubLayerCatalog _catalog;
    private readonly StubFeatureSource _featureSource;
    private readonly StubRegistrationService _registration;
    private readonly SceneTilesPublishExecutor _executor;

    public SceneTilesPublishExecutorTests()
    {
        _outputRoot = Path.Combine(Path.GetTempPath(), $"honua-scene-{Guid.NewGuid():N}");
        _catalog = new StubLayerCatalog();
        _featureSource = new StubFeatureSource();
        _registration = new StubRegistrationService();

        var options = Options.Create(new SceneGenerationServerOptions
        {
            OutputRoot = _outputRoot,
            MaxFeatureCount = 50_000,
            GeneratorTag = "honua-test-generator/1.0"
        });
        var environment = new TestHostEnvironment();

        _executor = new SceneTilesPublishExecutor(
            _catalog,
            _featureSource,
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
    public async Task Execute_ProducesDeterministicByteIdenticalOutput()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var intent1 = BuildIntent(sceneId: "deterministic-a");
        var intent2 = BuildIntent(sceneId: "deterministic-b");

        var outcome1 = await _executor.RunDirectAsync(intent1, CancellationToken.None);
        var outcome2 = await _executor.RunDirectAsync(intent2, CancellationToken.None);

        var tile1 = await File.ReadAllBytesAsync(Path.Combine(outcome1.Result.AssetRoot, "tile_0000.glb"));
        var tile2 = await File.ReadAllBytesAsync(Path.Combine(outcome2.Result.AssetRoot, "tile_0000.glb"));
        tile1.Should().Equal(tile2);

        var json1 = await File.ReadAllBytesAsync(Path.Combine(outcome1.Result.AssetRoot, "tileset.json"));
        var json2 = await File.ReadAllBytesAsync(Path.Combine(outcome2.Result.AssetRoot, "tileset.json"));
        json1.Should().Equal(json2);
    }

    [UnitTest]
    public async Task Execute_PreservesAttributesInGlbStructuralMetadata()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var outcome = await _executor.RunDirectAsync(BuildIntent(), CancellationToken.None);
        var tile = await File.ReadAllBytesAsync(Path.Combine(outcome.Result.AssetRoot, "tile_0000.glb"));
        var json = ExtractJsonChunk(tile);

        using var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement
            .GetProperty("extensions").GetProperty("EXT_structural_metadata")
            .GetProperty("schema").GetProperty("classes")
            .GetProperty("honua_feature_class").GetProperty("properties");

        properties.TryGetProperty("name", out _).Should().BeTrue();
        properties.TryGetProperty("height", out _).Should().BeTrue();
    }

    [UnitTest]
    public async Task Execute_RegistersSceneWithBoundsCoveringAllFeatures()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var outcome = await _executor.RunDirectAsync(BuildIntent(), CancellationToken.None);

        _registration.Records.Should().ContainSingle();
        var record = _registration.Records[0];
        record.Extent.Should().NotBeNull();
        record.Extent!.XMin.Should().BeLessThanOrEqualTo(-122.5);
        record.Extent.YMin.Should().BeLessThanOrEqualTo(37.7);
        record.Extent.XMax.Should().BeGreaterThanOrEqualTo(-122.4);
        record.Extent.YMax.Should().BeGreaterThanOrEqualTo(37.8);
        outcome.Result.Summary.BoundingRegionDegrees.Should().HaveCount(4);
    }

    [UnitTest]
    public async Task Execute_UnknownLayer_ReturnsLayerNotFoundCode()
    {
        _catalog.Layer = null;
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(BuildIntent(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.LayerNotFound);
    }

    [UnitTest]
    public async Task Execute_LayerWithoutCrs_ReturnsCrsUnknownCode()
    {
        _catalog.Layer = BuildLayer(spatialReference: new SpatialReference { Wkid = 0 });
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(BuildIntent(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.LayerCrsUnknown);
    }

    [UnitTest]
    public async Task Execute_ExceedingFeatureLimit_ReturnsFeatureLimitCode()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var intent = BuildIntent();
        var configWithLimit = new Dictionary<string, string>(intent.TargetConfig, StringComparer.Ordinal)
        {
            [SceneTilesPublishExecutor.TargetConfigMaxFeatureCount] = "1"
        };
        var limited = intent with { TargetConfig = configWithLimit };

        var act = () => _executor.RunDirectAsync(limited, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.FeatureLimitExceeded);
    }

    [UnitTest]
    public async Task Execute_AppliesExtrusionOverrideToProduceVerticalGeometry()
    {
        _catalog.Layer = BuildLayer(extrusion: new LayerExtrusionInfo
        {
            HeightField = "height",
            Unit = VerticalUnits.Meters
        });
        _featureSource.Features = SamplePolygons();

        var outcome = await _executor.RunDirectAsync(BuildIntent(), CancellationToken.None);
        var tile = await File.ReadAllBytesAsync(Path.Combine(outcome.Result.AssetRoot, "tile_0000.glb"));
        var json = ExtractJsonChunk(tile);

        using var doc = JsonDocument.Parse(json);
        var positionAccessor = doc.RootElement.GetProperty("accessors")[0];
        var maxZ = positionAccessor.GetProperty("max")[2].GetDouble();
        var minZ = positionAccessor.GetProperty("min")[2].GetDouble();
        // Extrusion produces a Z spread; in ECEF this manifests as a meaningful
        // delta on the up axis, larger than the floating-point noise of a
        // flat tile.
        (maxZ - minZ).Should().BeGreaterThan(1.0);

        // Bounds in summary should reflect the extruded max height.
        outcome.Result.Summary.BoundingRegionDegrees.Should().HaveCount(4);
    }

    [UnitTest]
    public async Task Execute_RejectsCollidingSceneIdAsValidation()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();
        _registration.RejectNextRegistration = true;

        var act = () => _executor.RunDirectAsync(BuildIntent(sceneId: "duplicate"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.SceneRegistrationConflict);
        ex.And.Message.Should().Contain("id 'duplicate-scene' or name");
    }

    [UnitTest]
    public async Task Execute_RejectsSceneIdWithPathTraversal()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(BuildIntent(sceneId: "../etc/escape"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
        Directory.Exists(_outputRoot).Should().BeFalse(
            "validation must reject before any directory is created.");
    }

    [UnitTest]
    public async Task Execute_BoundingRegionMaxHeight_AccountsForBaseHeightField()
    {
        _catalog.Layer = BuildLayer(extrusion: new LayerExtrusionInfo
        {
            HeightField = "height",
            BaseHeightField = "base",
            Unit = VerticalUnits.Meters
        });
        _featureSource.Features = SamplePolygons(baseHeight: 100.0);

        var outcome = await _executor.RunDirectAsync(BuildIntent(), CancellationToken.None);

        var tile = await File.ReadAllBytesAsync(Path.Combine(outcome.Result.AssetRoot, "tile_0000.glb"));
        var json = ExtractJsonChunk(tile);
        using var doc = JsonDocument.Parse(json);
        // GLB Z bounds reflect baseHeight + extrusionHeight in ECEF space.
        var positionAccessor = doc.RootElement.GetProperty("accessors")[0];
        var maxZ = positionAccessor.GetProperty("max")[2].GetDouble();
        var minZ = positionAccessor.GetProperty("min")[2].GetDouble();
        (maxZ - minZ).Should().BeGreaterThan(50.0,
            "the prism spans baseHeight=100 to baseHeight+max(height)=150");

        // Tileset.json bounding region heights are read from disk and must
        // include the 100m base offset; without the fix the max height would
        // be just the extrusion height (max ~50m) and ignore the 100m base.
        var tilesetJson = await File.ReadAllBytesAsync(Path.Combine(outcome.Result.AssetRoot, "tileset.json"));
        using var tilesetDoc = JsonDocument.Parse(tilesetJson);
        var region = tilesetDoc.RootElement.GetProperty("root").GetProperty("boundingVolume").GetProperty("region");
        var minHeightMeters = region[4].GetDouble();
        var maxHeightMeters = region[5].GetDouble();
        minHeightMeters.Should().BeApproximately(100.0, 0.001);
        maxHeightMeters.Should().BeApproximately(150.0, 0.001);
    }

    [UnitTest]
    public async Task Execute_BigIntegerValues_PreservedWithoutClamping()
    {
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, Length: null, Nullable: false),
            new FieldDefinition("shape", FieldType.Geometry, Length: null, Nullable: false),
            new FieldDefinition("name", FieldType.String, Length: 64, Nullable: true),
            new FieldDefinition("height", FieldType.Integer, Length: null, Nullable: true),
            new FieldDefinition("big_id", FieldType.BigInteger, Length: null, Nullable: true)
        };
        _catalog.Layer = new LayerDefinition(
            LayerId, "Buildings", null, GeometryType.Polygon,
            SpatialReference.Create(4326, 4326), fields);

        var basePolygons = SamplePolygons();
        var bigValues = new long[] { (long)int.MaxValue + 100L, (long)int.MinValue - 100L };
        var withBigId = new List<SceneFeature>(basePolygons.Count);
        for (var i = 0; i < basePolygons.Count; i++)
        {
            var attrs = new Dictionary<string, object?>(basePolygons[i].Attributes, StringComparer.Ordinal)
            {
                ["big_id"] = bigValues[i]
            };
            withBigId.Add(basePolygons[i] with { Attributes = attrs });
        }
        _featureSource.Features = withBigId;

        var outcome = await _executor.RunDirectAsync(BuildIntent(), CancellationToken.None);

        outcome.Result.Warnings.Should().NotContain(w =>
            w.Contains("big_id", StringComparison.Ordinal)
            && w.Contains("clamped", StringComparison.Ordinal));
    }

    private LayerDefinition BuildLayer(
        SpatialReference? spatialReference = null,
        LayerExtrusionInfo? extrusion = null)
    {
        var sr = spatialReference ?? SpatialReference.Create(4326, 4326);
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, Length: null, Nullable: false),
            new FieldDefinition("shape", FieldType.Geometry, Length: null, Nullable: false),
            new FieldDefinition("name", FieldType.String, Length: 64, Nullable: true),
            new FieldDefinition("height", FieldType.Integer, Length: null, Nullable: true)
        };
        return new LayerDefinition(
            LayerId,
            "Buildings",
            null,
            GeometryType.Polygon,
            sr,
            fields,
            Metadata: extrusion is null ? null : new CatalogMetadata { Extrusion = extrusion });
    }

    private static List<SceneFeature> SamplePolygons(double? baseHeight = null)
    {
        var ringA = new[]
        {
            new SceneVertex(-122.5, 37.7, 0),
            new SceneVertex(-122.4, 37.7, 0),
            new SceneVertex(-122.4, 37.8, 0),
            new SceneVertex(-122.5, 37.8, 0),
            new SceneVertex(-122.5, 37.7, 0)
        };
        var ringB = new[]
        {
            new SceneVertex(-122.49, 37.71, 0),
            new SceneVertex(-122.41, 37.71, 0),
            new SceneVertex(-122.41, 37.79, 0),
            new SceneVertex(-122.49, 37.79, 0),
            new SceneVertex(-122.49, 37.71, 0)
        };
        var attrsA = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "alpha",
            ["height"] = 25
        };
        var attrsB = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "beta",
            ["height"] = 50
        };
        if (baseHeight is { } b)
        {
            attrsA["base"] = b;
            attrsB["base"] = b;
        }
        return new List<SceneFeature>
        {
            new()
            {
                Id = 1,
                Geometry = new SceneFeatureGeometry { Kind = SceneGeometryKind.Polygon, Vertices = ringA },
                Attributes = attrsA
            },
            new()
            {
                Id = 2,
                Geometry = new SceneFeatureGeometry { Kind = SceneGeometryKind.Polygon, Vertices = ringB },
                Attributes = attrsB
            }
        };
    }

    private static PublishIntent BuildIntent(string? sceneId = null)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(sceneId))
        {
            config[SceneTilesPublishExecutor.TargetConfigSceneId] = sceneId;
        }
        return PublishIntent.CreateDraft(
            intentId: Guid.NewGuid().ToString("N"),
            sourceKind: PublishSourceKind.FeatureLayer,
            sourceId: LayerId.ToString(CultureInfo.InvariantCulture),
            targetKind: PublishTargetKind.SceneService,
            targetConfig: config);
    }

    private static string ExtractJsonChunk(byte[] glb)
    {
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        return Encoding.UTF8.GetString(glb.AsSpan(20, jsonLength)).TrimEnd('\0', ' ');
    }

    private sealed class StubLayerCatalog : ILayerCatalog
    {
        public LayerDefinition? Layer { get; set; }

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Layer);

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Layer is null ? Array.Empty<LayerDefinition>() : new[] { Layer });

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<ServiceDefinition?>(null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<ServiceDefinition>());

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Layer is not null);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }

    private sealed class StubFeatureSource : ISceneFeatureSource
    {
        public List<SceneFeature> Features { get; set; } = new();

        public async IAsyncEnumerable<SceneFeature> StreamAsync(
            LayerDefinition layer,
            IReadOnlyList<string> includeAttributes,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var feature in Features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return feature;
                await Task.Yield();
            }
        }
    }

    private sealed class StubRegistrationService : ISceneRegistrationService
    {
        public List<SceneDatasetRecord> Records { get; } = new();
        public bool RejectNextRegistration { get; set; }

        public Task<SceneDatasetRecord> RegisterAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
        {
            if (RejectNextRegistration)
            {
                throw new SceneDatasetAlreadyExistsException("duplicate");
            }
            Records.Add(record);
            return Task.FromResult(record);
        }

        public Task<SceneDatasetRecord?> GetAsync(Guid datasetId, CancellationToken cancellationToken = default)
            => Task.FromResult(Records.Find(r => r.DatasetId == datasetId));

        public Task<SceneDatasetRecord?> GetBySceneIdAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Records.Find(r => r.Id == id));

        public Task<IReadOnlyList<SceneDatasetRecord>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SceneDatasetRecord>>(Records);

        public Task<SceneDatasetRecord> UpdateAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default)
            => Task.FromResult(record);

        public Task<bool> DeactivateAsync(Guid datasetId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Honua.Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
