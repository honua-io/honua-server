// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Scene;

/// <summary>
/// Shared filesystem mechanics for the scene publishing pipeline: resolving the
/// final and staging output directories, atomically promoting a staged tileset
/// to its final path, and best-effort staging cleanup. Both the feature-layer
/// generation path (<see cref="SceneTilesPublishExecutor"/>) and the CityGML/BIM
/// ingest path (<see cref="CityGmlScenePublishExecutor"/>) write tileset bytes to
/// an intent-scoped staging directory, register the dataset, then promote — so
/// the directory mechanics live here once rather than being duplicated per
/// executor.
/// </summary>
internal static class SceneTilesetStaging
{
    /// <summary>
    /// Resolves the final asset directory for <paramref name="sceneId"/> under the
    /// configured output root, rooting a relative output root against the host
    /// content root.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="sceneId"/> is not a simple relative segment. Every current
    /// caller already validates sceneId via <c>SceneDatasetValidator.TryValidateSceneId</c>
    /// before reaching here, but this is a shared filesystem primitive reused by
    /// three publish executors — this is a defense-in-depth guard (matching the
    /// containment-check style used elsewhere in this assembly) so a validation
    /// gap in a future caller can never let a rooted/escaping sceneId silently
    /// drop <paramref name="contentRootPath"/>/<paramref name="outputRoot"/> via
    /// <see cref="Path.Combine(string, string)"/>.
    /// </exception>
    public static string ResolveOutputDirectory(string contentRootPath, string outputRoot, string sceneId)
    {
        if (!IsSimpleSegment(sceneId))
        {
            throw new InvalidOperationException(
                $"sceneId '{sceneId}' must be a simple relative segment (no path separators, rooted paths, or '..').");
        }

        // Reached only when outputRoot is confirmed relative above, so this combine
        // can never drop contentRootPath.
        var rooted = Path.IsPathRooted(outputRoot)
            ? outputRoot
            : Path.Join(contentRootPath, outputRoot);
        return Path.GetFullPath(Path.Join(rooted, sceneId));
    }

    /// <summary>
    /// Resolves an intent-scoped staging directory under the configured output
    /// root. The name is prefixed with '.' so it can never collide with a valid
    /// sceneId (the canonical validator forbids leading dots/hyphens), letting
    /// concurrent publishes for the same sceneId stage independently.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="intentId"/> is not a simple relative segment. Every current
    /// caller mints intentId itself via <c>Guid.NewGuid().ToString("N")</c>, but
    /// this defense-in-depth guard closes the same class of gap as
    /// <see cref="ResolveOutputDirectory"/> above for any future caller.
    /// </exception>
    public static string ResolveStagingDirectory(string contentRootPath, string outputRoot, string intentId)
    {
        if (!IsSimpleSegment(intentId))
        {
            throw new InvalidOperationException(
                $"intentId '{intentId}' must be a simple relative segment (no path separators, rooted paths, or '..').");
        }

        // Reached only when outputRoot is confirmed relative above, so this combine
        // can never drop contentRootPath.
        var rooted = Path.IsPathRooted(outputRoot)
            ? outputRoot
            : Path.Join(contentRootPath, outputRoot);
        return Path.GetFullPath(Path.Join(rooted, $".staging-{intentId}"));
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is safe to
    /// combine with a base directory: non-empty, contains no path separators or
    /// <c>..</c> segment, and is not rooted/absolute. Mirrors the
    /// <c>ArtifactNames.IsSimpleFileName</c> guard used for the same class of bug
    /// in the custom-code execution harness.
    /// </summary>
    private static bool IsSimpleSegment(string? value) =>
        !string.IsNullOrEmpty(value) &&
        !value.Contains('/', StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !Path.IsPathRooted(value);

    /// <summary>
    /// Atomically promotes a fully-written staging directory to its final path.
    /// If the final path already holds detritus from a prior partial run it is
    /// cleared first (the registry record is the canonical authority and now
    /// points at the staged bytes). <paramref name="onOverwroteStaleFinalDir"/>
    /// is invoked when a stale final directory is removed so the caller can log it.
    /// </summary>
    public static void PromoteStagingToFinal(
        string stagingDirectory,
        string finalDirectory,
        Action<string>? onOverwroteStaleFinalDir = null)
    {
        var parentDir = Path.GetDirectoryName(finalDirectory);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }
        if (Directory.Exists(finalDirectory))
        {
            onOverwroteStaleFinalDir?.Invoke(finalDirectory);
            Directory.Delete(finalDirectory, recursive: true);
        }
        Directory.Move(stagingDirectory, finalDirectory);
    }

    /// <summary>
    /// Best-effort delete of a staging directory. A failed delete is harmless (no
    /// registry record points at it, so it is never served);
    /// <paramref name="onError"/> is invoked so the caller can log it for a janitor sweep.
    /// </summary>
    public static void TryDeleteStaging(string stagingDirectory, Action<Exception>? onError = null)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        // Intentionally generic: staging cleanup is best-effort and must never throw
        // back into a publish/compensation flow; onError lets the caller log it for a
        // janitor sweep instead.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            onError?.Invoke(ex);
        }
    }
}
