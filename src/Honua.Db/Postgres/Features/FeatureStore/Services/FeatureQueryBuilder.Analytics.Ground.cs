// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Ground-distance limits for geographic cluster, density and buffer SQL.
/// Azimuthal equidistant tangential scale is <c>rho / sin(rho)</c>. At
/// <see cref="SinglePlaneMaxCornerMetres"/> from the plane centre on the WGS 84
/// semi-major axis that error is still under 1%. Wider extents are tiled into
/// <see cref="PlaneTileMetres"/> cells so a corner stays a few hundred kilometres
/// from its own centre.
/// </summary>
internal static class AzimuthalGroundPlane
{
    internal const double SinglePlaneMaxCornerMetres = 1_500_000d;

    internal const double PlaneTileMetres = 400_000d;

    internal const double Wgs84SemiMajorMetres = 6_378_137d;

    /// <summary>Keeps per-plane DBSCAN ids from colliding. Zero stays the local id.</summary>
    internal const long ClusterPlaneIdStride = 1L << 32;

    internal static double TangentialScaleError(double metresFromCentre)
    {
        if (metresFromCentre <= 0d)
        {
            return 0d;
        }

        var rho = metresFromCentre / Wgs84SemiMajorMetres;
        return (rho / Math.Sin(rho)) - 1d;
    }

    internal static double CornerRadiusMetres(double widthMetres, double heightMetres)
        => Math.Sqrt((widthMetres * widthMetres) + (heightMetres * heightMetres)) / 2d;

    internal static (double MetresPerDegreeLat, double MetresPerDegreeLon) MetresPerDegree(double latitudeDegrees)
    {
        var lat = latitudeDegrees * (Math.PI / 180d);
        var metresPerDegreeLat = 111132.954d - (559.822d * Math.Cos(2d * lat)) + (1.175d * Math.Cos(4d * lat));
        var metresPerDegreeLon = (111412.84d * Math.Cos(lat)) - (93.5d * Math.Cos(3d * lat)) + (0.118d * Math.Cos(5d * lat));
        return (metresPerDegreeLat, metresPerDegreeLon);
    }

    /// <summary>
    /// True when one azimuthal plane keeps the envelope corner inside the 1% scale limit.
    /// A longitude span wider than 180° is the complementary short arc, matching
    /// <c>TryResolveAeqdCenter</c>.
    /// </summary>
    internal static bool EnvelopeFitsOnePlane(double minX, double maxX, double minY, double maxY)
    {
        var span = maxX - minX;
        var widthDegrees = span > 180d ? 360d - span : span;
        var heightDegrees = maxY - minY;
        var (metresPerDegreeLat, metresPerDegreeLon) = MetresPerDegree((minY + maxY) / 2d);
        var widthMetres = widthDegrees * Math.Max(metresPerDegreeLon, 0d);
        var heightMetres = heightDegrees * metresPerDegreeLat;
        return CornerRadiusMetres(widthMetres, heightMetres) <= SinglePlaneMaxCornerMetres;
    }
}

internal sealed partial class FeatureQueryBuilder
{
    private static bool TryUseFixedGroundPlane(
        FeatureQuery query, int sourceSrid, out double latitude, out double longitude)
    {
        if (!TryResolveAeqdCenter(query, sourceSrid, out latitude, out longitude)
            || query.SpatialFilter is not { } filter
            || filter.EnvelopeMinX is not double minX
            || filter.EnvelopeMaxX is not double maxX
            || filter.EnvelopeMinY is not double minY
            || filter.EnvelopeMaxY is not double maxY)
        {
            return false;
        }

        return AzimuthalGroundPlane.EnvelopeFitsOnePlane(minX, maxX, minY, maxY);
    }

    private static string AeqdForwardFromColumns(string latitudeSql, string longitudeSql)
        => "('+proj=pipeline +step +proj=unitconvert +xy_in=deg +xy_out=rad +step +proj=aeqd +lat_0=' || to_char("
            + latitudeSql
            + ", 'FM999990.999999999') || ' +lon_0=' || to_char("
            + longitudeSql
            + ", 'FM999990.999999999') || ' +ellps=WGS84 +units=m')";

