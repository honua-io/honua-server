// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private static void AppendTemporalFilter(StringBuilder sql, FeatureQuery query, ref int paramIndex, List<object> parameters)
    {
        if (!query.TemporalFilter.HasValue)
        {
            return;
        }

        var filter = query.TemporalFilter.Value;
        var fieldName = filter.PropertyName;
        if (!IsValidFieldName(fieldName))
        {
            throw new ArgumentException($"Invalid temporal field name: {fieldName}", nameof(query));
        }

        var attributeValue = BuildAttributeValueExpression(fieldName, ref paramIndex, parameters);
        var valueExpression = filter.PropertyType switch
        {
            TemporalPropertyType.Date => $"NULLIF({attributeValue}, '')::date",
            _ => $"NULLIF({attributeValue}, '')::timestamptz"
        };

        string? predicate = null;

        if (filter.Start.HasValue && filter.End.HasValue)
        {
            var startIndex = paramIndex++;
            var endIndex = paramIndex++;
            parameters.Add(filter.Start.Value.UtcDateTime);
            parameters.Add(filter.End.Value.UtcDateTime);

            var startExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${startIndex}::date" : $"${startIndex}";
            var endExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${endIndex}::date" : $"${endIndex}";
            predicate = $"{valueExpression} >= {startExpr} AND {valueExpression} <= {endExpr}";
        }
        else if (filter.Start.HasValue)
        {
            var startIndex = paramIndex++;
            parameters.Add(filter.Start.Value.UtcDateTime);

            var startExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${startIndex}::date" : $"${startIndex}";
            predicate = $"{valueExpression} >= {startExpr}";
        }
        else if (filter.End.HasValue)
        {
            var endIndex = paramIndex++;
            parameters.Add(filter.End.Value.UtcDateTime);

            var endExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${endIndex}::date" : $"${endIndex}";
            predicate = $"{valueExpression} <= {endExpr}";
        }

        if (predicate is null)
        {
            return;
        }

        sql.Append(CultureInfo.InvariantCulture, $" AND {predicate}");
    }
}
