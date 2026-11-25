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

internal readonly record struct MaxTilesParseResult
{
    public bool IsValid { get; init; }
    public int MaxTiles { get; init; }
    public string? Error { get; init; }
}

internal readonly record struct JsonExtentParseResult
{
    public bool IsValid { get; init; }
    public SkiaMapRenderer.RenderExtent Extent { get; init; }
    public int Srid { get; init; }
    public string? Error { get; init; }
}

internal readonly record struct ExtentSridResolution
{
    public bool IsValid { get; init; }
    public int Srid { get; init; }
    public string? Error { get; init; }
}

internal static class GeoServicesExportTilesExtentParser
{
    internal static MaxTilesParseResult ParseMaxTiles(string? value, TileLimits limits)
    {
        var maxPerRequest = Math.Max(1, limits.MaxTilesPerRequest);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new MaxTilesParseResult { IsValid = true, MaxTiles = maxPerRequest };
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) ||
            requested <= 0)
        {
            return new MaxTilesParseResult
            {
                IsValid = false,
                MaxTiles = maxPerRequest,
                Error = "maxTiles must be a positive integer."
            };
        }

        return new MaxTilesParseResult { IsValid = true, MaxTiles = Math.Min(requested, maxPerRequest) };
    }

    internal static JsonExtentParseResult ParseJsonExtent(string value, string? fallbackSridValue)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                ReadJsonDouble(root, "xmin") is not { } xmin ||
                ReadJsonDouble(root, "ymin") is not { } ymin ||
                ReadJsonDouble(root, "xmax") is not { } xmax ||
                ReadJsonDouble(root, "ymax") is not { } ymax)
            {
                return InvalidJsonExtent(SpatialReference.WGS84.Wkid, "exportExtent JSON must include xmin, ymin, xmax, and ymax.");
            }

            var sridResolution = ResolveExtentSrid(root, fallbackSridValue);
            if (!sridResolution.IsValid)
            {
                return InvalidJsonExtent(sridResolution.Srid, sridResolution.Error);
            }

            var extent = new SkiaMapRenderer.RenderExtent(xmin, ymin, xmax, ymax);
            var isValid = ymin < ymax && (xmin < xmax || CoordinateTransformer.IsWrappedGeographicExtent(extent));
            return new JsonExtentParseResult { IsValid = isValid, Extent = extent, Srid = sridResolution.Srid };
        }
        catch (JsonException)
        {
            return InvalidJsonExtent(SpatialReference.WGS84.Wkid, "exportExtent contains invalid JSON.");
        }
    }

    private static JsonExtentParseResult InvalidJsonExtent(int srid, string? error)
        => new() { IsValid = false, Srid = srid, Error = error };

    internal static ExtentSridResolution ResolveExtentSrid(JsonElement root, string? fallbackSridValue)
    {
        if (root.TryGetProperty("spatialReference", out var srElement) &&
            srElement.ValueKind == JsonValueKind.Object)
        {
            if (ReadJsonInt(srElement, "latestWkid") is { } latestWkid)
            {
                return new ExtentSridResolution { IsValid = true, Srid = latestWkid };
            }

            if (ReadJsonInt(srElement, "wkid") is { } wkid)
            {
                return new ExtentSridResolution { IsValid = true, Srid = wkid };
            }
        }

        var parsedSrid = SpatialReferenceHelpers.TryParseSrid(fallbackSridValue);
        if (!string.IsNullOrWhiteSpace(fallbackSridValue) && !parsedSrid.HasValue)
        {
            return new ExtentSridResolution
            {
                IsValid = false,
                Srid = SpatialReference.WGS84.Wkid,
                Error = "Invalid exportExtentSR parameter."
            };
        }

        return new ExtentSridResolution { IsValid = true, Srid = parsedSrid ?? SpatialReference.WGS84.Wkid };
    }

    internal static double? ReadJsonDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    internal static int? ReadJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
