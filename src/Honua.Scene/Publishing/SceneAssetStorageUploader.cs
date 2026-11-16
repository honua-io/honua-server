// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Scene.Assets;

namespace Honua.Infrastructure.Scene;

internal static class SceneAssetStorageUploader
{
    private const string AssetContentType = "application/octet-stream";

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
            var absolute = Path.Join(sourceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
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
