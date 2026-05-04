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
    public async Task Execute_DuplicateSceneIdPreservesExistingFiles()
    {
        // Pre-populate the registry with an existing record so the preflight
        // detects the conflict before any filesystem writes happen.
        _registration.Records.Add(new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = "existing-scene",
            Name = "Existing scene",
            AssetRoot = "/tmp/existing",
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            CachePolicy = SceneCachePolicy.Default,
            IsPublic = true,
            Status = SceneDatasetStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test"
        });

        // Pre-create the on-disk artifacts to verify the executor does not
        // overwrite them when the sceneId already exists in the registry.
        var existingDir = Path.Combine(_outputRoot, "existing-scene");
        Directory.CreateDirectory(existingDir);
        var existingTileBytes = new byte[] { 0x01, 0x02, 0x03 };
        var existingTilesetBytes = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"1.1\"}}");
        await File.WriteAllBytesAsync(Path.Combine(existingDir, "tile_0000.glb"), existingTileBytes);
        await File.WriteAllBytesAsync(Path.Combine(existingDir, "tileset.json"), existingTilesetBytes);

        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(BuildIntent(sceneId: "existing-scene"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.SceneIdConflict);

        // Files must remain byte-identical — preflight catches the duplicate
        // before Directory.CreateDirectory or File.WriteAllBytesAsync runs.
        (await File.ReadAllBytesAsync(Path.Combine(existingDir, "tile_0000.glb")))
            .Should().Equal(existingTileBytes);
        (await File.ReadAllBytesAsync(Path.Combine(existingDir, "tileset.json")))
            .Should().Equal(existingTilesetBytes);
    }

    [UnitTest]
    public async Task Execute_RejectsExplicitSceneIdLongerThanLimit()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var tooLong = new string('a', 65);
        var act = () => _executor.RunDirectAsync(BuildIntent(sceneId: tooLong), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
        Directory.Exists(Path.Combine(_outputRoot, tooLong)).Should().BeFalse(
            "validation must reject before any directory is created.");
    }

    [UnitTest]
    public async Task Execute_RejectsExplicitSceneIdWithTrailingHyphen()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(BuildIntent(sceneId: "scene-"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
    }

    [UnitTest]
    public async Task Execute_RejectsCacheMaxAgeAboveLimit()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(
            BuildIntent(sceneId: "cache-too-long", cacheMaxAgeSeconds: 86_401),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
        Directory.Exists(Path.Combine(_outputRoot, "cache-too-long")).Should().BeFalse(
            "validation must reject before any directory is created.");
    }

    [UnitTest]
    public async Task Execute_RejectsInvalidEditionGate()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(
            BuildIntent(sceneId: "bad-gate", editionGate: "Pro Edition!"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
    }

    [UnitTest]
    public async Task Execute_RejectsDisplayNameLongerThanLimit()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var longName = new string('a', 129);
        var act = () => _executor.RunDirectAsync(
            BuildIntent(sceneId: "long-name", displayName: longName),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
    }

    [UnitTest]
    public async Task Execute_AutoGeneratedSceneIdHandlesNonAsciiLayerName()
    {
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, Length: null, Nullable: false),
            new FieldDefinition("shape", FieldType.Geometry, Length: null, Nullable: false),
            new FieldDefinition("name", FieldType.String, Length: 64, Nullable: true),
            new FieldDefinition("height", FieldType.Integer, Length: null, Nullable: true)
        };
        _catalog.Layer = new LayerDefinition(
            LayerId, "Bâtiments-2026", null, GeometryType.Polygon,
            SpatialReference.Create(4326, 4326), fields);
        _featureSource.Features = SamplePolygons();

        var outcome = await _executor.RunDirectAsync(BuildIntent(), CancellationToken.None);

        // The auto-generated id must satisfy the canonical SceneDatasetValidator
        // pattern even when the source layer name carries non-ASCII characters.
        outcome.Result.SceneId.Should().MatchRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$");
        outcome.Result.SceneId.Should().NotContainAny("â", "Â", "â");
    }

    [UnitTest]
    public async Task Execute_AcceptsCacheMaxAgeAtBoundary()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "cache-boundary", cacheMaxAgeSeconds: 86_400),
            CancellationToken.None);

        outcome.Result.SceneId.Should().Be("cache-boundary");
    }

    [UnitTest]
    public async Task Execute_RegistrationConflictAfterPreflight_PreservesExistingFinalFiles()
    {
        // Simulate the concurrent-publish race: two requests with the same
        // sceneId both pass the preflight (registry empty), the winner finishes
        // first and registers, and the loser's RegisterAsync throws
        // SceneDatasetAlreadyExistsException. The loser must NOT overwrite the
        // winner's final-path files. Pre-creating the final-path bytes here
        // models the post-winner state.
        var finalDir = Path.Combine(_outputRoot, "concurrent-loser");
        Directory.CreateDirectory(finalDir);
        var winnerTileBytes = new byte[] { 0xAA, 0xBB, 0xCC };
        var winnerTilesetBytes = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"1.1\"},\"winner\":true}");
        await File.WriteAllBytesAsync(Path.Combine(finalDir, "tile_0000.glb"), winnerTileBytes);
        await File.WriteAllBytesAsync(Path.Combine(finalDir, "tileset.json"), winnerTilesetBytes);

        // Preflight returns null (registry record is hidden) but RegisterAsync
        // throws — this is the case the staging-then-promote path is meant to
        // close.
        _registration.RejectNextRegistration = true;
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(BuildIntent(sceneId: "concurrent-loser"), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.SceneIdConflict);

        // Winner's bytes survived: registration failed before the rename, and
        // the loser's outputs were staged under a separate intent-scoped
        // directory.
        (await File.ReadAllBytesAsync(Path.Combine(finalDir, "tile_0000.glb")))
            .Should().Equal(winnerTileBytes);
        (await File.ReadAllBytesAsync(Path.Combine(finalDir, "tileset.json")))
            .Should().Equal(winnerTilesetBytes);

        // Staging directory must be cleaned up — only the final dir remains.
        var stagingEntries = Directory.GetDirectories(_outputRoot, ".staging-*");
        stagingEntries.Should().BeEmpty(
            "the executor must remove its staging directory when registration rejects the publish.");
    }

    [UnitTest]
    public async Task Execute_OverwritesStaleFinalDir_WhenRegistryHasNoRecord()
    {
        // A previous partial run can leave detritus at the final path even
        // though the registry has no record (e.g. crash between move and a
        // hypothetical post-promote step). The staging promotion path must
        // overwrite the stale directory so the registry record and disk
        // contents agree.
        var finalDir = Path.Combine(_outputRoot, "stale-overwrite");
        Directory.CreateDirectory(finalDir);
        await File.WriteAllBytesAsync(Path.Combine(finalDir, "leftover.txt"),
            Encoding.UTF8.GetBytes("orphaned"));

        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "stale-overwrite"), CancellationToken.None);

        outcome.Result.SceneId.Should().Be("stale-overwrite");
        File.Exists(Path.Combine(finalDir, "leftover.txt")).Should().BeFalse(
            "stale detritus must be removed during staging promotion.");
        File.Exists(Path.Combine(finalDir, "tile_0000.glb")).Should().BeTrue();
        File.Exists(Path.Combine(finalDir, "tileset.json")).Should().BeTrue();
    }

    [UnitTest]
    public async Task Execute_SuccessfulGeneration_LeavesNoStagingDirectory()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "no-staging"), CancellationToken.None);

        outcome.Result.SceneId.Should().Be("no-staging");
        var stagingEntries = Directory.GetDirectories(_outputRoot, ".staging-*");
        stagingEntries.Should().BeEmpty(
            "successful generation promotes staging to the final scene path; no staging dir should linger.");
    }

    [UnitTest]
    public async Task Execute_NoZValuesAndNoExtrusion_EmitsFlatZeroWarning()
    {
        // 2D layers without an extrusionInfo block must surface the
        // documented "flat at Z=0" warning so operators and admin tooling
        // can spot when a generated tileset lost vertical fidelity. The
        // Postgres scene source previously wrapped geometry in ST_Force3D
        // (Z=0 for every 2D vertex), which masked this branch — the warning
        // only fires when no vertex carries a Height value.
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygonsWithoutZ();

        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "flat-warning"), CancellationToken.None);

        outcome.Result.Warnings.Should().Contain(
            "Layer has no Z values and no extrusion configured; output is flat at Z=0.",
            "the flat-Z=0 warning must fire when neither vertex Z nor extrusion is configured.");
    }

    [UnitTest]
    public async Task Execute_PromotionFailureAfterRegistration_DeactivatesRegistryRecord()
    {
        // Pre-create a regular file at the final path. Directory.Exists()
        // returns false (it's a file, not a directory), so the promotion
        // path skips the stale-dir delete and proceeds straight to
        // Directory.Move — which throws because the destination file
        // already occupies the slot. The executor must then compensate
        // by deactivating the registry record it inserted moments before.
        Directory.CreateDirectory(_outputRoot);
        var finalPath = Path.Combine(_outputRoot, "promo-fail");
        await File.WriteAllTextAsync(finalPath, "blocks-move");

        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(
            BuildIntent(sceneId: "promo-fail"), CancellationToken.None);
        await act.Should().ThrowAsync<ServiceUnavailableException>();

        // The registry insert preceded the failed promotion, but the
        // executor must call DeactivateAsync on the inserted record so
        // the serving path does not resolve a record whose AssetRoot
        // bytes were never written.
        _registration.Records.Should().ContainSingle(r => r.Id == "promo-fail");
        var record = _registration.Records.Single(r => r.Id == "promo-fail");
        _registration.DeactivatedDatasetIds.Should().Contain(record.DatasetId,
            "the executor must compensate the registration when staging promotion fails.");
        record.Status.Should().Be(SceneDatasetStatus.Inactive,
            "the compensated record must reflect the inactive status the serving path filters on.");

        // Staging directory is still cleaned up via the finally block.
        var stagingEntries = Directory.GetDirectories(_outputRoot, ".staging-*");
        stagingEntries.Should().BeEmpty(
            "the executor's finally block must remove the staging directory even when promotion fails.");
    }

    [UnitTest]
    public async Task Execute_DefaultDisplayName_IsUniquePerSceneId()
    {
        // Two generations of the same layer with different sceneIds must
        // produce different default display names so the registry's name
        // uniqueness constraint does not surface as SCENE_ID_CONFLICT.
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var first = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "buildings-aaa"), CancellationToken.None);
        var second = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "buildings-bbb"), CancellationToken.None);

        first.Result.SceneId.Should().Be("buildings-aaa");
        second.Result.SceneId.Should().Be("buildings-bbb");
        _registration.Records.Should().HaveCount(2);
        var firstRecord = _registration.Records.Single(r => r.Id == "buildings-aaa");
        var secondRecord = _registration.Records.Single(r => r.Id == "buildings-bbb");
        firstRecord.Name.Should().Be("Buildings (buildings-aaa)");
        secondRecord.Name.Should().Be("Buildings (buildings-bbb)");
        firstRecord.Name.Should().NotBe(secondRecord.Name,
            "default display names must be unique so repeated generations of the same layer do not collide on the registry name uniqueness constraint.");
    }

    [UnitTest]
    public async Task Execute_ExplicitDisplayName_OverridesAutoSuffix()
    {
        // Explicit displayName is preserved as-is; the auto suffix only
        // applies when the operator omits the field.
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        await _executor.RunDirectAsync(
            BuildIntent(sceneId: "named-scene", displayName: "Custom Display"),
            CancellationToken.None);

        var record = _registration.Records.Single(r => r.Id == "named-scene");
        record.Name.Should().Be("Custom Display");
    }

    [UnitTest]
    public async Task Execute_ProtectedLayer_RegistersSceneAsProtectedWithRoles()
    {
        // High/correctness regression: source layers with a non-anonymous
        // access policy must materialise non-public scenes that forward
        // the role allow-list. Without this mapping, generating a tileset
        // from a protected layer would publish its geometry/attributes
        // anonymously through /scenes/{sceneId}/... — an RBAC bypass.
        var allowedRoles = new[] { "fieldops", "engineering" };
        var protectedPolicy = new AccessPolicy
        {
            AllowAnonymous = false,
            AllowedRoles = allowedRoles
        };
        _catalog.Layer = BuildLayer(accessPolicy: protectedPolicy);
        _featureSource.Features = SamplePolygons();

        await _executor.RunDirectAsync(
            BuildIntent(sceneId: "protected-scene"), CancellationToken.None);

        var record = _registration.Records.Single(r => r.Id == "protected-scene");
        record.IsPublic.Should().BeFalse(
            "scenes derived from protected layers must not be served anonymously.");
        record.RequiresAuth.Should().BeTrue();
        record.AllowedRoles.Should().NotBeNull().And.BeEquivalentTo(allowedRoles,
            "the layer's role allow-list must travel with the scene record.");
    }

    [UnitTest]
    public async Task Execute_AnonymousLayer_RegistersSceneAsPublic()
    {
        // AllowAnonymous=true on the source layer means anonymous reads
        // are accepted, so the scene tileset stays public.
        var anonymousPolicy = new AccessPolicy { AllowAnonymous = true };
        _catalog.Layer = BuildLayer(accessPolicy: anonymousPolicy);
        _featureSource.Features = SamplePolygons();

        await _executor.RunDirectAsync(
            BuildIntent(sceneId: "anon-scene"), CancellationToken.None);

        var record = _registration.Records.Single(r => r.Id == "anon-scene");
        record.IsPublic.Should().BeTrue();
        record.RequiresAuth.Should().BeFalse();
        record.AllowedRoles.Should().BeNull();
    }

    [UnitTest]
    public async Task Execute_NoAccessPolicy_DefaultsToPublic()
    {
        // Layers with no AccessPolicy keep the historic public-scene
        // default; only an explicit AllowAnonymous=false flips the bit.
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        await _executor.RunDirectAsync(
            BuildIntent(sceneId: "default-scene"), CancellationToken.None);

        var record = _registration.Records.Single(r => r.Id == "default-scene");
        record.IsPublic.Should().BeTrue();
        record.RequiresAuth.Should().BeFalse();
    }

    [UnitTest]
    public async Task Execute_DuplicateSceneId_DoesNotEnumerateFeatureSource()
    {
        // Performance regression: validators and registry preflight must
        // run BEFORE feature streaming so a 409 does not drag a full
        // 50 000-feature page through PostGIS first. Pre-seed the registry
        // with the target sceneId, then verify the executor refuses
        // before the StubFeatureSource records a single Stream call.
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();
        _registration.Records.Add(new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = "preexisting-scene",
            Name = "Existing",
            AssetRoot = "/var/lib/honua/scenes/preexisting-scene",
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            CachePolicy = new SceneCachePolicy(3600, NoStore: false),
            IsPublic = true,
            RequiresAuth = false,
            Status = SceneDatasetStatus.Active,
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test"
        });

        var act = () => _executor.RunDirectAsync(
            BuildIntent(sceneId: "preexisting-scene"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.SceneIdConflict);
        _featureSource.StreamInvocationCount.Should().Be(0,
            "the registry preflight must reject duplicate sceneIds before enumerating the feature source.");
    }

    [UnitTest]
    public async Task Execute_InvalidExplicitSceneId_DoesNotEnumerateFeatureSource()
    {
        // Mirror of the duplicate-id case for invalid sceneId values:
        // an unparseable slug must be rejected before any DB streaming.
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var act = () => _executor.RunDirectAsync(
            BuildIntent(sceneId: "not valid id with spaces"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
        _featureSource.StreamInvocationCount.Should().Be(0,
            "sceneId validation must run before any feature-source enumeration.");
    }

    [UnitTest]
    public async Task Execute_PropertyIdCollision_DeduplicatesDeterministically()
    {
        // SanitizePropertyId collapses non-alphanumerics to '_', so source
        // field names like "a-b" and "a_b" both map to "a_b" and would
        // shadow each other in EXT_structural_metadata.schema.classes.
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, Length: null, Nullable: false),
            new FieldDefinition("shape", FieldType.Geometry, Length: null, Nullable: false),
            new FieldDefinition("a-b", FieldType.String, Length: 64, Nullable: true),
            new FieldDefinition("a_b", FieldType.String, Length: 64, Nullable: true)
        };
        _catalog.Layer = new LayerDefinition(
            LayerId, "Buildings", null, GeometryType.Polygon,
            SpatialReference.Create(4326, 4326), fields);

        var basePolygons = SamplePolygons();
        var withCollidingFields = new List<SceneFeature>(basePolygons.Count);
        foreach (var poly in basePolygons)
        {
            var attrs = new Dictionary<string, object?>(poly.Attributes, StringComparer.Ordinal)
            {
                ["a-b"] = "hyphen-source",
                ["a_b"] = "underscore-source"
            };
            withCollidingFields.Add(poly with { Attributes = attrs });
        }
        _featureSource.Features = withCollidingFields;

        var outcome = await _executor.RunDirectAsync(
            BuildIntent(sceneId: "collision-test"), CancellationToken.None);

        var tile = await File.ReadAllBytesAsync(Path.Combine(outcome.Result.AssetRoot, "tile_0000.glb"));
        using var doc = JsonDocument.Parse(ExtractJsonChunk(tile));
        var properties = doc.RootElement
            .GetProperty("extensions").GetProperty("EXT_structural_metadata")
            .GetProperty("schema").GetProperty("classes")
            .GetProperty("honua_feature_class").GetProperty("properties");

        properties.TryGetProperty("a_b", out _).Should().BeTrue(
            "the first sanitized id keeps the canonical name.");
        properties.TryGetProperty("a_b_2", out _).Should().BeTrue(
            "the second collision must receive a deterministic suffix so it is not lost.");
    }

    [UnitTest]
    public async Task Execute_NegativeCacheMaxAgeSeconds_ReturnsOptionsInvalid()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var intent = BuildIntent(sceneId: "negative-cache");
        var configWithNegative = new Dictionary<string, string>(intent.TargetConfig, StringComparer.Ordinal)
        {
            [SceneTilesPublishExecutor.TargetConfigCacheMaxAge] = "-1"
        };
        var bad = intent with { TargetConfig = configWithNegative };

        var act = () => _executor.RunDirectAsync(bad, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
        ex.And.Message.Should().Contain("cacheMaxAgeSeconds");
    }

    [UnitTest]
    public async Task Execute_NonNumericCacheMaxAgeSeconds_ReturnsOptionsInvalid()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var intent = BuildIntent(sceneId: "nan-cache");
        var configWithNaN = new Dictionary<string, string>(intent.TargetConfig, StringComparer.Ordinal)
        {
            [SceneTilesPublishExecutor.TargetConfigCacheMaxAge] = "not-a-number"
        };
        var bad = intent with { TargetConfig = configWithNaN };

        var act = () => _executor.RunDirectAsync(bad, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
    }

    [UnitTest]
    public async Task Execute_NonPositiveMaxFeatureCount_ReturnsOptionsInvalid()
    {
        _catalog.Layer = BuildLayer();
        _featureSource.Features = SamplePolygons();

        var intent = BuildIntent(sceneId: "zero-cap");
        var configWithZero = new Dictionary<string, string>(intent.TargetConfig, StringComparer.Ordinal)
        {
            [SceneTilesPublishExecutor.TargetConfigMaxFeatureCount] = "0"
        };
        var bad = intent with { TargetConfig = configWithZero };

        var act = () => _executor.RunDirectAsync(bad, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.And.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
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
        LayerExtrusionInfo? extrusion = null,
        AccessPolicy? accessPolicy = null)
    {
        var sr = spatialReference ?? SpatialReference.Create(4326, 4326);
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, Length: null, Nullable: false),
            new FieldDefinition("shape", FieldType.Geometry, Length: null, Nullable: false),
            new FieldDefinition("name", FieldType.String, Length: 64, Nullable: true),
            new FieldDefinition("height", FieldType.Integer, Length: null, Nullable: true)
        };
        var metadata = (extrusion, accessPolicy) switch
        {
            (null, null) => null,
            _ => new CatalogMetadata { Extrusion = extrusion, AccessPolicy = accessPolicy }
        };
        return new LayerDefinition(
            LayerId,
            "Buildings",
            null,
            GeometryType.Polygon,
            sr,
            fields,
            Metadata: metadata);
    }

    private static List<SceneFeature> SamplePolygonsWithoutZ()
    {
        // Models a 2D PostGIS layer streamed without ST_Force3D: every
        // vertex has Height=null so SawAnyHeight stays false and the
        // executor's flat-Z=0 warning fires.
        var ring = new[]
        {
            new SceneVertex(-122.5, 37.7, null),
            new SceneVertex(-122.4, 37.7, null),
            new SceneVertex(-122.4, 37.8, null),
            new SceneVertex(-122.5, 37.8, null),
            new SceneVertex(-122.5, 37.7, null)
        };
        return new List<SceneFeature>
        {
            new()
            {
                Id = 1,
                Geometry = new SceneFeatureGeometry { Kind = SceneGeometryKind.Polygon, Vertices = ring },
                Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = "flat",
                    ["height"] = 0
                }
            }
        };
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

    private static PublishIntent BuildIntent(
        string? sceneId = null,
        string? displayName = null,
        string? editionGate = null,
        int? cacheMaxAgeSeconds = null)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(sceneId))
        {
            config[SceneTilesPublishExecutor.TargetConfigSceneId] = sceneId;
        }
        if (!string.IsNullOrEmpty(displayName))
        {
            config[SceneTilesPublishExecutor.TargetConfigDisplayName] = displayName;
        }
        if (!string.IsNullOrEmpty(editionGate))
        {
            config[SceneTilesPublishExecutor.TargetConfigEditionGate] = editionGate;
        }
        if (cacheMaxAgeSeconds is { } cache)
        {
            config[SceneTilesPublishExecutor.TargetConfigCacheMaxAge] =
                cache.ToString(CultureInfo.InvariantCulture);
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
        public int StreamInvocationCount { get; private set; }

        public async IAsyncEnumerable<SceneFeature> StreamAsync(
            LayerDefinition layer,
            IReadOnlyList<string> includeAttributes,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamInvocationCount++;
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
        public List<Guid> DeactivatedDatasetIds { get; } = new();
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
        {
            DeactivatedDatasetIds.Add(datasetId);
            var idx = Records.FindIndex(r => r.DatasetId == datasetId);
            if (idx < 0)
            {
                return Task.FromResult(false);
            }
            Records[idx] = Records[idx] with { Status = SceneDatasetStatus.Inactive };
            return Task.FromResult(true);
        }
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
