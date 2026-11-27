// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

internal static class RasterGridFrameSql
{
    // Frozen framing SQL embedded in migration 008_AddRasterSamplingFunctions.sql.
    // Do not modify without a forward migration; VerifyFunctionsAsync checks pg_proc.prosrc verbatim.
    internal static string FrameAlignedRasterV1(string rasterExpression, string gridExpression)
        => $"""
            (
                WITH frame_input AS MATERIALIZED (
                    SELECT {rasterExpression} AS rast, {gridExpression} AS grid
                ),
                frame_canvas AS (
                    SELECT ST_AddBand(i.grid, ARRAY(
                               SELECT ROW(NULL, m.pixeltype, COALESCE(m.nodatavalue, 0), COALESCE(m.nodatavalue, 0))::addbandarg
                               FROM generate_series(1, ST_NumBands(i.rast)) AS n,
                                    LATERAL ST_BandMetaData(i.rast, n) AS m
                               ORDER BY n)) AS rast
                    FROM frame_input i
                    WHERE i.rast IS NOT NULL
                ),
                frame_union AS (
                    SELECT ST_Union(layers.rast, 'LAST' ORDER BY layers.layer_order) AS rast
                    FROM (
                        SELECT rast, 1 AS layer_order FROM frame_canvas
                        UNION ALL
                        SELECT rast, 2 AS layer_order FROM frame_input WHERE rast IS NOT NULL
                    ) layers
                ),
                frame_clipped AS MATERIALIZED (
                    SELECT ST_Clip(u.rast, ST_Envelope(i.grid), TRUE) AS rast
                    FROM frame_union u, frame_input i
                    WHERE i.rast IS NOT NULL
                )
                SELECT ST_AddBand(i.grid, ARRAY(
                    SELECT ST_SetBandNoDataValue(ST_Band(c.rast, n), 1, ST_BandNoDataValue(i.rast, n))
                    FROM generate_series(1, ST_NumBands(i.rast)) AS n
                    ORDER BY n), 1)
                FROM frame_clipped c, frame_input i
                WHERE i.rast IS NOT NULL
            )
            """;

    internal static string FrameAlignedRaster(string rasterExpression, string gridExpression)
        => $"""
            (
                WITH frame_input AS MATERIALIZED (
                    SELECT {rasterExpression} AS rast, {gridExpression} AS grid
                )
                SELECT CASE
                    WHEN i.rast IS NULL THEN NULL
                    WHEN ST_SameAlignment(i.rast, i.grid) AND {MatchesGridExtentPredicate("i.rast", "i.grid")}
                    THEN i.rast
                    ELSE ({BuildCanvasFramedRasterSql()})
                END
                FROM frame_input i
                WHERE i.rast IS NOT NULL
            )
            """;

    private static string BuildCanvasFramedRasterSql()
        => $"""
            WITH frame_canvas AS (
                SELECT ST_AddBand(i.grid, ARRAY(
                           SELECT ROW(NULL, m.pixeltype, COALESCE(m.nodatavalue, 0), COALESCE(m.nodatavalue, 0))::addbandarg
                           FROM generate_series(1, ST_NumBands(i.rast)) AS n,
                                LATERAL ST_BandMetaData(i.rast, n) AS m
                           ORDER BY n)) AS rast
            ),
            frame_union AS (
                SELECT ST_Union(layers.rast, 'LAST' ORDER BY layers.layer_order) AS rast
                FROM (
                    SELECT rast, 1 AS layer_order FROM frame_canvas
                    UNION ALL
                    SELECT i.rast AS rast, 2 AS layer_order
                ) layers
            ),
            frame_clipped AS MATERIALIZED (
                SELECT CASE
                    WHEN {MatchesGridExtentPredicate("u.rast", "i.grid")}
                    THEN u.rast
                    ELSE ST_Clip(u.rast, ST_Envelope(i.grid), TRUE)
                END AS rast
                FROM frame_union u
            )
            SELECT CASE
                WHEN NOT EXISTS (
                    SELECT 1
                    FROM generate_series(1, ST_NumBands(i.rast)) AS n
                    WHERE ST_BandNoDataValue(i.rast, n) IS NULL
                ) THEN c.rast
                WHEN ST_NumBands(i.rast) = 1 THEN ST_SetBandNoDataValue(c.rast, 1, NULL::double precision)
                ELSE ST_AddBand(i.grid, ARRAY(
                    SELECT ST_SetBandNoDataValue(ST_Band(c.rast, n), 1, ST_BandNoDataValue(i.rast, n))
                    FROM generate_series(1, ST_NumBands(i.rast)) AS n
                    ORDER BY n), 1)
            END
            FROM frame_clipped c
            """;

    private static string MatchesGridExtentPredicate(string rasterExpr, string gridExpr)
        => $"""
            ST_Width({rasterExpr}) = ST_Width({gridExpr})
            AND ST_Height({rasterExpr}) = ST_Height({gridExpr})
            AND abs(ST_UpperLeftX({rasterExpr}) - ST_UpperLeftX({gridExpr})) <= abs(ST_ScaleX({gridExpr})) * 1e-6
            AND abs(ST_UpperLeftY({rasterExpr}) - ST_UpperLeftY({gridExpr})) <= abs(ST_ScaleY({gridExpr})) * 1e-6
            """;
}
