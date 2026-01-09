// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// PostgreSQL implementation of admin catalog operations for CRUD on services, layers, and relationships
/// </summary>
internal sealed class PostgresAdminCatalog : IAdminCatalog
{
    private static readonly string[] _supportedFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _supportedCapabilities = ["Query", "Extract"];
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _layersTable;
    private readonly string _fieldsTable;
    private readonly string _servicesTable;
    private readonly string _serviceLayersTable;
    private readonly string _relationshipsTable;

    public PostgresAdminCatalog(IDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));

        string schema = string.IsNullOrEmpty(schemaName) ? "honua" : schemaName;
        _layersTable = $"{schema}.layers";
        _fieldsTable = $"{schema}.layer_fields";
        _servicesTable = $"{schema}.services";
        _serviceLayersTable = $"{schema}.service_layers";
        _relationshipsTable = $"{schema}.relationships";
    }

    // ========================================================================
    // Service operations
    // ========================================================================

    /// <inheritdoc />
    public async Task<ServiceDefinition> CreateServiceAsync(
        string name,
        string description,
        SpatialReference spatialReference,
        int maxRecordCount = 1000,
        CatalogMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        string sql = $"""
            INSERT INTO {_servicesTable} (service_name, description, srid, max_record_count, supported_formats, capabilities, metadata)
            VALUES (@name, @description, @srid, @maxRecordCount, @supportedFormats, @capabilities, @metadata)
            ON CONFLICT (service_name) DO UPDATE SET service_name = EXCLUDED.service_name
            RETURNING service_name
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@name", name);
        _ = command.Parameters.AddWithValue("@description", description);
        _ = command.Parameters.AddWithValue("@srid", spatialReference.Wkid);
        _ = command.Parameters.AddWithValue("@maxRecordCount", maxRecordCount);
        _ = command.Parameters.AddWithValue("@supportedFormats", _supportedFormats);
        _ = command.Parameters.AddWithValue("@capabilities", _supportedCapabilities);
        _ = command.Parameters.Add(new NpgsqlParameter("@metadata", NpgsqlDbType.Jsonb)
        {
            Value = SerializeMetadata(metadata) ?? (object)DBNull.Value
        });

        _ = await command.ExecuteScalarAsync(cancellationToken);

        return new ServiceDefinition(
            name,
            description,
            [],
            spatialReference,
            maxRecordCount,
            Metadata: metadata);
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition?> UpdateServiceAsync(
        string name,
        string? description = null,
        int? maxRecordCount = null,
        CatalogMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var setClauses = new List<string>();
        var parameters = new List<NpgsqlParameter> { new("@name", name) };

        if (description != null)
        {
            setClauses.Add("description = @description");
            parameters.Add(new NpgsqlParameter("@description", description));
        }

        if (maxRecordCount.HasValue)
        {
            setClauses.Add("max_record_count = @maxRecordCount");
            parameters.Add(new NpgsqlParameter("@maxRecordCount", maxRecordCount.Value));
        }

        if (metadata != null)
        {
            setClauses.Add("metadata = @metadata");
            parameters.Add(new NpgsqlParameter("@metadata", NpgsqlDbType.Jsonb)
            {
                Value = SerializeMetadata(metadata) ?? (object)DBNull.Value
            });
        }

        if (setClauses.Count == 0)
        {
            // Nothing to update, just return existing service
            return await GetServiceInternalAsync(name, cancellationToken);
        }

        string sql = $"""
            UPDATE {_servicesTable}
            SET {string.Join(", ", setClauses)}
            WHERE LOWER(service_name) = LOWER(@name)
            RETURNING service_name, description, srid, max_record_count, metadata
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var param in parameters)
        {
            _ = command.Parameters.Add(param);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var updatedMetadata = ReadMetadata(reader, 4);

        return new ServiceDefinition(
            reader.GetString(0),
            reader.GetString(1),
            [],
            SpatialReference.Create(reader.GetInt32(2)),
            reader.GetInt32(3),
            Metadata: updatedMetadata);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteServiceAsync(string name, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            DELETE FROM {_servicesTable}
            WHERE LOWER(service_name) = LOWER(@name)
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@name", name);

        int rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> BindLayerToServiceAsync(string serviceName, int layerId, CancellationToken cancellationToken = default)
    {
        // Get next layer order for this service
        string orderSql = $"""
            SELECT COALESCE(MAX(layer_order), 0) + 1 FROM {_serviceLayersTable}
            WHERE LOWER(service_name) = LOWER(@serviceName)
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var orderCommand = new NpgsqlCommand(orderSql, connection);
        _ = orderCommand.Parameters.AddWithValue("@serviceName", serviceName);

        var nextOrder = (int?)await orderCommand.ExecuteScalarAsync(cancellationToken) ?? 0;

        string sql = $"""
            INSERT INTO {_serviceLayersTable} (service_name, layer_id, layer_order)
            SELECT s.service_name, @layerId, @layerOrder
            FROM {_servicesTable} s
            WHERE LOWER(s.service_name) = LOWER(@serviceName)
              AND EXISTS (SELECT 1 FROM {_layersTable} WHERE layer_id = @layerId)
            ON CONFLICT (service_name, layer_id) DO NOTHING
            RETURNING service_name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@serviceName", serviceName);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.AddWithValue("@layerOrder", nextOrder);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    /// <inheritdoc />
    public async Task<bool> UnbindLayerFromServiceAsync(string serviceName, int layerId, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            DELETE FROM {_serviceLayersTable}
            WHERE LOWER(service_name) = LOWER(@serviceName) AND layer_id = @layerId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@serviceName", serviceName);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        int rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    // ========================================================================
    // Layer operations
    // ========================================================================

    /// <inheritdoc />
    public async Task<LayerDefinition> CreateLayerAsync(
        string tableName,
        string schemaName,
        string displayName,
        string? description = null,
        CatalogMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        // Discover table metadata from PostGIS
        var tableInfo = await DiscoverTableAsync(schemaName, tableName, cancellationToken)
            ?? throw new InvalidOperationException($"Table '{schemaName}.{tableName}' not found or is not a valid geospatial table");

        // Use Serializable isolation level for critical catalog operations to prevent phantom reads
        var (dbConnection, dbTransaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await using var connection = (NpgsqlConnection)dbConnection;
        await using var transaction = (NpgsqlTransaction)dbTransaction;

        try
        {
            // Get next layer ID
            string idSql = $"SELECT COALESCE(MAX(layer_id), 0) + 1 FROM {_layersTable}";
            await using var idCommand = new NpgsqlCommand(idSql, connection, transaction);
            int newLayerId = (int?)await idCommand.ExecuteScalarAsync(cancellationToken) ?? 1;

            // Insert layer
            string sql = $"""
                INSERT INTO {_layersTable} (layer_id, layer_name, description, geometry_type, srid, table_schema, table_name, default_visibility, metadata)
                VALUES (@layerId, @layerName, @description, @geometryType, @srid, @tableSchema, @tableName, true, @metadata)
                RETURNING layer_id
                """;

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            _ = command.Parameters.AddWithValue("@layerId", newLayerId);
            _ = command.Parameters.AddWithValue("@layerName", displayName);
            _ = command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("@geometryType", tableInfo.GeometryType.ToString());
            _ = command.Parameters.AddWithValue("@srid", tableInfo.Srid);
            _ = command.Parameters.AddWithValue("@tableSchema", schemaName);
            _ = command.Parameters.AddWithValue("@tableName", tableName);
            _ = command.Parameters.Add(new NpgsqlParameter("@metadata", NpgsqlDbType.Jsonb)
            {
                Value = SerializeMetadata(metadata) ?? (object)DBNull.Value
            });

            _ = await command.ExecuteScalarAsync(cancellationToken);

            // Insert field definitions
            await InsertLayerFieldsAsync(connection, transaction, newLayerId, tableInfo.Fields, cancellationToken);

            var layerDefinition = new LayerDefinition(
                newLayerId,
                displayName,
                description,
                tableInfo.GeometryType,
                SpatialReference.Create(tableInfo.Srid),
                tableInfo.Fields,
                Metadata: metadata);

            await transaction.CommitAsync(cancellationToken);

            return layerDefinition;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> UpdateLayerAsync(
        int layerId,
        string? displayName = null,
        string? description = null,
        double? minScale = null,
        double? maxScale = null,
        bool? defaultVisibility = null,
        CatalogMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var setClauses = new List<string>();
        var parameters = new List<NpgsqlParameter> { new("@layerId", layerId) };

        if (displayName != null)
        {
            setClauses.Add("layer_name = @layerName");
            parameters.Add(new NpgsqlParameter("@layerName", displayName));
        }

        if (description != null)
        {
            setClauses.Add("description = @description");
            parameters.Add(new NpgsqlParameter("@description", description));
        }

        if (minScale.HasValue)
        {
            setClauses.Add("min_scale = @minScale");
            parameters.Add(new NpgsqlParameter("@minScale", minScale.Value));
        }

        if (maxScale.HasValue)
        {
            setClauses.Add("max_scale = @maxScale");
            parameters.Add(new NpgsqlParameter("@maxScale", maxScale.Value));
        }

        if (defaultVisibility.HasValue)
        {
            setClauses.Add("default_visibility = @defaultVisibility");
            parameters.Add(new NpgsqlParameter("@defaultVisibility", defaultVisibility.Value));
        }

        if (metadata != null)
        {
            setClauses.Add("metadata = @metadata");
            parameters.Add(new NpgsqlParameter("@metadata", NpgsqlDbType.Jsonb)
            {
                Value = SerializeMetadata(metadata) ?? (object)DBNull.Value
            });
        }

        if (setClauses.Count == 0)
        {
            // Nothing to update, just return existing layer
            return await GetLayerInternalAsync(layerId, cancellationToken);
        }

        string sql = $"""
            UPDATE {_layersTable}
            SET {string.Join(", ", setClauses)}
            WHERE layer_id = @layerId
            RETURNING layer_id, layer_name, description, geometry_type, srid, min_scale, max_scale, default_visibility, metadata
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var param in parameters)
        {
            _ = command.Parameters.Add(param);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var geometryType = Enum.Parse<GeometryType>(reader.GetString(3));
        var updatedMetadata = ReadMetadata(reader, 8);
        var layer = new LayerDefinition(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            geometryType,
            SpatialReference.Create(reader.GetInt32(4)),
            [],
            null,
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.GetBoolean(7),
            Metadata: updatedMetadata);

        reader.Close();

        // Get fields
        var fields = await GetLayerFieldsAsync(layerId, cancellationToken);
        return layer with { Fields = fields };
    }

    /// <inheritdoc />
    public async Task<bool> DeleteLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Use Serializable isolation level for critical catalog operations to prevent phantom reads
        var (dbConnection, dbTransaction) = await _connectionProvider.OpenTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await using var connection = (NpgsqlConnection)dbConnection;
        await using var transaction = (NpgsqlTransaction)dbTransaction;

        try
        {
            // Delete relationships first
            await using var relCommand = new NpgsqlCommand($"DELETE FROM {_relationshipsTable} WHERE layer_id = @layerId", connection, transaction);
            _ = relCommand.Parameters.AddWithValue("@layerId", layerId);
            _ = await relCommand.ExecuteNonQueryAsync(cancellationToken);

            // Delete service bindings
            await using var bindCommand = new NpgsqlCommand($"DELETE FROM {_serviceLayersTable} WHERE layer_id = @layerId", connection, transaction);
            _ = bindCommand.Parameters.AddWithValue("@layerId", layerId);
            _ = await bindCommand.ExecuteNonQueryAsync(cancellationToken);

            // Delete fields
            await using var fieldCommand = new NpgsqlCommand($"DELETE FROM {_fieldsTable} WHERE layer_id = @layerId", connection, transaction);
            _ = fieldCommand.Parameters.AddWithValue("@layerId", layerId);
            _ = await fieldCommand.ExecuteNonQueryAsync(cancellationToken);

            // Delete layer
            await using var layerCommand = new NpgsqlCommand($"DELETE FROM {_layersTable} WHERE layer_id = @layerId", connection, transaction);
            _ = layerCommand.Parameters.AddWithValue("@layerId", layerId);
            int rowsAffected = await layerCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return rowsAffected > 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> RefreshLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Get current layer metadata
        var existingLayer = await GetLayerInternalAsync(layerId, cancellationToken);
        if (existingLayer == null)
            return null;

        // Get table info from layer
        string tableSql = $"SELECT table_schema, table_name FROM {_layersTable} WHERE layer_id = @layerId";
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tableCommand = new NpgsqlCommand(tableSql, connection);
        _ = tableCommand.Parameters.AddWithValue("@layerId", layerId);

        await using var tableReader = await tableCommand.ExecuteReaderAsync(cancellationToken);
        if (!await tableReader.ReadAsync(cancellationToken))
            return null;

        string tableSchema = tableReader.GetString(0);
        string tableName = tableReader.GetString(1);
        tableReader.Close();

        // Rediscover table metadata
        var tableInfo = await DiscoverTableAsync(tableSchema, tableName, cancellationToken);
        if (tableInfo == null)
            return null;

        // Use RepeatableRead isolation level for layer refresh operations to ensure consistent field updates
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        try
        {
            // Delete old fields and insert new ones
            await using var deleteFieldsCommand = new NpgsqlCommand($"DELETE FROM {_fieldsTable} WHERE layer_id = @layerId", connection, transaction);
            _ = deleteFieldsCommand.Parameters.AddWithValue("@layerId", layerId);
            _ = await deleteFieldsCommand.ExecuteNonQueryAsync(cancellationToken);

            await InsertLayerFieldsAsync(connection, transaction, layerId, tableInfo.Fields, cancellationToken);

            // Update layer metadata
            string updateSql = $"""
                UPDATE {_layersTable}
                SET geometry_type = @geometryType, srid = @srid
                WHERE layer_id = @layerId
                """;

            await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
            _ = updateCommand.Parameters.AddWithValue("@layerId", layerId);
            _ = updateCommand.Parameters.AddWithValue("@geometryType", tableInfo.GeometryType.ToString());
            _ = updateCommand.Parameters.AddWithValue("@srid", tableInfo.Srid);
            _ = await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            var refreshedLayer = existingLayer with
            {
                GeometryType = tableInfo.GeometryType,
                SpatialReference = SpatialReference.Create(tableInfo.Srid),
                Fields = tableInfo.Fields
            };

            await transaction.CommitAsync(cancellationToken);

            return refreshedLayer;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ========================================================================
    // Relationship operations
    // ========================================================================

    /// <inheritdoc />
    public async Task<Relationship> CreateRelationshipAsync(
        int originLayerId,
        int relatedLayerId,
        string name,
        string relationshipType,
        string originForeignKey,
        string destinationForeignKey,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        // Get next relationship ID for this layer
        string idSql = $"SELECT COALESCE(MAX(relationship_id), 0) + 1 FROM {_relationshipsTable} WHERE layer_id = @layerId";
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var idCommand = new NpgsqlCommand(idSql, connection);
        _ = idCommand.Parameters.AddWithValue("@layerId", originLayerId);
        int newRelId = (int?)await idCommand.ExecuteScalarAsync(cancellationToken) ?? 1;

        string sql = $"""
            INSERT INTO {_relationshipsTable} (layer_id, relationship_id, name, related_layer_id, relationship_type, origin_foreign_key, destination_foreign_key, description)
            VALUES (@layerId, @relationshipId, @name, @relatedLayerId, @relationshipType, @originForeignKey, @destinationForeignKey, @description)
            RETURNING relationship_id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", originLayerId);
        _ = command.Parameters.AddWithValue("@relationshipId", newRelId);
        _ = command.Parameters.AddWithValue("@name", name);
        _ = command.Parameters.AddWithValue("@relatedLayerId", relatedLayerId);
        _ = command.Parameters.AddWithValue("@relationshipType", relationshipType);
        _ = command.Parameters.AddWithValue("@originForeignKey", originForeignKey);
        _ = command.Parameters.AddWithValue("@destinationForeignKey", destinationForeignKey);
        _ = command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);

        _ = await command.ExecuteScalarAsync(cancellationToken);

        return Relationship.Create(
            newRelId,
            name,
            relatedLayerId,
            relationshipType,
            originForeignKey,
            destinationForeignKey,
            description);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        string sql = $"""
            DELETE FROM {_relationshipsTable}
            WHERE layer_id = @layerId AND relationship_id = @relationshipId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.AddWithValue("@relationshipId", relationshipId);

        int rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    // ========================================================================
    // Private helper methods
    // ========================================================================

    private async Task<ServiceDefinition?> GetServiceInternalAsync(string name, CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT service_name, description, srid, max_record_count, metadata
            FROM {_servicesTable}
            WHERE LOWER(service_name) = LOWER(@name)
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@name", name);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var metadata = ReadMetadata(reader, 4);

        return new ServiceDefinition(
            reader.GetString(0),
            reader.GetString(1),
            [],
            SpatialReference.Create(reader.GetInt32(2)),
            reader.GetInt32(3),
            Metadata: metadata);
    }

    private async Task<LayerDefinition?> GetLayerInternalAsync(int layerId, CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT layer_id, layer_name, description, geometry_type, srid, min_scale, max_scale, default_visibility, metadata
            FROM {_layersTable}
            WHERE layer_id = @layerId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var geometryType = Enum.Parse<GeometryType>(reader.GetString(3));
        var metadata = ReadMetadata(reader, 8);
        var layer = new LayerDefinition(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            geometryType,
            SpatialReference.Create(reader.GetInt32(4)),
            [],
            null,
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.GetBoolean(7),
            Metadata: metadata);

        reader.Close();

        var fields = await GetLayerFieldsAsync(layerId, cancellationToken);
        return layer with { Fields = fields };
    }

    private async Task<FieldDefinition[]> GetLayerFieldsAsync(int layerId, CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT field_name, field_type, max_length, nullable, default_value, description
            FROM {_fieldsTable}
            WHERE layer_id = @layerId
            ORDER BY field_order
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fields = new List<FieldDefinition>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var fieldType = Enum.Parse<FieldType>(reader.GetString(1));
            fields.Add(new FieldDefinition(
                reader.GetString(0),
                fieldType,
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetValue(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return [.. fields];
    }

    private async Task InsertLayerFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        FieldDefinition[] fields,
        CancellationToken cancellationToken)
    {
        if (fields.Length == 0)
            return;

        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            string sql = $"""
                INSERT INTO {_fieldsTable} (layer_id, field_name, field_type, max_length, nullable, default_value, description, field_order)
                VALUES (@layerId, @fieldName, @fieldType, @maxLength, @nullable, @defaultValue, @description, @fieldOrder)
                """;

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            _ = command.Parameters.AddWithValue("@layerId", layerId);
            _ = command.Parameters.AddWithValue("@fieldName", field.Name);
            _ = command.Parameters.AddWithValue("@fieldType", field.Type.ToString());
            _ = command.Parameters.AddWithValue("@maxLength", (object?)field.Length ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("@nullable", field.Nullable);
            _ = command.Parameters.AddWithValue("@defaultValue", field.DefaultValue ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("@description", (object?)field.Description ?? DBNull.Value);
            _ = command.Parameters.AddWithValue("@fieldOrder", i);

            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<TableInfo?> DiscoverTableAsync(string schemaName, string tableName, CancellationToken cancellationToken)
    {
        // Discover geometry column
        string geometrySql = """
            SELECT type, srid
            FROM geometry_columns
            WHERE f_table_schema = @schema AND f_table_name = @table
            LIMIT 1
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var geomCommand = new NpgsqlCommand(geometrySql, connection);
        _ = geomCommand.Parameters.AddWithValue("@schema", schemaName);
        _ = geomCommand.Parameters.AddWithValue("@table", tableName);

        string geometryTypeStr = "Point";
        int srid = 4326;

        await using var geomReader = await geomCommand.ExecuteReaderAsync(cancellationToken);
        if (await geomReader.ReadAsync(cancellationToken))
        {
            geometryTypeStr = geomReader.GetString(0).Replace("MULTI", "Multi");
            srid = geomReader.GetInt32(1);
        }
        geomReader.Close();

        if (!Enum.TryParse<GeometryType>(geometryTypeStr, true, out var geometryType))
        {
            geometryType = GeometryType.Point;
        }

        // Discover columns
        string columnsSql = """
            SELECT column_name, data_type, character_maximum_length, is_nullable
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;

        await using var colCommand = new NpgsqlCommand(columnsSql, connection);
        _ = colCommand.Parameters.AddWithValue("@schema", schemaName);
        _ = colCommand.Parameters.AddWithValue("@table", tableName);

        await using var colReader = await colCommand.ExecuteReaderAsync(cancellationToken);
        var fields = new List<FieldDefinition>();

        while (await colReader.ReadAsync(cancellationToken))
        {
            string colName = colReader.GetString(0);
            string dataType = colReader.GetString(1);
            int? maxLength = colReader.IsDBNull(2) ? null : colReader.GetInt32(2);
            bool nullable = colReader.GetString(3) == "YES";

            FieldType fieldType = dataType.ToLowerInvariant() switch
            {
                "integer" or "int4" => FieldType.Integer,
                "bigint" or "int8" => FieldType.BigInteger,
                "real" or "float4" => FieldType.Float,
                "double precision" or "float8" => FieldType.Double,
                "boolean" or "bool" => FieldType.Boolean,
                "timestamp" or "timestamp without time zone" or "timestamp with time zone" => FieldType.DateTime,
                "date" => FieldType.Date,
                "time" or "time without time zone" or "time with time zone" => FieldType.Time,
                "geometry" or "geography" => FieldType.Geometry,
                "json" or "jsonb" => FieldType.Json,
                "bytea" => FieldType.Binary,
                "uuid" => FieldType.Uuid,
                _ => FieldType.String
            };

            fields.Add(new FieldDefinition(colName, fieldType, maxLength, nullable));
        }

        if (fields.Count == 0)
            return null;

        return new TableInfo(geometryType, srid, [.. fields]);
    }

    private static string? SerializeMetadata(CatalogMetadata? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        return JsonSerializer.Serialize(metadata, CatalogJsonContext.Default.CatalogMetadata);
    }

    private static CatalogMetadata? ReadMetadata(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogMetadata);
    }

    private sealed record TableInfo(GeometryType GeometryType, int Srid, FieldDefinition[] Fields);
}
