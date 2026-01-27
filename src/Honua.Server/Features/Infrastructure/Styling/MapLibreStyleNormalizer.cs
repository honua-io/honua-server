// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class MapLibreStyleNormalizer
{
    public static bool TryNormalize(
        JsonElement style,
        LayerDefinition layer,
        out string normalizedJson,
        out string? error)
    {
        normalizedJson = string.Empty;
        error = null;

        if (style.ValueKind != JsonValueKind.Object)
        {
            error = "MapLibre style must be a JSON object.";
            return false;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(style.GetRawText());
        }
        catch (JsonException)
        {
            error = "MapLibre style is not valid JSON.";
            return false;
        }

        if (rootNode is not JsonObject root)
        {
            error = "MapLibre style must be a JSON object.";
            return false;
        }

        if (!TryGetVersion(root, out var version))
        {
            error = "MapLibre style must include a version number.";
            return false;
        }

        if (version != 8)
        {
            error = "MapLibre style version must be 8.";
            return false;
        }

        if (root["layers"] is not JsonArray layers || layers.Count == 0)
        {
            error = "MapLibre style must include at least one layer.";
            return false;
        }

        var sources = root["sources"] as JsonObject ?? new JsonObject();
        root["sources"] = sources;

        var sourceId = StyleDefaults.GetSourceId(layer);
        sources[sourceId] = BuildSourceNode(layer.Id);

        var hasLayerSource = false;
        foreach (var layerNode in layers)
        {
            if (layerNode is not JsonObject layerObject)
            {
                continue;
            }

            if (layerObject["source"] is null)
            {
                layerObject["source"] = sourceId;
            }

            var sourceValue = TryGetString(layerObject["source"]);
            if (!string.Equals(sourceValue, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hasLayerSource = true;
            if (layerObject["source-layer"] is null)
            {
                layerObject["source-layer"] = StyleDefaults.SourceLayerName;
            }
        }

        if (!hasLayerSource)
        {
            error = "MapLibre style must include a layer using the Honua tile source.";
            return false;
        }

        normalizedJson = root.ToJsonString();
        return true;
    }

    private static JsonObject BuildSourceNode(int layerId)
    {
        return new JsonObject
        {
            ["type"] = "vector",
            ["tiles"] = new JsonArray(StyleDefaults.GetTileUrl(layerId)),
            ["minzoom"] = 0,
            ["maxzoom"] = 22
        };
    }

    private static bool TryGetVersion(JsonObject root, out int version)
    {
        version = 0;

        if (root["version"] is not JsonNode versionNode)
        {
            return false;
        }

        if (versionNode is JsonValue valueNode && valueNode.TryGetValue<int>(out var parsed))
        {
            version = parsed;
            return true;
        }

        if (versionNode is JsonValue stringNode && stringNode.TryGetValue<string>(out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedText))
        {
            version = parsedText;
            return true;
        }

        return false;
    }

    private static string? TryGetString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }
}
