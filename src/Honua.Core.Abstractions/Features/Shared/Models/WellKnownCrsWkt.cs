// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Canonical EPSG WKT1 for the CRSes that the CRS registries answer from a built-in
/// fast path instead of a spatial-reference catalog.
/// </summary>
/// <remarks>
/// <para>
/// <c>PostgresCrsRegistry</c> and <c>WellKnownCrsRegistry</c> both short-circuit SRIDs
/// 4326 and 3857 before consulting <c>spatial_ref_sys</c>. Those built-in
/// <see cref="CrsDefinition"/>s carried no <see cref="CrsDefinition.Wkt"/>, so every
/// consumer that needs the WKT of a CRS — the shapefile exporter's <c>.prj</c> sidecar and
/// the GeoPackage exporter's <c>gpkg_spatial_ref_sys</c> row — silently got nothing back for
/// the two SRIDs that cover nearly all real layers (honua-server#4419). Defining the WKT
/// here keeps the fast path answering the same question the catalog lookup answers.
/// </para>
/// <para>
/// These are WKT1 with EPSG <c>AUTHORITY</c> nodes, the form <c>spatial_ref_sys.srtext</c>
/// carries and the form shapefile readers expect in a <c>.prj</c>. They are deliberately
/// separate from <see cref="SpatialReference.WGS84"/>/<see cref="SpatialReference.WebMercator"/>,
/// whose shorter authority-free WKT is already emitted on protocol surfaces.
/// </para>
/// </remarks>
public static class WellKnownCrsWkt
{
    /// <summary>
    /// WGS 84 geographic (EPSG:4326). Also used for OGC:CRS84, which differs only in axis order.
    /// </summary>
    public const string Epsg4326 =
        "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563," +
        "AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]]," +
        "PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]]," +
        "UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]]," +
        "AUTHORITY[\"EPSG\",\"4326\"]]";

    /// <summary>
    /// WGS 84 / Pseudo-Mercator (EPSG:3857), the web-mapping projection.
    /// </summary>
    public const string Epsg3857 =
        "PROJCS[\"WGS 84 / Pseudo-Mercator\",GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\"," +
        "SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]]," +
        "AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]]," +
        "UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]]," +
        "AUTHORITY[\"EPSG\",\"4326\"]],PROJECTION[\"Mercator_1SP\"]," +
        "PARAMETER[\"central_meridian\",0],PARAMETER[\"scale_factor\",1]," +
        "PARAMETER[\"false_easting\",0],PARAMETER[\"false_northing\",0]," +
        "UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]],AXIS[\"Easting\",EAST]," +
        "AXIS[\"Northing\",NORTH],AUTHORITY[\"EPSG\",\"3857\"]]";
}
