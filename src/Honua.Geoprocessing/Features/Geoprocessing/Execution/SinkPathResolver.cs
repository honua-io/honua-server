// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Containment helper shared by the file-writing sink executors (<c>sink.geojson-file</c>,
/// <c>sink.quarantine</c>). When <see cref="GeoprocessingExecutorOptions.SinkRootDirectory"/> is
/// configured, a caller-supplied sink path is resolved within that root and any path that escapes it
/// (via <c>..</c> traversal or an absolute path) is rejected, bounding where an operator-submitted plan
/// can write. When no root is configured the path is returned unchanged (operator-trusted behavior).
/// </summary>
internal static class SinkPathResolver
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="path"/> against the optional <paramref name="rootDirectory"/>.
    /// </summary>
    /// <returns><see langword="true"/> with <paramref name="resolved"/> set when the path is allowed;
    /// <see langword="false"/> with <paramref name="error"/> set when it escapes the configured root.</returns>
    public static bool TryResolve(string? rootDirectory, string path, out string resolved, out string? error)
    {
        resolved = path;
        error = null;

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            // No containment configured: operator-trusted path, used as-is.
            return true;
        }

        var rootFull = Path.GetFullPath(rootDirectory);

        // Path.Combine returns an absolute `path` unchanged (discarding the root); Path.GetFullPath then
        // normalizes any ".." segments. The containment check below rejects anything outside the root.
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(rootFull, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "is not a valid file path.";
            return false;
        }

        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!candidate.Equals(rootFull, PathComparison) &&
            !candidate.StartsWith(rootWithSeparator, PathComparison))
        {
            error = "resolves outside the configured sink root directory.";
            return false;
        }

        resolved = candidate;
        return true;
    }
}
