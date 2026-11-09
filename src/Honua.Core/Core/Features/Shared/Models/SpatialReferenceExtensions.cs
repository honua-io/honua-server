// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;

namespace Honua.Core.Features.Shared.Models;

public static class SpatialReferenceExtensions
{
    public static SpatialReference ToUnifiedSpatialReference(this (int Srid, string? WellKnownText) catalogSpatialRef)
        => new()
        {
            Wkid = catalogSpatialRef.Srid,
            Wkt = catalogSpatialRef.WellKnownText
        };

    public static (int Srid, string? WellKnownText) ToCatalogSpatialReference(this SpatialReference spatialRef)
        => (spatialRef.Wkid, spatialRef.Wkt);

    public static string ToOgcCrsUri(this SpatialReference spatialRef)
        => spatialRef.Wkid switch
        {
            4326 => "http://www.opengis.net/def/crs/OGC/1.3/CRS84", // OGC standard for WGS84
            3857 => "http://www.opengis.net/def/crs/EPSG/0/3857",  // Web Mercator
            _ => $"http://www.opengis.net/def/crs/EPSG/0/{spatialRef.Wkid}"
        };

    public static SpatialReference? FromOgcCrsUri(string crsUri)
    {
        if (string.IsNullOrWhiteSpace(crsUri))
            return null;

        // PA-028: Normalize https:// → http:// before matching. The OGC canonical form uses http://,
        // but some servers (including Honua's Maps handler) emit https:// Content-Crs headers.
        var normalized = crsUri.StartsWith("https://www.opengis.net/", StringComparison.OrdinalIgnoreCase)
            ? "http://" + crsUri["https://".Length..]
            : crsUri;

        if (normalized == "http://www.opengis.net/def/crs/OGC/1.3/CRS84")
        {
            return SpatialReference.WGS84;
        }

        const string epsgUriPrefix = "http://www.opengis.net/def/crs/EPSG/0/";
        if (normalized.StartsWith(epsgUriPrefix, StringComparison.Ordinal) &&
            int.TryParse(normalized.AsSpan(epsgUriPrefix.Length), out var epsgCode))
        {
            return SpatialReference.Create(epsgCode);
        }

        return ExtentExtensions.TryExtractSridFromCrs(normalized, out var srid)
            ? SpatialReference.Create(srid)
            : null;
    }

    // The OGC URI for EPSG:4326 is north-east axis order, which is distinct from CRS84 east-north order.
    private const string Epsg4326CrsUri = "http://www.opengis.net/def/crs/EPSG/0/4326";

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

    public static bool IsSupportedByOgc(this SpatialReference spatialRef)
        => spatialRef.Wkid is 4326 or 3857 or > 0;

    // WKID 4326 is CRS84 and is governed by the OGC, so its authority name is OGC rather than EPSG.
    public static string GetAuthorityName(this SpatialReference spatialRef)
        => spatialRef.Wkid == 4326 ? "OGC" : "EPSG";

    // WKID 4326's authority code is the OGC fragment 1.3:CRS84, not the digits 4326.
    public static string GetAuthorityCode(this SpatialReference spatialRef)
        => spatialRef.Wkid == 4326
            ? "1.3:CRS84"
            : spatialRef.Wkid.ToString(CultureInfo.InvariantCulture);

    public static bool RequiresTransformation(this SpatialReference from, SpatialReference to)
        => from.Wkid != to.Wkid;

    // ArcGIS well-known ids 102100, 102113, 900913, and 3785 are the same coordinate system as EPSG:3857 and normalize to it.
    public static int NormalizeWebMercatorSrid(int srid)
        => srid switch
        {
            102100 or 102113 or 900913 or 3785 => 3857,
            _ => srid
        };

    public static bool IsWebMercatorSrid(int srid)
        => NormalizeWebMercatorSrid(srid) == 3857;

    public static SpatialReference FromWkid(int wkid)
        => SpatialReference.Create(wkid);

    public static SpatialReference FromWkidAndWkt(int wkid, string? wkt)
        => new() { Wkid = wkid, Wkt = wkt };

    public static SpatialReference? ToNullable(this SpatialReference spatialRef)
        => spatialRef;

    public static SpatialReference? FromOptionalWkid(int? wkid)
        => wkid.HasValue ? SpatialReference.Create(wkid.Value) : null;
}
