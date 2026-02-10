// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Shared utilities for parsing spatial reference system identifiers.
/// Handles EPSG codes, OGC URIs, and bare SRID numbers across all protocol features.
/// </summary>
internal static class SpatialReferenceHelpers
{
    /// <summary>
    /// Parses a CRS string into an SRID integer.
    /// Supports EPSG:XXXX, OGC URI format, CRS84, and bare SRID numbers.
    /// </summary>
    /// <param name="crs">The CRS string to parse (e.g., "EPSG:4326", "http://www.opengis.net/def/crs/EPSG/0/4326", "4326")</param>
    /// <returns>The SRID if successfully parsed, null otherwise</returns>
    public static int? TryParseSrid(string? crs)
    {
        if (string.IsNullOrEmpty(crs))
            return null;

        // Handle EPSG:XXXX format
        const string epsgPrefix = "EPSG:";
        var epsgIndex = crs.IndexOf(epsgPrefix, StringComparison.OrdinalIgnoreCase);
        if (epsgIndex >= 0)
        {
            var codeStr = crs[(epsgIndex + epsgPrefix.Length)..];
            if (int.TryParse(codeStr, out var code) && code > 0)
                return code;
        }

        // Handle OGC URI format: http://www.opengis.net/def/crs/EPSG/0/XXXX
        const string ogcCrsPrefix = "/def/crs/EPSG/";
        var ogcIndex = crs.IndexOf(ogcCrsPrefix, StringComparison.OrdinalIgnoreCase);
        if (ogcIndex >= 0)
        {
            var remainder = crs[(ogcIndex + ogcCrsPrefix.Length)..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex >= 0)
            {
                var codeStr = remainder[(slashIndex + 1)..];
                if (int.TryParse(codeStr, out var code) && code > 0)
                    return code;
            }
        }

        // Handle OGC CRS84 URI
        if (crs.EndsWith("CRS84", StringComparison.Ordinal))
            return 4326;

        // Handle bare SRID number
        if (int.TryParse(crs, out var srid) && srid > 0)
            return srid;

        return null;
    }
}
