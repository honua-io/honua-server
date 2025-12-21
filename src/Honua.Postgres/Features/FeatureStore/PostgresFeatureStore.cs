// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore;

/// <summary>
/// Holds a parameterized SQL query with its parameters
/// </summary>
internal record ParameterizedQuery(string Sql, List<object> WhereParameters);

/// <summary>
/// PostgreSQL implementation of feature storage and retrieval
/// </summary>
/// <remarks>
/// <para>Marked as internal to prevent exposure of database-specific implementations
/// outside the Infrastructure layer (Clean Architecture principle).</para>
///
/// <para><strong>SECURITY NOTICE</strong>: WHERE clause handling has been secured using
/// parameterized queries. The implementation parses simple WHERE expressions (e.g.,
/// 'field = value', 'age > 18') and properly parameterizes all literal values while
/// validating field names to prevent SQL injection attacks.</para>
///
/// <para>Supported WHERE clause formats:
/// - Field comparisons: name = 'value', age > 18, score >= 90
/// - String operations: description LIKE 'pattern%'
/// - Null checks: field IS NULL, field IS NOT NULL
/// Complex expressions with subqueries or functions are not supported for security.</para>
/// </remarks>
internal sealed class PostgresFeatureStore : IFeatureStore
{
    private const string UnsupportedWhereClauseMessage =
        "WHERE clause format not supported. Use simple comparisons like: name = 'value' or age > 18";

