// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalVsiPath
{
    public static string Build(
        CloudStorageProvider provider,
        string bucket,
        string objectKey,
        string? localStorageRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (objectKey.StartsWith("//", StringComparison.Ordinal) || objectKey.Contains('\\'))
        {
            throw new InvalidOperationException("Object key contains disallowed path syntax.");
        }

        var key = objectKey.TrimStart('/');
        if (!RasterDescriptorChecks.IsSafeObjectStoreKey(key))
        {
            throw new InvalidOperationException("Object key failed safety validation.");
        }

        return provider switch
        {
            CloudStorageProvider.AwsS3 => BuildRemote("/vsis3", bucket, key),
            CloudStorageProvider.AzureBlob => BuildRemote("/vsiaz", bucket, key),
            CloudStorageProvider.Local => CombineLocal(bucket, key, localStorageRoot),
            _ => throw new NotSupportedException(
                $"Cloud storage provider '{provider}' has no GDAL VSI handler for multidimensional coverage reads."),
        };
    }

    private static string BuildRemote(string prefix, string bucket, string key)
    {
        if (!RasterDescriptorChecks.IsOpaqueReference(bucket))
        {
            throw new InvalidOperationException("Storage bucket reference failed safety validation.");
        }

        return $"{prefix}/{bucket}/{key}";
    }

    private static string CombineLocal(string directory, string key, string? localStorageRoot)
    {
        if (directory.Any(char.IsControl))
        {
            throw new InvalidOperationException("Local bucket path contains control characters.");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Without a configured root the read would resolve against the worker's
        // working directory, outside any containment check.
        if (string.IsNullOrWhiteSpace(localStorageRoot))
        {
            throw new InvalidOperationException("Local storage reads need a configured local storage root.");
        }

        var safeRoot = Path.GetFullPath(localStorageRoot);
        var safeRootTrimmed = safeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var safeRootPrefix = safeRootTrimmed + Path.DirectorySeparatorChar;
        var root = Path.IsPathRooted(directory)
            ? Path.GetFullPath(directory)
            : Path.GetFullPath(Path.Join(safeRoot, directory));
        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(rootTrimmed, safeRootTrimmed, comparison)
            && !root.StartsWith(safeRootPrefix, comparison))
        {
            throw new InvalidOperationException("Local bucket directory must resolve under the configured local storage root.");
        }

        var normalizedKey = key.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedKey))
        {
            throw new InvalidOperationException("Local object key must be a relative path under the bucket root.");
        }

        var combined = Path.GetFullPath(Path.Join(root, normalizedKey));
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException("Local object key must resolve to a path under the bucket root.");
        }

        VerifyNoEscapingSymlink(combined, rootPrefix, comparison);
        return combined;
    }

    private static void VerifyNoEscapingSymlink(string path, string rootPrefix, StringComparison comparison)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            var resolved = Path.GetFullPath(target.FullName);
            if (!resolved.StartsWith(rootPrefix, comparison))
            {
                throw new InvalidOperationException("Local object key symlink target escapes the permitted bucket root.");
            }
        }
    }
}
