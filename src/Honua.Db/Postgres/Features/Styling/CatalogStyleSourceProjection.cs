// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Nodes;

namespace Honua.Db.Postgres.Features.Styling;

internal static class CatalogStyleSourceProjection
{
    public static string? ForLayer(string? json, int layerId)
    {
        if (json is null || JsonNode.Parse(json) is not JsonObject document ||
            document["sources"] is not JsonObject sources || document["layers"] is not JsonArray layers)
            return json;
        var target = $"layer-{layerId.ToString(CultureInfo.InvariantCulture)}";
        var ownedSources = sources.Where(IsMigratedLayerSource).Select(source => source.Key).ToHashSet(StringComparer.Ordinal);
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

    private static bool IsMigratedLayerSource(KeyValuePair<string, JsonNode?> source)
    {
        if (!source.Key.StartsWith("layer-", StringComparison.Ordinal) ||
            !int.TryParse(source.Key.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var layerId) ||
            source.Value is not JsonObject definition || definition["tiles"] is not JsonArray tiles)
            return false;
        var template = $"/tiles/{layerId.ToString(CultureInfo.InvariantCulture)}/{{z}}/{{x}}/{{y}}.mvt";
        return tiles.Any(tile => tile is JsonValue value && value.TryGetValue<string>(out var url) && url == template);
    }
}
