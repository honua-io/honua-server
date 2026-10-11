// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Warps bounded native-grid windows without trusting an automatically rounded source extent.
/// </summary>
internal static class RasterNativeWindowSql
{
    /// <summary>
    /// Tries the monotonic north-up identity or geographic-to-WebMercator family. Unsupported
    /// projections, over-budget windows and warps that do not cover the target return null.
    /// The native-cell margin is only a candidate: actual output coverage is mandatory.
    /// Mosaic inputs retain a separate coverage mask; single outputs restore native band types
    /// and their original NoData metadata, including opaque zero outside undeclared-NoData bands.
    /// </summary>
    internal static string TryWarp(string rasterExpression, string gridExpression, bool preserveCoverageMask = false)
    {
        var padded = RasterGridFrameSql.FrameAlignedRaster("w.rast", "w.grid");
        var result = preserveCoverageMask
            ? "f.rast"
            : """
              ST_AddBand(ST_MakeEmptyRaster(f.rast), ARRAY(
                  SELECT ST_SetBandNoDataValue(
                      ST_MapAlgebra(f.rast, n, ST_BandPixelType(i.rast, n), '[rast.val]',
                                    COALESCE(ST_BandNoDataValue(i.rast, n), 0)),
                      1, ST_BandNoDataValue(i.rast, n))
                  FROM generate_series(1, ST_NumBands(i.rast)) n ORDER BY n), 1)
              """;

        return $"""
            (
                WITH window_input AS MATERIALIZED (
                    SELECT {rasterExpression} AS rast, {gridExpression} AS grid
                ), window_eligible AS MATERIALIZED (
                    SELECT i.*, ST_MakeEmptyRaster(i.rast) AS header
                    FROM window_input i
                    WHERE i.rast IS NOT NULL AND NOT ST_IsEmpty(i.rast)
                      AND ST_SkewX(i.rast) = 0 AND ST_SkewY(i.rast) = 0
                      AND ST_SkewX(i.grid) = 0 AND ST_SkewY(i.grid) = 0
                      AND ST_ScaleX(i.rast) > 0 AND ST_ScaleY(i.rast) < 0
                      AND ST_ScaleX(i.grid) > 0 AND ST_ScaleY(i.grid) < 0
                      AND abs(ST_ScaleX(i.rast)) BETWEEN 1e-100 AND 1e100
                      AND abs(ST_ScaleY(i.rast)) BETWEEN 1e-100 AND 1e100
                      AND abs(ST_UpperLeftX(i.rast)) < 1e100
                      AND abs(ST_UpperLeftY(i.rast)) < 1e100
                      AND abs(ST_ScaleX(i.grid)) BETWEEN 1e-100 AND 1e100
                      AND abs(ST_ScaleY(i.grid)) BETWEEN 1e-100 AND 1e100
                      AND abs(ST_UpperLeftX(i.grid)) < 1e100
                      AND abs(ST_UpperLeftY(i.grid)) < 1e100
                      AND ST_NumBands(i.rast) > 0
                      AND ((ST_SRID(i.rast) = ST_SRID(i.grid) AND ST_SRID(i.rast) > 0)
                           OR (ST_SRID(i.rast) = 4326 AND ST_SRID(i.grid) = 3857
                               AND ST_XMin(ST_Envelope(i.grid)) >= -20037508.342789244
                               AND ST_XMax(ST_Envelope(i.grid)) <= 20037508.342789244
                               AND ST_YMin(ST_Envelope(i.grid)) >= -20037508.342789244
                               AND ST_YMax(ST_Envelope(i.grid)) <= 20037508.342789244))
                ), window_inverse AS MATERIALIZED (
                    SELECT i.*, CASE WHEN ST_SRID(i.rast) = ST_SRID(i.grid)
                                     THEN ST_Envelope(i.grid)
                                     ELSE ST_Transform(ST_Envelope(i.grid), 4326) END AS bounds
                    FROM window_eligible i
                ), window_coordinate_guard AS MATERIALIZED (
                    SELECT i.* FROM window_inverse i
                    WHERE (ST_XMin(bounds)::numeric - ST_UpperLeftX(header)::numeric)
                                / NULLIF(ST_ScaleX(header)::numeric, 0) BETWEEN -2147483644 AND 2147483644
                      AND (ST_XMax(bounds)::numeric - ST_UpperLeftX(header)::numeric)
                                / NULLIF(ST_ScaleX(header)::numeric, 0) BETWEEN -2147483644 AND 2147483644
                      AND (ST_YMin(bounds)::numeric - ST_UpperLeftY(header)::numeric)
                                / NULLIF(ST_ScaleY(header)::numeric, 0) BETWEEN -2147483644 AND 2147483644
                      AND (ST_YMax(bounds)::numeric - ST_UpperLeftY(header)::numeric)
                                / NULLIF(ST_ScaleY(header)::numeric, 0) BETWEEN -2147483644 AND 2147483644
                ), window_cells AS MATERIALIZED (
                    SELECT i.*, least(c1.columnx, c2.columnx) - 1 AS c0,
                           greatest(c1.columnx, c2.columnx) + 1 AS c1,
                           least(c1.rowy, c2.rowy) - 1 AS r0,
                           greatest(c1.rowy, c2.rowy) + 1 AS r1
                    FROM window_coordinate_guard i
                    CROSS JOIN LATERAL ST_WorldToRasterCoord(header, ST_XMin(bounds), ST_YMax(bounds)) c1
                    CROSS JOIN LATERAL ST_WorldToRasterCoord(header, ST_XMax(bounds), ST_YMin(bounds)) c2
                ), window_grid AS MATERIALIZED (
                    SELECT i.*, ST_MakeEmptyRaster((c1::bigint - c0 + 1)::integer, (r1::bigint - r0 + 1)::integer, p.longitude, p.latitude,
                               ST_ScaleX(header), ST_ScaleY(header), 0, 0, ST_SRID(header)) AS native_grid
                    FROM window_cells i
                    CROSS JOIN LATERAL ST_RasterToWorldCoord(header, c0, r0) p
                    WHERE c1::bigint - c0 + 1 BETWEEN 1 AND 65535
                      AND r1::bigint - r0 + 1 BETWEEN 1 AND 65535
                      AND (c1::numeric - c0 + 1) * (r1::numeric - r0 + 1) * ST_NumBands(rast) <= 1048576
                ), window_domain AS MATERIALIZED (
                    SELECT i.* FROM window_grid i
                    WHERE ST_SRID(rast) = ST_SRID(grid)
                       OR (ST_XMin(ST_Envelope(native_grid)) >= -180
                           AND ST_XMax(ST_Envelope(native_grid)) <= 180
                           AND ST_YMin(ST_Envelope(native_grid)) > -85.0511287798066
                           AND ST_YMax(ST_Envelope(native_grid)) < 85.0511287798066)
                ), window_projected AS MATERIALIZED (
                    SELECT i.*, CASE WHEN ST_SRID(rast) = ST_SRID(grid) THEN ST_Envelope(native_grid)
                                     ELSE ST_Transform(ST_Envelope(native_grid), ST_SRID(grid)) END AS projected
                    FROM window_domain i
                ), window_estimates AS MATERIALIZED (
                    SELECT i.*,
                           ceil((ST_XMax(projected)::numeric - ST_XMin(projected)::numeric)
                                    / NULLIF(ST_ScaleX(grid)::numeric, 0) + 2) AS projected_width,
                           ceil((ST_YMax(projected)::numeric - ST_YMin(projected)::numeric)
                                    / NULLIF(abs(ST_ScaleY(grid))::numeric, 0) + 2) AS projected_height
                    FROM window_projected i
                ), window_admitted AS MATERIALIZED (
                    SELECT i.* FROM window_estimates i
                    WHERE projected_width BETWEEN 1 AND 65535
                      AND projected_height BETWEEN 1 AND 65535
                      AND projected_width * projected_height * ST_NumBands(rast) <= 16777216
                ), window_clipped AS MATERIALIZED (
                    SELECT i.*, ST_Clip(rast, ST_Envelope(native_grid), TRUE) AS clipped
                    FROM window_admitted i
                ), window_restored AS MATERIALIZED (
                    SELECT i.grid, i.native_grid,
                           ST_AddBand(ST_MakeEmptyRaster(clipped), ARRAY(
                               SELECT ST_SetBandNoDataValue(ST_Band(clipped, n), 1, ST_BandNoDataValue(rast, n))
                               FROM generate_series(1, ST_NumBands(rast)) n ORDER BY n), 1) AS rast
                    FROM window_clipped i WHERE clipped IS NOT NULL AND NOT ST_IsEmpty(clipped)
                ), window_masked AS MATERIALIZED (
                    SELECT i.native_grid AS grid,
                           ST_AddBand(ST_MakeEmptyRaster(i.rast), ARRAY(
                               SELECT ST_SetBandNoDataValue(
                                   ST_MapAlgebra(i.rast, n, '64BF', '[rast.val]',
                                       CASE WHEN ST_BandPixelType(i.rast, n) = '64BF'
                                                 AND ST_ValueCount(i.rast, n, TRUE, -1.7976931348623157e308) > 0
                                            THEN ('raster_mosaic_64bf_nodata_collision:' || n)::double precision
                                            ELSE -1.7976931348623157e308 END),
                                   1, -1.7976931348623157e308)
                               FROM generate_series(1, ST_NumBands(i.rast)) n ORDER BY n), 1) AS rast
                    FROM window_restored i
                ), window_padded AS MATERIALIZED (
                    SELECT {padded} AS rast FROM window_masked w
                ), window_warped AS MATERIALIZED (
                    SELECT ST_Transform(w.rast, g.grid, 'NearestNeighbor', 0.0) AS rast
                    FROM window_padded w, window_input g
                ), window_covered AS MATERIALIZED (
                    SELECT w.rast, g.grid FROM window_warped w, window_input g
                    WHERE ST_Covers(ST_Envelope(w.rast), ST_Envelope(g.grid))
                ), window_framed AS MATERIALIZED (
                    SELECT {RasterGridFrameSql.FrameAlignedRaster("w.rast", "w.grid")} AS rast
                    FROM window_covered w
                )
                SELECT {result} FROM window_framed f, window_input i
            )
            """;
    }
}
