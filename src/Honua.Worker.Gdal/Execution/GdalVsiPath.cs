// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Builds the GDAL virtual file system (<c>/vsi*</c>) path for a cloud-hosted
/// object so GDAL CLI tools can range-read it in place — no whole-file download.
/// Credentials are supplied to GDAL out of band via the worker's environment
/// (<c>AWS_*</c> / instance profile for S3, <c>AZURE_STORAGE_*</c> for Azure),
/// matching ADR-0039's "GDAL stays out-of-tree, server stays AOT" stance.
/// </summary>
internal static class GdalVsiPath
{
    /// <summary>
    /// Maps a provider/bucket/key triple to a GDAL-readable path.
    /// </summary>
    /// <param name="provider">Cloud storage provider hosting the object.</param>
    /// <param name="bucket">Bucket / container name (or directory for <see cref="CloudStorageProvider.Local"/>).</param>
    /// <param name="objectKey">Object key / blob path (or file name for local).</param>
    /// <returns>A path GDAL can open, e.g. <c>/vsis3/bucket/key.nc</c>.</returns>
    /// <exception cref="ArgumentException">Bucket or key is null/empty.</exception>
    /// <exception cref="NotSupportedException">The provider has no GDAL VSI handler.</exception>
    /// <exception cref="InvalidOperationException">
    /// A <see cref="CloudStorageProvider.Local"/> key escapes the configured bucket root
    /// (rooted segment or <c>..</c> traversal).
    /// </exception>
    public static string Build(CloudStorageProvider provider, string bucket, string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var key = objectKey.TrimStart('/');

        return provider switch
        {
            CloudStorageProvider.AwsS3 => $"/vsis3/{bucket}/{key}",
            CloudStorageProvider.AzureBlob => $"/vsiaz/{bucket}/{key}",
            CloudStorageProvider.Local => CombineLocal(bucket, key),
            _ => throw new NotSupportedException(
                $"Cloud storage provider '{provider}' has no GDAL VSI handler for multidimensional coverage reads."),
        };
    }

    /// <summary>
    /// Combines a Local-provider bucket root with a relative object key, rejecting any
    /// key that escapes the root. Mirrors the root-prefix containment check in
    /// <see cref="GdalScratch.CreateWorkspace"/>: a rooted segment or a <c>..</c>
    /// traversal must never resolve outside the configured bucket directory.
    /// </summary>
    private static string CombineLocal(string directory, string key)
    {
        var normalizedKey = key.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedKey))
        {
            throw new InvalidOperationException("Local object key must be a relative path under the bucket root.");
        }

        var root = Path.GetFullPath(directory);
        var combined = Path.GetFullPath(Path.Combine(root, normalizedKey));
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Local object key must resolve to a path under the bucket root.");
        }

        return combined;
    }
}
