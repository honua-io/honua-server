// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Scene;

/// <summary>
/// Resolves scene datasets from <see cref="SceneDatasetOptions"/>.
/// Replaced by a database-backed implementation in honua-server-844.
/// </summary>
internal sealed class ConfigurationSceneDatasetRegistry : ISceneDatasetRegistry
{
    private readonly FrozenDictionary<string, SceneDataset> _scenes;

    public ConfigurationSceneDatasetRegistry(
        IOptions<SceneDatasetOptions> options,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var entries = options.Value?.Datasets ?? new List<SceneDatasetEntry>();
        var contentRoot = environment.ContentRootPath;

        var map = new Dictionary<string, SceneDataset>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!TryBuild(entry, contentRoot, out var dataset))
            {
                continue;
            }

            map[dataset.Id] = dataset;
        }

        _scenes = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public ValueTask<SceneDataset?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
        {
            return ValueTask.FromResult<SceneDataset?>(null);
        }

        return _scenes.TryGetValue(id, out var dataset)
            ? ValueTask.FromResult<SceneDataset?>(dataset)
            : ValueTask.FromResult<SceneDataset?>(null);
    }

    private static bool TryBuild(
        SceneDatasetEntry entry,
        string contentRoot,
        [NotNullWhen(true)] out SceneDataset? dataset)
    {
        dataset = null;

        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.AssetRoot))
        {
            return false;
        }

        var assetRoot = Path.IsPathRooted(entry.AssetRoot)
            ? entry.AssetRoot
            : Path.Combine(contentRoot, entry.AssetRoot);

        // Path.GetFullPath preserves a trailing directory separator, but
        // DirectoryInfo.FullName never reports one for non-root directories.
        // Strip the trailing separator here so downstream string equality
        // checks (notably SceneAssetResolver.HasLinkBetweenFileAndRoot)
        // can match parent walks against this canonical form.
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetRoot));

        dataset = new SceneDataset
        {
            Id = entry.Id,
            Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name!,
            Description = entry.Description,
            AssetRoot = canonical,
            TilesetFileName = string.IsNullOrWhiteSpace(entry.TilesetFileName)
                ? "tileset.json"
                : entry.TilesetFileName!,
            AccessPolicy = entry.AccessPolicy
        };
        return true;
    }
}
