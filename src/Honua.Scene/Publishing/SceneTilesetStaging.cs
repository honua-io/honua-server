// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Scene;

internal static class SceneTilesetStaging
{
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

    private static bool IsSimpleSegment(string? value) =>
        !string.IsNullOrEmpty(value) &&
        !value.Contains('/', StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !Path.IsPathRooted(value);

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