    private static readonly Regex _comparisonRegex = new(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex _nullCheckRegex = new(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex _trueLiteralRegex = new(
        @"^(?:1\s*=\s*1|TRUE)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        var countQuery = BuildCountQuery(layerId, query);
        var totalCount = await ExecuteCountQuery(countQuery, query, layerId, cancellationToken);

        if (totalCount == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        // Build the main query
        var selectQuery = BuildSelectQuery(layerId, query);
        var features = await ExecuteSelectQuery(selectQuery, query, layerId, cancellationToken);

        var hasMore = query.Offset.HasValue && query.Limit.HasValue &&
                      query.Offset.Value + query.Limit.Value < totalCount;

        return QueryResult<Feature>.Create(totalCount, features, hasMore);
    }

    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var countQuery = BuildCountQuery(layerId, query);
        return await ExecuteCountQuery(countQuery, query, layerId, cancellationToken);
    }

    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var extentQuery = BuildExtentQuery(layerId, query ?? new FeatureQuery());

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(extentQuery.Sql, connection);
        AddQueryParameters(command, query ?? new FeatureQuery(), layerId, extentQuery.WhereParameters);

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
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await CreateWithConnectionAsync(layerId, feature, connection, transaction: null, cancellationToken);
    }

    private async Task<Feature> CreateWithConnectionAsync(
        int layerId,
        Feature feature,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var sql = $@"
            INSERT INTO {_tableName} (layer_id, geometry, attributes)
            VALUES ($1, $2, $3)
            RETURNING objectid, geometry, attributes";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
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
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await UpdateWithConnectionAsync(layerId, feature, connection, transaction: null, cancellationToken);
    }

    private async Task<Feature> UpdateWithConnectionAsync(
        int layerId,
        Feature feature,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var sql = $@"
            UPDATE {_tableName}
            SET geometry = $3, attributes = $4
            WHERE layer_id = $1 AND objectid = $2
            RETURNING objectid, geometry, attributes";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
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
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await DeleteWithConnectionAsync(layerId, featureId, connection, transaction: null, cancellationToken);
    }

    private async Task<bool> DeleteWithConnectionAsync(
        int layerId,
        long featureId,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var sql = $@"
            DELETE FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
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
            var (createdIds, createErrors) = await ProcessCreatesAsync(
                layerId,
                editBatch.Creates,
                connection,
                transaction,
                cancellationToken);
            var (updatedCount, updateErrors) = await ProcessUpdatesAsync(
                layerId,
                editBatch.Updates,
                connection,
                transaction,
                cancellationToken);
            var (deletedCount, deleteErrors) = await ProcessDeletesAsync(
                layerId,
                editBatch.Deletes,
                connection,
                transaction,
                cancellationToken);

            var allErrors = createErrors.Concat(updateErrors).Concat(deleteErrors).ToList();
            if (allErrors.Count != 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return FeatureEditResult.Failure(allErrors.ToArray());
            }

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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var createdIds = new List<long>();
        var errors = new List<string>();

        foreach (var feature in features)
        {
            try
            {
                var created = await CreateWithConnectionAsync(
                    layerId,
                    feature,
                    connection,
                    transaction,
                    cancellationToken);
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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var updatedCount = 0;
        var errors = new List<string>();

        foreach (var feature in features)
        {
            try
            {
                await UpdateWithConnectionAsync(
                    layerId,
                    feature,
                    connection,
                    transaction,
                    cancellationToken);
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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        var errors = new List<string>();

        foreach (var featureId in featureIds)
        {
            try
            {
                if (await DeleteWithConnectionAsync(
                    layerId,
                    featureId,
                    connection,
                    transaction,
                    cancellationToken))
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

        // Inject objectid into attributes for Esri FeatureServer compatibility
        // This ensures consistent behavior with TestFeatureStore and proper response formatting
        convertedAttributes["objectid"] = id;

        var attributes = convertedAttributes.ToImmutableDictionary();

        return Feature.Create(id, geometry, attributes);
    }


    private ParameterizedQuery BuildSelectQuery(int layerId, FeatureQuery query)
    {
        var sql = new StringBuilder($"SELECT objectid, geometry, attributes FROM {_tableName} WHERE layer_id = $1");
        var paramIndex = 2;
        var parameters = new List<object>();

        AppendWhereClause(sql, query, ref paramIndex, parameters);
        AppendSpatialFilter(sql, query, ref paramIndex);

        if (query.Limit.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT ${paramIndex++}");
        }

        if (query.Offset.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET ${paramIndex}");
        }

        return new ParameterizedQuery(sql.ToString(), parameters);
    }

    private ParameterizedQuery BuildCountQuery(int layerId, FeatureQuery query)
    {
        var sql = new StringBuilder($"SELECT COUNT(*) FROM {_tableName} WHERE layer_id = $1");
        var paramIndex = 2;
        var parameters = new List<object>();

        AppendWhereClause(sql, query, ref paramIndex, parameters);
        AppendSpatialFilter(sql, query, ref paramIndex);

        return new ParameterizedQuery(sql.ToString(), parameters);
    }

    private ParameterizedQuery BuildExtentQuery(int layerId, FeatureQuery query)
    {
        var sql = new StringBuilder($@"
            SELECT
                ST_XMin(extent), ST_YMin(extent), ST_XMax(extent), ST_YMax(extent)
            FROM (
                SELECT ST_Extent(geometry) as extent
                FROM {_tableName}
                WHERE layer_id = $1 AND geometry IS NOT NULL");

        var paramIndex = 2;
        var parameters = new List<object>();

        AppendWhereClause(sql, query, ref paramIndex, parameters);
        AppendSpatialFilter(sql, query, ref paramIndex);

        sql.Append(") AS extent_query");
        return new ParameterizedQuery(sql.ToString(), parameters);
    }

    private static void AppendWhereClause(StringBuilder sql, FeatureQuery query, ref int paramIndex, List<object> parameters)
    {
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            var whereClause = query.Where.Trim();

            // Parse and parameterize simple WHERE clauses
            // Supports: field = 'value', field > 123, field LIKE 'pattern%'
            var parameterizedClause = ParseAndParameterizeWhereClause(whereClause, ref paramIndex, parameters);

            sql.Append(CultureInfo.InvariantCulture, $" AND ({parameterizedClause})");
        }
    }

    private static string ParseAndParameterizeWhereClause(string whereClause, ref int paramIndex, List<object> parameters)
    {
        var dangerousPattern = FindDangerousPattern(whereClause);
        if (dangerousPattern != null)
        {
            throw new ArgumentException($"WHERE clause contains dangerous pattern: {dangerousPattern}");
        }

        var expressions = SplitOnAnd(whereClause);
        if (expressions.Count == 0)
        {
            throw new ArgumentException(UnsupportedWhereClauseMessage);
        }

        var parameterizedExpressions = new List<string>(expressions.Count);

        foreach (var expression in expressions)
        {
            var trimmedExpression = expression.Trim();
            if (trimmedExpression.Length == 0)
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }

            if (_trueLiteralRegex.IsMatch(trimmedExpression))
            {
                parameterizedExpressions.Add("TRUE");
                continue;
            }

            var nullMatch = _nullCheckRegex.Match(trimmedExpression);
            if (nullMatch.Success)
            {
                var fieldName = nullMatch.Groups["field"].Value;
                var notToken = nullMatch.Groups["not"].Value;
                var notClause = string.IsNullOrWhiteSpace(notToken) ? string.Empty : "NOT ";
                parameterizedExpressions.Add($"{fieldName} IS {notClause}NULL");
                continue;
            }

            var comparisonMatch = _comparisonRegex.Match(trimmedExpression);
            if (comparisonMatch.Success)
            {
                var fieldName = comparisonMatch.Groups["field"].Value;
                var operatorValue = NormalizeOperator(comparisonMatch.Groups["op"].Value);
                var valueToken = comparisonMatch.Groups["value"].Value;

                parameters.Add(ParseValueToken(valueToken));
                parameterizedExpressions.Add($"{fieldName} {operatorValue} ${paramIndex++}");
                continue;
            }

            throw new ArgumentException(UnsupportedWhereClauseMessage);
        }

        return string.Join(" AND ", parameterizedExpressions);
    }

    private static string NormalizeOperator(string operatorValue)
    {
        var normalized = Regex.Replace(operatorValue, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return normalized.ToUpperInvariant();
    }

    private static object ParseValueToken(string valueToken)
    {
        if (valueToken.StartsWith('\''))
        {
            return UnescapeSqlString(valueToken);
        }

        if (double.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue))
        {
            return numericValue;
        }

        throw new ArgumentException($"Invalid numeric value: {valueToken}");
    }

    private static string UnescapeSqlString(string valueToken)
    {
        if (valueToken.Length < 2 || valueToken[0] != '\'' || valueToken[^1] != '\'')
        {
            throw new ArgumentException(UnsupportedWhereClauseMessage);
        }

        var innerValue = valueToken.Substring(1, valueToken.Length - 2);
        return innerValue.Replace("''", "'", StringComparison.Ordinal);
    }

    private static List<string> SplitOnAnd(string whereClause)
    {
        var expressions = new List<string>();
        var current = new StringBuilder();
        var inString = false;

        for (var i = 0; i < whereClause.Length; i++)
        {
            var c = whereClause[i];

            if (c == '\'')
            {
                current.Append(c);

                if (inString && i + 1 < whereClause.Length && whereClause[i + 1] == '\'')
                {
                    current.Append(whereClause[i + 1]);
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (!inString && IsAndTokenAt(whereClause, i))
            {
                expressions.Add(current.ToString());
                current.Clear();
                i += 2;
                continue;
            }

            current.Append(c);
        }

        if (inString)
        {
            throw new ArgumentException(UnsupportedWhereClauseMessage);
        }

        expressions.Add(current.ToString());
        return expressions;
    }

    private static bool IsAndTokenAt(string whereClause, int index)
    {
        if (index + 2 >= whereClause.Length)
        {
            return false;
        }

        if (!whereClause.AsSpan(index, 3).Equals("AND", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var before = index == 0 ? ' ' : whereClause[index - 1];
        var after = index + 3 < whereClause.Length ? whereClause[index + 3] : ' ';

        return !IsIdentifierChar(before) && !IsIdentifierChar(after);
    }

    private static bool IsIdentifierChar(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static string? FindDangerousPattern(string whereClause)
    {
        var patterns = new[] { ";", "--", "/*", "*/" };
        foreach (var pattern in patterns)
        {
            if (ContainsOutsideQuotes(whereClause, pattern))
            {
                return pattern;
            }
        }

        return null;
    }

    private static bool ContainsOutsideQuotes(string input, string pattern)
    {
        var inString = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (c == '\'')
            {
                if (inString && i + 1 < input.Length && input[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (!inString && input.AsSpan(i).StartsWith(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (inString)
        {
            throw new ArgumentException(UnsupportedWhereClauseMessage);
        }

        return false;
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

    private void AddQueryParameters(NpgsqlCommand command, FeatureQuery query, int layerId, List<object> whereParameters)
    {
        // Layer ID is always first parameter
        command.Parameters.AddWithValue(layerId);

        // Add WHERE clause parameters (these come after layerId but before spatial/pagination params)
        foreach (var param in whereParameters)
        {
            command.Parameters.AddWithValue(param);
        }

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

    private async Task<long> ExecuteCountQuery(ParameterizedQuery countQuery, FeatureQuery query, int layerId, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(countQuery.Sql, connection);
        AddQueryParameters(command, query, layerId, countQuery.WhereParameters);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<ImmutableArray<Feature>> ExecuteSelectQuery(ParameterizedQuery selectQuery, FeatureQuery query, int layerId, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(selectQuery.Sql, connection);
        AddQueryParameters(command, query, layerId, selectQuery.WhereParameters);

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
