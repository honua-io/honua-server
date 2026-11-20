// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Scene;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

// Scene assets are served straight from a dataset's asset root, so a root registered through the
// admin API must stay inside a directory set aside for scene assets: the documented scene data
// directory, the scene generation output root, or one an operator lists in configuration. A root
// is resolved the way the registry resolves it (relative to the content root) before the check, and
// must be a subdirectory: a root equal to a shared directory would expose (or, on rehydration,
// replace) every other scene stored in it.
internal static class SceneAssetRootPolicy
{
    internal const string DefaultSceneDirectory = "/var/lib/honua/scenes";

    internal const string Refusal =
        "Asset root must be a subdirectory of a scene asset directory (Scenes:AdminAssetDirectories, "
        + DefaultSceneDirectory + " or the SceneGeneration output root).";

    public static bool Allows(IServiceProvider services, string assetRoot)
    {
        var contentRoot = services.GetRequiredService<IHostEnvironment>().ContentRootPath;
        var configured = services.GetService<IOptions<SceneDatasetOptions>>()?.Value.AdminAssetDirectories ?? [];
        var generationRoot = services.GetService<IOptions<SceneGenerationServerOptions>>()?.Value.OutputRoot
            ?? new SceneGenerationServerOptions().OutputRoot;
        return IsInside(contentRoot, assetRoot, [DefaultSceneDirectory, generationRoot, .. configured]);
    }

    internal static bool IsInside(string contentRoot, string assetRoot, IEnumerable<string> directories)
    {
        var root = Resolve(contentRoot, assetRoot);
        return directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Resolve(contentRoot, directory))
            .Any(directory => root.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string Resolve(string contentRoot, string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Join(contentRoot, path)));
}
