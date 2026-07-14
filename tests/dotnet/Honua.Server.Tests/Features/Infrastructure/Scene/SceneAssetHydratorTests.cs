// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Honua.Scene.Assets;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// Unit tests for the scene asset read-through materialization cache (#2459,
/// ADR-0060). A fake in-memory object store stands in for S3/Azure so the
/// hydrate / no-op / re-hydrate / single-download behaviours are exercised
/// without Docker.
/// </summary>
public sealed class SceneAssetHydratorTests : IDisposable
{
    private readonly string _workRoot;

    public SceneAssetHydratorTests()
    {
        _workRoot = Path.Join(Path.GetTempPath(), $"honua-hydrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workRoot))
        {
            Directory.Delete(_workRoot, recursive: true);
        }
    }

    [UnitTest]
    public async Task EnsureLocalAsync_LegacyRecordWithoutPrefix_IsNoOp()
    {
        var storage = new InMemoryCloudFileStorage();
        var hydrator = new SceneAssetHydrator(storage, NullLogger<SceneAssetHydrator>.Instance);
        var localRoot = Path.Join(_workRoot, "legacy-local");

        var record = BuildRecord("legacy-scene", Guid.NewGuid(), assetStoragePrefix: null, localRoot);

        await hydrator.EnsureLocalAsync(record, localRoot, CancellationToken.None);

        Directory.Exists(localRoot).Should().BeFalse(
            "a legacy filesystem-only record has no storage prefix and must not trigger any download.");
        storage.DownloadCount.Should().Be(0);
    }

    [UnitTest]
    public async Task EnsureLocalAsync_WithPrefix_HydratesTreeFromStorage()
    {
        var storage = new InMemoryCloudFileStorage();
        var (prefix, files) = await SeedSceneAsync(storage, "hydrate-scene");
        var datasetId = Guid.NewGuid();

        var hydrator = new SceneAssetHydrator(storage, NullLogger<SceneAssetHydrator>.Instance);
        var localRoot = Path.Join(_workRoot, "node-b", "hydrate-scene");
        var record = BuildRecord("hydrate-scene", datasetId, prefix, localRoot);

        await hydrator.EnsureLocalAsync(record, localRoot, CancellationToken.None);

        foreach (var (relative, expected) in files)
        {
            var path = Path.Join(localRoot, relative);
            File.Exists(path).Should().BeTrue($"'{relative}' must be materialized locally.");
            (await File.ReadAllBytesAsync(path)).Should().Equal(expected);
        }

        File.Exists(Path.Join(localRoot, SceneAssetHydration.MarkerFileName)).Should().BeTrue(
            "hydration must stamp the local cache with a version marker.");
    }

    [UnitTest]
    public async Task EnsureLocalAsync_MarkerCurrent_DoesNotReDownload()
    {
        var storage = new InMemoryCloudFileStorage();
        var (prefix, _) = await SeedSceneAsync(storage, "cached-scene");
        var datasetId = Guid.NewGuid();

        var hydrator = new SceneAssetHydrator(storage, NullLogger<SceneAssetHydrator>.Instance);
        var localRoot = Path.Join(_workRoot, "cached", "cached-scene");
        var record = BuildRecord("cached-scene", datasetId, prefix, localRoot);

        await hydrator.EnsureLocalAsync(record, localRoot, CancellationToken.None);
        var afterFirst = storage.DownloadCount;
        afterFirst.Should().BeGreaterThan(0);

        // Second resolution of the same version must be a marker-only fast path.
        await hydrator.EnsureLocalAsync(record, localRoot, CancellationToken.None);

        storage.DownloadCount.Should().Be(afterFirst,
            "a current local cache must not trigger any further downloads.");
    }

    [UnitTest]
    public async Task EnsureLocalAsync_MarkerMismatch_ReHydrates()
    {
        var storage = new InMemoryCloudFileStorage();
        var (prefix, _) = await SeedSceneAsync(storage, "republish-scene");

        var hydrator = new SceneAssetHydrator(storage, NullLogger<SceneAssetHydrator>.Instance);
        var localRoot = Path.Join(_workRoot, "republish", "republish-scene");

        // First registration hydrates the original bytes.
        var firstRecord = BuildRecord("republish-scene", Guid.NewGuid(), prefix, localRoot);
        await hydrator.EnsureLocalAsync(firstRecord, localRoot, CancellationToken.None);
        (await File.ReadAllTextAsync(Path.Join(localRoot, "tileset.json")))
            .Should().Contain("v1");

        // Republish overwrites the stored bytes and mints a new dataset id, so the
        // marker token differs and the stale local cache must re-hydrate.
        var updatedTileset = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"v2\"}}");
        storage.Put(SceneAssetHydration.BuildObjectKey(prefix, "tileset.json"), updatedTileset);

        var secondRecord = BuildRecord("republish-scene", Guid.NewGuid(), prefix, localRoot);
        await hydrator.EnsureLocalAsync(secondRecord, localRoot, CancellationToken.None);

        (await File.ReadAllTextAsync(Path.Join(localRoot, "tileset.json")))
            .Should().Contain("v2", "a changed content version must re-hydrate the local cache.");
    }

