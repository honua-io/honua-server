// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    public async Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken)
    {
        if (query.ObjectIds.Length == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        var relatedLayerId = ResolveRelatedLayerId(query);
        var originForeignKeyField = ResolveOriginForeignKeyField(query);
        var destinationForeignKeyField = ResolveDestinationForeignKeyField(query);

        if (!FeatureQueryBuilder.IsValidFieldName(originForeignKeyField))
        {
            throw new ArgumentException($"Invalid relationship field: {originForeignKeyField}");
        }

        if (!FeatureQueryBuilder.IsValidFieldName(destinationForeignKeyField))
        {
            throw new ArgumentException($"Invalid relationship field: {destinationForeignKeyField}");
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        var foreignKeyValues = await GetOriginForeignKeyValuesAsync(
            connection,
            layerId,
            query,
            originForeignKeyField,
            cancellationToken).ConfigureAwait(false);
        if (foreignKeyValues.Count == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());

        var sql = new StringBuilder();
        sql.Append("SELECT objectid, ")
            .Append(geometrySelect)
            .Append(", attributes FROM ")
            .Append(_tableName)
            .Append(" WHERE layer_id = $1")
            .Append($" AND {DatabaseSchema.AttributesColumn}->> $2 = ANY($3)");

        var parameters = new List<object>
        {
            relatedLayerId,
            destinationForeignKeyField,
            foreignKeyValues.ToArray()
        };

        var paramIndex = 4;
        if (query.SqlFilter != null)
        {
            var sqlFragment = query.SqlFilter;
            var convertedSql = FeatureQueryBuilder.ConvertNamedParametersToPositional(sqlFragment.Sql, ref paramIndex);
            sql.Append(CultureInfo.InvariantCulture, $" AND ({convertedSql})");

            foreach (var param in sqlFragment.Parameters)
            {
                parameters.Add(param ?? DBNull.Value);
            }
        }
        else if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var parameterizedClause = FeatureQueryBuilder.ParseAndParameterizeWhereClause(query.Where.Trim(), ref paramIndex, parameters);
            sql.Append(CultureInfo.InvariantCulture, $" AND ({parameterizedClause})");
        }

        sql.Append(" ORDER BY objectid");

        if (query.Limit.HasValue && query.Limit.Value > 0)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
            parameters.Add(query.Limit.Value);
        }

        if (query.Offset.HasValue && query.Offset.Value > 0)
        {
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex++}");
            parameters.Add(query.Offset.Value);
        }

        await using var command = CreateSafeCommand(connection, sql.ToString());
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }
        ApplyCommandTimeout(command, _queryTimeoutSeconds);

        var features = new List<Feature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var feature = await ReadFeatureAsync(reader, cancellationToken);

            if (query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty)
            {
                feature = FilterFeatureFields(feature, query.OutFields.Value);
            }

            features.Add(feature);
        }

        return features.Count == 0
            ? QueryResult<Feature>.Empty()
            : QueryResult<Feature>.Create(features.Count, features.ToImmutableArray());
    }

    private async Task<List<string>> GetOriginForeignKeyValuesAsync(
        NpgsqlConnection connection,
        int layerId,
        RelatedQuery query,
        string originForeignKeyField,
        CancellationToken cancellationToken)
    {
        var sql = $@"
            SELECT DISTINCT {DatabaseSchema.AttributesColumn}->> $3 AS fk_value
            FROM {_tableName}
            WHERE layer_id = $1 AND objectid = ANY($2)
              AND {DatabaseSchema.AttributesColumn}->> $3 IS NOT NULL";

        await using var command = CreateSafeCommand(connection, sql);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(query.ObjectIds);
        command.Parameters.AddWithValue(originForeignKeyField);
        ApplyCommandTimeout(command, _queryTimeoutSeconds);

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                var value = reader.GetString(0);
                if (!string.IsNullOrEmpty(value))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static int ResolveRelatedLayerId(RelatedQuery query)
    {
        if (query.RelatedLayerId is int relatedLayerId)
        {
            return relatedLayerId;
        }

        throw new ArgumentException("Related layer id is required.");
    }

    private static string ResolveOriginForeignKeyField(RelatedQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.OriginForeignKeyField))
        {
            return query.OriginForeignKeyField;
        }

        throw new ArgumentException("Origin relationship field is required.");
    }

    private static string ResolveDestinationForeignKeyField(RelatedQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.DestinationForeignKeyField))
        {
            return query.DestinationForeignKeyField;
        }

        throw new ArgumentException("Destination relationship field is required.");
    }
}
