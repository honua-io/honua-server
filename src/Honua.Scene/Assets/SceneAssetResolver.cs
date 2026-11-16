// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene.Assets;

internal readonly record struct ResolvedSceneAsset(FileInfo File, string ContentType);

internal enum SceneAssetResolutionError
{
    None,

    InvalidPath,

    OutsideRoot,

    NotFound
}

internal static class SceneAssetResolver
{
    public static bool TryResolve(
        SceneDataset dataset,
        string assetPath,
        out ResolvedSceneAsset resolved,
        out SceneAssetResolutionError error)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return TryResolve(dataset.AssetRoot, assetPath, out resolved, out error);
    }

    public static bool TryResolve(
        string assetRoot,
        string assetPath,
        out ResolvedSceneAsset resolved,
        out SceneAssetResolutionError error)
    {
        resolved = default;
        error = SceneAssetResolutionError.None;

        if (string.IsNullOrEmpty(assetPath))
        {
            error = SceneAssetResolutionError.InvalidPath;
            return false;
        }

        if (!IsSafeRelativePath(assetPath))
        {
            error = SceneAssetResolutionError.InvalidPath;
            return false;
        }

        // Safe: IsSafeRelativePath above already rejects a leading '/'/'\', a
        // drive-letter/UNC prefix, and '..' segments; IsUnderRoot below re-verifies
        // containment on the canonicalized result, so this combine cannot escape
        // assetRoot even under encoding tricks.
        var combined = Path.Join(assetRoot, assetPath);
        string canonical;
        try
        {
            canonical = Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = SceneAssetResolutionError.InvalidPath;
            return false;
        }

        if (!IsUnderRoot(canonical, assetRoot))
        {
            error = SceneAssetResolutionError.OutsideRoot;
            return false;
        }

        var file = new FileInfo(canonical);
        if (!file.Exists)
        {
            error = SceneAssetResolutionError.NotFound;
            return false;
        }

        // The lexical prefix check above guarantees the resolved string sits
        // under AssetRoot, but a symbolic link or other reparse-point segment
        // can still redirect file I/O to a target outside the root. Reject
        // any link found between the leaf and AssetRoot.
        if (HasLinkBetweenFileAndRoot(file, assetRoot))
        {
            error = SceneAssetResolutionError.OutsideRoot;
            return false;
        }

        resolved = new ResolvedSceneAsset(file, SceneContentTypes.Resolve(canonical));
        return true;
    }

    private static bool HasLinkBetweenFileAndRoot(FileInfo file, string assetRoot)
    {
        // DirectoryInfo.FullName never reports a trailing separator for
        // non-root directories, so trim AssetRoot to match. Callers
        // (ConfigurationSceneDatasetRegistry) already canonicalize, but
        // trimming here keeps the resolver correct for any other source
        // and makes the comparison robust under all input shapes.
        var normalizedRoot = Path.TrimEndingDirectorySeparator(assetRoot);

        FileSystemInfo? current = file;
        while (current is not null)
        {
            // Stop walking once we reach the asset root itself; the operator
            // owns whether AssetRoot resolves through a link, so we only
            // police segments strictly under it.
            if (current is DirectoryInfo dir &&
                string.Equals(dir.FullName, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsReparsePoint(current))
            {
                return true;
            }

            current = current switch
            {
                FileInfo f => f.Directory,
                DirectoryInfo d => d.Parent,
                _ => null
            };
        }

        // Walked past the asset root without ever matching it; treat as
        // outside-root so the request fails closed.
        return true;
    }

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        if (info.LinkTarget is not null)
        {
            return true;
        }

        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsSafeRelativePath(string assetPath)
    {
        if (assetPath.Length == 0)
        {
            return false;
        }

        if (assetPath[0] == '/' || assetPath[0] == '\\')
        {
            return false;
        }

        if (assetPath.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (assetPath.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        // Drive-letter or UNC prefix.
        if (assetPath.Length >= 2 && assetPath[1] == ':')
        {
            return false;
        }

        // Reject percent-encoded traversal that survived URL decoding accidentally.
        if (assetPath.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            assetPath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            assetPath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var span = assetPath.AsSpan();
        while (!span.IsEmpty)
        {
            var slash = span.IndexOf('/');
            ReadOnlySpan<char> segment;
            if (slash < 0)
            {
                segment = span;
                span = default;
            }
            else
            {
                segment = span[..slash];
                span = span[(slash + 1)..];
            }

            if (segment.IsEmpty)
            {
                // Disallow empty segments (collapsed slashes) and trailing slashes.
                return false;
            }

            if (segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUnderRoot(string canonical, string assetRoot)
    {
        // Asset roots are pre-canonicalized by ConfigurationSceneDatasetRegistry.
        var rootWithSeparator = assetRoot.EndsWith(Path.DirectorySeparatorChar)
            ? assetRoot
            : assetRoot + Path.DirectorySeparatorChar;

        // Match either the root itself (rare; would mean an empty asset path) or
        // anything strictly beneath it. Use OrdinalIgnoreCase to tolerate
        // case-folding filesystems while still requiring a separator boundary.
        if (string.Equals(canonical, assetRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return canonical.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
