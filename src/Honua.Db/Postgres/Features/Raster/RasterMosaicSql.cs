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
    /// <see cref="RasterMergeStrategy"/> and <see cref="RasterMosaicOrdering"/>. The
    /// expression assumes the source CTE projects <c>rast</c>, <c>id</c>,
    /// <c>created_at</c>, and <c>effective_acquisition</c> columns.
    /// </summary>
    /// <param name="mergeStrategy">
    /// Pixel-resolution operation. Only the LAST/FIRST operations (Newest/Oldest) honour
    /// <paramref name="ordering"/>; MEAN/MAX/MIN combine overlapping values without regard
    /// to raster order and ignore it.
    /// </param>
    /// <param name="ordering">
    /// Ordering applied to the LAST/FIRST union so a single raster wins each contested pixel.
    /// </param>
    /// <param name="attributeSort">
    /// Optional non-date attribute ordering for an <c>esriMosaicByAttribute</c> mosaic. Used only
    /// when <paramref name="ordering"/> is <see cref="RasterMosaicOrdering.Attribute"/>; names the
    /// allowlisted catalog column and direction that resolve a contested pixel.
    /// </param>
    public static string CreateMosaicAggregateExpression(
        RasterMergeStrategy mergeStrategy,
        RasterMosaicOrdering ordering = RasterMosaicOrdering.AcquisitionNewest,
        RasterMosaicAttributeSort? attributeSort = null) => mergeStrategy switch
        {
            // MEAN/MAX/MIN are order-independent; the ordering clause is meaningless for them.
            RasterMergeStrategy.Average => "ST_Union(rast, 'MEAN')",
            RasterMergeStrategy.Max => "ST_Union(rast, 'MAX')",
            RasterMergeStrategy.Min => "ST_Union(rast, 'MIN')",

            // Oldest is an explicit FIRST/oldest-acquisition selection regardless of the
            // requested ordering; ordering only refines the newest/Northwest/lock cases below.
            RasterMergeStrategy.Oldest => $"ST_Union(rast, 'FIRST' ORDER BY {OrderByClause(ordering, attributeSort)})",

            // Newest (and the default) honour the requested ordering via a LAST union: the row
            // sorted last in the ORDER BY wins the contested pixel.
            _ => $"ST_Union(rast, 'LAST' ORDER BY {OrderByClause(ordering, attributeSort)})"
        };

    // The ORDER BY orients the union so the desired raster sorts LAST (and therefore wins a
    // LAST union). 'id ASC' is always appended as a unique tiebreaker for determinism.
    private static string OrderByClause(RasterMosaicOrdering ordering, RasterMosaicAttributeSort? attributeSort) => ordering switch
    {
        // esriMosaicByAttribute over a non-date attribute: the raster with the winning attribute
        // value must sort LAST. Esri's default sort is descending (the highest value wins), so an
        // ascending request sorts the lowest value last (DESC) while a descending request sorts
        // the highest value last (ASC). The column is a strictly allowlisted physical column name
        // resolved upstream, never caller free text, so it is safe to interpolate. 'id ASC' keeps
        // the result deterministic on ties.
        RasterMosaicOrdering.Attribute when attributeSort is { } sort =>
            $"{sort.Column} {(sort.Ascending ? "DESC" : "ASC")}, id ASC",

        // Oldest-first: ascending acquisition means the newest sorts last. For a FIRST union
        // this keeps the oldest pixel; for a LAST union it keeps the newest.
        RasterMosaicOrdering.AcquisitionOldest => "effective_acquisition ASC, created_at ASC, id ASC",

        // Northwest: the upper-left-most raster must sort last so a LAST union keeps it.
        // Highest YMax (further north) sorts last via ASC; lowest XMin (further west)
        // sorts last via DESC. 'id ASC' tiebreaker keeps the result deterministic.
        RasterMosaicOrdering.Northwest =>
            "ST_YMax(ST_Envelope(rast)) ASC, ST_XMin(ST_Envelope(rast)) DESC, id ASC",

        // esriMosaicNadir (#1870): the raster acquired closest to straight-down (the lowest
        // off-nadir angle) must sort LAST so a LAST union keeps it. Ordering by off_nadir DESC
        // puts the highest (most off-nadir) first and the lowest (most nadir) last. Rasters with
        // no recorded off-nadir angle (NULL off_nadir, e.g. no sensor metadata row) rank FIRST so
        // any raster with a known, more-nadir view outranks an unknown one. effective_acquisition
        // (newest last) then created_at/id break ties deterministically. The off_nadir value is
        // projected into the source CTE from the allowlisted sensor-metadata payload, never caller
        // free text.
        RasterMosaicOrdering.Nadir =>
            "off_nadir DESC NULLS FIRST, effective_acquisition ASC, created_at ASC, id ASC",

        // Seamline clips each raster to its cutline upstream (in the source CTE); among the
        // clipped pieces the union still resolves any residual overlap by newest acquisition.
        // LockOrder composites the caller-pinned set ordered by newest acquisition, matching
        // the default newest-wins behaviour among the locked rasters.
        // AcquisitionNewest (default): ascending acquisition so the newest raster sorts last
        // and wins the LAST union.
        _ => "effective_acquisition ASC, created_at ASC, id ASC"
    };
}
