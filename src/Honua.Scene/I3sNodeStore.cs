// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Assets;

namespace Honua.Scene;

/// <summary>
/// Loads a hosted scene's 3D Tiles tileset and projects it into the Esri I3S
/// node store a SceneServer client traverses: node pages (#1809) and per-field
/// attribute statistics (#1811). This is the read-side seam the I3S protocol
/// adapter consumes so the projection / tileset-loading logic stays in
/// <c>Honua.Scene</c> (which owns <see cref="SceneAssetResolver"/> and the
/// node-page projector) rather than the protocol slice.
/// </summary>
public static class I3sNodeStore
{
    /// <summary>
    /// Attempts to load and project the scene's tileset into ordered I3S node
    /// pages. Returns <see langword="false"/> when the scene has no loadable /
    /// parseable tileset (in which case the descriptor must not advertise node
    /// pages), so the caller can fall back to a descriptor-only response.
    /// </summary>
    /// <param name="scene">The hosted scene dataset.</param>
    /// <param name="pages">The projected node pages on success.</param>
    /// <returns><see langword="true"/> when node pages were produced.</returns>
    public static bool TryBuildNodePages(
        SceneDataset scene,
        out IReadOnlyList<I3sNodePageDocument> pages)
    {
        pages = [];
        if (!TryLoadTileset(scene, out var document))
        {
            return false;
        }

        var projected = I3sNodePageProjector.BuildPages(document);
        if (projected.Count == 0)
        {
            return false;
        }

        pages = projected;
        return true;
    }

    /// <summary>
    /// Loads and parses the scene's root <c>tileset.json</c> from disk under the
    /// asset root. Returns <see langword="false"/> for an absent, locked, or
    /// malformed tileset so callers fall back to descriptor-only behaviour rather
    /// than failing the request.
    /// </summary>
    /// <param name="scene">The hosted scene dataset.</param>
    /// <param name="document">The parsed tileset on success.</param>
    /// <returns><see langword="true"/> when the tileset was loaded.</returns>
    public static bool TryLoadTileset(
        SceneDataset scene,
        out TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(scene);
        document = new TilesetDocument();

        if (!SceneAssetResolver.TryResolve(scene, scene.TilesetFileName, out var resolved, out _))
        {
            return false;
        }

        try
        {
            using var stream = resolved.File.OpenRead();
            var parsed = JsonSerializer.Deserialize(stream, TilesetJsonContext.Default.TilesetDocument);
            if (parsed is null)
            {
                return false;
            }

            document = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
