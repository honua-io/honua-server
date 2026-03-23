// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Models;

internal static class ProtocolRequestClassifier
{
    internal static string MapODataCode(int statusCode, bool includeConflict = true) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "BadRequest",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "ResourceNotFound",
        StatusCodes.Status408RequestTimeout => "RequestTimeout",
        StatusCodes.Status409Conflict when includeConflict => "Conflict",
        StatusCodes.Status413PayloadTooLarge => "PayloadTooLarge",
        StatusCodes.Status429TooManyRequests => "TooManyRequests",
        StatusCodes.Status500InternalServerError => "InternalServerError",
        StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
        _ => "Error"
    };

    internal static bool IsOData(PathString path) => path.StartsWithSegments("/odata");

    internal static bool IsOgc(PathString path) =>
        path.StartsWithSegments("/ogc/features") ||
        path.StartsWithSegments("/ogc/tiles") ||
        path.StartsWithSegments("/ogc/maps") ||
        path.StartsWithSegments("/collections");

    internal static bool IsWfs(PathString path) => path.StartsWithSegments("/wfs");

    internal static bool IsAdmin(PathString path)
    {
        if (!path.StartsWithSegments("/api", out var remaining))
        {
            return false;
        }

        var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is null || segments.Length < 2)
        {
            return false;
        }

        var versionSegment = segments[0];
        if (!versionSegment.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var versionPart = versionSegment.AsSpan(1);
        if (versionPart.IsEmpty)
        {
            return false;
        }

        foreach (var ch in versionPart)
        {
            if (!char.IsDigit(ch) && ch != '.')
            {
                return false;
            }
        }

        return string.Equals(segments[1], "admin", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsGeoServices(PathString path) =>
        path.StartsWithSegments("/rest/services") ||
        path.StartsWithSegments("/tiles");
}
