// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing.Execution;

internal static class SinkPathResolver
{
    private const string InvalidPathMessage =
        "path must be a relative file path under the configured geoprocessing output root.";

    public static bool TryResolveOutputPath(
        string outputRootDirectory,
        string requestedPath,
        ExecutionJobRecord job,
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
            // Job ids are server-issued and tenant/owner scoped by admission. Hashing
            // keeps legacy ids from becoming filesystem path components; attempts never
            // share a destination, so a stale worker cannot replace a retry's output.
            if (string.IsNullOrWhiteSpace(job.OperationId) || job.AttemptCount < 0)
            {
                error = "a trusted job identity and execution attempt are required.";
                return false;
            }

            var jobKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(job.OperationId)));
            var root = Path.GetFullPath(Path.Join(outputRootDirectory, "jobs", jobKey,
                "a" + job.AttemptCount.ToString(CultureInfo.InvariantCulture)));
            // requestedPath is already checked non-rooted above, so this combine cannot
            // silently discard root; the canonicalized candidate is also re-validated
            // against rootPrefix below to reject ".." traversal that escapes the root.
            var candidate = Path.GetFullPath(Path.Join(root, requestedPath));
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
