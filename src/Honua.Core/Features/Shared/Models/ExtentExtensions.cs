// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Extension methods for converting between different extent representations
/// </summary>
public static class ExtentExtensions
{
    /// <summary>
    /// Converts a FeatureExtent to a bounding box array for OGC APIs
    /// </summary>
    /// <param name="extent">Feature extent</param>
    /// <returns>Bounding box as [minx, miny, maxx, maxy]</returns>
    public static double[] ToBoundingBox(this FeatureExtent extent)
        => [extent.MinX, extent.MinY, extent.MaxX, extent.MaxY];

    /// <summary>
    /// Converts a FeatureExtent to a 2D bounding box array for OGC APIs
    /// </summary>
    /// <param name="extent">Feature extent</param>
    /// <returns>2D bounding box as [[minx, miny], [maxx, maxy]]</returns>
    public static double[][] ToBoundingBox2D(this FeatureExtent extent)
        => [[extent.MinX, extent.MinY], [extent.MaxX, extent.MaxY]];

    /// <summary>
    /// Creates a FeatureExtent from a bounding box array
    /// </summary>
    /// <param name="bbox">Bounding box as [minx, miny, maxx, maxy]</param>
    /// <param name="spatialReference">Spatial reference system ID</param>
    /// <returns>Feature extent instance</returns>
    public static FeatureExtent ToFeatureExtent(this double[] bbox, int spatialReference)
    {
        if (bbox.Length != 4)
            throw new ArgumentException("Bounding box must have exactly 4 coordinates", nameof(bbox));

        return FeatureExtent.Create(bbox[0], bbox[1], bbox[2], bbox[3], spatialReference);
    }

    /// <summary>
    /// Creates a FeatureExtent from a 2D bounding box array
    /// </summary>
    /// <param name="bbox">2D bounding box as [[minx, miny], [maxx, maxy]]</param>
    /// <param name="spatialReference">Spatial reference system ID</param>
    /// <returns>Feature extent instance</returns>
    public static FeatureExtent ToFeatureExtent(this double[][] bbox, int spatialReference)
    {
        if (bbox.Length != 2 || bbox[0].Length != 2 || bbox[1].Length != 2)
            throw new ArgumentException("2D bounding box must have format [[minx, miny], [maxx, maxy]]", nameof(bbox));

        return FeatureExtent.Create(bbox[0][0], bbox[0][1], bbox[1][0], bbox[1][1], spatialReference);
    }

    extension(string? crsUri)
    {
        /// <summary>
        /// Extracts SRID from CRS URI (e.g., "http://www.opengis.net/def/crs/EPSG/0/4326" returns 4326)
        /// </summary>
        /// <returns>SRID if found</returns>
        public int ExtractSridFromCrs()
        {
            if (TryExtractSridFromCrs(crsUri, out var srid))
            {
                return srid;
            }

            throw new FormatException("CRS must be a supported EPSG or CRS84 identifier.");
        }
    }

    /// <summary>
    /// Attempts to extract an SRID from a CRS identifier.
    /// </summary>
    public static bool TryExtractSridFromCrs(string? crsUri, out int srid)
    {
        srid = default;
        if (string.IsNullOrWhiteSpace(crsUri))
        {
            return false;
        }

        var trimmed = crsUri.Trim();
        if (trimmed.Equals(GetDefaultCrsUri(), StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("CRS84", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("OGC:CRS84", StringComparison.OrdinalIgnoreCase))
        {
            srid = 4326;
            return true;
        }

        ReadOnlySpan<char> span = trimmed;
        const string epsgMarker = "/EPSG/";

        var epsgIndex = span.IndexOf(epsgMarker.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (epsgIndex >= 0)
        {
            var sridSpan = span[(epsgIndex + epsgMarker.Length)..];
            var slashIndex = sridSpan.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                sridSpan = sridSpan[(slashIndex + 1)..];
            }

            if (int.TryParse(sridSpan, out srid))
            {
                return true;
            }
        }

        if (span.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var sridSpan = span["EPSG:".Length..];
            return int.TryParse(sridSpan, out srid);
        }

        return false;
    }

    /// <summary>
    /// Converts SRID to CRS URI format
    /// </summary>
    /// <param name="srid">Spatial Reference System ID</param>
    /// <returns>CRS URI string</returns>
    public static string ToOgcCrsUri(int srid)
        => $"http://www.opengis.net/def/crs/EPSG/0/{srid}";

    /// <summary>
    /// Gets the default CRS URI (WGS84)
    /// </summary>
    /// <returns>Default CRS URI</returns>
    public static string GetDefaultCrsUri()
        => "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
}
