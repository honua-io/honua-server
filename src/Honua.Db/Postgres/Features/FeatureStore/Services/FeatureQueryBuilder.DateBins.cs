// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Infrastructure;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

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
            var timestampExpr = BuildEpochAwareTemporalExpression(fieldExpr, MetadataV2FieldType.DateTime);

            var boundary = DateBinBoundarySql.Build(dateBin, timestampExpr, origin =>
            {
                parameters.Add(origin);
                return $"${paramIndex++}";
            });
            sql.Append(CultureInfo.InvariantCulture, $"SELECT {boundary} AS \"boundary\"");

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
}
