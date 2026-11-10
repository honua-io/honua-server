// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.OData.Services;

internal static class ODataSqlFragmentMergeHelper
{
    public static FeatureQuery Merge(FeatureQuery query, SqlFragment? sqlFragment)
    {
        var combinedFilter = SqlFragmentHelpers.CombineSqlFilters(query.SqlFilter, sqlFragment);
        if (combinedFilter is null || (query.SqlFilter is not null && sqlFragment is null))
        {
            return query;
        }

        return query with
        {
            SqlFilter = combinedFilter,
            Where = null
        };
    }
}
