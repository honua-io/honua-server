// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Models;

internal static class ProtocolRequestClassifier
{
    internal static string MapODataCode(int statusCode, bool includeConflict = true) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "BadRequest",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status402PaymentRequired => "PaymentRequired",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "ResourceNotFound",
        StatusCodes.Status408RequestTimeout => "RequestTimeout",
        StatusCodes.Status409Conflict when includeConflict => "Conflict",
        StatusCodes.Status412PreconditionFailed => "PreconditionFailed",
        StatusCodes.Status413PayloadTooLarge => "PayloadTooLarge",
        StatusCodes.Status429TooManyRequests => "TooManyRequests",
        StatusCodes.Status500InternalServerError => "InternalServerError",
        StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
        _ => "Error"
    };

    internal static bool IsOData(PathString path) => path.StartsWithSegments("/odata");

    internal static bool IsOgcServiceAlias(PathString path)
    {
        if (!path.StartsWithSegments("/ogc/services", out var remaining))
        {
            return IsRestOgcServiceAlias(path);
        }

        var span = remaining.Value.AsSpan();
        Span<Range> ranges = stackalloc Range[3];
        var count = span.Split(ranges, '/', StringSplitOptions.RemoveEmptyEntries);
        if (count < 2)
        {
            return false;
        }

        var second = span[ranges[1]];
        return second.Equals("wms", StringComparison.OrdinalIgnoreCase) ||
               second.Equals("wmts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRestOgcServiceAlias(PathString path)
    {
        if (!path.StartsWithSegments("/rest/services", out var remaining))
        {
            return false;
        }

        var span = remaining.Value.AsSpan();
        Span<Range> ranges = stackalloc Range[4];
        var count = span.Split(ranges, '/', StringSplitOptions.RemoveEmptyEntries);
        if (count < 3)
        {
            return false;
        }

        var third = span[ranges[2]];
        return span[ranges[1]].Equals("MapServer", StringComparison.OrdinalIgnoreCase) &&
               (third.Equals("wms", StringComparison.OrdinalIgnoreCase) ||
                third.Equals("wmts", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsOgc(PathString path) =>
        path.StartsWithSegments("/ogc/features") ||
        path.StartsWithSegments("/ogc/tiles") ||
        path.StartsWithSegments("/ogc/maps") ||
        path.StartsWithSegments("/ogc/coverages") ||
        path.StartsWithSegments("/collections");

    internal static bool IsWfs(PathString path) => path.StartsWithSegments("/wfs");

    internal static bool IsWcs(PathString path)
    {
        if (path.StartsWithSegments("/ogc/services", out var remaining))
        {
            return MatchesOgcServiceSegment(remaining, "wcs");
        }

        // /ogc/wcs/{serviceId}: the same service-scoped WCS without a "services"
        // segment, for clients that read /services/ as an ArcGIS Server site (ADR-0079).
        if (path.StartsWithSegments("/ogc/wcs", out var aliasRemaining))
        {
            var span = aliasRemaining.Value.AsSpan();
            Span<Range> ranges = stackalloc Range[2];
            return span.Split(ranges, '/', StringSplitOptions.RemoveEmptyEntries) == 1;
        }

        if (path.StartsWithSegments("/rest/services", out var restRemaining))
        {
            return MatchesRestServiceAlias(restRemaining, "ImageServer", "WCS");
        }

        return false;
    }

    internal static bool IsWps(PathString path) => path.StartsWithSegments("/wps");

    internal static bool IsAdmin(PathString path)
    {
        if (!path.StartsWithSegments("/api", out var remaining))
        {
            return false;
        }

        var span = remaining.Value.AsSpan();
        Span<Range> ranges = stackalloc Range[3];
        var count = span.Split(ranges, '/', StringSplitOptions.RemoveEmptyEntries);
        if (count < 2)
        {
            return false;
        }

        var versionSegment = span[ranges[0]];
        if (versionSegment.Length < 2 || versionSegment[0] is not ('v' or 'V'))
        {
            return false;
        }

        var versionPart = versionSegment[1..];
        foreach (var ch in versionPart)
        {
            if (!char.IsDigit(ch) && ch != '.')
            {
                return false;
            }
        }

        return span[ranges[1]].Equals("admin", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsGeoServices(PathString path) =>
        path.StartsWithSegments("/rest/services") ||
        path.StartsWithSegments("/tiles") ||
        path.StartsWithSegments("/sharing/rest") ||
        path.StartsWithSegments("/rest/info");

    internal static bool IsWmsAlias(PathString path)
    {
        if (path.StartsWithSegments("/ogc/services", out var remaining))
        {
            return MatchesOgcServiceSegment(remaining, "wms");
        }

        if (path.StartsWithSegments("/rest/services", out var restRemaining))
        {
            return MatchesRestServiceAlias(restRemaining, "MapServer", "wms");
        }

        return false;
    }

    internal static bool IsWmtsAlias(PathString path)
    {
        if (path.StartsWithSegments("/ogc/services", out var remaining))
        {
            return MatchesOgcServiceSegment(remaining, "wmts");
        }

        if (path.StartsWithSegments("/rest/services", out var restRemaining))
        {
            return MatchesRestServiceAlias(restRemaining, "MapServer", "wmts");
        }

        return false;
    }

    private static bool MatchesOgcServiceSegment(PathString remaining, ReadOnlySpan<char> service)
    {
        var span = remaining.Value.AsSpan();
        Span<Range> ranges = stackalloc Range[3];
        var count = span.Split(ranges, '/', StringSplitOptions.RemoveEmptyEntries);
        return count >= 2 && span[ranges[1]].Equals(service, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRestServiceAlias(PathString remaining, ReadOnlySpan<char> serverType, ReadOnlySpan<char> alias)
    {
        var span = remaining.Value.AsSpan();
        Span<Range> ranges = stackalloc Range[4];
        var count = span.Split(ranges, '/', StringSplitOptions.RemoveEmptyEntries);
        return count >= 3
            && span[ranges[1]].Equals(serverType, StringComparison.OrdinalIgnoreCase)
            && span[ranges[2]].Equals(alias, StringComparison.OrdinalIgnoreCase);
    }
}
