// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Execution;

internal static class SinkPathResolver
{
    private const string InvalidPathMessage =
        "path must be a relative file path under the configured geoprocessing output root.";

    public static bool TryResolveOutputPath(
        string outputRootDirectory,
        string requestedPath,
        out string resolvedPath,
        out string? error)
    {
        resolvedPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(outputRootDirectory))
        {
            error = "geoprocessing output root is not configured.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestedPath) ||
            Path.IsPathRooted(requestedPath) ||
            requestedPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = InvalidPathMessage;
            return false;
        }

        try
        {
            var root = Path.GetFullPath(outputRootDirectory);
            var candidate = Path.GetFullPath(Path.Combine(root, requestedPath));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(rootPrefix, comparison) ||
                string.IsNullOrWhiteSpace(Path.GetFileName(candidate)))
            {
                error = InvalidPathMessage;
                return false;
            }

            resolvedPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = InvalidPathMessage;
            return false;
        }
    }
}
