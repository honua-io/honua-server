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
    /// A valid lowest-finite 64BF value bypasses the native window's temporary mask for a single
    /// output. Coverage-aware mosaics retain RasterMosaicSql's explicit collision diagnostic:
    /// the reserved NoData marker cannot also represent that valid source value.
    /// </summary>
    internal static string Sample(string rasterExpression, string gridExpression, bool preserveCoverageMask = false)
        => $"COALESCE({RasterNativeWindowSql.TryWarp(rasterExpression, gridExpression, preserveCoverageMask)}, " +
           $"{RasterSeparableNearestSql.TrySample(rasterExpression, gridExpression, preserveCoverageMask)}, " +
           $"{RasterGeneralNearestSql.Sample(rasterExpression, gridExpression, preserveCoverageMask)})";

    /// <summary>
    /// Restores the canonical metadata aggregate's band types and NoData after the coverage-aware union.
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

    /// <summary>
    /// Creates a one-cell typed input for the existing mosaic aggregate's metadata contract.
    /// Its pixels are not rendered; the original ordering, union type and band metadata determine
    /// the output metadata without allocating a union of full source rasters.
    /// </summary>
    internal static string MetadataHeader(string rasterExpression, string gridExpression)
        => $"""
            (SELECT ST_AddBand(ST_MakeEmptyRaster(1, 1,
                        ST_UpperLeftX(i.grid), ST_UpperLeftY(i.grid), ST_ScaleX(i.grid), ST_ScaleY(i.grid),
                        ST_SkewX(i.grid), ST_SkewY(i.grid), ST_SRID(i.grid)), ARRAY(
                        SELECT ROW(NULL, m.pixeltype, COALESCE(m.nodatavalue, 0), m.nodatavalue)::addbandarg
                        FROM generate_series(1, ST_NumBands(i.rast)) n,
                             LATERAL ST_BandMetadata(i.rast, n) m ORDER BY n))
             FROM (SELECT {rasterExpression} AS rast, {gridExpression} AS grid) i)
            """;

    /// <summary>
    /// Keeps the union's per-band metadata, except that a band with no declared source NoData
    /// retains opaque outside zero and valid source zero rather than inheriting a default sentinel.
    /// </summary>
    internal static string RestoreUndeclaredNoData(string rasterExpression, string sourceCte)
        => $"""
            (SELECT ST_AddBand(ST_MakeEmptyRaster(i.rast), ARRAY(
                SELECT ST_SetBandNoDataValue(ST_Band(i.rast, n), 1,
                    CASE WHEN EXISTS (SELECT 1 FROM {sourceCte} s
                                      WHERE ST_BandNoDataValue(s.original, n) IS NOT NULL)
                         THEN ST_BandNoDataValue(i.rast, n) ELSE NULL END)
                FROM generate_series(1, ST_NumBands(i.rast)) n ORDER BY n), 1)
             FROM (SELECT {rasterExpression} AS rast) i WHERE i.rast IS NOT NULL)
            """;
}
