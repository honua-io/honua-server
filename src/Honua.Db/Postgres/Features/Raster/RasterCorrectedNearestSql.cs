// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

internal static class RasterCorrectedNearestSql
{
    internal const int RendererVersion = 3;
    internal static string Sample(string rasterExpression, string gridExpression, bool preserveCoverageMask = false)
        => $"COALESCE({RasterNativeWindowSql.TryWarp(rasterExpression, gridExpression, preserveCoverageMask)}, " +
           $"{RasterSeparableNearestSql.TrySample(rasterExpression, gridExpression, preserveCoverageMask)}, " +
           $"{RasterGeneralNearestSql.Sample(rasterExpression, gridExpression, preserveCoverageMask)})";

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
