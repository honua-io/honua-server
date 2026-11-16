// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Db.Postgres.Features.Raster;

internal static class RasterMosaicSql
{
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

    internal static string CreateAlignedRasterExpression(string sourceCte)
    {
        var reference = CreateReferenceGridExpression(sourceCte);

        var widenSource = $"""
            (EXISTS (
                SELECT 1 FROM {sourceCte} AS candidate
                WHERE candidate.rast IS NOT NULL AND NOT ST_IsEmpty(candidate.rast)
                  AND NOT COALESCE(ST_SameAlignment(candidate.rast, {reference}), TRUE))
             AND EXISTS (
                SELECT 1 FROM {sourceCte} AS candidate
                CROSS JOIN LATERAL generate_series(1, ST_NumBands(candidate.rast)) AS band(n)
                WHERE candidate.rast IS NOT NULL AND NOT ST_IsEmpty(candidate.rast)
                  AND ST_BandNoDataValue(candidate.rast, band.n) IS NULL))
            """;

        // Map algebra emits the requested NoData value for an input NoData pixel. Resetting
        // that value on the wider result keeps existing masks intact while preserving valid
        // zero, signed -1 and floating values. Every band is widened because ST_Union inherits the first
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

        return $"""
            (SELECT CASE
                        WHEN ST_IsEmpty(rast) OR COALESCE(ST_SameAlignment(rast, {reference}), TRUE)
                            THEN prepared.rast
                        ELSE ST_Resample(prepared.rast, {reference}, 'NearestNeighbor')
                    END
             FROM (SELECT CASE WHEN {widenSource} THEN {wide} ELSE rast END AS rast) AS prepared)
            """;
    }

    internal static string CreateReferenceGridExpression(string sourceCte)
    {
        const string Determinant = "(ST_ScaleX(candidate.rast) * ST_ScaleY(candidate.rast) - ST_SkewX(candidate.rast) * ST_SkewY(candidate.rast))";
        const string Trace = "(power(ST_ScaleX(candidate.rast), 2) + power(ST_ScaleY(candidate.rast), 2) + power(ST_SkewX(candidate.rast), 2) + power(ST_SkewY(candidate.rast), 2))";
        // The smaller singular value is evaluated in its rationalized form to avoid
        // cancellation for a nearly singular affine transform.
        var skewResolution = $"sqrt(2 * power({Determinant}, 2) / nullif({Trace} + sqrt(greatest(0, power({Trace}, 2) - 4 * power({Determinant}, 2))), 0))";
        var reference = $"""
            (SELECT CASE WHEN NOT EXISTS (
                        SELECT 1 FROM {sourceCte} AS candidate
                        WHERE candidate.rast IS NOT NULL AND NOT ST_IsEmpty(candidate.rast)
                          AND NOT COALESCE(ST_SameAlignment(candidate.rast, first.rast), TRUE))
                        THEN first.rast
                        ELSE ST_MakeEmptyRaster(
                            CASE WHEN NOT (grid.x_resolution > 0 AND grid.y_resolution > 0
                                           AND grid.x_resolution < 'Infinity'::double precision
                                           AND grid.y_resolution < 'Infinity'::double precision)
                                THEN ('raster_mosaic_invalid_affine_grid:' || ST_SRID(first.rast))::integer
                                WHEN EXISTS (
                                SELECT 1 FROM {sourceCte} AS candidate
                                WHERE candidate.rast IS NOT NULL AND NOT ST_IsEmpty(candidate.rast)
                                  AND abs(ST_ScaleX(candidate.rast) * ST_ScaleY(candidate.rast)
                                          - ST_SkewX(candidate.rast) * ST_SkewY(candidate.rast))
                                      / nullif(grid.x_resolution * grid.y_resolution, 0) > 256)
                                THEN ('raster_mosaic_grid_amplification_exceeded:' || ST_SRID(first.rast))::integer
                                ELSE 1 END, 1,
                            ST_UpperLeftX(first.rast), ST_UpperLeftY(first.rast),
                            CASE WHEN ST_ScaleX(first.rast) < 0 THEN -grid.x_resolution ELSE grid.x_resolution END,
                            CASE WHEN ST_ScaleY(first.rast) > 0 THEN grid.y_resolution ELSE -grid.y_resolution END,
                            0, 0, ST_SRID(first.rast))
                    END
             FROM (SELECT align_ref.rast FROM {sourceCte} AS align_ref
                   WHERE align_ref.rast IS NOT NULL AND NOT ST_IsEmpty(align_ref.rast)
                   ORDER BY align_ref.id ASC LIMIT 1) AS first
             CROSS JOIN LATERAL (
                 SELECT least(min(nullif(abs(ST_ScaleX(candidate.rast)), 0)),
                              min(CASE WHEN ST_SkewX(candidate.rast) <> 0 OR ST_SkewY(candidate.rast) <> 0
                                       THEN {skewResolution} END)) AS x_resolution,
                        least(min(nullif(abs(ST_ScaleY(candidate.rast)), 0)),
                              min(CASE WHEN ST_SkewX(candidate.rast) <> 0 OR ST_SkewY(candidate.rast) <> 0
                                       THEN {skewResolution} END)) AS y_resolution
                 FROM {sourceCte} AS candidate
                 WHERE candidate.rast IS NOT NULL AND NOT ST_IsEmpty(candidate.rast)) AS grid)
            """;

        return reference;
    }

