// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Catalog.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// PostgreSQL implementation of layer publishing operations.
/// </summary>
internal sealed partial class PostgreSqlLayerPublishingService(
    ITableDiscoveryService tableDiscoveryService,
    ILogger<PostgreSqlLayerPublishingService> logger) : ILayerPublishingService
{
    private const string DefaultServiceName = "default";
    private static readonly Regex _identifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly string[] _defaultFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _defaultCapabilities = ["Query", "Extract"];

    private readonly ITableDiscoveryService _tableDiscoveryService = tableDiscoveryService;
    private readonly ILogger<PostgreSqlLayerPublishingService> _logger = logger;

    public async Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
        string connectionString,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var normalizedService = NormalizeServiceName(serviceName);
        var layers = new List<PublishedLayerSummary>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.geometry_type,
                l.srid,
                l.enabled
            FROM honua.layers l
            LEFT JOIN honua.service_layers sl
                ON sl.layer_id = l.layer_id AND sl.service_name = @serviceName
            ORDER BY l.layer_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", normalizedService);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            layers.Add(new PublishedLayerSummary
            {
                LayerId = reader.GetInt32(0),
                LayerName = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Schema = reader.GetString(3),
                Table = reader.GetString(4),
                GeometryType = reader.GetString(5),
                Srid = reader.GetInt32(6),
                Enabled = reader.GetBoolean(7),
                FieldCount = 0,
                ServiceName = normalizedService
            });
        }

        return layers;
    }

    public async Task<PublishedLayerSummary> PublishLayerAsync(
        string connectionString,
        LayerPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        var schema = request.Schema?.Trim();
        var table = request.Table?.Trim();
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Schema and table are required.");
        }

        if (string.IsNullOrWhiteSpace(request.LayerName))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Layer name is required.");
        }

        if (!IsSafeIdentifier(schema) || !IsSafeIdentifier(table))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Schema or table contains invalid characters.");
        }

        var serviceName = NormalizeServiceName(request.ServiceName);

        var tableInfo = await ResolveTableInfoAsync(connectionString, schema, table, cancellationToken)
            ?? throw new LayerPublishingException(
                LayerPublishingErrorKind.NotFound,
                $"Table '{schema}.{table}' was not found or has no geometry column.");

        var geometryColumn = string.IsNullOrWhiteSpace(request.GeometryColumn)
            ? tableInfo.GeometryColumn
            : request.GeometryColumn;

        if (string.IsNullOrWhiteSpace(geometryColumn))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Geometry column is required.");
        }

        var geometryTypeRaw = string.IsNullOrWhiteSpace(request.GeometryType)
            ? tableInfo.GeometryType
            : request.GeometryType;

        if (string.IsNullOrWhiteSpace(geometryTypeRaw))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Geometry type is required.");
        }

        var geometryType = NormalizeGeometryType(geometryTypeRaw!);

        var srid = request.Srid ?? tableInfo.Srid ?? 4326;
        if (srid <= 0)
        {
            srid = 4326;
        }

        var columns = tableInfo.Columns;
        if (columns.Count == 0)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "No columns available for publishing.");
        }

        var selectedColumns = ResolveSelectedColumns(columns, request.Fields);
        if (selectedColumns.Count == 0)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "No fields selected for publishing.");
        }

        var primaryKeyName = ResolvePrimaryKeyName(selectedColumns, request.PrimaryKey)
            ?? throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Primary key is required.");

        var primaryKeyColumn = selectedColumns.FirstOrDefault(col =>
            string.Equals(col.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
        if (primaryKeyColumn == null)
        {
            var existsInTable = columns.Any(col =>
                string.Equals(col.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
            var message = existsInTable
                ? $"Primary key field '{primaryKeyName}' must be included in selected fields."
                : $"Primary key field '{primaryKeyName}' was not found on the source table.";
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, message);
        }
        var primaryKeyType = MapPostgresType(primaryKeyColumn.DataType);
        if (primaryKeyType is not FieldType.Integer and not FieldType.BigInteger)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Primary key must be an integer column.");
        }

        var fields = BuildLayerFields(selectedColumns, primaryKeyColumn, geometryColumn);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureServiceAsync(connection, transaction, serviceName, srid, request.ConnectionId, cancellationToken);

        var existingLayerId = await FindExistingLayerAsync(connection, transaction, schema, table, cancellationToken);
        if (existingLayerId.HasValue)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                $"Layer already exists for table '{schema}.{table}'.");
        }

        await EnsureLayerSequenceAsync(connection, transaction, cancellationToken);

        var layerId = await InsertLayerAsync(
            connection,
            transaction,
            request.LayerName.Trim(),
            request.Description,
            schema,
            table,
            geometryType,
            srid,
            request.Enabled,
            cancellationToken);

        await InsertFieldsAsync(connection, transaction, layerId, fields, cancellationToken);

        await EnsureServiceLayerAsync(connection, transaction, serviceName, layerId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new PublishedLayerSummary
        {
            LayerId = layerId,
            LayerName = request.LayerName.Trim(),
            Description = request.Description,
            Schema = schema,
            Table = table,
            GeometryType = geometryType,
            Srid = srid,
            PrimaryKey = primaryKeyColumn.Name,
            FieldCount = fields.Count,
            Enabled = request.Enabled,
            ServiceName = serviceName
        };
    }

    public async Task<PublishedLayerSummary?> SetLayerEnabledAsync(
        string connectionString,
        int layerId,
        string serviceName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (layerId < 0)
        {
            return null;
        }

        var normalizedService = NormalizeServiceName(serviceName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var layer = await GetLayerSummaryAsync(connection, transaction, layerId, normalizedService, cancellationToken);
        if (layer == null)
        {
            return null;
        }

        const string updateSql = """
            UPDATE honua.layers
            SET enabled = @enabled
            WHERE layer_id = @layerId;
            """;
        await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
        updateCommand.Parameters.AddWithValue("@enabled", enabled);
        updateCommand.Parameters.AddWithValue("@layerId", layerId);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        layer = CloneWithEnabled(layer, enabled);

        await transaction.CommitAsync(cancellationToken);
        return layer;
    }

    public async Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
        string connectionString,
        string serviceName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var normalizedService = NormalizeServiceName(serviceName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string updateSql = """
            UPDATE honua.layers
            SET enabled = @enabled
            WHERE layer_id IN (
                SELECT layer_id
                FROM honua.service_layers
                WHERE service_name = @serviceName
            );
            """;

        await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
        updateCommand.Parameters.AddWithValue("@enabled", enabled);
        updateCommand.Parameters.AddWithValue("@serviceName", normalizedService);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return await ListPublishedLayersAsync(connectionString, normalizedService, cancellationToken);
    }

    private async Task<TableInfo?> ResolveTableInfoAsync(
        string connectionString,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        try
        {
            var tables = await _tableDiscoveryService
                .DiscoverPostGisTablesAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);

            return tables.FirstOrDefault(t =>
                string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.TableDiscoveryFailed(_logger, ex);
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Unknown,
                "Failed to discover tables for publishing.");
        }
    }

    private static List<ColumnInfo> ResolveSelectedColumns(
        List<ColumnInfo> columns,
        IReadOnlyList<string> selected)
    {
        if (selected == null || selected.Count == 0)
        {
            return columns;
        }

        var lookup = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<ColumnInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in selected)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                continue;
            }

            if (!lookup.TryGetValue(field, out var column))
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Validation,
                    $"Field '{field}' was not found on the source table.");
            }

            if (seen.Add(column.Name))
            {
                result.Add(column);
            }
        }

        return result;
    }

    private static string? ResolvePrimaryKeyName(
        List<ColumnInfo> selectedColumns,
        string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        var primaryKey = selectedColumns.FirstOrDefault(c => c.IsPrimaryKey)?.Name;
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            return primaryKey;
        }

        return selectedColumns.FirstOrDefault(c => IsDefaultPrimaryKeyName(c.Name))?.Name;
    }

    private static bool IsDefaultPrimaryKeyName(string name)
    {
        return name.Equals("id", StringComparison.OrdinalIgnoreCase)
               || name.Equals("objectid", StringComparison.OrdinalIgnoreCase)
               || name.Equals("fid", StringComparison.OrdinalIgnoreCase);
    }

    private static List<LayerFieldInsert> BuildLayerFields(
        List<ColumnInfo> selectedColumns,
        ColumnInfo primaryKeyColumn,
        string geometryColumn)
    {
        var fields = new List<LayerFieldInsert>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var primaryKeyType = MapPostgresType(primaryKeyColumn.DataType);
        fields.Add(new LayerFieldInsert(
            primaryKeyColumn.Name,
            primaryKeyType,
            primaryKeyColumn.MaxLength,
            primaryKeyColumn.IsNullable,
            null));
        _ = added.Add(primaryKeyColumn.Name);

        foreach (var column in selectedColumns)
        {
            if (added.Contains(column.Name))
            {
                continue;
            }

            var fieldType = MapPostgresType(column.DataType);
            fields.Add(new LayerFieldInsert(
                column.Name,
                fieldType,
                column.MaxLength,
                column.IsNullable,
                null));
            _ = added.Add(column.Name);
        }

        fields.Add(new LayerFieldInsert(
            geometryColumn,
            FieldType.Geometry,
            null,
            true,
            "Geometry"));

        return fields;
    }

    private static FieldType MapPostgresType(string dataType)
    {
        var normalized = dataType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "smallint" => FieldType.Integer,
            "integer" => FieldType.Integer,
            "bigint" => FieldType.BigInteger,
            "real" => FieldType.Float,
            "double precision" => FieldType.Double,
            "numeric" => FieldType.Double,
            "decimal" => FieldType.Double,
            "boolean" => FieldType.Boolean,
            "date" => FieldType.Date,
            "timestamp without time zone" => FieldType.DateTime,
            "timestamp with time zone" => FieldType.DateTime,
            "time without time zone" => FieldType.Time,
            "time with time zone" => FieldType.Time,
            "uuid" => FieldType.Uuid,
            "json" => FieldType.Json,
            "jsonb" => FieldType.Json,
            "bytea" => FieldType.Binary,
            "character varying" => FieldType.String,
            "character" => FieldType.String,
            "text" => FieldType.String,
            _ => FieldType.String
        };
    }

    private static string NormalizeGeometryType(string raw)
    {
        if (Enum.TryParse<GeometryType>(raw, true, out var parsed))
        {
            return parsed.ToString();
        }

        var normalized = raw.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();

        var mapped = normalized switch
        {
            "POINT" => GeometryType.Point,
            "MULTIPOINT" => GeometryType.MultiPoint,
            "LINESTRING" => GeometryType.LineString,
            "MULTILINESTRING" => GeometryType.MultiLineString,
            "POLYGON" => GeometryType.Polygon,
            "MULTIPOLYGON" => GeometryType.MultiPolygon,
            "GEOMETRYCOLLECTION" => GeometryType.GeometryCollection,
            _ => GeometryType.None
        };

        if (mapped == GeometryType.None)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                $"Unsupported geometry type '{raw}'.");
        }

        return mapped.ToString();
    }

    private static string NormalizeServiceName(string? serviceName)
    {
        return string.IsNullOrWhiteSpace(serviceName) ? DefaultServiceName : serviceName.Trim();
    }

    private static bool IsSafeIdentifier(string value) => _identifierRegex.IsMatch(value);

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
        string geometryType,
        int srid,
        bool enabled,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO honua.layers (
                layer_name,
                description,
                table_schema,
                table_name,
                geometry_type,
                srid,
                default_visibility,
                enabled
            )
            VALUES (@name, @description, @schema, @table, @geometryType, @srid, TRUE, @enabled)
            RETURNING layer_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@name", layerName);
        command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@geometryType", geometryType);
        command.Parameters.AddWithValue("@srid", srid);
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
                description
            )
            VALUES (@layerId, @fieldName, @fieldType, @fieldOrder, @maxLength, @nullable, @defaultValue, @description);
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
                l.geometry_type,
                l.srid,
                l.enabled
            FROM honua.layers l
            LEFT JOIN honua.service_layers sl
                ON sl.layer_id = l.layer_id AND sl.service_name = @serviceName
            WHERE l.layer_id = @layerId;
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
            GeometryType = reader.GetString(5),
            Srid = reader.GetInt32(6),
            Enabled = reader.GetBoolean(7),
            FieldCount = 0,
            ServiceName = serviceName
        };
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

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 8201,
            Level = LogLevel.Error,
            Message = "Failed to discover tables for layer publishing")]
        public static partial void TableDiscoveryFailed(ILogger logger, Exception exception);
    }

    private sealed record LayerFieldInsert(
        string Name,
        FieldType Type,
        int? MaxLength,
        bool Nullable,
        string? Description,
        object? DefaultValue = null);
}
