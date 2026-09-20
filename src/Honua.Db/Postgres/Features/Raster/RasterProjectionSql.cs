// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Builds raster projection and resizing expressions that preserve the requested grid.
/// </summary>
internal static class RasterProjectionSql
{
    // Unlike the geometry overload, ST_Transform(raster, its_own_srid) is not an
    // identity: GDAL can square non-square pixels and change the grid and extent.
    internal static string TransformIfNeeded(string raster, string targetSrid)
        => $"(CASE WHEN ST_SRID({raster}) = {targetSrid} THEN {raster} ELSE ST_Transform({raster}, {targetSrid}) END)";

    // GDAL can change the geographic footprint of non-square or rotated rasters
    // while resizing. Resize the pixel array on a unit grid, then restore the
    // source origin and proportionally scaled geographic basis vectors.
    internal static string ResizePreservingGrid(string raster, string width, string height)
        => $"""
            (SELECT CASE WHEN ST_Width(resize_source.rast) = {width} AND ST_Height(resize_source.rast) = {height}
                THEN resize_source.rast
                ELSE ST_SetSRID(ST_SetGeoReference(
                    ST_Resize(ST_SetSRID(ST_SetGeoReference(resize_source.rast,
                        0, ST_Height(resize_source.rast), 1, -1, 0, 0), 0), {width}, {height}, 'NearestNeighbor'),
                    ST_UpperLeftX(resize_source.rast), ST_UpperLeftY(resize_source.rast),
                    ST_ScaleX(resize_source.rast) * ST_Width(resize_source.rast) / {width},
                    ST_ScaleY(resize_source.rast) * ST_Height(resize_source.rast) / {height},
                    ST_SkewX(resize_source.rast) * ST_Height(resize_source.rast) / {height},
                    ST_SkewY(resize_source.rast) * ST_Width(resize_source.rast) / {width}),
                    ST_SRID(resize_source.rast)) END
             FROM (SELECT {raster} AS rast) resize_source)
            """;
}