    [UnitTest]
    public async Task EnsureLocalAsync_ConcurrentCalls_DownloadOnce()
    {
        // A small download delay guarantees the concurrent callers overlap so the
        // per-scene lock is actually contended.
        var storage = new InMemoryCloudFileStorage(downloadDelay: TimeSpan.FromMilliseconds(25));
        var (prefix, files) = await SeedSceneAsync(storage, "herd-scene");
        var datasetId = Guid.NewGuid();

        var hydrator = new SceneAssetHydrator(storage, NullLogger<SceneAssetHydrator>.Instance);
        var localRoot = Path.Join(_workRoot, "herd", "herd-scene");
        var record = BuildRecord("herd-scene", datasetId, prefix, localRoot);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => hydrator.EnsureLocalAsync(record, localRoot, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);

        // One hydration pass reads the manifest plus every asset exactly once; the
        // other seven callers observe the fresh marker after the single download.
        var expectedDownloads = files.Count + 1; // +1 for the manifest
        storage.DownloadCount.Should().Be(expectedDownloads,
            "concurrent first requests on a fresh node must trigger a single download pass.");
    }

    [UnitTest]
    public async Task EnsureLocalAsync_ManifestWithTraversalEscape_WritesNothingOutsideTempDirectory()
    {
        var storage = new InMemoryCloudFileStorage();
        var prefix = SceneAssetHydration.BuildPrefix("evil-scene");
        var localRoot = Path.Join(_workRoot, "evil-node", "evil-scene");

        // ../../ from the hydrate temp dir (localRoot + ".hydrate-<guid>", under _workRoot/evil-node)
        // resolves to a sentinel under _workRoot — outside the scene cache tree.
        var escapeTarget = Path.Join(_workRoot, "escaped-pwned.txt");
        var manifest = string.Join('\n', "../../escaped-pwned.txt", "tileset.json");
        storage.Put(
            SceneAssetHydration.BuildObjectKey(prefix, SceneAssetHydration.ManifestObjectName),
            Encoding.UTF8.GetBytes(manifest));
        // Seed bytes so a naive Path.Combine + WriteAllBytes implementation would actually plant the file.
        storage.Put(
            SceneAssetHydration.BuildObjectKey(prefix, "../../escaped-pwned.txt"),
            Encoding.UTF8.GetBytes("pwned"));

        var hydrator = new SceneAssetHydrator(storage, NullLogger<SceneAssetHydrator>.Instance);
        var record = BuildRecord("evil-scene", Guid.NewGuid(), prefix, localRoot);

        await hydrator.EnsureLocalAsync(record, localRoot, CancellationToken.None);

        File.Exists(escapeTarget).Should().BeFalse(
            "a manifest '..' traversal must never write outside the scene cache directory.");
        Directory.Exists(localRoot).Should().BeFalse(
            "hydration must abort on an unsafe manifest path rather than install a partial tree.");
    }

