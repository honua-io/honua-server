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

        // Map each distinct origin foreign-key value back to the origin object id(s)
        // that carry it. The foreign-key value is the origin's OriginForeignKeyField,
        // which is NOT necessarily its object id, so grouping by the destination key
        // value alone would only resolve records when the foreign key happens to be
        // the object-id column. Stamping each related row with its origin object id
        // lets callers group correctly for any origin object id magnitude.
        var originObjectIdsByForeignKey = await GetOriginForeignKeyValuesAsync(
            connection,
            layerId,
            query,
            originForeignKeyField,
            cancellationToken).ConfigureAwait(false);
        if (originObjectIdsByForeignKey.Count == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        var foreignKeyValues = originObjectIdsByForeignKey.Keys.ToList();

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

            // Resolve the origin object id(s) this related row belongs to BEFORE field
            // filtering so the destination key is still available, then re-stamp the
            // (possibly filtered) feature so grouping can bucket by origin object id.
            long[]? originObjectIds = null;
            if (feature.Attributes.TryGetValue(destinationForeignKeyField, out var destinationKeyValue) &&
                TryNormalizeForeignKeyValue(destinationKeyValue, out var normalizedKey) &&
                originObjectIdsByForeignKey.TryGetValue(normalizedKey, out var matchedOriginIds))
            {
                originObjectIds = matchedOriginIds.ToArray();
            }

            if (query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty)
            {
                feature = FilterFeatureFields(feature, query.OutFields.Value);
            }

            if (originObjectIds is { Length: > 0 })
            {
                feature = feature with
                {
                    Attributes = feature.Attributes.SetItem(
                        RelatedQuery.OriginObjectIdsAttribute,
                        originObjectIds)
                };
            }

            features.Add(feature);
        }

        return features.Count == 0
            ? QueryResult<Feature>.Empty()
            : QueryResult<Feature>.Create(features.Count, features.ToImmutableArray());
    }

    private async Task<Dictionary<string, List<long>>> GetOriginForeignKeyValuesAsync(
        NpgsqlConnection connection,
        int layerId,
        RelatedQuery query,
        string originForeignKeyField,
        CancellationToken cancellationToken)
    {
        // Return the foreign-key value alongside the origin object id so callers can
        // map related rows back to their origin object id. Do NOT collapse with
        // DISTINCT — distinct object ids can share a foreign-key value (and the same
        // related rows then belong to each of them).
        //
        // When the origin foreign key IS the object-id column, source the value from the
        // objectid column itself rather than the attributes JSON. The objectid is the
        // canonical primary key column and is injected into a feature's attributes only
        // at read time; rows added via the edit API (which assigns the objectid) do not
        // carry it in the attributes JSON, so attributes->>'objectid' would be NULL for
        // them and their related records would not resolve. Reading the column keeps
        // object-id-keyed relationships working for any origin object id, including the
        // high auto-assigned ids produced by addFeatures.
        var originKeyIsObjectId =
            originForeignKeyField.Equals(DatabaseSchema.ObjectIdColumn, StringComparison.OrdinalIgnoreCase) ||
            originForeignKeyField.Equals(DatabaseSchema.ObjectIdColumnAlt, StringComparison.OrdinalIgnoreCase);

        var fkValueExpression = originKeyIsObjectId
            ? "objectid::text"
            : $"{DatabaseSchema.AttributesColumn}->> $3";

        var sql = $@"
            SELECT objectid, {fkValueExpression} AS fk_value
            FROM {_tableName}
            WHERE layer_id = $1 AND objectid = ANY($2)
              AND {fkValueExpression} IS NOT NULL";

        await using var command = CreateSafeCommand(connection, sql);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(query.ObjectIds);
        if (!originKeyIsObjectId)
        {
            command.Parameters.AddWithValue(originForeignKeyField);
        }

        ApplyCommandTimeout(command, _queryTimeoutSeconds);

        var map = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1))
            {
                continue;
            }

            var value = reader.GetString(1);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var objectId = reader.GetInt64(0);
            if (!map.TryGetValue(value, out var objectIds))
            {
                objectIds = [];
                map[value] = objectIds;
            }

            objectIds.Add(objectId);
        }

        return map;
    }

    /// <summary>
    /// Normalizes a related row's destination foreign-key attribute value into the
    /// same textual form Postgres produces for the origin via <c>attributes->></c>,
    /// so origin and destination keys join regardless of JSON numeric/string typing.
    /// </summary>
    private static bool TryNormalizeForeignKeyValue(object? value, out string normalized)
    {
        switch (value)
        {
            case null:
                normalized = string.Empty;
                return false;
            case string s:
                normalized = s;
                return !string.IsNullOrEmpty(s);
            case long l:
                normalized = l.ToString(CultureInfo.InvariantCulture);
                return true;
            case int i:
                normalized = i.ToString(CultureInfo.InvariantCulture);
                return true;
            case short sh:
                normalized = sh.ToString(CultureInfo.InvariantCulture);
                return true;
            case byte b:
                normalized = b.ToString(CultureInfo.InvariantCulture);
                return true;
            case IFormattable formattable:
                normalized = formattable.ToString(null, CultureInfo.InvariantCulture);
                return !string.IsNullOrEmpty(normalized);
            default:
                normalized = value.ToString() ?? string.Empty;
                return !string.IsNullOrEmpty(normalized);
        }
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
