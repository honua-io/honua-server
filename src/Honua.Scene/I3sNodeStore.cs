// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene.Assets;

namespace Honua.Scene;

public static class I3sNodeStore
{
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
