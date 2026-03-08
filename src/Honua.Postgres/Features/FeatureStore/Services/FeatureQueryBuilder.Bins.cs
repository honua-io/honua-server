// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    public CoreParameterizedQuery BuildBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition binDefinition,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        if (!IsValidFieldName(binDefinition.Field))
        {
            throw new ArgumentException($"Invalid bin field name: {binDefinition.Field}", nameof(binDefinition));
        }

        return binDefinition.Type switch
        {
            BinType.FixedInterval => BuildFixedIntervalBinsQuery(layerId, query, binDefinition, geometryStorageType),
            BinType.AutoInterval => BuildAutoIntervalBinsQuery(layerId, query, binDefinition, geometryStorageType),
            BinType.FixedBoundaries => BuildFixedBoundariesBinsQuery(layerId, query, binDefinition, geometryStorageType),
            BinType.Date => BuildDateTypeBinsQuery(layerId, query, binDefinition, geometryStorageType),
            BinType.Classification => BuildClassificationBinsQuery(layerId, query, binDefinition, geometryStorageType),
            _ => throw new ArgumentOutOfRangeException(nameof(binDefinition), binDefinition.Type, "Unsupported bin type")
        };
    }

    private CoreParameterizedQuery BuildFixedIntervalBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition bin,
        CoreGeometryStorageType geometryStorageType)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var fieldExpr = GetFieldExpression(bin.Field);
            var numericExpr = $"({fieldExpr})::numeric";

            // Use width_bucket for fixed intervals
            var start = bin.IntervalStart ?? 0;
            var end = bin.IntervalEnd ?? 0;
            var intervalSize = bin.IntervalSize ?? 1;
            var numBuckets = (int)Math.Ceiling((end - start) / intervalSize);
            if (numBuckets <= 0)
            {
                numBuckets = 1;
            }

            sql.Append("SELECT ");
            sql.Append(CultureInfo.InvariantCulture,
                $"({start} + (width_bucket({numericExpr}, {start}, {end}, {numBuckets}) - 1) * {intervalSize})::numeric AS \"lowerBoundary\"");
            sql.Append(CultureInfo.InvariantCulture,
                $", ({start} + width_bucket({numericExpr}, {start}, {end}, {numBuckets}) * {intervalSize})::numeric AS \"upperBoundary\"");

            AppendBinStatisticsColumns(sql, bin);

            sql.Append(CultureInfo.InvariantCulture,
                $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            sql.Append(CultureInfo.InvariantCulture,
                $" GROUP BY width_bucket({numericExpr}, {start}, {end}, {numBuckets})");
            sql.Append(CultureInfo.InvariantCulture,
                $" ORDER BY width_bucket({numericExpr}, {start}, {end}, {numBuckets})");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private CoreParameterizedQuery BuildAutoIntervalBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition bin,
        CoreGeometryStorageType geometryStorageType)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var fieldExpr = GetFieldExpression(bin.Field);
            var numericExpr = $"({fieldExpr})::numeric";
            var numBins = bin.NumBins ?? 10;

            // CTE to compute min/max, then width_bucket
            sql.Append("WITH range AS (");
            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT MIN({numericExpr}) AS min_val, MAX({numericExpr}) AS max_val");
            sql.Append(CultureInfo.InvariantCulture,
                $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");

            var cteParamIndex = paramIndex;
            var cteParameters = new List<object>();
            AppendWhereClause(sql, query, ref cteParamIndex, cteParameters);

            sql.Append(") SELECT ");
            sql.Append(CultureInfo.InvariantCulture,
                $"(range.min_val + (width_bucket({numericExpr}, range.min_val, range.max_val + 0.0001, {numBins}) - 1) * ((range.max_val - range.min_val) / {numBins}))::numeric AS \"lowerBoundary\"");
            sql.Append(CultureInfo.InvariantCulture,
                $", (range.min_val + width_bucket({numericExpr}, range.min_val, range.max_val + 0.0001, {numBins}) * ((range.max_val - range.min_val) / {numBins}))::numeric AS \"upperBoundary\"");

            AppendBinStatisticsColumns(sql, bin);

            sql.Append(CultureInfo.InvariantCulture,
                $" FROM {_tableName}, range WHERE {DatabaseSchema.LayerIdColumn} = $1");

            // Reuse CTE parameters
            parameters.AddRange(cteParameters);
            paramIndex = cteParamIndex;
            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            sql.Append(CultureInfo.InvariantCulture,
                $" GROUP BY width_bucket({numericExpr}, range.min_val, range.max_val + 0.0001, {numBins}), range.min_val, range.max_val");
            sql.Append(CultureInfo.InvariantCulture,
                $" ORDER BY width_bucket({numericExpr}, range.min_val, range.max_val + 0.0001, {numBins})");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private CoreParameterizedQuery BuildFixedBoundariesBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition bin,
        CoreGeometryStorageType geometryStorageType)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var fieldExpr = GetFieldExpression(bin.Field);
            var numericExpr = $"({fieldExpr})::numeric";
            var boundaries = bin.Boundaries!.Value;

            // Build CASE expression for bin assignment
            sql.Append("SELECT CASE ");
            for (var i = 0; i < boundaries.Length - 1; i++)
            {
                var lower = boundaries[i].ToString(CultureInfo.InvariantCulture);
                var upper = boundaries[i + 1].ToString(CultureInfo.InvariantCulture);
                sql.Append(CultureInfo.InvariantCulture,
                    $"WHEN {numericExpr} >= {lower} AND {numericExpr} < {upper} THEN {i} ");
            }

            // Last bucket includes upper bound
            var lastLower = boundaries[^2].ToString(CultureInfo.InvariantCulture);
            var lastUpper = boundaries[^1].ToString(CultureInfo.InvariantCulture);
            sql.Append(CultureInfo.InvariantCulture,
                $"WHEN {numericExpr} >= {lastLower} AND {numericExpr} <= {lastUpper} THEN {boundaries.Length - 2} ");
            sql.Append("END AS bin_idx");

            // Add boundary columns
            sql.Append(", CASE ");
            for (var i = 0; i < boundaries.Length - 1; i++)
            {
                var lower = boundaries[i].ToString(CultureInfo.InvariantCulture);
                sql.Append(CultureInfo.InvariantCulture,
                    $"WHEN {numericExpr} >= {lower} AND {numericExpr} {(i == boundaries.Length - 2 ? "<=" : "<")} {boundaries[i + 1].ToString(CultureInfo.InvariantCulture)} THEN {lower}::numeric ");
            }

            sql.Append("END AS \"lowerBoundary\"");

            sql.Append(", CASE ");
            for (var i = 0; i < boundaries.Length - 1; i++)
            {
                var lower = boundaries[i].ToString(CultureInfo.InvariantCulture);
                var upper = boundaries[i + 1].ToString(CultureInfo.InvariantCulture);
                sql.Append(CultureInfo.InvariantCulture,
                    $"WHEN {numericExpr} >= {lower} AND {numericExpr} {(i == boundaries.Length - 2 ? "<=" : "<")} {upper} THEN {upper}::numeric ");
            }

            sql.Append("END AS \"upperBoundary\"");

            AppendBinStatisticsColumns(sql, bin);

            sql.Append(CultureInfo.InvariantCulture,
                $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            sql.Append(" GROUP BY bin_idx, \"lowerBoundary\", \"upperBoundary\"");
            sql.Append(" HAVING CASE ");
            for (var i = 0; i < boundaries.Length - 1; i++)
            {
                var lower = boundaries[i].ToString(CultureInfo.InvariantCulture);
                var upper = boundaries[i + 1].ToString(CultureInfo.InvariantCulture);
                sql.Append(CultureInfo.InvariantCulture,
                    $"WHEN {numericExpr} >= {lower} AND {numericExpr} {(i == boundaries.Length - 2 ? "<=" : "<")} {upper} THEN {i} ");
            }

            sql.Append("END IS NOT NULL");
            sql.Append(" ORDER BY bin_idx");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private CoreParameterizedQuery BuildDateTypeBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition bin,
        CoreGeometryStorageType geometryStorageType)
    {
        if (!bin.DateBin.HasValue)
        {
            throw new ArgumentException("DateBin must be specified for date-type binning.", nameof(bin));
        }

        return BuildDateBinsQuery(layerId, query, bin.DateBin.Value, geometryStorageType);
    }

    private CoreParameterizedQuery BuildClassificationBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition bin,
        CoreGeometryStorageType geometryStorageType)
    {
        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var fieldExpr = GetFieldExpression(bin.Field);

            sql.Append(CultureInfo.InvariantCulture,
                $"SELECT {fieldExpr} AS {SanitizeAlias(bin.Field)}");

            AppendBinStatisticsColumns(sql, bin);

            sql.Append(CultureInfo.InvariantCulture,
                $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");

            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            sql.Append(CultureInfo.InvariantCulture, $" GROUP BY {fieldExpr}");
            sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {fieldExpr}");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private static void AppendBinStatisticsColumns(StringBuilder sql, BinDefinition bin)
    {
        if (bin.OutStatistics.HasValue && !bin.OutStatistics.Value.IsDefaultOrEmpty)
        {
            foreach (var stat in bin.OutStatistics.Value)
            {
                if (!IsValidFieldName(stat.OnStatisticField))
                {
                    throw new ArgumentException($"Invalid statistic field name: {stat.OnStatisticField}");
                }

                var statFieldExpr = GetFieldExpression(stat.OnStatisticField);
                var aggregateExpr = BuildAggregateExpression(stat.StatisticType, statFieldExpr);
                sql.Append(CultureInfo.InvariantCulture,
                    $", {aggregateExpr} AS {SanitizeAlias(stat.OutStatisticFieldName)}");
            }
        }
        else
        {
            sql.Append(CultureInfo.InvariantCulture,
                $", COUNT({DatabaseSchema.ObjectIdColumn}) AS \"count\"");
        }
    }
}
