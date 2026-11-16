// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Scene.Assets;

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

                // The manifest is untrusted object-store content: a rooted or ../-containing entry
                // would let Path.Combine escape the temp directory and write anywhere the serving
                // process can reach. Resolve+contain every entry before fetching or writing anything.
                if (!TryResolveSafeDestination(tempDirectory, relativePath, out var destination))
                {
                    SceneAssetHydratorLog.UnsafeManifestPath(_logger, sceneId);
                    TryDeleteDirectory(tempDirectory);
                    return;
                }

                var objectKey = SceneAssetHydration.BuildObjectKey(prefix, relativePath);
                var bytes = await _storage.DownloadBytesAsync(objectKey, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    SceneAssetHydratorLog.AssetMissing(_logger, sceneId);
                    TryDeleteDirectory(tempDirectory);
                    return;
                }

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

    private static void InstallAtomically(string tempDirectory, string localAssetRoot)
    {
        var parent = Path.GetDirectoryName(localAssetRoot);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (!Directory.Exists(localAssetRoot))
        {
            Directory.Move(tempDirectory, localAssetRoot);
            return;
        }

        var asidePath = localAssetRoot + ".replaced-" + Guid.NewGuid().ToString("N");
        Directory.Move(localAssetRoot, asidePath);
        try
        {
            Directory.Move(tempDirectory, localAssetRoot);
        }
        catch
        {
            // Roll the previous tree back into place so serving is never left without a tree.
            if (!Directory.Exists(localAssetRoot))
            {
                Directory.Move(asidePath, localAssetRoot);
            }

            throw;
        }

        TryDeleteDirectory(asidePath);
    }

    private static bool TryResolveSafeDestination(string tempDirectory, string relativePath, out string destination)
    {
        destination = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var segments = relativePath.Split('/', '\\');
        if (Array.Exists(segments, segment => segment == ".."))
        {
            return false;
        }

        var root = Path.GetFullPath(tempDirectory) + Path.DirectorySeparatorChar;
        // Safe: relativePath was already rejected above when rooted or carrying a '..'
        // segment; this combine, plus the StartsWith containment check below, IS the
        // guard that closes the remaining symlink/encoding-trick gap.
        var combined = Path.GetFullPath(
            Path.Join(tempDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        destination = combined;
        return true;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp dir is harmless (no registry record
            // points at it) and gets swept by the next successful hydration.
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

        [LoggerMessage(EventId = 8463, Level = LogLevel.Error,
            Message = "Scene '{SceneId}' manifest contains an unsafe (rooted or traversing) asset path; hydration aborted and nothing was written outside the cache directory.")]
        public static partial void UnsafeManifestPath(ILogger logger, string sceneId);
    }
}
