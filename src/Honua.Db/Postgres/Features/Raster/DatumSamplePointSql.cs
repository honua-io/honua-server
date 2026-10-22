// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Features.Raster;

/// <summary>
/// Sample-point reprojection for raster identify. A catalog entry uses that pipeline.
/// No entry keeps two-argument <c>ST_Transform</c>.
/// </summary>
internal static class DatumSamplePointSql
{
    internal static string Build(int pointSrid, int rasterSrid)
    {
        const string point = "ST_SetSRID(ST_MakePoint(@x, @y), @pointSrid)";
        if (rasterSrid <= 0 || pointSrid == rasterSrid)
        {
            return point;
        }

        var selection = DatumOutputSelection.Resolve(DatumCatalog.Shared, pointSrid, rasterSrid);
        return DatumTransformSql.BuildTransformExpression(point, rasterSrid, selection, pointSrid);
    }
}
