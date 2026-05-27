// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    public CoreParameterizedQuery BuildStatisticsQuery(
        int layerId,
        FeatureQuery query,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        if (!query.OutStatistics.HasValue || query.OutStatistics.Value.IsDefaultOrEmpty)
        {
            throw new ArgumentException("OutStatistics must be specified for a statistics query.", nameof(query));
        }

        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var statistics = query.OutStatistics.Value;
            var groupByFields = query.GroupByFields;

            sql.Append("SELECT ");
            BuildStatisticsSelectClause(sql, statistics, groupByFields);
            sql.Append(CultureInfo.InvariantCulture, $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            if (groupByFields.HasValue && !groupByFields.Value.IsDefaultOrEmpty)
            {
                AppendGroupByClause(sql, groupByFields.Value);
            }

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private static void BuildStatisticsSelectClause(
        StringBuilder sql,
        ImmutableArray<StatisticDefinition> statistics,
        ImmutableArray<string>? groupByFields)
    {
        var first = true;

        // Add group-by fields first so they appear in the result
        if (groupByFields.HasValue && !groupByFields.Value.IsDefaultOrEmpty)
        {
            foreach (var field in groupByFields.Value)
            {
                if (!IsValidFieldName(field))
                {
                    throw new ArgumentException($"Invalid group-by field name: {field}");
                }

                if (!first)
                {
                    sql.Append(", ");
                }

                var fieldExpr = GetFieldExpression(field);
                sql.Append(CultureInfo.InvariantCulture, $"{fieldExpr} AS {SanitizeAlias(field)}");
                first = false;
            }
        }

        // Add aggregate expressions
        foreach (var stat in statistics)
        {
            if (!IsValidFieldName(stat.OnStatisticField))
            {
                throw new ArgumentException($"Invalid statistic field name: {stat.OnStatisticField}");
            }

            if (!IsValidFieldName(stat.OutStatisticFieldName))
            {
                throw new ArgumentException($"Invalid output statistic field name: {stat.OutStatisticFieldName}");
            }

            if (!first)
            {
                sql.Append(", ");
            }

            var fieldExpr = GetFieldExpression(stat.OnStatisticField);
            var aggregateExpr = BuildAggregateExpression(stat.StatisticType, fieldExpr, stat.FieldType);
            sql.Append(CultureInfo.InvariantCulture, $"{aggregateExpr} AS {SanitizeAlias(stat.OutStatisticFieldName)}");
            first = false;
        }
    }

    private static string BuildAggregateExpression(
        StatisticType statisticType,
        string fieldExpr,
        MetadataV2FieldType? fieldType = null)
    {
        var numericExpr = $"({fieldExpr})::numeric";
        var orderedExpr = IsNumericFieldType(fieldType) ? numericExpr : fieldExpr;
        return statisticType switch
        {
            StatisticType.Count => $"COUNT({fieldExpr})",
            StatisticType.Sum => $"SUM({numericExpr})",
            StatisticType.Min => $"MIN({orderedExpr})",
            StatisticType.Max => $"MAX({orderedExpr})",
            StatisticType.Avg => $"AVG({numericExpr})",
            StatisticType.Stddev => $"STDDEV_SAMP({numericExpr})",
            StatisticType.Var => $"VAR_SAMP({numericExpr})",
            _ => throw new ArgumentOutOfRangeException(nameof(statisticType), statisticType, "Unsupported statistic type")
        };
    }

    private static bool IsNumericFieldType(MetadataV2FieldType? fieldType)
    {
        return fieldType is MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Float
            or MetadataV2FieldType.Double;
    }

    private static string GetFieldExpression(string fieldName)
    {
        return string.Equals(fieldName, DatabaseSchema.ObjectIdColumn, StringComparison.OrdinalIgnoreCase)
            ? DatabaseSchema.ObjectIdColumn
            : DatabaseSchema.BuildJsonPath(fieldName);
    }

    private static void AppendGroupByClause(StringBuilder sql, ImmutableArray<string> groupByFields)
    {
        sql.Append(" GROUP BY ");
        for (var i = 0; i < groupByFields.Length; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append(GetFieldExpression(groupByFields[i]));
        }
    }

    private static string SanitizeAlias(string alias)
    {
        // Use double-quoting for safe SQL identifiers
        var escaped = alias.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
