// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Core.Abstractions;
using Honua.Core.Domain.Features;
using Npgsql;

namespace Honua.Postgres.Features;

/// <summary>
/// PostgreSQL implementation of feature storage and retrieval
/// </summary>
/// <remarks>
/// Marked as internal to prevent exposure of database-specific implementations
/// outside the Infrastructure layer (Clean Architecture principle).
/// </remarks>
internal sealed class PostgresFeatureStore : IFeatureStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _tableName;

    public PostgresFeatureStore(NpgsqlDataSource dataSource, string? schemaName = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _tableName = string.IsNullOrEmpty(schemaName) ? "features" : $"{schemaName}.features";
    }

    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            SELECT objectid, geometry, attributes
            FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadFeature(reader);
    }

    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        // Build the count query first
        var countSql = BuildCountQuery(layerId, query);
        var totalCount = await ExecuteCountQuery(countSql, query, layerId, cancellationToken);

        if (totalCount == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        // Build the main query
        var selectSql = BuildSelectQuery(layerId, query);
        var features = await ExecuteSelectQuery(selectSql, query, layerId, cancellationToken);

        var hasMore = query.Offset.HasValue && query.Limit.HasValue &&
                      query.Offset.Value + query.Limit.Value < totalCount;

        return QueryResult<Feature>.Create(totalCount, features, hasMore);
    }

    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var sql = BuildCountQuery(layerId, query);
        return await ExecuteCountQuery(sql, query, layerId, cancellationToken);
    }

    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var sql = BuildExtentQuery(layerId, query ?? new FeatureQuery());

        await using var command = _dataSource.CreateCommand(sql);
        AddQueryParameters(command, query ?? new FeatureQuery(), layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
        {
            return null;
        }

        return FeatureExtent.Create(
            reader.GetDouble(0), // minx
            reader.GetDouble(1), // miny
            reader.GetDouble(2), // maxx
            reader.GetDouble(3), // maxy
            4326 // Assuming WGS84 for now - should be configurable
        );
    }

    public async Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            INSERT INTO {_tableName} (layer_id, geometry, attributes)
            VALUES ($1, $2, $3)
            RETURNING objectid, geometry, attributes";

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(feature.Geometry ?? (object)DBNull.Value);

        // Serialize to JSON string and pass as JSONB parameter (AOT-compatible)
        var attributesJson = AotJsonSerializer.SerializeAttributes(feature.Attributes);
        var attributesParam = new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb };
        command.Parameters.Add(attributesParam);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Failed to create feature: no result returned");
        }

        return ReadFeature(reader);
    }

    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            UPDATE {_tableName}
            SET geometry = $3, attributes = $4
            WHERE layer_id = $1 AND objectid = $2
            RETURNING objectid, geometry, attributes";

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(feature.Id);
        command.Parameters.AddWithValue(feature.Geometry ?? (object)DBNull.Value);

        // Serialize to JSON string and pass as JSONB parameter (AOT-compatible)
        var attributesJson = AotJsonSerializer.SerializeAttributes(feature.Attributes);
        var attributesParam = new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb };
        command.Parameters.Add(attributesParam);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Feature with ID {feature.Id} not found in layer {layerId}");
        }

        return ReadFeature(reader);
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            DELETE FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    public async Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
    {
        if (editBatch.IsEmpty)
        {
            return FeatureEditResult.Success(0, 0, 0);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var createdIds = new List<long>();
            var errors = new List<string>();

            // Process creates
            foreach (var feature in editBatch.Creates)
            {
                try
                {
                    var created = await CreateAsync(layerId, feature, cancellationToken);
                    createdIds.Add(created.Id);
                }
                catch (Exception ex)
                {
                    errors.Add($"Create failed: {ex.Message}");
                }
            }

            // Process updates
            var updatedCount = 0;
            foreach (var feature in editBatch.Updates)
            {
                try
                {
                    await UpdateAsync(layerId, feature, cancellationToken);
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Update failed for feature {feature.Id}: {ex.Message}");
                }
            }

            // Process deletes
            var deletedCount = 0;
            foreach (var featureId in editBatch.Deletes)
            {
                try
                {
                    if (await DeleteAsync(layerId, featureId, cancellationToken))
                    {
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Delete failed for feature {featureId}: {ex.Message}");
                }
            }

            await transaction.CommitAsync(cancellationToken);

            return FeatureEditResult.Success(
                createdIds.Count,
                updatedCount,
                deletedCount,
                createdIds.ToImmutableArray()
            );
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FeatureEditResult.Failure($"Transaction failed: {ex.Message}");
        }
    }

    private static Feature ReadFeature(NpgsqlDataReader reader)
    {
        var id = reader.GetInt64(0);
        var geometry = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        var attributesJson = reader.GetString(2);

        // Deserialize JSON using AOT-compatible deserializer
        var attributes = AotJsonSerializer.DeserializeAttributes(attributesJson);

        return Feature.Create(id, geometry, attributes);
    }


    private string BuildSelectQuery(int layerId, FeatureQuery query)
    {
        var sql = new StringBuilder($"SELECT objectid, geometry, attributes FROM {_tableName} WHERE layer_id = $1");
        var paramIndex = 2;

        AppendWhereClause(sql, query, ref paramIndex);
        AppendSpatialFilter(sql, query, ref paramIndex);

        if (query.Limit.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
        }

        if (query.Offset.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex}");
        }

        return sql.ToString();
    }

    private string BuildCountQuery(int layerId, FeatureQuery query)
    {
        var sql = new StringBuilder($"SELECT COUNT(*) FROM {_tableName} WHERE layer_id = $1");
        var paramIndex = 2;

        AppendWhereClause(sql, query, ref paramIndex);
        AppendSpatialFilter(sql, query, ref paramIndex);

        return sql.ToString();
    }

    private string BuildExtentQuery(int layerId, FeatureQuery query)
    {
        var sql = new StringBuilder($@"
            SELECT
                ST_XMin(extent), ST_YMin(extent), ST_XMax(extent), ST_YMax(extent)
            FROM (
                SELECT ST_Extent(geometry) as extent
                FROM {_tableName}
                WHERE layer_id = $1 AND geometry IS NOT NULL");

        var paramIndex = 2;
        AppendWhereClause(sql, query, ref paramIndex);
        AppendSpatialFilter(sql, query, ref paramIndex);

        sql.Append(") AS extent_query");
        return sql.ToString();
    }

    private static void AppendWhereClause(StringBuilder sql, FeatureQuery query, ref int paramIndex)
    {
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            // Basic SQL injection prevention - reject dangerous patterns
            var whereClause = query.Where.Trim();

            // Check for obvious SQL injection patterns
            var dangerousPatterns = new[]
            {
                ";", "--", "/*", "*/", "DROP", "DELETE", "INSERT", "UPDATE",
                "CREATE", "ALTER", "TRUNCATE", "EXEC", "EXECUTE"
            };

            foreach (var pattern in dangerousPatterns)
            {
                if (whereClause.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"WHERE clause contains potentially dangerous pattern: {pattern}");
                }
            }

            sql.Append(CultureInfo.InvariantCulture, $" AND ({whereClause})");
        }
    }

    private static void AppendSpatialFilter(StringBuilder sql, FeatureQuery query, ref int paramIndex)
    {
        if (query.SpatialFilter.HasValue)
        {
            var spatialFunction = query.SpatialFilter.Value.SpatialRelationship switch
            {
                SpatialRelationship.Intersects => "ST_Intersects",
                SpatialRelationship.Within => "ST_Within",
                SpatialRelationship.Contains => "ST_Contains",
                SpatialRelationship.EnvelopeIntersects => "ST_Intersects",
                _ => "ST_Intersects"
            };

            sql.Append(CultureInfo.InvariantCulture, $" AND {spatialFunction}(geometry, ST_GeomFromWKB(${paramIndex++}))");
        }
    }

    private void AddQueryParameters(NpgsqlCommand command, FeatureQuery query, int layerId)
    {
        // Layer ID is always first parameter
        command.Parameters.AddWithValue(layerId);

        // Add spatial filter parameter if present
        if (query.SpatialFilter.HasValue)
        {
            command.Parameters.AddWithValue(query.SpatialFilter.Value.Geometry);
        }

        // Add pagination parameters
        if (query.Limit.HasValue)
        {
            command.Parameters.AddWithValue(query.Limit.Value);
        }

        if (query.Offset.HasValue)
        {
            command.Parameters.AddWithValue(query.Offset.Value);
        }
    }

    private async Task<long> ExecuteCountQuery(string sql, FeatureQuery query, int layerId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        AddQueryParameters(command, query, layerId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<ImmutableArray<Feature>> ExecuteSelectQuery(string sql, FeatureQuery query, int layerId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        AddQueryParameters(command, query, layerId);

        var features = new List<Feature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            features.Add(ReadFeature(reader));
        }

        return features.ToImmutableArray();
    }
}
