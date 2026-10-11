// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Chooses a corrected nearest path while retaining exact sampling for unsupported fast paths.
/// </summary>
internal static class RasterCorrectedNearestSql
{
    internal const int RendererVersion = 1;
    /// <summary>
    /// Uses SQL COALESCE's lazy evaluation: a rejected native window reaches separable sampling,
    /// and unsupported axes or CRS reach the canonical batched-coordinate fallback. The final
    /// fallback retains all source bands and the caller's complete grid; no fast-path cap silently
    /// removes an existing request. General fallback latency remains unqualified.
    /// </summary>
    internal static string Sample(string rasterExpression, string gridExpression, bool preserveCoverageMask = false)
        => $"COALESCE({RasterNativeWindowSql.TryWarp(rasterExpression, gridExpression, preserveCoverageMask)}, " +
           $"{RasterSeparableNearestSql.TrySample(rasterExpression, gridExpression, preserveCoverageMask)}, " +
           $"{RasterGeneralNearestSql.Sample(rasterExpression, gridExpression, preserveCoverageMask)})";

    /// <summary>
    /// Restores the reference source's band types and NoData only after the coverage-aware union.
    /// An uncovered cell in a band without declared NoData becomes opaque zero at this final step;
    /// it cannot hide valid data from another source during the merge.
    /// </summary>
    internal static string RestoreAfterMosaic(string rasterExpression, string referenceExpression)
        => $"""
            (
                WITH restore_input AS MATERIALIZED (
                    SELECT {rasterExpression} AS rast, {referenceExpression} AS reference
                )
                SELECT ST_AddBand(ST_MakeEmptyRaster(i.rast), ARRAY(
                    SELECT ST_SetBandNoDataValue(
                        ST_MapAlgebra(i.rast, n, ST_BandPixelType(i.reference, n), '[rast.val]',
                                      COALESCE(ST_BandNoDataValue(i.reference, n), 0)),
                        1, ST_BandNoDataValue(i.reference, n))
                    FROM generate_series(1, ST_NumBands(i.rast)) n ORDER BY n), 1)
                FROM restore_input i WHERE i.rast IS NOT NULL AND i.reference IS NOT NULL
            )
            """;
}
