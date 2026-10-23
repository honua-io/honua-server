// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Web Mercator SQL for raster tile pre-generation. Geographic envelopes use the
/// same latitude clip as <see cref="DatumTransformSql"/> so a ±90° corner cannot
/// abort <c>ST_Transform</c> to EPSG:3857.
/// </summary>
internal static class RasterWebMercatorSql
{
    internal const int TileSrid = 3857;

    internal static string BuildEnvelopeExpression(string envelopeOperand, int sourceSrid)
        => DatumTransformSql.BuildTransformExpression(envelopeOperand, TileSrid, selection: null, sourceSrid);

    /// <summary>
    /// Reprojects a raster to EPSG:3857. Geographic rasters whose envelope leaves
    /// the Mercator band are cropped first; rasters already inside the band keep
    /// the bare <c>ST_Transform</c> so ordinary imports do not change pixels.
    /// </summary>
    internal static string BuildRasterExpression(string rasterOperand, int sourceSrid)
    {
        if (!GeographicSridClassifier.IsGeographicSrid(sourceSrid))
        {
            return $"ST_Transform({rasterOperand}, {TileSrid})";
        }

        var latitude = DatumTransformSql.WebMercatorMaxLatitudeLiteral;
        var clip = DatumTransformSql.BuildWebMercatorClipEnvelope(sourceSrid);
        return $"""
            ST_Transform(CASE
                WHEN ST_YMax(ST_Envelope({rasterOperand})) > {latitude}
                  OR ST_YMin(ST_Envelope({rasterOperand})) < -{latitude}
                THEN ST_Clip({rasterOperand}, {clip}, TRUE)
                ELSE {rasterOperand}
            END, {TileSrid})
            """;
    }
}
