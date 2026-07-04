// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.PointCloud;
using Honua.Infrastructure.Scene;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// Hermetic unit tests for the wired LAS point-cloud ingest executor (#1201).
/// They exercise the full decode → tile → register → promote pipeline against a
/// temp output root and an in-memory registration stub, proving the produced
/// <c>.pnts</c> tileset is written to a servable asset root with point-cloud
/// dataset semantics without standing up Postgres or an HTTP host.
/// </summary>
public sealed class PointCloudScenePublishExecutorTests : IDisposable
{
    private readonly string _outputRoot;
    private readonly StubRegistrationService _registration;
    private readonly PointCloudScenePublishExecutor _executor;

    public PointCloudScenePublishExecutorTests()
    {
        _outputRoot = Path.Combine(Path.GetTempPath(), $"honua-pcloud-{Guid.NewGuid():N}");
        _registration = new StubRegistrationService();

        var options = Options.Create(new SceneGenerationServerOptions
        {
            OutputRoot = _outputRoot,
            GeneratorTag = "honua-test-generator/1.0",
            // A small per-tile cap so the synthetic grid subdivides into a
            // multi-tile quadtree exercising the LOD path.
            MaxFeaturesPerTile = 32,
            MaxLodDepth = 8,
            InteriorSampleCount = 16
        });

        _executor = new PointCloudScenePublishExecutor(
            new TestHostEnvironment(),
            options,
            NullLogger<PointCloudScenePublishExecutor>.Instance,
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
    public async Task Ingest_ColoredGrid_WritesServableTilesetAndRegistersDataset()
    {
        var outcome = await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(
                PointCloudSceneFixtures.ColoredGridGeographic(),
                SceneId: "pcloud-grid",
                DisplayName: "Point Cloud Grid",
                EditionGate: "enterprise"),
            CancellationToken.None);

        outcome.SceneId.Should().Be("pcloud-grid");
        outcome.PointCount.Should().Be(256);
        outcome.TileCount.Should().BeGreaterThan(1, "the grid must subdivide under the small per-tile cap.");
        outcome.HasColor.Should().BeTrue("format-3 LAS carries RGB.");

        // The tileset.json plus every referenced .pnts tile must exist under the
        // promoted asset root so the standard scene serving path can stream them.
        var tilesetPath = Path.Combine(outcome.AssetRoot, "tileset.json");
        File.Exists(tilesetPath).Should().BeTrue();

        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(tilesetPath));
        json.RootElement.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
        var uris = new List<string>();
        CollectContentUris(json.RootElement.GetProperty("root"), uris);
        uris.Should().NotBeEmpty();
        foreach (var uri in uris)
        {
            File.Exists(Path.Combine(outcome.AssetRoot, uri)).Should().BeTrue($"tile '{uri}' must be on disk.");
            uri.Should().EndWith(".pnts");
        }

        // No staging directory should survive a successful promotion.
        Directory.GetDirectories(_outputRoot, ".staging-*").Should().BeEmpty();

        _registration.Records.Should().ContainSingle();
        var record = _registration.Records[0];
        record.Id.Should().Be("pcloud-grid");
        record.Name.Should().Be("Point Cloud Grid");
        record.AssetRoot.Should().Be(outcome.AssetRoot);
        record.TilesetFileName.Should().Be("tileset.json");
        record.DatasetType.Should().Be(SceneDatasetType.HostedTiles);
        record.Crs.Should().Be("EPSG:4979");
        record.EditionGate.Should().Be("enterprise");
        record.IsPublic.Should().BeTrue();
        record.RequiresAuth.Should().BeFalse();
        record.Status.Should().Be(SceneDatasetStatus.Active);
    }

    [UnitTest]
    public async Task Ingest_PreservesClassificationIntensityAndRgb()
    {
        var outcome = await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(
                PointCloudSceneFixtures.SinglePointGeographic(),
                SceneId: "pcloud-attrs"),
            CancellationToken.None);

        // A single point produces exactly one .pnts leaf; decode it and assert
        // the PNTS feature/batch tables carry the preserved attributes.
        var pnts = await File.ReadAllBytesAsync(Path.Combine(outcome.AssetRoot, FindFirstPnts(outcome.AssetRoot)));

        var featureJson = ReadPntsFeatureTableJson(pnts);
        featureJson.RootElement.GetProperty("POINTS_LENGTH").GetInt32().Should().Be(1);
        featureJson.RootElement.TryGetProperty("RGB", out _).Should().BeTrue("coloured cloud must emit the RGB semantic.");

