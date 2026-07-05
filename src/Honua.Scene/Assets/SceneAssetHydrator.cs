// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Scene.Assets;

/// <summary>
/// Read-through local materialization cache for hosted 3D Tiles scene assets
/// (#2459, ADR-0060). When a scene registration carries an object-store prefix and
/// the node's local asset root is missing or stale, the tree is downloaded from the
/// shared object store and atomically installed so the standard filesystem serving
/// path (<see cref="SceneAssetResolver"/>) can stream it. Legacy datasets (no
/// storage prefix) are skipped entirely and keep serving straight off local disk.
/// </summary>
internal sealed partial class SceneAssetHydrator : ISceneAssetHydrator
{
    private readonly ICloudFileStorage _storage;
    private readonly ILogger<SceneAssetHydrator> _logger;

    // One lock per scene id so a thundering herd of first requests on a fresh node
    // performs a single download; other callers wait, then observe the fresh marker.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public SceneAssetHydrator(ICloudFileStorage storage, ILogger<SceneAssetHydrator> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task EnsureLocalAsync(
        SceneDatasetRecord record,
        string localAssetRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(localAssetRoot);

        var prefix = record.AssetStoragePrefix;
        if (string.IsNullOrEmpty(prefix))
        {
            // Legacy filesystem-only dataset: nothing to hydrate.
            return;
        }

        var token = SceneAssetHydration.BuildToken(record.DatasetId, prefix);
        if (SceneAssetHydration.IsMarkerCurrent(localAssetRoot, token))
        {
            // Fast path: local cache already materialized for this version.
            return;
        }

        var gate = _locks.GetOrAdd(record.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check under the lock: a concurrent caller may have hydrated
            // while we waited, so only one download runs per version.
            if (SceneAssetHydration.IsMarkerCurrent(localAssetRoot, token))
            {
                return;
            }

            await DownloadAndInstallAsync(record.Id, prefix, localAssetRoot, token, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadAndInstallAsync(
        string sceneId,
        string prefix,
        string localAssetRoot,
        string token,
        CancellationToken cancellationToken)
    {
        var manifestKey = SceneAssetHydration.BuildObjectKey(prefix, SceneAssetHydration.ManifestObjectName);
        var manifestBytes = await _storage.DownloadBytesAsync(manifestKey, cancellationToken).ConfigureAwait(false);
        if (manifestBytes is null)
        {
            // The registry claims a prefix but the object store has no manifest.
            // Degrade gracefully: leave the local cache untouched so the resolver
            // returns 404 rather than a 500. A later request retries.
            SceneAssetHydratorLog.ManifestMissing(_logger, sceneId);
            return;
        }

        var relativePaths = ParseManifest(manifestBytes);
        var tempDirectory = localAssetRoot + ".hydrate-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(tempDirectory);

            foreach (var relativePath in relativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var objectKey = SceneAssetHydration.BuildObjectKey(prefix, relativePath);
                var bytes = await _storage.DownloadBytesAsync(objectKey, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    SceneAssetHydratorLog.AssetMissing(_logger, sceneId);
                    TryDeleteDirectory(tempDirectory);
                    return;
                }

                var destination = Path.Combine(
                    tempDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var destinationDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }
                await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
            }

            // Stamp the marker before install so the promoted directory is
            // self-describing and the next request short-circuits.
            await SceneAssetHydration.WriteMarkerAsync(tempDirectory, token, cancellationToken)
                .ConfigureAwait(false);

            InstallAtomically(tempDirectory, localAssetRoot);
            SceneAssetHydratorLog.Hydrated(_logger, sceneId, relativePaths.Count);
        }
        catch
        {
            TryDeleteDirectory(tempDirectory);
            throw;
        }
    }

    /// <summary>
    /// Atomically replaces the local asset root with the freshly downloaded temp
    /// directory. <see cref="Directory.Move"/> is atomic on the same filesystem, so
    /// any concurrent reader observes either the previous or the complete new tree.
    /// </summary>
    private static void InstallAtomically(string tempDirectory, string localAssetRoot)
    {
        var parent = Path.GetDirectoryName(localAssetRoot);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        if (Directory.Exists(localAssetRoot))
        {
            Directory.Delete(localAssetRoot, recursive: true);
        }
        Directory.Move(tempDirectory, localAssetRoot);
    }

    private static List<string> ParseManifest(byte[] manifestBytes)
    {
        var text = Encoding.UTF8.GetString(manifestBytes);
        var results = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            results.Add(line);
        }
        return results;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover temp dir is harmless (no registry record
            // points at it) and gets swept by the next successful hydration.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static partial class SceneAssetHydratorLog
    {
        [LoggerMessage(EventId = 8460, Level = LogLevel.Information,
            Message = "Hydrated scene '{SceneId}' local asset cache from object store ({FileCount} files).")]
        public static partial void Hydrated(ILogger logger, string sceneId, int fileCount);

        [LoggerMessage(EventId = 8461, Level = LogLevel.Warning,
            Message = "Scene '{SceneId}' declares a storage prefix but its manifest is absent from the object store; serving will fall back to local assets if present.")]
        public static partial void ManifestMissing(ILogger logger, string sceneId);

        [LoggerMessage(EventId = 8462, Level = LogLevel.Warning,
            Message = "Scene '{SceneId}' manifest references an object missing from the store; hydration aborted and will retry on the next request.")]
        public static partial void AssetMissing(ILogger logger, string sceneId);
    }
}
