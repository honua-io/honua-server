// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Features.Raster;

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
