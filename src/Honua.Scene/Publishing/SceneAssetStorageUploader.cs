// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Scene.Assets;

namespace Honua.Infrastructure.Scene;

/// <summary>
/// Uploads a freshly staged scene tileset tree to the shared object store so it
/// can be hydrated on any node (#2459, ADR-0060). Each asset is uploaded under a
/// deterministic <c>scenes/{sceneId}/{relativePath}</c> key, plus a manifest of
/// relative paths that a hydrating node reads first to enumerate the tree without
/// relying on list-API pagination or ordering.
/// </summary>
/// <remarks>
/// Per-file <see cref="ICloudFileStorage.UploadAsync(FileUploadRequest, CancellationToken)"/>
/// with an explicit <see cref="FileUploadRequest.ObjectKeyOverride"/> is used rather
/// than <see cref="ICloudFileStorage.UploadBatchAsync"/> because the batch API mints
/// random object ids and cannot produce the stable, prefix-scoped keys the
/// read-through hydrator downloads by.
/// </remarks>
internal static class SceneAssetStorageUploader
{
    private const string AssetContentType = "application/octet-stream";

    /// <summary>
    /// Uploads every file under <paramref name="sourceDirectory"/> (the staged
    /// tileset tree) to the shared object store under a stable scene prefix and
    /// writes the accompanying manifest. Returns the storage prefix to record on
    /// the scene registration, or <c>null</c> when no object store is available so
    /// the caller falls back to the legacy filesystem-only (node-local) path.
    /// </summary>
    /// <param name="storage">The shared object store, or <c>null</c> when unavailable.</param>
    /// <param name="sourceDirectory">Fully written staging directory holding the tileset tree.</param>
    /// <param name="sceneId">Resolved scene slug; anchors the storage prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<string?> UploadTreeAsync(
        ICloudFileStorage? storage,
        string sourceDirectory,
        string sceneId,
        CancellationToken cancellationToken)
    {
        if (storage is null)
        {
            return null;
        }

        var prefix = SceneAssetHydration.BuildPrefix(sceneId);
        var relativePaths = EnumerateRelativePaths(sourceDirectory);

        foreach (var relativePath in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Safe: relativePath comes from EnumerateRelativePaths below, which derives
            // it via Path.GetRelativePath from files Directory.EnumerateFiles already
            // found under sourceDirectory — it can never be rooted or escape the tree.
            var absolute = Path.Combine(sourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            await using var content = new FileStream(
                absolute, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var result = await storage.UploadAsync(
                new FileUploadRequest
                {
                    Content = content,
                    FileName = Path.GetFileName(relativePath),
                    ContentType = AssetContentType,
                    ObjectKeyOverride = SceneAssetHydration.BuildObjectKey(prefix, relativePath)
                },
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to upload scene asset '{relativePath}' to the object store.");
            }
        }

        // Manifest last: only a complete tree yields a manifest, so a hydrating
        // node that reads the manifest is guaranteed every listed object exists.
        var manifestBytes = Encoding.UTF8.GetBytes(string.Join('\n', relativePaths));
        await using (var manifestStream = new MemoryStream(manifestBytes, writable: false))
        {
            var manifestResult = await storage.UploadAsync(
                new FileUploadRequest
                {
                    Content = manifestStream,
                    FileName = SceneAssetHydration.ManifestObjectName,
                    ContentType = "text/plain",
                    ObjectKeyOverride = SceneAssetHydration.BuildObjectKey(
                        prefix, SceneAssetHydration.ManifestObjectName)
                },
                cancellationToken).ConfigureAwait(false);

            if (!manifestResult.Success)
            {
                throw new InvalidOperationException(
                    "Failed to upload the scene asset manifest to the object store.");
            }
        }

        return prefix;
    }

    /// <summary>
    /// Enumerates every file under <paramref name="sourceDirectory"/> as
    /// forward-slash relative paths in a deterministic ordinal order, excluding any
    /// local-only hydration marker that may already be present.
    /// </summary>
    private static List<string> EnumerateRelativePaths(string sourceDirectory)
    {
        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            if (string.Equals(relative, SceneAssetHydration.MarkerFileName, StringComparison.Ordinal))
            {
                continue;
            }
            results.Add(relative);
        }
        results.Sort(StringComparer.Ordinal);
        return results;
    }
}