    private static string AeqdInverseFromColumns(string latitudeSql, string longitudeSql)
        => "('+proj=pipeline +step +inv +proj=aeqd +lat_0=' || to_char("
            + latitudeSql
            + ", 'FM999990.999999999') || ' +lon_0=' || to_char("
            + longitudeSql
            + ", 'FM999990.999999999') || ' +ellps=WGS84 +units=m +step +proj=unitconvert +xy_in=rad +xy_out=deg')";

    /// <summary>
    /// Assigns each filtered row to a ground-metre azimuthal plane. A longitude
    /// set whose largest gap is interior straddles the antimeridian: the centre
    /// and the tiles then lie on the short arc. One plane uses the box centroid
    /// when the rows do not wrap, which is the same centre as
    /// <c>ST_Centroid(ST_Extent)</c>.
    /// </summary>
    private static void AppendGroundPlaneCtes(StringBuilder sql, string geometryColumn)
    {
        if (geometryColumn is not ("geom" or "pt_geo"))
        {
            throw new ArgumentOutOfRangeException(nameof(geometryColumn));
        }

        var tile = SqlDouble(AzimuthalGroundPlane.PlaneTileMetres);
        var limit = SqlDouble(AzimuthalGroundPlane.SinglePlaneMaxCornerMetres);
        sql.Append(CultureInfo.InvariantCulture, $$"""
            plane_extent AS (
                SELECT ST_Extent({{geometryColumn}}) AS ext FROM filtered
            ), plane_lons AS (
                SELECT ST_X(ST_Centroid({{geometryColumn}})) AS lon
                FROM filtered
                WHERE ST_X(ST_Centroid({{geometryColumn}})) IS NOT NULL
            ), plane_ordered AS (
                SELECT lon, lead(lon) OVER (ORDER BY lon) AS next_lon FROM plane_lons
            ), plane_gaps AS (
                SELECT lon AS left_lon, next_lon AS right_lon, next_lon - lon AS gap, false AS is_wrap
                FROM plane_ordered
                WHERE next_lon IS NOT NULL
                UNION ALL
                SELECT max(lon), min(lon), min(lon) + 360.0 - max(lon), true FROM plane_lons
            ), plane_best AS (
                SELECT left_lon, right_lon, gap, is_wrap
                FROM plane_gaps
                ORDER BY gap DESC NULLS LAST, is_wrap DESC, left_lon
                LIMIT 1
            ), plane_frame AS (
                SELECT
                    ST_Y(ST_Centroid(e.ext)) AS mid_lat,
                    ST_X(ST_Centroid(e.ext)) AS box_lon,
                    ST_XMin(e.ext) AS min_x,
                    ST_XMax(e.ext) AS max_x,
                    ST_YMin(e.ext) AS min_y,
                    ST_YMax(e.ext) AS max_y,
                    b.left_lon,
                    b.right_lon,
                    b.gap,
                    COALESCE(b.is_wrap, true) AS is_wrap
                FROM (SELECT 1 AS present) AS anchor
                LEFT JOIN plane_extent e ON true
                LEFT JOIN plane_best b ON true
            ), plane_scale AS (
                SELECT f.*,
                    CASE
                        WHEN NOT f.is_wrap AND f.gap IS NOT NULL AND f.right_lon IS NOT NULL
                            THEN {{NormalizeLongitudeSql("(f.right_lon + f.left_lon + 360.0) / 2.0")}}
                        ELSE COALESCE(f.box_lon, 0)
                    END AS center_lon_single,
                    CASE
                        WHEN NOT f.is_wrap AND f.gap IS NOT NULL AND f.right_lon IS NOT NULL THEN f.right_lon
                        ELSE COALESCE(f.min_x, 0)
                    END AS origin_lon,
                    CASE
                        WHEN NOT f.is_wrap AND f.gap IS NOT NULL THEN 360.0 - f.gap
                        ELSE COALESCE(f.max_x - f.min_x, 0)
                    END AS width_deg,
                    COALESCE(f.max_y - f.min_y, 0) AS height_deg,
                    (111132.954 - 559.822 * cos(radians(2 * COALESCE(f.mid_lat, 0)))
                        + 1.175 * cos(radians(4 * COALESCE(f.mid_lat, 0)))) AS metres_per_degree_lat,
                    (111412.84 * cos(radians(COALESCE(f.mid_lat, 0)))
                        - 93.5 * cos(radians(3 * COALESCE(f.mid_lat, 0)))
                        + 0.118 * cos(radians(5 * COALESCE(f.mid_lat, 0)))) AS metres_per_degree_lon
                FROM plane_frame f
            ), plane_decision AS (
                SELECT s.*,
                    s.width_deg * GREATEST(s.metres_per_degree_lon, 0) AS width_m,
                    s.height_deg * s.metres_per_degree_lat AS height_m,
                    sqrt(
                        power(s.width_deg * GREATEST(s.metres_per_degree_lon, 0) / 2.0, 2)
                        + power(s.height_deg * s.metres_per_degree_lat / 2.0, 2)
                    ) > {{limit}} AS split
                FROM plane_scale s
            ), plane_points AS (
                SELECT f.*,
                    ST_X(ST_Centroid(f.{{geometryColumn}})) AS plane_lon,
                    ST_Y(ST_Centroid(f.{{geometryColumn}})) AS plane_lat
                FROM filtered f
            ), plane_located AS (
                SELECT p.*,
                    d.center_lon_single, d.mid_lat, d.origin_lon, d.min_y, d.width_m, d.height_m,
                    d.metres_per_degree_lat, d.metres_per_degree_lon, d.split,
                    CASE
                        WHEN p.plane_lon IS NULL THEN NULL
                        WHEN d.is_wrap OR d.gap IS NULL OR d.right_lon IS NULL THEN p.plane_lon
                        WHEN p.plane_lon >= d.right_lon THEN p.plane_lon
                        ELSE p.plane_lon + 360.0
                    END AS ulon
                FROM plane_points p
                CROSS JOIN plane_decision d
            ), plane_cells AS (
                SELECT l.*,
                    CASE
                        WHEN l.ulon IS NULL OR l.split IS NOT TRUE THEN 0
                        ELSE LEAST(
                            floor(GREATEST((l.ulon - l.origin_lon) * GREATEST(l.metres_per_degree_lon, 1.0), 0) / {{tile}})::integer,
                            GREATEST(ceil(l.width_m / {{tile}})::integer - 1, 0))
                    END AS plane_x,
                    CASE
                        WHEN l.plane_lat IS NULL OR l.split IS NOT TRUE THEN 0
                        ELSE LEAST(
                            floor(GREATEST((l.plane_lat - l.min_y) * l.metres_per_degree_lat, 0) / {{tile}})::integer,
                            GREATEST(ceil(l.height_m / {{tile}})::integer - 1, 0))
                    END AS plane_y
                FROM plane_located l
            ), grounded AS (
                SELECT c.*,
                    CASE
                        WHEN c.ulon IS NULL OR c.split IS NOT TRUE THEN 0
                        ELSE c.plane_x::bigint * 100000 + c.plane_y::bigint
                    END AS plane_id,
                    CASE
                        WHEN c.ulon IS NULL OR c.split IS NOT TRUE THEN COALESCE(c.center_lon_single, 0)
                        ELSE {{NormalizeLongitudeSql("c.origin_lon + (c.plane_x + 0.5) * " + tile + " / GREATEST(c.metres_per_degree_lon, 1.0)")}}
                    END AS center_lon,
                    CASE
                        WHEN c.ulon IS NULL OR c.split IS NOT TRUE THEN COALESCE(c.mid_lat, 0)
                        ELSE c.min_y + (c.plane_y + 0.5) * {{tile}} / GREATEST(c.metres_per_degree_lat, 1.0)
                    END AS center_lat
                FROM plane_cells c
            )
            """);
    }

    private static string NormalizeLongitudeSql(string expression)
    {
        var value = $"({expression})::double precision";
        // Match C# remainder into [-180, 180], mapping the exact -180 boundary to 180
        // so an antimeridian centre is lon_0=180 rather than the long-way box centre.
        return $"(CASE WHEN ({value} - floor(({value} + 180.0) / 360.0) * 360.0) <= -180.0 THEN 180.0 ELSE ({value} - floor(({value} + 180.0) / 360.0) * 360.0) END)";
    }
}
