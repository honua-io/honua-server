// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Db.Postgres.Features.Raster;

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
    /// <param name="alignToSourceCte">
    /// When set, the name of the CTE (or table) the aggregate reads <c>rast</c> from. Every input
    /// raster that does not share the pixel grid of a reference raster drawn from that source is
    /// resampled onto it before the union (see <see cref="CreateAlignedRasterExpression"/>), so a
    /// layer that mixes native resolutions or grid origins can be unioned at all. When null the
    /// aggregate reads <c>rast</c> unchanged; use that only for inputs already warped onto one grid.
    /// </param>
    public static string CreateMosaicAggregateExpression(
        RasterMergeStrategy mergeStrategy,
        RasterMosaicOrdering ordering = RasterMosaicOrdering.AcquisitionNewest,
        RasterMosaicAttributeSort? attributeSort = null,
        string? alignToSourceCte = null)
    {
        var input = alignToSourceCte is null ? "rast" : CreateAlignedRasterExpression(alignToSourceCte);
        return mergeStrategy switch
        {
            // MEAN/MAX/MIN are order-independent; the ordering clause is meaningless for them.
            RasterMergeStrategy.Average => $"ST_Union({input}, 'MEAN')",
            RasterMergeStrategy.Max => $"ST_Union({input}, 'MAX')",
            RasterMergeStrategy.Min => $"ST_Union({input}, 'MIN')",

            // Oldest is an explicit FIRST/oldest-acquisition selection regardless of the
            // requested ordering; ordering only refines the newest/Northwest/lock cases below.
            RasterMergeStrategy.Oldest => $"ST_Union({input}, 'FIRST' ORDER BY {OrderByClause(ordering, attributeSort)})",

            // Newest (and the default) honour the requested ordering via a LAST union: the row
            // sorted last in the ORDER BY wins the contested pixel.
            _ => $"ST_Union({input}, 'LAST' ORDER BY {OrderByClause(ordering, attributeSort)})"
        };
    }

    /// <summary>
    /// Returns a per-row raster expression that puts <c>rast</c> on one pixel grid shared by every
    /// row of <paramref name="sourceCte"/>, for use as the input of an <c>ST_Union</c> aggregate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ST_Union</c> requires every input to share scale, skew and a grid origin a whole number
    /// of pixels apart; otherwise PostGIS raises <c>rt_raster_from_two_rasters: The two rasters
    /// provided do not have the same alignment</c>. A layer that mixes native resolutions (national
    /// imagery patched with finer local tiles) or grid origins therefore failed every mosaic
    /// operation (honua-server#4792). This is the same resample-to-a-reference-grid step the
    /// dataset map renderer applies before its union (honua-server#2487).
    /// </para>
    /// <para>
    /// For axis-aligned inputs, the shared grid uses the smallest absolute X and Y pixel scales
    /// found in the source set, so an anisotropic raster cannot be coarsened along either axis
    /// merely because another raster has an equal or smaller affine area. Its origin comes from
    /// the lowest-id source so the grid remains deterministic. This can produce a finer synthetic
    /// grid than every one input raster, which is required when the finest X and Y scales occur in
    /// different rasters. For skewed inputs, the smallest-area source affine transform is retained
    /// as the reference; skew terms cannot be combined independently without changing the grid's
    /// orientation.
    /// Inputs are resampled with nearest-neighbour, which invents no pixel values. An aligned
    /// layer passes through untouched, so its mosaics are byte-identical to the pre-#4792 result.
    /// The reference lookup is an uncorrelated scalar subquery evaluated once per statement;
    /// the source must project
    /// <c>rast</c> and <c>id</c>, which every mosaic source already does.
    /// </para>
    /// <para>
    /// Snapping to the reference grid widens a raster to whole reference pixels. For unsigned
    /// bands without NoData, zero can be valid data. If an unaligned mosaic has such a band,
    /// every input is converted to 64BF before union and reserves the lowest finite double
    /// as NoData. That value lies outside every integer and 32BF source range. Double precision
    /// exactly represents every source integer pixel type, and a uniform output type prevents
    /// ST_Union from narrowing back to its first input's pixel type.
    /// A valid 64BF pixel equal to that reserved value instead raises an explicit collision
    /// error; one NoData marker cannot also represent that valid value.
    /// Outside that promotion path, NoData-less bands receive their pixel type's default as
    /// NoData after resampling so the snapped margin is excluded. Signed and floating pixels
    /// holding that default can therefore be treated as NoData in an unaligned mosaic.
    /// </para>
    /// </remarks>
    internal static string CreateAlignedRasterExpression(string sourceCte)
    {
        var reference = $"""
            (SELECT CASE WHEN skew.has_skew THEN anchor.rast ELSE ST_MakeEmptyRaster(
                        ST_Width(anchor.rast), ST_Height(anchor.rast),
                        ST_UpperLeftX(anchor.rast), ST_UpperLeftY(anchor.rast),
                        CASE WHEN ST_ScaleX(anchor.rast) < 0 THEN -axes.scale_x ELSE axes.scale_x END,
                        CASE WHEN ST_ScaleY(anchor.rast) < 0 THEN -axes.scale_y ELSE axes.scale_y END,
                        0, 0, ST_SRID(anchor.rast)) END
             FROM (
                 SELECT align_anchor.rast
                 FROM {sourceCte} AS align_anchor
                 WHERE align_anchor.rast IS NOT NULL AND NOT ST_IsEmpty(align_anchor.rast)
                 ORDER BY abs(ST_ScaleX(align_anchor.rast) * ST_ScaleY(align_anchor.rast)
                            - ST_SkewX(align_anchor.rast) * ST_SkewY(align_anchor.rast)) ASC,
                          align_anchor.id ASC
                 LIMIT 1
             ) AS anchor
             CROSS JOIN LATERAL (
                 SELECT min(abs(ST_ScaleX(align_axes.rast))) AS scale_x,
                        min(abs(ST_ScaleY(align_axes.rast))) AS scale_y
                 FROM {sourceCte} AS align_axes
                 WHERE align_axes.rast IS NOT NULL AND NOT ST_IsEmpty(align_axes.rast)
             ) AS axes
             CROSS JOIN LATERAL (
                 SELECT EXISTS (
                     SELECT 1 FROM {sourceCte} AS skew_candidate
                     WHERE skew_candidate.rast IS NOT NULL AND NOT ST_IsEmpty(skew_candidate.rast)
                       AND (ST_SkewX(skew_candidate.rast) <> 0 OR ST_SkewY(skew_candidate.rast) <> 0)
                 ) AS has_skew
             ) AS skew)
            """;

        // The pixel type's default NoData value, i.e. the value PostGIS fills the snapped-out
        // margin with (rt_band_get_default_nodata).
        const string DefaultNoData = """
            CASE ST_BandPixelType(resampled.rast, band.n)
                WHEN '8BSI' THEN -128
                WHEN '16BSI' THEN -32768
                WHEN '32BSI' THEN -123457
                WHEN '32BF' THEN -123456.789
                WHEN '64BF' THEN -123456.789
                ELSE 0
            END
            """;

        const string UnsignedTypes = "'1BB', '2BUI', '4BUI', '8BUI', '16BUI', '32BUI'";
        var widenSource = $"""
            (EXISTS (
                SELECT 1 FROM {sourceCte} AS candidate
                WHERE candidate.rast IS NOT NULL AND NOT ST_IsEmpty(candidate.rast)
                  AND NOT COALESCE(ST_SameAlignment(candidate.rast, {reference}), TRUE))
             AND EXISTS (
                SELECT 1 FROM {sourceCte} AS candidate
                CROSS JOIN LATERAL generate_series(1, ST_NumBands(candidate.rast)) AS band(n)
                WHERE candidate.rast IS NOT NULL AND NOT ST_IsEmpty(candidate.rast)
                  AND ST_BandPixelType(candidate.rast, band.n) IN ({UnsignedTypes})
                  AND ST_BandNoDataValue(candidate.rast, band.n) IS NULL))
            """;

        // Map algebra emits the requested NoData value for an input NoData pixel. Resetting
        // that value on the wider result keeps existing masks intact while preserving valid
        // zero and signed -1. Every band is widened because ST_Union inherits the first
        // input's pixel type, independently of which row needed resampling.
        // A 64BF source can itself contain the reserved value. Detect that exceptional
        // collision before conversion and fail explicitly instead of silently erasing it.
        // The cast deliberately carries a stable diagnostic into the PostgreSQL error.
        const string WideNoData = """
            CASE WHEN ST_BandPixelType(rast, band.n) = '64BF'
                       AND ST_ValueCount(rast, band.n, TRUE, -1.7976931348623157e308) > 0
                 THEN ('raster_mosaic_64bf_nodata_collision:' || ST_BandPixelType(rast, band.n))::double precision
                 ELSE -1.7976931348623157e308
            END
            """;
        var wide = $"""
            ST_AddBand(
                ST_MakeEmptyRaster(rast),
                (SELECT array_agg(
                            ST_SetBandNoDataValue(
                                ST_MapAlgebra(rast, band.n, '64BF', '[rast.val]', {WideNoData}),
                                1, {WideNoData}) ORDER BY band.n)
                 FROM generate_series(1, ST_NumBands(rast)) AS band(n)))
            """;

        var resampled = $"""
            (SELECT ST_AddBand(
                        ST_MakeEmptyRaster(resampled.rast),
                        (SELECT array_agg(
                                    CASE WHEN ST_BandNoDataValue(resampled.rast, band.n) IS NULL
                                         THEN ST_SetBandNoDataValue(ST_Band(resampled.rast, band.n), 1, {DefaultNoData})
                                         ELSE ST_Band(resampled.rast, band.n)
                                    END ORDER BY band.n)
                         FROM generate_series(1, ST_NumBands(resampled.rast)) AS band(n)))
             FROM (SELECT ST_Resample(prepared.rast, {reference}, 'NearestNeighbor') AS rast) AS resampled)
            """;

        return $"""
            (SELECT CASE
                        WHEN ST_IsEmpty(rast) OR COALESCE(ST_SameAlignment(rast, {reference}), TRUE)
                            THEN prepared.rast
                        ELSE {resampled}
                    END
             FROM (SELECT CASE WHEN {widenSource} THEN {wide} ELSE rast END AS rast) AS prepared)
            """;
    }

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
