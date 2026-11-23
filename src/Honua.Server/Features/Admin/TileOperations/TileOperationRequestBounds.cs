// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Server.Features.Admin.TileOperations;

// The job clamps these values while it runs, so an out-of-range request would be accepted and quietly do different work.
internal static class TileOperationRequestBounds
{
    internal static string? FindViolation(TileOperationStartRequest request, TileLimits limits)
        => ZoomViolation(request.MinZoom, "minZoom", limits)
           ?? ZoomViolation(request.MaxZoom, "maxZoom", limits)
           ?? MaxTilesViolation(request.MaxTiles)
           ?? BboxViolation(request.Bbox);

    private static string? ZoomViolation(int? zoom, string property, TileLimits limits)
        => zoom is { } value && (value < limits.MinTileZoom || value > limits.MaxTileZoom)
            ? $"Property '{property}' must be between {limits.MinTileZoom} and {limits.MaxTileZoom}."
            : null;

    private static string? MaxTilesViolation(int? maxTiles)
        => maxTiles is < 1 ? "Property 'maxTiles' must be at least 1." : null;

    private static string? BboxViolation(double[]? bbox)
    {
        if (bbox is not { Length: 4 })
        {
            return null;
        }

        if (bbox.Any(static value => !double.IsFinite(value)))
        {
            return "Property 'bbox' values must be finite numbers.";
        }

        var (minLon, minLat, maxLon, maxLat) = (bbox[0], bbox[1], bbox[2], bbox[3]);
        if (minLon < -180d || maxLon > 180d || minLat < -90d || maxLat > 90d)
        {
            return "Property 'bbox' must lie within longitude -180..180 and latitude -90..90.";
        }

        if (minLat > maxLat)
        {
            return "Property 'bbox' must have minLat less than or equal to maxLat.";
        }

        return minLon > maxLon
            ? "Property 'bbox' crosses the antimeridian (minLon > maxLon); submit one job for each side."
            : null;
    }
}
