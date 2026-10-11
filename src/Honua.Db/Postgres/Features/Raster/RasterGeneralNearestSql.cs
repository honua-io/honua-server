// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Samples source pixels directly onto a requested grid without an intermediate warp extent.
/// </summary>
internal static class RasterGeneralNearestSql
{
    /// <summary>
    /// Uses target pixel centers and the source affine transform for nearest-neighbor sampling.
    /// Coordinate projection is batched once per grid; each distinct requested source cell is
    /// read once per band. Work arrays contain only the requested output cells, never a dump of
    /// the entire source. Mosaic inputs retain a temporary coverage mask; single outputs retain native metadata.
    /// </summary>
    internal static string Sample(string rasterExpression, string gridExpression, bool preserveCoverageMask = false)
    {
        var pixelType = preserveCoverageMask ? "'64BF'" : "m.pixeltype";
        var noData = preserveCoverageMask ? "-1.7976931348623157e308" : "m.nodatavalue";
        var outside = preserveCoverageMask ? "-1.7976931348623157e308" : "COALESCE(m.nodatavalue, 0)";
        var inside = preserveCoverageMask
            ? """
              CASE WHEN v.value IS NOT DISTINCT FROM m.nodatavalue THEN -1.7976931348623157e308
                   WHEN v.value = -1.7976931348623157e308
                   THEN ('raster_mosaic_64bf_nodata_collision:' || m.n)::double precision
                   ELSE v.value END
              """
            : "v.value";
        return $"""
            (
                WITH nearest_input AS MATERIALIZED (
                    SELECT s.*, ST_MakeEmptyRaster(s.rast) AS header,
                           ST_Width(s.rast) AS sw, ST_Height(s.rast) AS sh,
                           ST_Width(s.grid) AS w, ST_Height(s.grid) AS h,
                           ST_UpperLeftX(s.grid) AS ulx, ST_UpperLeftY(s.grid) AS uly,
                           ST_ScaleX(s.grid) AS sx, ST_ScaleY(s.grid) AS sy,
                           ST_SkewX(s.grid) AS kx, ST_SkewY(s.grid) AS ky,
                           ST_UpperLeftX(s.rast)::numeric AS sulx, ST_UpperLeftY(s.rast)::numeric AS suly,
                           ST_ScaleX(s.rast)::numeric AS ssx, ST_ScaleY(s.rast)::numeric AS ssy,
                           ST_SkewX(s.rast)::numeric AS skx, ST_SkewY(s.rast)::numeric AS sky,
                           ST_SRID(s.grid) AS grid_srid
                    FROM (SELECT {rasterExpression} AS rast, {gridExpression} AS grid) s
                ),
                nearest_centers AS MATERIALIZED (
                    SELECT ST_Collect(ARRAY(
                        SELECT ST_SetSRID(ST_MakePoint(
                            i.ulx + (x - 0.5) * i.sx + (y - 0.5) * i.kx,
                            i.uly + (x - 0.5) * i.ky + (y - 0.5) * i.sy),
                            i.grid_srid)
                        FROM generate_series(1, i.h) y,
                             generate_series(1, i.w) x
                        ORDER BY y, x)) AS points
                    FROM nearest_input i WHERE i.rast IS NOT NULL
                ),
                nearest_projected AS MATERIALIZED (
                    SELECT CASE WHEN ST_SRID(i.rast) = i.grid_srid THEN c.points
                        ELSE ST_Transform(c.points, ST_SRID(i.rast)) END AS points
                    FROM nearest_input i, nearest_centers c
                ),
                nearest_affine AS MATERIALIZED (
                    SELECT (p.path[1] - 1) % i.w + 1 AS x,
                           (p.path[1] - 1) / i.w + 1 AS y,
                           ST_X(p.geom) AS wx, ST_Y(p.geom) AS wy,
                           ((ST_X(p.geom)::numeric - i.sulx) * i.ssy
                            - (ST_Y(p.geom)::numeric - i.suly) * i.skx)
                                / NULLIF(i.ssx * i.ssy - i.skx * i.sky, 0) AS cx,
                           ((ST_Y(p.geom)::numeric - i.suly) * i.ssx
                            - (ST_X(p.geom)::numeric - i.sulx) * i.sky)
                                / NULLIF(i.ssx * i.ssy - i.skx * i.sky, 0) AS cy
                    FROM nearest_input i, nearest_projected t,
                         LATERAL ST_DumpPoints(t.points) p
                ),
                nearest_coordinates AS MATERIALIZED (
                    -- Numeric affine bounds are only an overflow guard. PostGIS retains
                    -- canonical cell-boundary rounding for every representable point.
                    -- CASE protects the native calls independently of planner WHERE ordering.
                    SELECT p.x, p.y,
                           CASE WHEN p.cx BETWEEN -2147483644 AND 2147483644
                                     AND p.cy BETWEEN -2147483644 AND 2147483644
                                THEN ST_WorldToRasterCoordX(i.header, p.wx, p.wy) END AS columnx,
                           CASE WHEN p.cx BETWEEN -2147483644 AND 2147483644
                                     AND p.cy BETWEEN -2147483644 AND 2147483644
                                THEN ST_WorldToRasterCoordY(i.header, p.wx, p.wy) END AS rowy
                    FROM nearest_input i, nearest_affine p
                ),
                nearest_metadata AS MATERIALIZED (
                    SELECT n, ST_BandPixelType(i.rast, n) AS pixeltype,
                           ST_BandNoDataValue(i.rast, n) AS nodatavalue
                    FROM nearest_input i, generate_series(1, ST_NumBands(i.rast)) n
                ),
                nearest_requested AS MATERIALIZED (
                    SELECT DISTINCT c.columnx, c.rowy
                    FROM nearest_coordinates c, nearest_input i
                    WHERE c.columnx BETWEEN 1 AND i.sw
                      AND c.rowy BETWEEN 1 AND i.sh
                ),
                nearest_values AS MATERIALIZED (
                    SELECT c.columnx, c.rowy, m.n,
                           ST_Value(i.rast, m.n, c.columnx, c.rowy, FALSE) AS value
                    FROM nearest_requested c, nearest_input i, nearest_metadata m
                ),
                nearest_rows AS (
                    SELECT m.n, c.y, array_agg(CASE WHEN v.columnx IS NOT NULL THEN {inside}
                        ELSE {outside} END ORDER BY c.x) AS cell_values
                    FROM nearest_coordinates c CROSS JOIN nearest_metadata m
                    LEFT JOIN nearest_values v ON v.columnx = c.columnx AND v.rowy = c.rowy AND v.n = m.n
                    GROUP BY m.n, c.y
                ),
                nearest_arrays AS (
                    SELECT n, array_agg(cell_values ORDER BY y) AS cell_values
                    FROM nearest_rows GROUP BY n
                ),
                nearest_bands AS (
                    SELECT a.n, ST_SetValues(
                        ST_AddBand(i.grid, {pixelType}, {outside}, {noData}),
                        1, 1, 1, a.cell_values) AS rast
                    FROM nearest_arrays a JOIN nearest_metadata m USING (n), nearest_input i
                )
                SELECT ST_AddBand(i.grid, ARRAY(SELECT b.rast FROM nearest_bands b ORDER BY b.n), 1)
                FROM nearest_input i WHERE i.rast IS NOT NULL
            )
            """;
    }
}
