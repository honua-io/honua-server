// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Db.MySql.Features.Infrastructure;

/// <summary>
/// Engine-aware SQL fragments for MySQL/MariaDB spatial WKB I/O.
/// Centralises the axis-order option handling so callers do not duplicate the
/// per-engine branch.
/// </summary>
internal static class MySqlSpatialSql
{
    private const string MysqlAxisOrderOption = "'axis-order=long-lat'";

    /// <summary>
    /// Returns the <c>ST_AsWKB</c> fragment for the supplied (already-quoted) geometry column.
    /// On MySQL the <c>'axis-order=long-lat'</c> option forces canonical X/Y order regardless
    /// of the SRS-defined axis order; MariaDB does not support the option and uses WKB-natural
    /// order by default.
    /// </summary>
    public static string AsWkb(string quotedColumn, MySqlEngineFlavor flavor) =>
        flavor == MySqlEngineFlavor.Mysql
            ? $"ST_AsWKB({quotedColumn}, {MysqlAxisOrderOption})"
            : $"ST_AsWKB({quotedColumn})";

    /// <summary>
    /// Returns the <c>ST_GeomFromWKB</c> fragment that parses an X/Y WKB literal and tags it
    /// with <paramref name="srid"/>. On MySQL the <c>'axis-order=long-lat'</c> option matches
    /// the canonical Honua WKB contract; MariaDB uses the 2-argument form.
    /// </summary>
    public static string GeomFromWkb(string wkbParam, int srid, MySqlEngineFlavor flavor)
    {
        var sridLiteral = srid.ToString(CultureInfo.InvariantCulture);
        return flavor == MySqlEngineFlavor.Mysql
            ? $"ST_GeomFromWKB({wkbParam}, {sridLiteral}, {MysqlAxisOrderOption})"
            : $"ST_GeomFromWKB({wkbParam}, {sridLiteral})";
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="srid"/> is an SRS the provider supports for
    /// geodesic distance. Restricted to EPSG:4326 (WGS 84) and SRID 0. Projected SRSes
    /// raise <c>ER_INVALID_GIS_DATA</c>, and other geographic SRSes are a different
    /// ellipsoid — pre-project to EPSG:4326 before issuing distance filters.
    /// </summary>
    public static bool IsDistanceSphereCompatibleSrid(int srid) =>
        srid is 0 or 4326;

    /// <summary>
    /// IUGG mean Earth radius, metres. MySQL <c>ST_Distance_Sphere</c> is a sphere, and
    /// without this argument the engine uses its own default radius.
    /// </summary>
    public const double DistanceSphereRadiusMeters = 6_371_008.8;

    /// <summary>
    /// Geodesic distance in metres. MySQL on EPSG:4326 uses <c>ST_Distance</c>, which is
    /// the host geodetic distance on the WGS 84 ellipsoid. MariaDB has no spheroid
    /// function, and SRID 0 has no ellipsoid, so those hosts keep
    /// <c>ST_Distance_Sphere</c> with <see cref="DistanceSphereRadiusMeters"/> visible in
    /// the SQL.
    /// </summary>
    public static string GeodesicDistance(string left, string right, int srid, MySqlEngineFlavor flavor)
    {
        if (flavor == MySqlEngineFlavor.Mysql && srid == 4326)
        {
            return $"ST_Distance({left}, {right})";
        }

        return DistanceSphere(left, right);
    }

    /// <summary>
    /// <c>ST_Distance_Sphere(left, right, radius)</c> with the explicit mean-Earth radius.
    /// Sphere fallback for hosts that have no spheroid function.
    /// </summary>
    public static string DistanceSphere(string left, string right)
    {
        var radius = DistanceSphereRadiusMeters.ToString(CultureInfo.InvariantCulture);
        return $"ST_Distance_Sphere({left}, {right}, {radius})";
    }
}
