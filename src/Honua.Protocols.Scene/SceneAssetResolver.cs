// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Protocols.Scene;

/// <summary>
/// Result of resolving a scene asset path to a concrete file under the scene's asset root.
/// </summary>
internal readonly record struct ResolvedSceneAsset(FileInfo File, string ContentType);

/// <summary>
/// Reasons a scene asset path cannot be resolved.
/// </summary>
internal enum SceneAssetResolutionError
{
    None,

    /// <summary>
    /// The path was empty, contained traversal segments, an absolute path,
    /// a UNC prefix, or otherwise rejected before file I/O. Returned as
    /// <c>400 Bad Request</c> rather than <c>404</c> so traversal probes
    /// cannot use response status to fingerprint the layout.
    /// </summary>
    InvalidPath,

    /// <summary>
    /// The path canonicalized to a location outside <see cref="SceneDataset.AssetRoot"/>.
    /// </summary>
    OutsideRoot,

    /// <summary>
    /// The resolved path does not exist on disk.
    /// </summary>
    NotFound
}

/// <summary>
/// Canonicalizes an asset path under a scene's <see cref="SceneDataset.AssetRoot"/>
/// and rejects anything that escapes the root or contains traversal hints.
/// </summary>
/// <remarks>
/// Cesium resolves nested tileset and content URIs relative to the
/// <c>tileset.json</c> URL, so every well-behaved request will arrive with a
/// forward-slash relative path. This resolver enforces that contract.
/// </remarks>
internal static class SceneAssetResolver
{
    /// <summary>
    /// Resolves an asset path under the scene's asset root.
    /// </summary>
    /// <param name="dataset">Scene whose asset root anchors resolution.</param>
    /// <param name="assetPath">Relative asset path from the URL.</param>
    /// <param name="resolved">The resolved asset on success.</param>
    /// <param name="error">The failure mode when resolution fails.</param>
    /// <returns><c>true</c> when the asset was resolved to an existing file under the root.</returns>
    public static bool TryResolve(
        SceneDataset dataset,
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

        var combined = Path.Combine(dataset.AssetRoot, assetPath);
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

        if (!IsUnderRoot(canonical, dataset.AssetRoot))
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
        if (HasLinkBetweenFileAndRoot(file, dataset.AssetRoot))
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
