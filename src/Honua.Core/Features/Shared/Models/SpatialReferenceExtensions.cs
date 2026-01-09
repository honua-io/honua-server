// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Extension methods for SpatialReference conversions and utilities
/// </summary>
public static class SpatialReferenceExtensions
{
    /// <summary>
    /// Converts from the legacy Catalog SpatialReference format (Srid, WellKnownText) to the unified model
    /// </summary>
    /// <param name="catalogSpatialRef">Legacy catalog spatial reference with (Srid, WellKnownText)</param>
    /// <returns>Unified SpatialReference</returns>
    public static SpatialReference ToUnifiedSpatialReference(this (int Srid, string? WellKnownText) catalogSpatialRef)
        => new()
        {
            Wkid = catalogSpatialRef.Srid,
            Wkt = catalogSpatialRef.WellKnownText
        };

    /// <summary>
    /// Converts the unified SpatialReference to legacy Catalog format
    /// </summary>
    /// <param name="spatialRef">Unified spatial reference</param>
    /// <returns>Tuple with Srid and WellKnownText</returns>
    public static (int Srid, string? WellKnownText) ToCatalogSpatialReference(this SpatialReference spatialRef)
        => (spatialRef.Wkid, spatialRef.Wkt);

    /// <summary>
    /// Converts the unified SpatialReference to OGC CRS URI format
    /// </summary>
    /// <param name="spatialRef">Unified spatial reference</param>
    /// <returns>OGC CRS URI string</returns>
    public static string ToOgcCrsUri(this SpatialReference spatialRef)
        => spatialRef.Wkid switch
        {
            4326 => "http://www.opengis.net/def/crs/OGC/1.3/CRS84", // OGC standard for WGS84
            3857 => "http://www.opengis.net/def/crs/EPSG/0/3857",  // Web Mercator
            _ => $"http://www.opengis.net/def/crs/EPSG/0/{spatialRef.Wkid}"
        };

    /// <summary>
    /// Creates a SpatialReference from an OGC CRS URI
    /// </summary>
    /// <param name="crsUri">OGC CRS URI string</param>
    /// <returns>SpatialReference, or null if URI format is not recognized</returns>
    public static SpatialReference? FromOgcCrsUri(string crsUri)
    {
        if (string.IsNullOrWhiteSpace(crsUri))
            return null;

        return crsUri switch
        {
            "http://www.opengis.net/def/crs/OGC/1.3/CRS84" => SpatialReference.WGS84,
            var uri when uri.StartsWith("http://www.opengis.net/def/crs/EPSG/0/", StringComparison.Ordinal) =>
                int.TryParse(uri.AsSpan("http://www.opengis.net/def/crs/EPSG/0/".Length), out int epsgCode)
                    ? SpatialReference.Create(epsgCode)
                    : null,
            _ => null
        };
    }

    /// <summary>
    /// Gets the list of supported CRS URIs for OGC APIs
    /// </summary>
    /// <param name="spatialRef">Base spatial reference</param>
    /// <returns>Immutable array of supported CRS URIs</returns>
    public static ImmutableArray<string> GetSupportedCrsUris(this SpatialReference spatialRef)
    {
        var uris = new List<string>
        {
            spatialRef.ToOgcCrsUri()
        };

        // Always include WGS84 as a supported CRS for OGC compatibility
        if (spatialRef.Wkid != 4326)
        {
            uris.Add(SpatialReference.WGS84.ToOgcCrsUri());
        }

        // Include Web Mercator if it's commonly used
        if (spatialRef.Wkid != 3857)
        {
            uris.Add(SpatialReference.WebMercator.ToOgcCrsUri());
        }

        return uris.ToImmutableArray();
    }

    /// <summary>
    /// Validates if the spatial reference is supported by OGC APIs
    /// </summary>
    /// <param name="spatialRef">Spatial reference to validate</param>
    /// <returns>True if supported, false otherwise</returns>
    public static bool IsSupportedByOgc(this SpatialReference spatialRef)
        => spatialRef.Wkid is 4326 or 3857 or > 0;

    /// <summary>
    /// Gets the authority name (e.g., "EPSG") for the spatial reference
    /// </summary>
    /// <param name="spatialRef">Spatial reference</param>
    /// <returns>Authority name, typically "EPSG"</returns>
    public static string GetAuthorityName(this SpatialReference spatialRef)
        => "EPSG";

    /// <summary>
    /// Gets the authority code as a string
    /// </summary>
    /// <param name="spatialRef">Spatial reference</param>
    /// <returns>Authority code as string</returns>
    public static string GetAuthorityCode(this SpatialReference spatialRef)
        => spatialRef.Wkid.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Determines if transformation is needed between two spatial reference systems
    /// </summary>
    /// <param name="from">Source spatial reference</param>
    /// <param name="to">Target spatial reference</param>
    /// <returns>True if transformation is needed, false if they are the same</returns>
    public static bool RequiresTransformation(this SpatialReference from, SpatialReference to)
        => from.Wkid != to.Wkid;

    /// <summary>
    /// Creates a minimal spatial reference with just the WKID
    /// </summary>
    /// <param name="wkid">Well-known ID (EPSG code)</param>
    /// <returns>SpatialReference instance</returns>
    public static SpatialReference FromWkid(int wkid)
        => SpatialReference.Create(wkid);

    /// <summary>
    /// Creates a spatial reference with WKID and WKT
    /// </summary>
    /// <param name="wkid">Well-known ID (EPSG code)</param>
    /// <param name="wkt">Well-known text representation</param>
    /// <returns>SpatialReference instance</returns>
    public static SpatialReference FromWkidAndWkt(int wkid, string? wkt)
        => new() { Wkid = wkid, Wkt = wkt };

    /// <summary>
    /// Converts SpatialReference to nullable SpatialReference for optional assignments
    /// </summary>
    /// <param name="spatialRef">Non-nullable spatial reference</param>
    /// <returns>Nullable spatial reference</returns>
    public static SpatialReference? ToNullable(this SpatialReference spatialRef)
        => spatialRef;

    /// <summary>
    /// Creates a nullable SpatialReference from an optional WKID
    /// </summary>
    /// <param name="wkid">Optional Well-known ID (EPSG code)</param>
    /// <returns>SpatialReference instance or null if wkid is null</returns>
    public static SpatialReference? FromOptionalWkid(int? wkid)
        => wkid.HasValue ? SpatialReference.Create(wkid.Value) : null;
}
