// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Core.Configuration;

namespace Honua.Infrastructure.Helpers;

internal static class StyleSourceZoomRange
{
    // Narrow only: an author's tighter range (for example maxzoom 14 to overzoom) is kept, and a
    // range that lies wholly outside the route's is left as authored rather than inverted.
    internal static bool Narrow(JsonObject source, TileLimits tileLimits)
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
}