        var batchJson = ReadPntsBatchTableJson(pnts);
        batchJson.RootElement.TryGetProperty("INTENSITY", out _).Should().BeTrue();
        batchJson.RootElement.TryGetProperty("CLASSIFICATION", out _).Should().BeTrue();
    }

    [UnitTest]
    public async Task Ingest_SameSourceTwice_ProducesByteIdenticalTileset()
    {
        var las = PointCloudSceneFixtures.ColoredGridGeographic();
        var first = await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(las, SceneId: "det-a"), CancellationToken.None);
        var second = await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(las, SceneId: "det-b"), CancellationToken.None);

        var tileset1 = await File.ReadAllBytesAsync(Path.Combine(first.AssetRoot, "tileset.json"));
        var tileset2 = await File.ReadAllBytesAsync(Path.Combine(second.AssetRoot, "tileset.json"));
        tileset1.Should().Equal(tileset2, "identical LAS input must produce byte-identical tileset.json output.");

        var tile1 = await File.ReadAllBytesAsync(Path.Combine(first.AssetRoot, "points_0000.pnts"));
        var tile2 = await File.ReadAllBytesAsync(Path.Combine(second.AssetRoot, "points_0000.pnts"));
        tile1.Should().Equal(tile2, "identical LAS input must produce byte-identical .pnts output.");
    }

    [UnitTest]
    public async Task Ingest_RequiresAuth_RegistersProtectedScene()
    {
        var outcome = await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(
                PointCloudSceneFixtures.SinglePointGeographic(),
                SceneId: "protected-pcloud",
                RequiresAuth: true),
            CancellationToken.None);

        outcome.SceneId.Should().Be("protected-pcloud");
        var record = _registration.Records.Single();
        record.RequiresAuth.Should().BeTrue();
        record.IsPublic.Should().BeFalse();
    }

    [UnitTest]
    public async Task Ingest_DuplicateSceneId_ThrowsRegistrationConflict()
    {
        await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(PointCloudSceneFixtures.SinglePointGeographic(), SceneId: "dup"),
            CancellationToken.None);

        var act = async () => await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(PointCloudSceneFixtures.SinglePointGeographic(), SceneId: "dup"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.SceneRegistrationConflict);
    }

    [UnitTest]
    public async Task Ingest_LazCompressed_ThrowsLasFormatExceptionAndLeavesNothing()
    {
        var laz = PointCloudSceneFixtures.MarkCompressed(PointCloudSceneFixtures.ColoredGridGeographic());

        var act = async () => await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(laz, SceneId: "laz-rejected"),
            CancellationToken.None);

        await act.Should().ThrowAsync<LasFormatException>();

        // A rejected ingest must not leave any registration or output behind.
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Ingest_GarbageBytes_ThrowsLasFormatException()
    {
        var act = async () => await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(Encoding.UTF8.GetBytes("this is not a LAS point cloud")),
            CancellationToken.None);

        await act.Should().ThrowAsync<LasFormatException>();
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Ingest_InvalidCacheMaxAge_ThrowsOptionsInvalid()
    {
        var act = async () => await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(
                PointCloudSceneFixtures.SinglePointGeographic(),
                SceneId: "bad-cache",
                CacheMaxAgeSeconds: -1),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
    }

    [UnitTest]
    public async Task Ingest_PromotionFails_MarksActivityAsErrorAndRethrows()
    {
        // PA-203: IngestAsync already starts a span but never marked it failed when the
        // stage -> register -> promote pipeline's final promote step throws. Force
        // Directory.Move to fail by pre-occupying the final path with a plain file (not a
        // directory), which is exactly what the promotion catch/compensation branch guards
        // against, and assert the span now carries ActivityStatusCode.Error.
        const string sceneId = "pcloud-promote-fail";
        Directory.CreateDirectory(_outputRoot);
        var finalDirectory = Path.Combine(_outputRoot, sceneId);
        await File.WriteAllTextAsync(finalDirectory, "blocks Directory.Move");

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HonuaTelemetry.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var act = async () => await _executor.IngestAsync(
            new PointCloudSceneIngestRequest(PointCloudSceneFixtures.SinglePointGeographic(), SceneId: sceneId),
            CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();

        var span = activities.Should().ContainSingle(a =>
            a.OperationName == HonuaTelemetry.Activities.TileGeneration &&
            (string?)a.GetTagItem("honua.scene.source_kind") == "pointcloud").Which;
        span.Status.Should().Be(ActivityStatusCode.Error);
    }

    private static string FindFirstPnts(string assetRoot)
        => Path.GetFileName(Directory.GetFiles(assetRoot, "*.pnts").OrderBy(p => p, StringComparer.Ordinal).First());

    private static void CollectContentUris(JsonElement node, List<string> sink)
    {
        if (node.TryGetProperty("content", out var content)
            && content.TryGetProperty("uri", out var uri)
            && uri.GetString() is { } value)
        {
            sink.Add(value);
        }
        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                CollectContentUris(child, sink);
            }
        }
    }

    private static JsonDocument ReadPntsFeatureTableJson(byte[] pnts)
    {
        var featureJsonLength = BitConverter.ToInt32(pnts, 12);
        return JsonDocument.Parse(Encoding.UTF8.GetString(pnts, 28, featureJsonLength));
    }

    private static JsonDocument ReadPntsBatchTableJson(byte[] pnts)
    {
        var featureJsonLength = BitConverter.ToInt32(pnts, 12);
        var featureBinaryLength = BitConverter.ToInt32(pnts, 16);
        var batchJsonLength = BitConverter.ToInt32(pnts, 20);
        var batchJsonOffset = 28 + featureJsonLength + featureBinaryLength;
        return JsonDocument.Parse(Encoding.UTF8.GetString(pnts, batchJsonOffset, batchJsonLength));
    }
}
