// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;

namespace Honua.Db.Postgres.Features.Styling;

internal static class CatalogStyleSourceProjection
{
    public static string? ForLayer(string? json, int layerId, IReadOnlyCollection<int> associatedLayerIds)
    {
        if (associatedLayerIds.Count < 2 || json is null || JsonNode.Parse(json) is not JsonObject document ||
            document["sources"] is not JsonObject sources || document["layers"] is not JsonArray layers)
            return json;
        var target = CatalogStyleLayerSource.GetId(layerId);
        var ownedSources = associatedLayerIds.Where(id => IsAssociatedLayerSource(sources, id))
            .Select(CatalogStyleLayerSource.GetId).ToHashSet(StringComparer.Ordinal);
        if (!ownedSources.Contains(target) || ownedSources.Count < 2)
            return json;
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            if (layers[index] is JsonObject layer && layer["source"] is JsonValue value &&
                value.TryGetValue<string>(out var source) && ownedSources.Contains(source) && source != target)
                layers.RemoveAt(index);
        }
        foreach (var source in ownedSources.Where(source => source != target))
            sources.Remove(source);
        return document.ToJsonString();
    }

    private static bool IsAssociatedLayerSource(JsonObject sources, int layerId)
    {
        if (sources[CatalogStyleLayerSource.GetId(layerId)] is not JsonObject definition ||
            definition["tiles"] is not JsonArray tiles)
            return false;
        var template = CatalogStyleLayerSource.GetTileUrl(layerId);
        return tiles.Any(tile => tile is JsonValue value && value.TryGetValue<string>(out var url) && url == template);
    }
}
