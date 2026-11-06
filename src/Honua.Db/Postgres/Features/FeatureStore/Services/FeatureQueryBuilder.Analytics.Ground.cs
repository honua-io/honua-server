// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    /// <summary>
    /// Centre of a tile after its latitude span is clipped to [-90, 90].
    /// A partial polar tile must not report a centre outside that range.
    /// </summary>
    internal static double ClippedTileCenterLatitude(double southDegrees, double northDegrees)
    {
        var south = Math.Clamp(southDegrees, -90d, 90d);
        var north = Math.Clamp(northDegrees, -90d, 90d);
        return (south + north) / 2d;
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
    /// <c>plane_decision</c> is one row and stays MATERIALIZED. Inlined, its
    /// extent, gap and scale expressions are copied into every per-row column
    /// that reads them and evaluated once per feature, which made 100K-point
    /// DBSCAN several times slower than the single-plane SQL it replaced.
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
            ), plane_decision AS MATERIALIZED (
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
                        WHEN c.ulon IS NULL OR c.split IS NOT TRUE THEN LEAST(GREATEST(COALESCE(c.mid_lat, 0), -90.0), 90.0)
                        ELSE {{ClippedTileCenterLatitudeSql(
                            "c.min_y + c.plane_y * " + tile + " / GREATEST(c.metres_per_degree_lat, 1.0)",
                            "c.min_y + (c.plane_y + 1) * " + tile + " / GREATEST(c.metres_per_degree_lat, 1.0)")}}
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

    private static string ClippedTileCenterLatitudeSql(string southSql, string northSql)
        => $"(LEAST(GREATEST(({southSql}), -90.0), 90.0) + LEAST(GREATEST(({northSql}), -90.0), 90.0)) / 2.0";

    /// <summary>
    /// One row per feature per azimuthal tile the feature can affect.
    /// Tiles overlap by <paramref name="epsParam"/> so a pair closer than eps
    /// is not split by a tile edge. The home row keeps the single-plane centre
    /// when the extent is not split.
    /// </summary>
    private static void AppendDbscanTileMembership(StringBuilder sql, string epsParam)
    {
        if (epsParam is not ['$', ..] || !epsParam[1..].All(char.IsAsciiDigit))
        {
            throw new ArgumentOutOfRangeException(nameof(epsParam));
        }

        var tile = SqlDouble(AzimuthalGroundPlane.PlaneTileMetres);
        var steps = $"ceil({epsParam} / {tile})::integer";
        sql.Append(CultureInfo.InvariantCulture, $$"""
            , tile_membership AS (
                SELECT t.objectid, t.attributes, t.geom, t.split,
                    (t.plane_x + v.dx)::bigint * 100000 + (t.plane_y + v.dy)::bigint AS plane_id,
                    CASE
                        WHEN t.split IS NOT TRUE THEN COALESCE(t.center_lon_single, 0)
                        ELSE {{NormalizeLongitudeSql("t.origin_lon + (t.plane_x + v.dx + 0.5) * " + tile + " / GREATEST(t.metres_per_degree_lon, 1.0)")}}
                    END AS center_lon,
                    CASE
                        WHEN t.split IS NOT TRUE THEN LEAST(GREATEST(COALESCE(t.mid_lat, 0), -90.0), 90.0)
                        ELSE {{ClippedTileCenterLatitudeSql(
                            "t.min_y + (t.plane_y + v.dy) * " + tile + " / GREATEST(t.metres_per_degree_lat, 1.0)",
                            "t.min_y + (t.plane_y + v.dy + 1) * " + tile + " / GREATEST(t.metres_per_degree_lat, 1.0)")}}
                    END AS center_lat
                FROM (
                    SELECT g.*,
                        (g.ulon - g.origin_lon) * GREATEST(g.metres_per_degree_lon, 1.0) AS east_m,
                        (g.plane_lat - g.min_y) * g.metres_per_degree_lat AS north_m
                    FROM grounded g
                ) t
                CROSS JOIN LATERAL (
                    SELECT dx, dy
                    FROM generate_series((-1) * ({{steps}}), ({{steps}})) dx
                    CROSS JOIN generate_series((-1) * ({{steps}}), ({{steps}})) dy
                ) v
                WHERE (v.dx = 0 AND v.dy = 0)
                    OR (
                        t.split IS TRUE
                        AND NOT (v.dx = 0 AND v.dy = 0)
                        AND t.east_m IS NOT NULL
                        AND sqrt(
                            power(CASE
                                WHEN t.east_m < (t.plane_x + v.dx) * {{tile}} THEN (t.plane_x + v.dx) * {{tile}} - t.east_m
                                WHEN t.east_m >= (t.plane_x + v.dx + 1) * {{tile}} THEN t.east_m - (t.plane_x + v.dx + 1) * {{tile}}
                                ELSE 0 END, 2)
                            + power(CASE
                                WHEN t.north_m < (t.plane_y + v.dy) * {{tile}} THEN (t.plane_y + v.dy) * {{tile}} - t.north_m
                                WHEN t.north_m >= (t.plane_y + v.dy + 1) * {{tile}} THEN t.north_m - (t.plane_y + v.dy + 1) * {{tile}}
                                ELSE 0 END, 2)
                        ) <= {{epsParam}}
                    )
            )
            """);
    }

    /// <summary>
    /// Merges per-tile DBSCAN labels that share a feature id, then emits one
    /// row per feature. An unsplit extent keeps PostGIS's own cluster ids.
    /// </summary>
    private static void AppendDbscanLabelMerge(StringBuilder sql, string clusterExpression)
    {
        sql.Append(CultureInfo.InvariantCulture, $$"""
            , local_cluster AS (
                SELECT objectid, attributes, geom, plane_id, split, {{clusterExpression}} AS local_id
                FROM projected
            ), cluster_member AS (
                SELECT objectid, plane_id, local_id
                FROM local_cluster
                WHERE local_id IS NOT NULL
            ), cluster_seed AS (
                SELECT plane_id, local_id, MIN(objectid) AS label
                FROM cluster_member
                GROUP BY plane_id, local_id
            ), cluster_walk AS (
                SELECT plane_id, local_id, label FROM cluster_seed
                UNION
                SELECT w.plane_id, w.local_id, s2.label
                FROM cluster_walk w
                JOIN cluster_member m1 ON m1.plane_id = w.plane_id AND m1.local_id = w.local_id
                JOIN cluster_member m2 ON m2.objectid = m1.objectid
                JOIN cluster_seed s2 ON s2.plane_id = m2.plane_id AND s2.local_id = m2.local_id
                WHERE s2.label < w.label
            ), cluster_label AS (
                SELECT plane_id, local_id, MIN(label) AS label
                FROM cluster_walk
                GROUP BY plane_id, local_id
            ), src AS (
                SELECT DISTINCT ON (l.objectid) l.objectid, l.attributes, l.geom,
                    CASE
                        WHEN l.split IS NOT TRUE THEN l.local_id::bigint
                        ELSE lab.label
                    END AS cluster_id
                FROM local_cluster l
                LEFT JOIN cluster_label lab ON lab.plane_id = l.plane_id AND lab.local_id = l.local_id
                ORDER BY l.objectid, lab.label NULLS LAST
            )
            """);
    }
}
