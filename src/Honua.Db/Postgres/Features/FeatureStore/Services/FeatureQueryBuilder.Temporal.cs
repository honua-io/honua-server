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
        var startFieldName = filter.PropertyName;
        if (!IsValidFieldName(startFieldName))
        {
            throw new ArgumentException($"Invalid temporal field name: {startFieldName}", nameof(query));
        }

        var startAttributeValue = BuildAttributeValueExpression(startFieldName, ref paramIndex, parameters);
        var startValueExpression = WrapTemporalValueExpression(startAttributeValue, filter.PropertyType);

        // Interval-intersection mode mirrors GeoServices query?time= semantics:
        // the row's interval is [start, COALESCE(end, start)] and matches when
        // it overlaps the requested [filter.Start, filter.End] window. Render
        // and tile paths set EndPropertyName when the layer's configured end
        // field resolves so WMS/WMTS/MVT and GeoServices share filter results.
        string? endValueExpression = null;
        if (!string.IsNullOrWhiteSpace(filter.EndPropertyName)
            && !filter.EndPropertyName.Equals(startFieldName, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsValidFieldName(filter.EndPropertyName))
            {
                throw new ArgumentException($"Invalid temporal field name: {filter.EndPropertyName}", nameof(query));
            }

            var endAttributeValue = BuildAttributeValueExpression(filter.EndPropertyName, ref paramIndex, parameters);
            var endRaw = WrapTemporalValueExpression(endAttributeValue, filter.PropertyType);
            endValueExpression = $"COALESCE({endRaw}, {startValueExpression})";
        }

        string? predicate = null;

        if (filter.Start.HasValue && filter.End.HasValue)
        {
            var startIndex = paramIndex++;
            var endIndex = paramIndex++;
            parameters.Add(filter.Start.Value.UtcDateTime);
            parameters.Add(filter.End.Value.UtcDateTime);

            var startExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${startIndex}::date" : $"${startIndex}";
            var endExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${endIndex}::date" : $"${endIndex}";
            var rowEnd = endValueExpression ?? startValueExpression;
            predicate = $"{rowEnd} >= {startExpr} AND {startValueExpression} <= {endExpr}";
        }
        else if (filter.Start.HasValue)
        {
            var startIndex = paramIndex++;
            parameters.Add(filter.Start.Value.UtcDateTime);

            var startExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${startIndex}::date" : $"${startIndex}";
            var rowEnd = endValueExpression ?? startValueExpression;
            predicate = $"{rowEnd} >= {startExpr}";
        }
        else if (filter.End.HasValue)
        {
            var endIndex = paramIndex++;
            parameters.Add(filter.End.Value.UtcDateTime);

            var endExpr = filter.PropertyType == TemporalPropertyType.Date ? $"${endIndex}::date" : $"${endIndex}";
            predicate = $"{startValueExpression} <= {endExpr}";
        }

        if (predicate is null)
        {
            return;
        }

        sql.Append(CultureInfo.InvariantCulture, $" AND {predicate}");
    }

    private static string WrapTemporalValueExpression(string attributeValue, TemporalPropertyType propertyType)
        => propertyType switch
        {
            TemporalPropertyType.Date => $"NULLIF({attributeValue}, '')::date",
            _ => $"NULLIF({attributeValue}, '')::timestamptz"
        };
}
