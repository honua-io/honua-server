// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Builds raster reprojection expressions without resampling an unchanged CRS.
/// </summary>
internal static class RasterProjectionSql
{
    // Unlike the geometry overload, ST_Transform(raster, its_own_srid) is not an
    // identity: GDAL can square non-square pixels and change the grid and extent.
    internal static string TransformIfNeeded(string raster, string targetSrid)
        => $"(CASE WHEN ST_SRID({raster}) = {targetSrid} THEN {raster} ELSE ST_Transform({raster}, {targetSrid}) END)";
}
