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
    /// Supports EPSG:XXXX, OGC URI format, safe CURIE format, CRS84, and bare SRID numbers.
    /// </summary>
    /// <param name="crs">The CRS string to parse (e.g., "EPSG:4326", "http://www.opengis.net/def/crs/EPSG/0/4326", "4326")</param>
    /// <returns>The SRID if successfully parsed, null otherwise</returns>
    public static int? TryParseSrid(string? crs)
    {
        if (string.IsNullOrWhiteSpace(crs))
        {
            return null;
        }

        var normalized = crs.Trim();
        if (normalized.Length > 2 &&
            normalized[0] == '[' &&
            normalized[^1] == ']')
        {
            normalized = normalized[1..^1].Trim();
        }

        // Handle OGC CRS84 in URI, CURIE, and bare forms.
        if (normalized.EndsWith("CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return 4326;
        }

        // Handle EPSG:XXXX format
        if (TryParsePositiveIntAfterPrefix(normalized, "EPSG:", out var epsgSrid))
        {
            return epsgSrid;
        }

        // Handle URN format: urn:ogc:def:crs:EPSG::XXXX
        if (TryParsePositiveIntAfterPrefix(normalized, "urn:ogc:def:crs:EPSG::", out var urnSrid))
        {
            return urnSrid;
        }

        // Handle OGC URI format: https://www.opengis.net/def/crs/EPSG/0/XXXX
        const string ogcCrsPrefix = "/def/crs/EPSG/";
        var ogcIndex = normalized.IndexOf(ogcCrsPrefix, StringComparison.OrdinalIgnoreCase);
        if (ogcIndex >= 0)
        {
            var remainder = normalized[(ogcIndex + ogcCrsPrefix.Length)..];
            var slashIndex = remainder.LastIndexOf('/');
            var codeStr = slashIndex >= 0
                ? remainder[(slashIndex + 1)..]
                : remainder;

            if (TryParsePositiveInt(codeStr, out var code))
            {
                return code;
            }
        }

        // Handle bare SRID number
        if (TryParsePositiveInt(normalized, out var srid))
        {
            return srid;
        }

        return null;
    }

    private static bool TryParsePositiveIntAfterPrefix(string value, string prefix, out int parsed)
    {
        parsed = 0;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = value[prefix.Length..];
        return TryParsePositiveInt(suffix, out parsed);
    }

    private static bool TryParsePositiveInt(string value, out int parsed)
    {
        parsed = 0;
        return int.TryParse(value, out parsed) && parsed > 0;
    }
}