    [UnitTest]
    public async Task EnsureLocalAsync_ManifestWithAbsolutePath_WritesNothingOutsideTempDirectory()
    {
        var storage = new InMemoryCloudFileStorage();
        var prefix = SceneAssetHydration.BuildPrefix("abs-scene");
        var localRoot = Path.Join(_workRoot, "abs-node", "abs-scene");

        var absoluteTarget = Path.Join(_workRoot, "abs-pwned.txt");
        var manifest = string.Join('\n', absoluteTarget, "tileset.json");
        storage.Put(
            SceneAssetHydration.BuildObjectKey(prefix, SceneAssetHydration.ManifestObjectName),
            Encoding.UTF8.GetBytes(manifest));
        storage.Put(
            SceneAssetHydration.BuildObjectKey(prefix, absoluteTarget),
            Encoding.UTF8.GetBytes("pwned"));

        var hydrator = new SceneAssetHydrator(storage, NullLogger<SceneAssetHydrator>.Instance);
        var record = BuildRecord("abs-scene", Guid.NewGuid(), prefix, localRoot);

        await hydrator.EnsureLocalAsync(record, localRoot, CancellationToken.None);

        File.Exists(absoluteTarget).Should().BeFalse(
            "a rooted manifest path must never write outside the scene cache directory.");
        Directory.Exists(localRoot).Should().BeFalse();
    }

    [UnitTest]
    public async Task EnsureLocalAsync_Republish_KeepsAServableTreeThroughoutTheSwap()
    {
        var storage = new InMemoryCloudFileStorage();
        var (prefix, _) = await SeedSceneAsync(storage, "swap-scene");

        var hydrator = new SceneAssetHydrator(storage, NullLogger<SceneAssetHydrator>.Instance);
        var localRoot = Path.Join(_workRoot, "swap", "swap-scene");

        await hydrator.EnsureLocalAsync(
            BuildRecord("swap-scene", Guid.NewGuid(), prefix, localRoot), localRoot, CancellationToken.None);

        // Republish new content and re-hydrate; the install swap must leave a complete tree in place
        // (never the delete-then-move window with no tree at all).
        storage.Put(
            SceneAssetHydration.BuildObjectKey(prefix, "tileset.json"),
            Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"v2\"}}"));

        await hydrator.EnsureLocalAsync(
            BuildRecord("swap-scene", Guid.NewGuid(), prefix, localRoot), localRoot, CancellationToken.None);

        Directory.Exists(localRoot).Should().BeTrue();
        File.Exists(Path.Join(localRoot, "tileset.json")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Join(localRoot, "tileset.json"))).Should().Contain("v2");
        // No leftover move-aside sibling should remain after a successful swap.
        Directory.GetDirectories(Path.Join(_workRoot, "swap"))
            .Should().NotContain(d => d.Contains(".replaced-", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes a small tileset tree to a staging directory and uploads it through
    /// the production uploader so the stored keys/manifest match exactly what the
    /// hydrator expects. Returns the storage prefix and the (relativePath, bytes)
    /// contents (excluding the manifest).
    /// </summary>
    private async Task<(string Prefix, IReadOnlyList<(string Relative, byte[] Bytes)> Files)> SeedSceneAsync(
        InMemoryCloudFileStorage storage,
        string sceneId)
    {
        var stagingDir = Path.Join(_workRoot, "staging", sceneId);
        Directory.CreateDirectory(stagingDir);

        var files = new List<(string Relative, byte[] Bytes)>
        {
            ("tileset.json", Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"v1\"}}")),
            ("tile_0000.glb", new byte[] { 0x67, 0x6C, 0x54, 0x46, 0x01, 0x02, 0x03 })
        };
        foreach (var (relative, bytes) in files)
        {
            await File.WriteAllBytesAsync(Path.Join(stagingDir, relative), bytes);
        }

        var prefix = await SceneAssetStorageUploader.UploadTreeAsync(
            storage, stagingDir, sceneId, CancellationToken.None);
        prefix.Should().Be(SceneAssetHydration.BuildPrefix(sceneId));
        return (prefix!, files);
    }

    private static SceneDatasetRecord BuildRecord(
        string sceneId,
        Guid datasetId,
        string? assetStoragePrefix,
        string assetRoot)
        => new()
        {
            DatasetId = datasetId,
            Id = sceneId,
            Name = sceneId,
            AssetRoot = assetRoot,
            AssetStoragePrefix = assetStoragePrefix,
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            CachePolicy = SceneCachePolicy.Default,
            IsPublic = true,
            Status = SceneDatasetStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test"
        };
}
