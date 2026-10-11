// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Samples bounded north-up target pages using separable canonical source coordinates.
/// </summary>
internal static class RasterSeparableNearestSql
{
    /// <summary>
    /// Tries north-up same-CRS or EPSG:4326-to-3857 nearest sampling. Other inputs return
    /// null and require an exact general fallback. A source cell is read once per band;
    /// projection and canonical coordinate conversion run once per target row or column.
    /// Mosaic inputs use a temporary coverage mask rather than opaque outside zero.
    /// The general fallback remains available when this bounded path is not admitted.
    /// Larger request sets dump only a bounded native-cell rectangle once per band. Raw values
    /// preserve valid zero even if clipping temporarily supplies a default NoData marker.
    /// An unaligned or over-budget rectangle retains canonical individual cell reads.
    /// </summary>
    internal static string TrySample(string rasterExpression, string gridExpression, bool preserveCoverageMask = false)
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
                WITH sample_input AS MATERIALIZED (
                    SELECT {rasterExpression} AS rast, {gridExpression} AS grid
                ), sample_eligible AS MATERIALIZED (
                    SELECT i.*, ST_MakeEmptyRaster(i.rast) AS header,
                           ST_Width(i.rast) AS sw, ST_Height(i.rast) AS sh,
                           ST_Width(i.grid) AS w, ST_Height(i.grid) AS h,
                           ST_UpperLeftX(i.rast) AS sulx, ST_UpperLeftY(i.rast) AS suly,
                           ST_UpperLeftX(i.grid) AS gulx, ST_UpperLeftY(i.grid) AS guly,
                           ST_ScaleX(i.grid) AS gsx, ST_ScaleY(i.grid) AS gsy
                    FROM sample_input i
                    WHERE i.rast IS NOT NULL AND NOT ST_IsEmpty(i.rast)
                      AND ST_Width(i.grid) BETWEEN 1 AND 256 AND ST_Height(i.grid) BETWEEN 1 AND 256
                      AND ST_NumBands(i.rast) BETWEEN 1 AND 16
                      AND ST_SkewX(i.rast) = 0 AND ST_SkewY(i.rast) = 0
                      AND ST_SkewX(i.grid) = 0 AND ST_SkewY(i.grid) = 0
                      AND ST_ScaleX(i.rast) BETWEEN 1e-100 AND 1e100
                      AND -ST_ScaleY(i.rast) BETWEEN 1e-100 AND 1e100
                      AND ST_ScaleX(i.grid) BETWEEN 1e-100 AND 1e100
                      AND -ST_ScaleY(i.grid) BETWEEN 1e-100 AND 1e100
                      AND abs(ST_UpperLeftX(i.rast)) < 1e100 AND abs(ST_UpperLeftY(i.rast)) < 1e100
                      AND abs(ST_UpperLeftX(i.grid)) < 1e100 AND abs(ST_UpperLeftY(i.grid)) < 1e100
                      AND ((ST_SRID(i.rast) = ST_SRID(i.grid) AND ST_SRID(i.rast) > 0)
                           OR (ST_SRID(i.rast) = 4326 AND ST_SRID(i.grid) = 3857
                               AND ST_XMin(ST_Envelope(i.grid)) >= -20037508.342789244
                               AND ST_XMax(ST_Envelope(i.grid)) <= 20037508.342789244
                               AND ST_YMin(ST_Envelope(i.grid)) >= -20037508.342789244
                               AND ST_YMax(ST_Envelope(i.grid)) <= 20037508.342789244))
                ), sample_axis AS MATERIALIZED (
                    SELECT i.*, CASE WHEN ST_SRID(rast) = ST_SRID(grid) THEN ST_Envelope(grid)
                                     ELSE ST_Transform(ST_Envelope(grid), 4326) END AS inverse_bounds
                    FROM sample_eligible i
                ), sample_guard AS MATERIALIZED (
                    SELECT i.* FROM sample_axis i
                    WHERE (ST_XMin(inverse_bounds)::numeric - sulx::numeric)
                                  / NULLIF(ST_ScaleX(header)::numeric, 0) BETWEEN -2147483644 AND 2147483644
                      AND (ST_XMax(inverse_bounds)::numeric - sulx::numeric)
                                  / NULLIF(ST_ScaleX(header)::numeric, 0) BETWEEN -2147483644 AND 2147483644
                      AND (ST_YMin(inverse_bounds)::numeric - suly::numeric)
                                  / NULLIF(ST_ScaleY(header)::numeric, 0) BETWEEN -2147483644 AND 2147483644
                      AND (ST_YMax(inverse_bounds)::numeric - suly::numeric)
                                  / NULLIF(ST_ScaleY(header)::numeric, 0) BETWEEN -2147483644 AND 2147483644
                ), sample_columns AS MATERIALIZED (
                    SELECT x, c.columnx FROM sample_guard i CROSS JOIN generate_series(1, 256) x
                    CROSS JOIN LATERAL (
                        SELECT CASE WHEN ST_SRID(header) = ST_SRID(grid)
                                    THEN gulx + (x - 0.5) * gsx
                                    ELSE ST_X(ST_Transform(ST_SetSRID(ST_MakePoint(gulx + (x - 0.5) * gsx, 0), 3857), 4326)) END AS longitude
                    ) p
                    CROSS JOIN LATERAL ST_WorldToRasterCoord(header, p.longitude, suly) c
                    WHERE x <= w
                ), sample_rows AS MATERIALIZED (
                    SELECT y, c.rowy FROM sample_guard i CROSS JOIN generate_series(1, 256) y
                    CROSS JOIN LATERAL (
                        SELECT CASE WHEN ST_SRID(header) = ST_SRID(grid)
                                    THEN guly + (y - 0.5) * gsy
                                    ELSE ST_Y(ST_Transform(ST_SetSRID(ST_MakePoint(0, guly + (y - 0.5) * gsy), 3857), 4326)) END AS latitude
                    ) p
                    CROSS JOIN LATERAL ST_WorldToRasterCoord(header, sulx, p.latitude) c
                    WHERE y <= h
                ), sample_coordinates AS MATERIALIZED (
                    SELECT x, y, columnx, rowy FROM sample_columns CROSS JOIN sample_rows
                ), sample_metadata AS MATERIALIZED (
                    SELECT n, ST_BandPixelType(rast, n) AS pixeltype,
                           ST_BandNoDataValue(rast, n) AS nodatavalue
                    FROM sample_guard, generate_series(1, 16) n WHERE n <= ST_NumBands(rast)
                ), sample_requested AS MATERIALIZED (
                    SELECT DISTINCT columnx, rowy FROM sample_coordinates, sample_guard
                    WHERE columnx BETWEEN 1 AND sw AND rowy BETWEEN 1 AND sh
                ), sample_value_bounds AS MATERIALIZED (
                    SELECT min(columnx) AS c0, max(columnx) AS c1,
                           min(rowy) AS r0, max(rowy) AS r1, count(*) AS requested
                    FROM sample_requested
                ), sample_value_grid AS MATERIALIZED (
                    SELECT b.*, ST_MakeEmptyRaster(c1 - c0 + 1, r1 - r0 + 1,
                        i.sulx + (c0 - 1) * ST_ScaleX(i.header),
                        i.suly + (r0 - 1) * ST_ScaleY(i.header),
                        ST_ScaleX(i.header), ST_ScaleY(i.header), 0, 0, ST_SRID(i.header)) AS grid
                    FROM sample_value_bounds b, sample_guard i
                    WHERE requested > 256
                      AND (c1::numeric - c0 + 1) * (r1::numeric - r0 + 1) * ST_NumBands(i.rast) <= 16777216
                ), sample_value_clip AS MATERIALIZED (
                    SELECT g.*, ST_Clip(i.rast, ST_Envelope(g.grid), TRUE) AS rast
                    FROM sample_value_grid g, sample_guard i
                ), sample_value_arrays AS MATERIALIZED (
                    SELECT m.n, v.c0, v.r0, ST_DumpValues(v.rast, m.n, FALSE) AS vals
                    FROM sample_value_clip v, sample_metadata m
                    WHERE ST_Width(v.rast) = v.c1 - v.c0 + 1
                      AND ST_Height(v.rast) = v.r1 - v.r0 + 1
                      AND ST_SameAlignment(v.rast, v.grid)
                      AND ST_Equals(ST_ConvexHull(v.rast), ST_ConvexHull(v.grid))
                ), sample_values AS MATERIALIZED (
                    SELECT r.columnx, r.rowy, m.n,
                           CASE WHEN a.n IS NOT NULL THEN a.vals[r.rowy - a.r0 + 1][r.columnx - a.c0 + 1]
                                ELSE ST_Value(i.rast, m.n, r.columnx, r.rowy, FALSE) END AS value
                    FROM sample_requested r CROSS JOIN sample_metadata m CROSS JOIN sample_guard i
                    LEFT JOIN sample_value_arrays a ON a.n = m.n
                ), sample_row_arrays AS MATERIALIZED (
                    SELECT m.n, y, array_agg(CASE WHEN v.columnx IS NOT NULL THEN {inside}
                                               ELSE {outside} END ORDER BY x) AS vals
                    FROM sample_coordinates c CROSS JOIN sample_metadata m
                    LEFT JOIN sample_values v ON v.columnx = c.columnx AND v.rowy = c.rowy AND v.n = m.n
                    GROUP BY m.n, y
                ), sample_arrays AS MATERIALIZED (
                    SELECT n, array_agg(vals ORDER BY y) AS vals FROM sample_row_arrays GROUP BY n
                ), sample_bands AS MATERIALIZED (
                    SELECT a.n, ST_SetValues(ST_AddBand(i.grid, {pixelType}, {outside}, {noData}),
                                             1, 1, 1, a.vals) AS rast
                    FROM sample_arrays a JOIN sample_metadata m USING (n), sample_guard i
                )
                SELECT ST_AddBand(i.grid, ARRAY(SELECT b.rast FROM sample_bands b ORDER BY n), 1)
                FROM sample_guard i
            )
            """;
    }
}
