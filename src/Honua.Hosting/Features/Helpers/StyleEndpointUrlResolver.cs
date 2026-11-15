// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Configuration;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Helpers;

// TileJSON requires absolute tile endpoints, and the canonical and deprecated style routes must emit the same document.
// Sources served only by the owned /tiles route advertise a zoom range no wider than that route accepts:
// MapLibre requests every level up to a source's maxzoom rather than overzooming the last served one.
internal sealed record StyleUrlContext(string BaseUrl, HttpRequest? Request, TileLimits TileLimits)
{
    public static StyleUrlContext From(HttpContext context) => new(
        BaseUrlResolver.GetBaseUrl(context),
        context.Request,
        context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles);
}

internal static class StyleEndpointUrlResolver
{
    public static JsonElement Resolve(JsonElement style, StyleUrlContext urlContext)
    {
        if (style.ValueKind != JsonValueKind.Object)
        {
            return style;
        }

        var resolved = Resolve(style.GetRawText(), urlContext, out var changed);
        if (!changed)
        {
            return style;
        }

        using var document = JsonDocument.Parse(resolved);
        return document.RootElement.Clone();
    }

    public static string Resolve(string styleJson, StyleUrlContext urlContext)
        => Resolve(styleJson, urlContext, out _);

    private static string Resolve(string styleJson, StyleUrlContext urlContext, out bool changed)
    {
        var (baseUrl, request, tileLimits) = urlContext;
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

                if (IsServedOnlyByOwnedVectorTiles(tiles, baseUrl))
                {
                    changed |= ClampZoomRange(sourceObject, tileLimits);
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

    private static bool IsServedOnlyByOwnedVectorTiles(JsonArray tiles, string baseUrl)
    {
        if (tiles.Count == 0)
        {
            return false;
        }

        foreach (var tile in tiles)
        {
            if (tile is not JsonValue value || !value.TryGetValue<string>(out var url) ||
                !IsOwnedTileUrl(url, baseUrl) || !url.Split('?')[0].EndsWith("/{z}/{x}/{y}.mvt", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Narrow only: an author's tighter range (for example maxzoom 14 to overzoom) is kept, and a
    // range that lies wholly outside the route's is left as authored rather than inverted.
    private static bool ClampZoomRange(JsonObject source, TileLimits tileLimits)
    {
        var maxZoom = ReadZoom(source, "maxzoom");
        var minZoom = ReadZoom(source, "minzoom");
        var clampMax = maxZoom is null || maxZoom > tileLimits.MaxTileZoom;
        var clampMin = minZoom is null ? tileLimits.MinTileZoom > 0 : minZoom < tileLimits.MinTileZoom;
        var newMax = clampMax ? tileLimits.MaxTileZoom : maxZoom!.Value;
        var newMin = clampMin ? tileLimits.MinTileZoom : minZoom ?? 0;
        if ((!clampMax && !clampMin) || newMin > newMax)
        {
            return false;
        }

        if (clampMax)
        {
            source["maxzoom"] = tileLimits.MaxTileZoom;
        }

        if (clampMin)
        {
            source["minzoom"] = tileLimits.MinTileZoom;
        }

        return true;
    }

    private static double? ReadZoom(JsonObject source, string propertyName)
        => source[propertyName] is JsonValue value && value.TryGetValue(out double zoom) && double.IsFinite(zoom) ? zoom : null;

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
