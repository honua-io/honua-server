// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Honua.Infrastructure.Helpers;

/// <summary>
/// Rewrites root-relative endpoint URLs in a MapLibre style document (<c>sprite</c>,
/// <c>glyphs</c>, and each source's <c>tiles</c>) to absolute URLs against the public
/// base URL. TileJSON 3.0.0 §3.2 requires absolute tile endpoints (honua-server#5442);
/// shared by the canonical <c>/ogc/styles/{styleId}</c> route and the deprecated
/// <c>/api/styles/{layerId}.json</c> alias so both emit the same document.
/// </summary>
internal static class StyleEndpointUrlResolver
{
    public static JsonElement Resolve(JsonElement style, string baseUrl)
    {
        if (style.ValueKind != JsonValueKind.Object)
        {
            return style;
        }

        var resolved = Resolve(style.GetRawText(), baseUrl, out var changed);
        if (!changed)
        {
            return style;
        }

        using var document = JsonDocument.Parse(resolved);
        return document.RootElement.Clone();
    }

    public static string Resolve(string styleJson, string baseUrl)
        => Resolve(styleJson, baseUrl, out _);

    private static string Resolve(string styleJson, string baseUrl, out bool changed)
    {
        changed = false;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(styleJson);
        }
        catch (JsonException)
        {
            return styleJson;
        }

        if (node is not JsonObject root)
        {
            return styleJson;
        }

        changed = ResolveProperty(root, "sprite", baseUrl);
        changed |= ResolveProperty(root, "glyphs", baseUrl);

        if (root["sources"] is JsonObject sources)
        {
            foreach (var source in sources)
            {
                if (source.Value is not JsonObject sourceObject || sourceObject["tiles"] is not JsonArray tiles)
                {
                    continue;
                }

                for (var index = 0; index < tiles.Count; index++)
                {
                    if (tiles[index] is JsonValue value && value.TryGetValue<string>(out var url))
                    {
                        var resolvedUrl = ResolveUrl(url, baseUrl);
                        if (!string.Equals(url, resolvedUrl, StringComparison.Ordinal))
                        {
                            tiles[index] = resolvedUrl;
                            changed = true;
                        }
                    }
                }
            }
        }

        return changed ? root.ToJsonString() : styleJson;
    }

    private static bool ResolveProperty(JsonObject root, string propertyName, string baseUrl)
    {
        if (root[propertyName] is JsonValue value && value.TryGetValue<string>(out var url))
        {
            var resolved = ResolveUrl(url, baseUrl);
            if (!string.Equals(url, resolved, StringComparison.Ordinal))
            {
                root[propertyName] = resolved;
                return true;
            }
        }

        return false;
    }

    // Root-relative only: "//host/..." is scheme-relative and already names an origin.
    private static string ResolveUrl(string url, string baseUrl)
        => url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
            ? string.Concat(baseUrl.TrimEnd('/'), url)
            : url;
}
