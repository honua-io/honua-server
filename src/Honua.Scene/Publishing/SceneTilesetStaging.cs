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
    public static string ResolveOutputDirectory(string contentRootPath, string outputRoot, string sceneId)
    {
        var rooted = Path.IsPathRooted(outputRoot)
            ? outputRoot
            : Path.Combine(contentRootPath, outputRoot);
        return Path.GetFullPath(Path.Combine(rooted, sceneId));
    }

    /// <summary>
    /// Resolves an intent-scoped staging directory under the configured output
    /// root. The name is prefixed with '.' so it can never collide with a valid
    /// sceneId (the canonical validator forbids leading dots/hyphens), letting
    /// concurrent publishes for the same sceneId stage independently.
    /// </summary>
    public static string ResolveStagingDirectory(string contentRootPath, string outputRoot, string intentId)
    {
        var rooted = Path.IsPathRooted(outputRoot)
            ? outputRoot
            : Path.Combine(contentRootPath, outputRoot);
        return Path.GetFullPath(Path.Combine(rooted, $".staging-{intentId}"));
    }

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
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }
}
