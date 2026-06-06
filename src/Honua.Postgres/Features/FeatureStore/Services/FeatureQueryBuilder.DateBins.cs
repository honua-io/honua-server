// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    public CoreParameterizedQuery BuildDateBinsQuery(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        CoreGeometryStorageType geometryStorageType = CoreGeometryStorageType.Geometry)
    {
        GuardVersionedReadSupported(query, "date-bins");
        if (!IsValidFieldName(dateBin.BinField))
        {
            throw new ArgumentException($"Invalid bin field name: {dateBin.BinField}", nameof(dateBin));
        }

        var sql = _stringBuilderPool.Get();
        try
        {
            var paramIndex = 2;
            var parameters = new List<object>();
            var fieldExpr = GetFieldExpression(dateBin.BinField);
            var timestampExpr = $"({fieldExpr})::timestamptz";

            sql.Append("SELECT ");

            // Build the boundary expression based on bin type
            if (dateBin.IsCalendarBin)
            {
                var truncUnit = MapCalendarUnitToPostgres(dateBin.CalendarUnit!);
                sql.Append(CultureInfo.InvariantCulture,
                    $"date_trunc('{truncUnit}', {timestampExpr}) AS \"boundary\"");
            }
            else if (dateBin.FixedInterval.HasValue)
            {
                var intervalStr = FormatInterval(dateBin.FixedInterval.Value);
                if (dateBin.FixedIntervalOrigin.HasValue)
                {
                    sql.Append(CultureInfo.InvariantCulture,
                        $"date_bin('{intervalStr}'::interval, {timestampExpr}, ${paramIndex}::timestamptz) AS \"boundary\"");
                    parameters.Add(dateBin.FixedIntervalOrigin.Value);
                    paramIndex++;
                }
                else
                {
                    sql.Append(CultureInfo.InvariantCulture,
                        $"date_bin('{intervalStr}'::interval, {timestampExpr}, '1970-01-01T00:00:00Z'::timestamptz) AS \"boundary\"");
                }
            }
            else
            {
                throw new ArgumentException("DateBinDefinition must specify either CalendarUnit or FixedInterval.", nameof(dateBin));
            }

            // Add optional statistics columns
            if (dateBin.OutStatistics.HasValue && !dateBin.OutStatistics.Value.IsDefaultOrEmpty)
            {
                foreach (var stat in dateBin.OutStatistics.Value)
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
                // Default: count per bin
                sql.Append(CultureInfo.InvariantCulture,
                    $", COUNT({DatabaseSchema.ObjectIdColumn}) AS \"count\"");
            }

            // FROM clause
            sql.Append(CultureInfo.InvariantCulture,
                $" FROM {_tableName} WHERE {DatabaseSchema.LayerIdColumn} = $1");

            // Append filters
            AppendWhereClause(sql, query, ref paramIndex, parameters);
            AppendTemporalFilter(sql, query, ref paramIndex, parameters);
            AppendSpatialFilter(sql, query, geometryStorageType, ref paramIndex, parameters);

            // GROUP BY and ORDER BY
            sql.Append(" GROUP BY \"boundary\" ORDER BY \"boundary\"");

            return new CoreParameterizedQuery(sql.ToString(), parameters);
        }
        finally
        {
            _stringBuilderPool.Return(sql);
        }
    }

    private static string MapCalendarUnitToPostgres(string unit)
    {
        return unit.ToLowerInvariant() switch
        {
            "year" => "year",
            "quarter" => "quarter",
            "month" => "month",
            "week" => "week",
            "day" => "day",
            "hour" => "hour",
            "minute" => "minute",
            "second" => "second",
            _ => "month" // Default to month
        };
    }

    private static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalDays >= 1 && interval.TotalDays == Math.Floor(interval.TotalDays))
        {
            return FormattableString.Invariant($"{(int)interval.TotalDays} days");
        }

        if (interval.TotalHours >= 1 && interval.TotalHours == Math.Floor(interval.TotalHours))
        {
            return FormattableString.Invariant($"{(int)interval.TotalHours} hours");
        }

        if (interval.TotalMinutes >= 1 && interval.TotalMinutes == Math.Floor(interval.TotalMinutes))
        {
            return FormattableString.Invariant($"{(int)interval.TotalMinutes} minutes");
        }

        return FormattableString.Invariant($"{(int)interval.TotalSeconds} seconds");
    }
}
