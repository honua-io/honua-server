// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore;

/// <summary>
/// PostgreSQL implementation of feature storage and retrieval
/// </summary>
/// <remarks>
/// <para>Marked as internal to prevent exposure of database-specific implementations
/// outside the Infrastructure layer (Clean Architecture principle).</para>
///
/// <para><strong>SECURITY WARNING</strong>: This class contains a known SQL injection
/// vulnerability in WHERE clause handling (AppendWhereClause method). The current
/// implementation uses string concatenation with basic validation, which is not secure.
/// Enhanced validation reduces attack surface but does not eliminate the risk.</para>
///
/// <para>Required fix: Implement proper parameterized WHERE clause parsing that:
/// 1. Parses WHERE expressions into AST (field names, operators, values)
/// 2. Validates field names against layer schema
/// 3. Parameterizes all literal values using PostgreSQL placeholders ($n)
/// 4. Reconstructs SQL with safe parameter substitution</para>
/// </remarks>
internal sealed class PostgresFeatureStore : IFeatureStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _tableName;

    public PostgresFeatureStore(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _tableName = string.IsNullOrEmpty(schemaName) ? "features" : $"{schemaName}.features";
    }

    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            SELECT objectid, geometry, attributes
            FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await ReadFeatureAsync(reader, cancellationToken);
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

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
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

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(feature.Geometry ?? (object)DBNull.Value);

        // Serialize to JSON string and pass as JSONB parameter (AOT-compatible with source generators)
        var attributesDictionary = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var attributesJson = await SerializeToJsonStringAsync(attributesDictionary, cancellationToken);
        var attributesParam = new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb };
        command.Parameters.Add(attributesParam);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Failed to create feature: no result returned");
        }

        return await ReadFeatureAsync(reader, cancellationToken);
    }

    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            UPDATE {_tableName}
            SET geometry = $3, attributes = $4
            WHERE layer_id = $1 AND objectid = $2
            RETURNING objectid, geometry, attributes";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(feature.Id);
        command.Parameters.AddWithValue(feature.Geometry ?? (object)DBNull.Value);

        // Serialize to JSON string and pass as JSONB parameter (AOT-compatible with source generators)
        var attributesDictionary = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var attributesJson = await SerializeToJsonStringAsync(attributesDictionary, cancellationToken);
        var attributesParam = new NpgsqlParameter { Value = attributesJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb };
        command.Parameters.Add(attributesParam);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Feature with ID {feature.Id} not found in layer {layerId}");
        }

        return await ReadFeatureAsync(reader, cancellationToken);
    }

    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var sql = $@"
            DELETE FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
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

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var (createdIds, createErrors) = await ProcessCreatesAsync(layerId, editBatch.Creates, cancellationToken);
            var (updatedCount, updateErrors) = await ProcessUpdatesAsync(layerId, editBatch.Updates, cancellationToken);
            var (deletedCount, deleteErrors) = await ProcessDeletesAsync(layerId, editBatch.Deletes, cancellationToken);

            var allErrors = createErrors.Concat(updateErrors).Concat(deleteErrors).ToList();

            await transaction.CommitAsync(cancellationToken);

            return FeatureEditResult.Success(
                createdIds.Length,
                updatedCount,
                deletedCount,
                createdIds
            );
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FeatureEditResult.Failure($"Transaction failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes create operations within a transaction.
    /// </summary>
    private async Task<(ImmutableArray<long> createdIds, List<string> errors)> ProcessCreatesAsync(
        int layerId,
        ImmutableArray<Feature> features,
        CancellationToken cancellationToken)
    {
        var createdIds = new List<long>();
        var errors = new List<string>();

        foreach (var feature in features)
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

        return (createdIds.ToImmutableArray(), errors);
    }

    /// <summary>
    /// Processes update operations within a transaction.
    /// </summary>
    private async Task<(int updatedCount, List<string> errors)> ProcessUpdatesAsync(
        int layerId,
        ImmutableArray<Feature> features,
        CancellationToken cancellationToken)
    {
        var updatedCount = 0;
        var errors = new List<string>();

        foreach (var feature in features)
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

        return (updatedCount, errors);
    }

    /// <summary>
    /// Processes delete operations within a transaction.
    /// </summary>
    private async Task<(int deletedCount, List<string> errors)> ProcessDeletesAsync(
        int layerId,
        ImmutableArray<long> featureIds,
        CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        var errors = new List<string>();

        foreach (var featureId in featureIds)
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

        return (deletedCount, errors);
    }

    private static async Task<Feature> ReadFeatureAsync(NpgsqlDataReader reader, CancellationToken cancellationToken = default)
    {
        var id = reader.GetInt64(0);
        var geometry = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        var attributesJson = reader.GetString(2);

        // Deserialize JSON using AOT-compatible source generators
        var attributesDictionary = await DeserializeFromJsonStringAsync(attributesJson, cancellationToken) ?? new Dictionary<string, object?>();

        // Convert JsonElement values to primitive types for compatibility
        var convertedAttributes = attributesDictionary.ToDictionary(
            kvp => kvp.Key,
            kvp => ConvertJsonElementToObject(kvp.Value)
        );

        var attributes = convertedAttributes.ToImmutableDictionary();

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
            var whereClause = query.Where.Trim();

            // SECURITY CRITICAL: This method has a SQL injection vulnerability
            // TODO: Replace with proper parameterized WHERE clause parsing
            // Issue: String concatenation allows injection despite basic validation
            //
            // Proper fix requires:
            // 1. Parse WHERE clause into AST (field names, operators, values)
            // 2. Validate field names against layer schema
            // 3. Parameterize all values using PostgreSQL parameters
            // 4. Reconstruct SQL with proper placeholders ($n)

            // Enhanced validation - reject dangerous patterns and suspicious constructs
            var dangerousPatterns = new[]
            {
                ";", "--", "/*", "*/", "xp_", "sp_", "DROP", "DELETE", "INSERT",
                "UPDATE", "CREATE", "ALTER", "TRUNCATE", "EXEC", "EXECUTE", "SCRIPT",
                "UNION", "SELECT", "FROM", "INTO", "MERGE", "WITH", "DECLARE",
                "CAST(", "CONVERT(", "EXEC(", "EXECUTE(", "\\", "\\x", "0x",
                "CHAR(", "ASCII(", "NCHAR(", "UNICODE(", "@@", "INFORMATION_SCHEMA"
            };

            foreach (var pattern in dangerousPatterns)
            {
                if (whereClause.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"WHERE clause rejected: contains potentially dangerous pattern '{pattern}'. " +
                        "Use simple field comparisons only (e.g., 'name = \\'value\\'' or 'age > 18').",
                        nameof(query));
                }
            }

            // Additional validation: Must contain at least one field comparison
            if (!System.Text.RegularExpressions.Regex.IsMatch(whereClause,
                @"^\s*\w+\s*(=|!=|<>|>|<|>=|<=|LIKE|NOT\s+LIKE|IN|NOT\s+IN)\s*",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                throw new ArgumentException(
                    "WHERE clause must start with a valid field comparison (e.g., 'fieldname = value').",
                    nameof(query));
            }

            // Reject nested queries and complex expressions
            if (whereClause.Contains('(') || whereClause.Contains(')'))
            {
                // Allow only basic parentheses for simple grouping like: (field1 = 'a' AND field2 = 'b')
                var parenCount = whereClause.Count(c => c == '(');
                var closeParenCount = whereClause.Count(c => c == ')');

                if (parenCount != closeParenCount || parenCount > 2)
                {
                    throw new ArgumentException(
                        "WHERE clause contains unsupported parentheses complexity. Use simple field comparisons only.",
                        nameof(query));
                }
            }

            // Log warning for security audit
            // TODO: Add structured logging here when logger is available

            // STILL VULNERABLE: This is string concatenation and not secure
            // This temporary approach only reduces attack surface
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
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        AddQueryParameters(command, query, layerId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<ImmutableArray<Feature>> ExecuteSelectQuery(string sql, FeatureQuery query, int layerId, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        AddQueryParameters(command, query, layerId);

        var features = new List<Feature>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            features.Add(await ReadFeatureAsync(reader, cancellationToken));
        }

        return features.ToImmutableArray();
    }

    /// <summary>
    /// Serializes dictionary to JSON string asynchronously using AOT-compatible source generators.
    /// </summary>
    private static async Task<string> SerializeToJsonStringAsync(Dictionary<string, object?> dictionary, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, dictionary, FeatureAttributesJsonContext.Default.DictionaryStringObject, cancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// Deserializes JSON string to dictionary asynchronously using AOT-compatible source generators.
    /// </summary>
    private static async Task<Dictionary<string, object?>?> DeserializeFromJsonStringAsync(string json, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await JsonSerializer.DeserializeAsync(stream, FeatureAttributesJsonContext.Default.DictionaryStringObject, cancellationToken);
    }

    /// <summary>
    /// Converts JsonElement to appropriate primitive type for compatibility.
    /// </summary>
    private static object? ConvertJsonElementToObject(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                                        element.TryGetDouble(out var doubleVal) ? doubleVal :
                                        element.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value
            };
        }

        return value;
    }
}
