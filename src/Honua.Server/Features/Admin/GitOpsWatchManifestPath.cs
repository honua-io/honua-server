// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static class GitOpsWatchManifestPath
{
    internal const string DefaultPath = "manifests/";
    internal const string HonuaManifestFileName = "honua-manifest.json";
    internal const string ManifestFileName = "manifest.json";
    internal const string RelativePathErrorMessage =
        "Manifest path must be a relative repository path without '.' or '..' segments.";
    internal const string GlobUnsupportedErrorMessage =
        "Manifest path glob patterns are not supported. Use an exact manifest file path or a directory containing honua-manifest.json or manifest.json.";

    internal static bool TryNormalize(
        string? path,
        out string normalizedPath,
        out string? errorMessage)
    {
        normalizedPath = string.IsNullOrWhiteSpace(path) ? DefaultPath : path.Trim();

        if (!IsRelativeRepositoryPath(normalizedPath))
        {
            errorMessage = RelativePathErrorMessage;
            return false;
        }

        if (ContainsGlobSyntax(normalizedPath))
        {
            errorMessage = GlobUnsupportedErrorMessage;
            return false;
        }

        if (IsDirectoryIntent(normalizedPath) && !normalizedPath.EndsWith('/'))
        {
            normalizedPath += "/";
        }

        errorMessage = null;
        return true;
    }

    internal static bool IsDirectoryIntent(string path)
    {
        if (path.EndsWith('/'))
        {
            return true;
        }

        var lastSegmentStart = path.LastIndexOf('/') + 1;
        var lastSegment = path[lastSegmentStart..];
        return !lastSegment.Contains('.');
    }

    internal static IReadOnlyList<string> BuildSparseCheckoutPatterns(string manifestPath)
    {
        var patterns = new List<string>();
        if (!IsDirectoryIntent(manifestPath))
        {
            patterns.Add(manifestPath);
        }

        var directoryPath = manifestPath.TrimEnd('/');

        if (!string.IsNullOrEmpty(directoryPath))
        {
            patterns.Add($"{directoryPath}/{HonuaManifestFileName}");
            patterns.Add($"{directoryPath}/{ManifestFileName}");
        }

        return patterns.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsRelativeRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith('/') ||
            path.StartsWith('\\') ||
            path.Contains('\\'))
        {
            return false;
        }

        foreach (var c in path)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsGlobSyntax(string path)
    {
        foreach (var c in path)
        {
            if (c is '*' or '?' or '[' or ']' or '{' or '}')
            {
                return true;
            }
        }

        return false;
    }
}
