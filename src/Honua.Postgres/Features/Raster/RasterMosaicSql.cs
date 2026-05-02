// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Shared SQL fragments for raster mosaic aggregation. Centralised here so
/// every Postgres raster pipeline (catalog mosaic, terrain tiles, elevation
/// query/profile) speaks the same merge semantics.
/// </summary>
internal static class RasterMosaicSql
{
    /// <summary>
    /// Returns an <c>ST_Union</c> aggregate expression that resolves overlapping
    /// rasters in a deterministic order driven by the requested
    /// <see cref="RasterMergeStrategy"/>. The expression assumes the source
    /// CTE projects <c>rast</c>, <c>id</c>, <c>created_at</c>, and
    /// <c>effective_acquisition</c> columns.
    /// </summary>
    public static string CreateMosaicAggregateExpression(RasterMergeStrategy mergeStrategy) => mergeStrategy switch
    {
        RasterMergeStrategy.Newest => "ST_Union(rast, 'LAST' ORDER BY effective_acquisition ASC, created_at ASC, id ASC)",
        RasterMergeStrategy.Oldest => "ST_Union(rast, 'FIRST' ORDER BY effective_acquisition ASC, created_at ASC, id ASC)",
        RasterMergeStrategy.Average => "ST_Union(rast, 'MEAN')",
        RasterMergeStrategy.Max => "ST_Union(rast, 'MAX')",
        RasterMergeStrategy.Min => "ST_Union(rast, 'MIN')",
        _ => "ST_Union(rast, 'LAST' ORDER BY effective_acquisition ASC, created_at ASC, id ASC)"
    };
}
