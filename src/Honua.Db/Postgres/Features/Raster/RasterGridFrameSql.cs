// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Pads and crops an aligned raster to an explicit output grid without stretching its pixels.
/// </summary>
internal static class RasterGridFrameSql
{
    /// <summary>
    /// Frames trusted SQL raster expressions onto the reference grid. Resampling alone preserves
    /// the source extent; the NoData canvas supplies uncovered cells and clipping removes overflow.
    /// </summary>
    internal static string FrameAlignedRaster(string rasterExpression, string gridExpression)
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
                )
                SELECT ST_Clip(u.rast, ST_Envelope(i.grid), TRUE)
                FROM frame_union u, frame_input i
                WHERE i.rast IS NOT NULL
            )
            """;
}
