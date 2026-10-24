// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private static void AppendPagination(StringBuilder sql, bool isKnnQuery, FeatureQuery query, SpatialFilter? spatialFilter, ref int paramIndex)
    {
        if (isKnnQuery)
        {
            // For KNN, use NearestCount as LIMIT if specified, otherwise use regular Limit
            var limit = spatialFilter?.NearestCount ?? query.Limit;
            if (limit.HasValue)
            {
                sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
            }
        }
        else if (query.Limit.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
        }

        if (query.Offset.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
        }
    }
}
