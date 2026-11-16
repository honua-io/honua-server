// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalVsiPath
{
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

    private static string CombineLocal(string directory, string key)
    {
        var normalizedKey = key.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedKey))
        {
            throw new InvalidOperationException("Local object key must be a relative path under the bucket root.");
        }

        var root = Path.GetFullPath(directory);
        // key is attacker-influenced (an object key from job configuration) but
        // normalizedKey is validated immediately above to not be rooted, so it can
        // never make Path.Combine here silently discard root; the containment check
        // below is the actual escape guard.
        var combined = Path.GetFullPath(Path.Join(root, normalizedKey));
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
