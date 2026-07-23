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

        // PA-028: Normalize https:// → http:// before matching. The OGC canonical form uses http://,
        // but some servers (including Honua's Maps handler) emit https:// Content-Crs headers.
        var normalized = crsUri.StartsWith("https://www.opengis.net/", StringComparison.OrdinalIgnoreCase)
            ? "http://" + crsUri["https://".Length..]
            : crsUri;

        return normalized switch
        {
            // Preserve the original WKT-populated WGS84 instance for the canonical CRS84 URI.
            "http://www.opengis.net/def/crs/OGC/1.3/CRS84" => SpatialReference.WGS84,
            // codeql[cs/constant-condition] -- the defensive branch preserves compatibility and documents the accepted wire or domain shape.
            var uri when uri.StartsWith("http://www.opengis.net/def/crs/EPSG/0/", StringComparison.Ordinal)
                && int.TryParse(uri.AsSpan("http://www.opengis.net/def/crs/EPSG/0/".Length), out int epsgCode)
                    => SpatialReference.Create(epsgCode),
            // Delegate everything else to the shared, more complete CRS parser
            // (case-insensitive prefixes, CRS84/OGC:CRS84/EPSG: short forms, /EPSG/ variants).
            _ => ExtentExtensions.TryExtractSridFromCrs(normalized, out var srid)
                    ? SpatialReference.Create(srid)
                    : null
        };
    }

    /// <summary>
    /// The OGC URI for EPSG:4326 (latitude/longitude, NorthEast axis order).
    /// Distinct from CRS84 (<c>OGC:CRS84</c>) which uses EastNorth (lon/lat) axis order.
    /// </summary>
    private const string Epsg4326CrsUri = "http://www.opengis.net/def/crs/EPSG/0/4326";

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

        if (spatialRef.Wkid == 4326)
        {
            // PA-022: EPSG:4326 is distinct from CRS84: it uses NorthEast (lat/lon) axis order.
            // Both must be advertised so clients sending ?crs=EPSG:4326 receive a supported-CRS hit.
            // CRS84 is already the first URI via ToOgcCrsUri() above.
            uris.Add(Epsg4326CrsUri);
        }
        else
        {
            // Include CRS84 (WGS84 EastNorth) as a fallback CRS for non-geographic layers
            uris.Add(SpatialReference.WGS84.ToOgcCrsUri());
        }

        // PA-023: Web Mercator (EPSG:3857) is only advertised when it is the layer's own CRS.
        // Unconditional advertisement was a false capability claim for providers that do not
        // support on-the-fly ST_Transform to 3857. The 3857 URI is already emitted as the
        // first entry for 3857-native layers, so no additional entry is needed here.

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
    /// Gets the authority name for the spatial reference. WKID 4326 (OGC CRS84) is governed
    /// by the OGC, not EPSG, so this returns "OGC" for that CRS. All other WKIDs return "EPSG".
    /// WFS 2.0, WCS, and OGC API conformance URNs of the form
    /// <c>urn:ogc:def:crs:{authority}::{code}</c> must use "OGC" for CRS84.
    /// </summary>
    /// <param name="spatialRef">Spatial reference</param>
    /// <returns>"OGC" for WKID 4326 (CRS84); "EPSG" for all other WKIDs.</returns>
    public static string GetAuthorityName(this SpatialReference spatialRef)
        => spatialRef.Wkid == 4326 ? "OGC" : "EPSG";

    /// <summary>
    /// Gets the authority code as a string. For WKID 4326 (OGC CRS84) this returns the
    /// correct OGC URN fragment "1.3:CRS84" so callers can form
    /// <c>urn:ogc:def:crs:OGC:1.3:CRS84</c>. All other WKIDs return the numeric WKID.
    /// </summary>
    /// <param name="spatialRef">Spatial reference</param>
    /// <returns>"1.3:CRS84" for WKID 4326; the numeric WKID string for all other CRS.</returns>
    public static string GetAuthorityCode(this SpatialReference spatialRef)
        => spatialRef.Wkid == 4326
            ? "1.3:CRS84"
            : spatialRef.Wkid.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Determines if transformation is needed between two spatial reference systems
    /// </summary>
    /// <param name="from">Source spatial reference</param>
    /// <param name="to">Target spatial reference</param>
    /// <returns>True if transformation is needed, false if they are the same</returns>
    public static bool RequiresTransformation(this SpatialReference from, SpatialReference to)
        => from.Wkid != to.Wkid;

    /// <summary>
    /// Normalizes well-known WGS 84 / Web Mercator SRID aliases
    /// (<c>102100</c>, <c>102113</c>, <c>900913</c>, <c>3785</c>) to the canonical
    /// EPSG code <c>3857</c>. All other SRIDs are returned unchanged.
    /// </summary>
    /// <remarks>
    /// ArcGIS Pro and the ArcGIS JS API send <c>{wkid:102100, latestWkid:3857}</c> for
    /// content Honua stores and advertises as EPSG:3857. These identifiers denote the
    /// same coordinate system with identical coordinate values, so callers comparing an
    /// incoming SRID against a stored/layer SRID must normalize both sides through this
    /// method to treat them as equivalent. This is the shared canonical mapping used by
    /// the query, import, and edit paths.
    /// </remarks>
    /// <param name="srid">SRID to normalize.</param>
    /// <returns>EPSG:3857 for any Web Mercator alias; otherwise the input SRID.</returns>
    public static int NormalizeWebMercatorSrid(int srid)
        => srid switch
        {
            102100 or 102113 or 900913 or 3785 => 3857,
            _ => srid
        };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="srid"/> is EPSG:3857 or any of its
    /// well-known Web Mercator aliases (<c>102100</c>, <c>102113</c>, <c>900913</c>, <c>3785</c>).
    /// </summary>
    /// <remarks>
    /// Canonical replacement for the ad hoc <c>srid is 3857 or 900913 or 102100 or 102113 or 3785</c>
    /// predicates that were duplicated across providers, render, tile, and geometry paths (#2732).
    /// Defined in terms of <see cref="NormalizeWebMercatorSrid(int)"/> so the alias set stays in one
    /// place.
    /// </remarks>
    /// <param name="srid">SRID to test.</param>
    /// <returns><see langword="true"/> for EPSG:3857 or a Web Mercator alias; otherwise <see langword="false"/>.</returns>
    public static bool IsWebMercatorSrid(int srid)
        => NormalizeWebMercatorSrid(srid) == 3857;

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
