// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Assets;

namespace Honua.Infrastructure.Scene;

internal static class SceneAssetStorageUploader
{
    private const string AssetContentType = "application/octet-stream";

    internal static async Task<string?> UploadTreeAsync(
        ICloudFileStorage? storage,
        string sourceDirectory,
        string sceneId,
        CancellationToken cancellationToken,
        Guid? publicationId = null)
    {
        if (storage is null)
        {
            return null;
        }

        // The returned prefix is recorded on the scene and read back verbatim, so it carries the
        // provider key prefix: a task role scoped to "<prefix>/*" can then write and read the assets.
        var prefix = storage.ObjectKeyPrefix + SceneAssetHydration.BuildPrefix(sceneId);
        if (publicationId is { } id)
        {
            prefix = $"{prefix}/{id:N}";
        }

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
                throw UploadFailure(result, $"Failed to upload scene asset '{relativePath}' to the object store.");
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
                throw UploadFailure(manifestResult, "Failed to upload the scene asset manifest to the object store.");
            }
        }

        return prefix;
    }

    // A permission refusal is a configuration fault that retrying cannot fix; anything else may be transient.
    private static Exception UploadFailure(UploadResult result, string message)
    {
        var failure = new InvalidOperationException(message);
        return result.IsAccessDenied ? SceneOutputUnwritableException.ForObjectStore(failure) : failure;
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
