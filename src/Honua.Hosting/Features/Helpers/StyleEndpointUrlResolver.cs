// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

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
    public static JsonElement Resolve(JsonElement style, string baseUrl, HttpRequest? request = null)
    {
        if (style.ValueKind != JsonValueKind.Object)
        {
            return style;
        }

        var resolved = Resolve(style.GetRawText(), baseUrl, request, out var changed);
        if (!changed)
        {
            return style;
        }

        using var document = JsonDocument.Parse(resolved);
        return document.RootElement.Clone();
    }

    public static string Resolve(string styleJson, string baseUrl, HttpRequest? request = null)
        => Resolve(styleJson, baseUrl, request, out _);

    private static string Resolve(string styleJson, string baseUrl, HttpRequest? request, out bool changed)
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
                if (source.Value is not JsonObject sourceObject)
                {
                    continue;
                }

                changed |= ResolveProperty(sourceObject, "url", baseUrl, request);
                if (sourceObject["tiles"] is not JsonArray tiles)
                {
                    continue;
                }

                for (var index = 0; index < tiles.Count; index++)
                {
                    if (tiles[index] is JsonValue value && value.TryGetValue<string>(out var url))
                    {
                        var resolvedUrl = ResolveUrl(url, baseUrl, request);
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

    private static bool ResolveProperty(JsonObject root, string propertyName, string baseUrl, HttpRequest? request = null)
    {
        if (root[propertyName] is JsonValue value && value.TryGetValue<string>(out var url))
        {
            var resolved = ResolveUrl(url, baseUrl, request);
            if (!string.Equals(url, resolved, StringComparison.Ordinal))
            {
                root[propertyName] = resolved;
                return true;
            }
        }

        return false;
    }

    // Root-relative only: "//host/..." is scheme-relative and already names an origin.
    private static string ResolveUrl(string url, string baseUrl, HttpRequest? request = null)
    {
        var resolved = url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
            ? string.Concat(baseUrl.TrimEnd('/'), url)
            : url;

        // Only our known tile/TileJSON routes inherit a URL token. A stored style can
        // reference external sources, redirects, sprites or glyphs; never disclose the
        // caller's credential to those URLs, or overwrite a source's own credential.
        if (request is null || !IsOwnedTileUrl(resolved, baseUrl))
        {
            return resolved;
        }

        var queryIndex = resolved.IndexOf('?');
        if (queryIndex >= 0 && QueryHelpers.ParseQuery(resolved[queryIndex..]).ContainsKey("token"))
        {
            return resolved;
        }

        return BaseUrlResolver.PreserveToken(request, resolved);
    }

    private static bool IsOwnedTileUrl(string url, string baseUrl)
    {
        var prefix = string.Concat(baseUrl.TrimEnd('/'), "/tiles/");
        if (!url.StartsWith(prefix, StringComparison.Ordinal) || url.Contains('#'))
        {
            return false;
        }

        var path = url[prefix.Length..].Split('?')[0];
        var segments = path.Split('/');
        if (!int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        return (segments.Length == 2 && segments[1] == "tile.json") ||
            (segments.Length == 4 && segments[1] == "{z}" && segments[2] == "{x}" && segments[3] == "{y}.mvt");
    }
}
