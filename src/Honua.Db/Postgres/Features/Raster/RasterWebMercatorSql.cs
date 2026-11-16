// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Features.Raster;

internal static class RasterWebMercatorSql
{
    internal const int TileSrid = 3857;

    internal static string BuildEnvelopeExpression(string envelopeOperand, int sourceSrid)
        => DatumTransformSql.BuildTransformExpression(envelopeOperand, TileSrid, selection: null, sourceSrid);

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