    // The ORDER BY orients the union so the desired raster sorts LAST (and therefore wins a
    // LAST union). 'id ASC' is always appended as a unique tiebreaker for determinism.
    private static string OrderByClause(RasterMosaicOrdering ordering, RasterMosaicAttributeSort? attributeSort) => ordering switch
    {
        // esriMosaicAttribute over a non-date attribute: the raster with the winning attribute
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

    internal static string CreateIdentifyContributorArraySql(
        RasterMergeStrategy mergeStrategy,
        RasterMosaicOrdering ordering,
        RasterMosaicAttributeSort? attributeSort,
        string samplePointSql)
    {
        var aligned = CreateAlignedRasterExpression("source");
        var order = OrderByClause(ordering, attributeSort);
        // One row per band that has a value at the sample. FIRST/LAST then pick within
        // that band, so a raster that is NoData on band 2 cannot hide the raster that
        // supplied band 2. MIN/MAX compare that same per-band sample.
        // Keep alignment behind a projection boundary so each band's probes reuse the computed raster.
        var ranked = $"""
            SELECT candidate.id,
                   band.n AS band,
                   ST_Value(candidate.aligned_rast, band.n, {samplePointSql}) AS sample,
                   row_number() OVER (PARTITION BY band.n ORDER BY {order}) AS n,
                   count(*) OVER (PARTITION BY band.n) AS total
            FROM (
                SELECT source.*, {aligned} AS aligned_rast
                FROM source
                WHERE source.rast IS NOT NULL
                OFFSET 0
            ) AS candidate
            CROSS JOIN LATERAL generate_series(1, ST_NumBands(candidate.aligned_rast)) AS band(n)
            WHERE candidate.aligned_rast IS NOT NULL
              AND ST_Value(candidate.aligned_rast, band.n, {samplePointSql}) IS NOT NULL
            """;

        var picked = mergeStrategy switch
        {
            // Every non-NoData sample contributes to the mean. Order is irrelevant.
            RasterMergeStrategy.Average => DistinctIds(ranked, "TRUE"),

            // MIN/MAX ignore mosaic order and resolve each band. id ASC keeps a tied value
            // on one raster. The reported set is every band's winner.
            RasterMergeStrategy.Min => WinnerByBandValue(ranked, "ASC"),
            RasterMergeStrategy.Max => WinnerByBandValue(ranked, "DESC"),

            // Oldest is the FIRST union: the first non-NoData raster in mosaic order wins
            // that band. A later raster still contributes when an earlier one is NoData there.
            RasterMergeStrategy.Oldest => DistinctIds(ranked, "ranked.n = 1"),

            // Newest and the other LAST unions: the last non-NoData raster in mosaic order
            // wins that band.
            _ => DistinctIds(ranked, "ranked.n = ranked.total"),
        };

        return $"({picked})";
    }

    private static string DistinctIds(string ranked, string predicate) => $"""
        SELECT COALESCE(array_agg(DISTINCT ranked.id ORDER BY ranked.id), ARRAY[]::bigint[])
        FROM ({ranked}) AS ranked
        WHERE {predicate}
        """;

    private static string WinnerByBandValue(string ranked, string direction) => $"""
        SELECT COALESCE(array_agg(DISTINCT picked.id ORDER BY picked.id), ARRAY[]::bigint[])
        FROM (
            SELECT DISTINCT ON (ranked.band) ranked.id
            FROM ({ranked}) AS ranked
            ORDER BY ranked.band, ranked.sample {direction}, ranked.id ASC
        ) AS picked
        """;
}
