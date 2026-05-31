// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: honua.* catalog persistence.
//
// All transactional writes against the honua catalog: locating/inserting honua.layers,
// inserting honua.layer_fields, ensuring honua.services + honua.service_layers wiring,
// resolving persisted data_connections, advancing the layer_id sequence, materializing
// features into the canonical features table, and the small read paths that hydrate
// PublishedLayerSummary (single-layer and by-id). Also hosts SetLayerEnabledCoreAsync and
// the CloneWithEnabled DTO helper used by the enable/disable surface. Grouped here so the
// catalog-mutating SQL stays together and is easy to review for transactional consistency.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private static async Task<int?> FindExistingLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT layer_id
            FROM honua.layers
            WHERE table_schema = @schema AND table_name = @table;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int id ? id : null;
    }

    private static async Task<int> InsertLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string layerName,
        string? description,
        string schema,
        string table,
        string primaryKeyColumn,
        string geometryColumn,
        string geometryType,
        int srid,
        int storageSrid,
        LayerExtentInsert? extent,
        bool enabled,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO honua.layers (
                layer_name,
                description,
                table_schema,
                table_name,
                primary_key_column,
                geometry_column,
                storage_options,
                storage_srid,
                geometry_type,
                srid,
                extent,
                default_visibility,
                enabled
            )
            VALUES (
                @name,
                @description,
                @schema,
                @table,
                @primaryKeyColumn,
                @geometryColumn,
                @storageOptions,
                @storageSrid,
                @geometryType,
                @srid,
                CASE
                    WHEN @extentMinX IS NULL THEN NULL
                    ELSE ST_MakeEnvelope(@extentMinX, @extentMinY, @extentMaxX, @extentMaxY, @extentSrid)
                END,
                TRUE,
                @enabled
            )
            RETURNING layer_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@name", layerName);
        command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@primaryKeyColumn", primaryKeyColumn);
        command.Parameters.AddWithValue("@geometryColumn", geometryColumn);
        command.Parameters.Add("@storageOptions", NpgsqlDbType.Jsonb).Value = SourceBackedStorageOptionsJson;
        command.Parameters.AddWithValue("@geometryType", geometryType);
        command.Parameters.AddWithValue("@srid", srid);
        command.Parameters.AddWithValue("@storageSrid", storageSrid);
        AddNullableDouble(command, "@extentMinX", extent?.MinX);
        AddNullableDouble(command, "@extentMinY", extent?.MinY);
        AddNullableDouble(command, "@extentMaxX", extent?.MaxX);
        AddNullableDouble(command, "@extentMaxY", extent?.MaxY);
        command.Parameters.AddWithValue("@extentSrid", extent?.Srid ?? CatalogExtentSrid);
        command.Parameters.AddWithValue("@enabled", enabled);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not int layerId)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Unknown,
                "Failed to create layer.");
        }

        return layerId;
    }

    private static async Task EnsureLayerSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH layer_state AS (
                SELECT COALESCE(MAX(layer_id), 0) AS max_layer_id
                FROM honua.layers
            ),
            sequence_state AS (
                SELECT last_value, is_called
                FROM honua.layers_layer_id_seq
            )
            SELECT setval(
                pg_get_serial_sequence('honua.layers', 'layer_id'),
                GREATEST(layer_state.max_layer_id, sequence_state.last_value),
                CASE
                    WHEN layer_state.max_layer_id = 0 AND sequence_state.is_called = FALSE THEN FALSE
                    ELSE TRUE
                END
            )
            FROM layer_state
            CROSS JOIN sequence_state;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> MaterializeLayerFeaturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        string schema,
        string table,
        string geometryColumn,
        int srid,
        IReadOnlyList<ColumnInfo> attributeColumns,
        CancellationToken cancellationToken)
    {
        // TODO(honua-server#974): replace this one-time snapshot with the settled
        // publish refresh/CDC path once source-of-truth policy is finalized.
        var sourceTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var sourceGeometry = $"src.{QuoteIdentifier(geometryColumn)}";
        var canonicalGeometry = BuildCanonicalGeometryExpression(sourceGeometry);
        var attributesExpression = BuildAttributesExpression(attributeColumns);

        var sql = $"""
            WITH inserted AS (
                INSERT INTO features (layer_id, geometry, attributes)
                SELECT
                    @layerId,
                    {canonicalGeometry},
                    {attributesExpression}
                FROM {sourceTable} AS src
                RETURNING 1
            )
            SELECT COUNT(*)::int
            FROM inserted;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);
        command.Parameters.AddWithValue("@srid", srid);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int count
            ? count
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ServiceExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM honua.services
                WHERE service_name = @serviceName
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool exists && exists;
    }

    private static async Task<List<int>> ListServiceLayerIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT layer_id
            FROM honua.service_layers
            WHERE service_name = @serviceName
            ORDER BY layer_order, layer_id;
            """;

        var layerIds = new List<int>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            layerIds.Add(reader.GetInt32(0));
        }

        return layerIds;
    }

    private static async Task<int?> ResolveExistingServiceSridAsync(
        string connectionString,
        string serviceName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HonuaTableExistsAsync(connection, "services", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        const string sql = """
            SELECT srid
            FROM honua.services
            WHERE service_name = @serviceName
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result == null || result == DBNull.Value
            ? null
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task InsertFieldsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        List<LayerFieldInsert> fields,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO honua.layer_fields (
                layer_id,
                field_name,
                field_type,
                field_order,
                max_length,
                nullable,
                default_value,
                description,
                domain
            )
            VALUES (@layerId, @fieldName, @fieldType, @fieldOrder, @maxLength, @nullable, @defaultValue, @description, @domain);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var layerIdParameter = command.Parameters.Add("@layerId", NpgsqlDbType.Integer);
        var fieldNameParameter = command.Parameters.Add("@fieldName", NpgsqlDbType.Varchar);
        var fieldTypeParameter = command.Parameters.Add("@fieldType", NpgsqlDbType.Varchar);
        var fieldOrderParameter = command.Parameters.Add("@fieldOrder", NpgsqlDbType.Integer);
        var maxLengthParameter = command.Parameters.Add("@maxLength", NpgsqlDbType.Integer);
        var nullableParameter = command.Parameters.Add("@nullable", NpgsqlDbType.Boolean);
        var defaultValueParameter = command.Parameters.Add("@defaultValue", NpgsqlDbType.Text);
        var descriptionParameter = command.Parameters.Add("@description", NpgsqlDbType.Text);
        var domainParameter = command.Parameters.Add("@domain", NpgsqlDbType.Jsonb);

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            layerIdParameter.Value = layerId;
            fieldNameParameter.Value = field.Name;
            fieldTypeParameter.Value = field.Type.ToString();
            fieldOrderParameter.Value = i + 1;
            maxLengthParameter.Value = (object?)field.MaxLength ?? DBNull.Value;
            nullableParameter.Value = field.Nullable;
            defaultValueParameter.Value = (object?)field.DefaultValue ?? DBNull.Value;
            descriptionParameter.Value = (object?)field.Description ?? DBNull.Value;
            // Use the source-generated context (same path as
            // PostgresLayerFieldConfigurationStore) so an operator viewing the
            // admin field-configuration response sees the same domain JSON the
            // V2 graph + queryDomains expose.
            domainParameter.Value = field.Domain is null
                ? DBNull.Value
                : JsonSerializer.Serialize(
                    field.Domain,
                    MetadataV2JsonContext.Default.MetadataV2FieldDomain);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureServiceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        int srid,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        var persistedConnectionId = await ResolvePersistedConnectionIdAsync(
            connection,
            transaction,
            connectionId,
            cancellationToken);

        const string sql = """
            INSERT INTO honua.services (
                service_name,
                description,
                srid,
                supported_formats,
                capabilities,
                connection_id
            )
            VALUES (@serviceName, @description, @srid, @formats, @capabilities, @connectionId)
            ON CONFLICT (service_name) DO NOTHING;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        command.Parameters.AddWithValue("@description", $"Honua service '{serviceName}'");
        command.Parameters.AddWithValue("@srid", srid);
        command.Parameters.AddWithValue("@formats", _defaultFormats);
        command.Parameters.AddWithValue("@capabilities", _defaultCapabilities);
        command.Parameters.AddWithValue("@connectionId", (object?)persistedConnectionId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> ResolvePersistedConnectionIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? connectionId,
        CancellationToken cancellationToken)
    {
        if (!connectionId.HasValue)
        {
            return null;
        }

        const string tableExistsSql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'honua'
                  AND table_name = 'data_connections'
            );
            """;

        await using (var tableCommand = new NpgsqlCommand(tableExistsSql, connection, transaction))
        {
            var tableExists = (bool?)await tableCommand.ExecuteScalarAsync(cancellationToken) ?? false;
            if (!tableExists)
            {
                return null;
            }
        }

        const string connectionExistsSql = """
            SELECT EXISTS (
                SELECT 1
                FROM honua.data_connections
                WHERE connection_id = @connectionId
            );
            """;

        await using var connectionCommand = new NpgsqlCommand(connectionExistsSql, connection, transaction);
        connectionCommand.Parameters.AddWithValue("@connectionId", connectionId.Value);
        var connectionExists = (bool?)await connectionCommand.ExecuteScalarAsync(cancellationToken) ?? false;
        return connectionExists ? connectionId : null;
    }

    private static async Task EnsureServiceLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string orderSql = """
            SELECT COALESCE(MAX(layer_order), 0) + 1
            FROM honua.service_layers
            WHERE service_name = @serviceName;
            """;

        await using var orderCommand = new NpgsqlCommand(orderSql, connection, transaction);
        orderCommand.Parameters.AddWithValue("@serviceName", serviceName);
        var orderResult = await orderCommand.ExecuteScalarAsync(cancellationToken);
        var nextOrder = Convert.ToInt32(orderResult, CultureInfo.InvariantCulture);

        const string insertSql = """
            INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
            VALUES (@serviceName, @layerId, @layerOrder)
            ON CONFLICT (service_name, layer_id) DO NOTHING;
            """;

        await using var insertCommand = new NpgsqlCommand(insertSql, connection, transaction);
        insertCommand.Parameters.AddWithValue("@serviceName", serviceName);
        insertCommand.Parameters.AddWithValue("@layerId", layerId);
        insertCommand.Parameters.AddWithValue("@layerOrder", nextOrder);

        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PublishedLayerSummary?> GetLayerSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled,
                COUNT(f.field_name)::int AS field_count
            FROM honua.layers l
            INNER JOIN honua.service_layers sl
                ON sl.layer_id = l.layer_id
            LEFT JOIN honua.layer_fields f
                ON f.layer_id = l.layer_id
            WHERE sl.service_name = @serviceName
                AND l.layer_id = @layerId
            GROUP BY
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PublishedLayerSummary
        {
            LayerId = reader.GetInt32(0),
            LayerName = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            Schema = reader.GetString(3),
            Table = reader.GetString(4),
            PrimaryKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            GeometryType = reader.GetString(6),
            Srid = reader.GetInt32(7),
            Enabled = reader.GetBoolean(8),
            FieldCount = reader.GetInt32(9),
            ServiceName = serviceName
        };
    }

    private static async Task<PublishedLayerSummary?> GetLayerSummaryByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled,
                COUNT(f.field_name)::int AS field_count
            FROM honua.layers l
            LEFT JOIN honua.layer_fields f
                ON f.layer_id = l.layer_id
            WHERE l.layer_id = @layerId
            GROUP BY
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PublishedLayerSummary
        {
            LayerId = reader.GetInt32(0),
            LayerName = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            Schema = reader.GetString(3),
            Table = reader.GetString(4),
            PrimaryKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            GeometryType = reader.GetString(6),
            Srid = reader.GetInt32(7),
            Enabled = reader.GetBoolean(8),
            FieldCount = reader.GetInt32(9),
            ServiceName = DefaultServiceName
        };
    }

    private static async Task SetLayerEnabledCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE honua.layers
            SET enabled = @enabled
            WHERE layer_id = @layerId;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@enabled", enabled);
        command.Parameters.AddWithValue("@layerId", layerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PublishedLayerSummary CloneWithEnabled(PublishedLayerSummary layer, bool enabled)
    {
        return new PublishedLayerSummary
        {
            LayerId = layer.LayerId,
            LayerName = layer.LayerName,
            Description = layer.Description,
            Schema = layer.Schema,
            Table = layer.Table,
            GeometryType = layer.GeometryType,
            Srid = layer.Srid,
            PrimaryKey = layer.PrimaryKey,
            FieldCount = layer.FieldCount,
            Enabled = enabled,
            ServiceName = layer.ServiceName
        };
    }
}
