// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;

namespace Honua.Protocols.GeoServices;

internal static class GeoServicesExportTilesExtentParser
{
    internal static bool TryParseMaxTiles(
        string? value,
        TileLimits limits,
        out int maxTiles,
        out string? error)
    {
        maxTiles = Math.Max(1, limits.MaxTilesPerRequest);
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) ||
            requested <= 0)
        {
            error = "maxTiles must be a positive integer.";
            return false;
        }

        maxTiles = Math.Min(requested, Math.Max(1, limits.MaxTilesPerRequest));
        return true;
    }

    internal static bool TryParseJsonExtent(
        string value,
        string? fallbackSridValue,
        out SkiaMapRenderer.RenderExtent extent,
        out int srid,
        out string? error)
    {
        extent = default;
        srid = SpatialReference.WGS84.Wkid;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetJsonDouble(root, "xmin", out var xmin) ||
                !TryGetJsonDouble(root, "ymin", out var ymin) ||
                !TryGetJsonDouble(root, "xmax", out var xmax) ||
                !TryGetJsonDouble(root, "ymax", out var ymax))
            {
                error = "exportExtent JSON must include xmin, ymin, xmax, and ymax.";
                return false;
            }

            if (!TryResolveExtentSrid(root, fallbackSridValue, out srid, out error))
            {
                return false;
            }

            extent = new SkiaMapRenderer.RenderExtent(xmin, ymin, xmax, ymax);
            return ymin < ymax && (xmin < xmax || CoordinateTransformer.IsWrappedGeographicExtent(extent));
        }
        catch (JsonException)
        {
            error = "exportExtent contains invalid JSON.";
            return false;
        }
    }

    internal static bool TryResolveExtentSrid(
        JsonElement root,
        string? fallbackSridValue,
        out int srid,
        out string? error)
    {
        srid = SpatialReference.WGS84.Wkid;
        error = null;

        if (root.TryGetProperty("spatialReference", out var srElement) &&
            srElement.ValueKind == JsonValueKind.Object)
        {
            if (TryGetJsonInt(srElement, "latestWkid", out var latestWkid))
            {
                srid = latestWkid;
                return true;
            }

            if (TryGetJsonInt(srElement, "wkid", out var wkid))
            {
                srid = wkid;
                return true;
            }
        }

        var parsedSrid = SpatialReferenceHelpers.TryParseSrid(fallbackSridValue);
        if (!string.IsNullOrWhiteSpace(fallbackSridValue) && !parsedSrid.HasValue)
        {
            error = "Invalid exportExtentSR parameter.";
            return false;
        }

        srid = parsedSrid ?? SpatialReference.WGS84.Wkid;
        return true;
    }

    internal static bool TryGetJsonDouble(JsonElement element, string propertyName, out double value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    internal static bool TryGetJsonInt(JsonElement element, string propertyName, out int value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
