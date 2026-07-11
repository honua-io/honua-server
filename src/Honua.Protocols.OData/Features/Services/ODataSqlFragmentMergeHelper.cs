// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
