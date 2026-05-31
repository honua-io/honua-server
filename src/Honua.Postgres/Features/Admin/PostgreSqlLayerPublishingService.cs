// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Main partial: class declaration, DI surface, public ILayerPublishingService methods,
// shared constants/fields, the LoggerMessage Log class, and the nested DTO records
// (LayerFieldInsert, LayerExtentInsert, LayerExtentRefreshMetadata, GeometryHealth,
// ResolvedPublishValidation) used across partials. Implementation helpers live in:
//   * PostgreSqlLayerPublishingService.MetadataV2Graph.cs   — V2 graph projection
//   * PostgreSqlLayerPublishingService.Validation.cs        — publish validation pipeline
//   * PostgreSqlLayerPublishingService.TableIntrospection.cs — source-table discovery
//   * PostgreSqlLayerPublishingService.LayerPersistence.cs  — honua.* catalog writes
//   * PostgreSqlLayerPublishingService.ExtentRefresh.cs     — extent recomputation SQL
//   * PostgreSqlLayerPublishingService.SqlHelpers.cs        — shared quoting / expression helpers

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// PostgreSQL implementation of layer publishing operations.
/// </summary>
internal sealed partial class PostgreSqlLayerPublishingService(
    ITableDiscoveryService tableDiscoveryService,
    IMetadataV2GraphStore metadataGraphStore,
    ILogger<PostgreSqlLayerPublishingService> logger,
    string? metadataSchema = null) : ILayerPublishingService
{
    private const string DefaultServiceName = "default";
    private const int CatalogExtentSrid = 4326;
    private const string SourceBackedStorageOptionsJson = """{"sourceBacked":"true"}""";
    private const string SeverityPass = "pass";
    private const string SeverityWarning = "warning";
    private const string SeverityError = "error";
    private const string LayerConflictCheckCode = "layer-conflict";
    private const string SourceIntegerPrimaryKeyObjectIdStrategy = "source-integer-primary-key";
    private const string UnsupportedSourcePrimaryKeyObjectIdStrategy = "unsupported-source-primary-key";
    private const string UnresolvedObjectIdStrategy = "unresolved";
    private const int MaxJsonbBuildObjectPairs = 50;
    private static readonly string[] _defaultFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _defaultCapabilities = ["Query", "Extract"];

    private readonly ITableDiscoveryService _tableDiscoveryService = tableDiscoveryService;
    private readonly IMetadataV2GraphStore _metadataGraphStore = metadataGraphStore;
    private readonly ILogger<PostgreSqlLayerPublishingService> _logger = logger;

    // The Honua-managed metadata schema that owns the shared `features` table
    // (default 'honua', overridable via Database:Schema). Only a `features` table in
    // THIS schema is the shared multi-layer table whose rows carry the JSONB
    // `attributes` column and the `layer_id` discriminator; a user source table that
    // merely happens to be named `features` in another schema (e.g. public.features)
    // must NOT be treated as the shared table. (honua-server#1238 follow-up.)
    private readonly string _metadataSchema = string.IsNullOrWhiteSpace(metadataSchema)
        ? "honua"
        : metadataSchema.Trim();

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
            GROUP BY
                l.layer_id,
                l.layer_name,
                l.description,
                l.table_schema,
                l.table_name,
                l.primary_key_column,
                l.geometry_type,
                l.srid,
                l.enabled
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
                PrimaryKey = reader.IsDBNull(5) ? null : reader.GetString(5),
                GeometryType = reader.GetString(6),
                Srid = reader.GetInt32(7),
                Enabled = reader.GetBoolean(8),
                FieldCount = reader.GetInt32(9),
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

        var validation = await ValidateTableForPublishAsync(
                connectionString,
                new TablePublishValidationRequest
                {
                    Schema = schema,
                    Table = table,
                    LayerName = request.LayerName,
                    ServiceName = serviceName,
                    TargetSrid = request.Srid,
                    GeometryColumn = request.GeometryColumn,
                    PrimaryKey = request.PrimaryKey,
                    Fields = request.Fields
                },
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfPublishValidationFailed(validation);

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

        var serviceSrid = await ResolveExistingServiceSridAsync(connectionString, serviceName, cancellationToken)
            .ConfigureAwait(false);
        var srid = serviceSrid ?? request.Srid ?? tableInfo.Srid ?? 4326;
        if (srid <= 0)
        {
            srid = 4326;
        }
        var storageSrid = tableInfo.Srid is > 0 ? tableInfo.Srid.Value : srid;

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
        if (primaryKeyType is not MetadataV2FieldType.Integer and not MetadataV2FieldType.BigInteger)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Primary key must be an integer column.");
        }

        var fields = BuildLayerFields(selectedColumns, primaryKeyColumn, geometryColumn, request.FieldDomains);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureServiceAsync(connection, transaction, serviceName, srid, request.ConnectionId, cancellationToken);
        var existingLayerId = await FindExistingLayerAsync(connection, transaction, schema, table, cancellationToken);
        if (existingLayerId.HasValue)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                $"Layer already exists for table '{schema}.{table}'.",
                existingLayerId);
        }

        await EnsureLayerSequenceAsync(connection, transaction, cancellationToken);

        var extent = await ReadLayerExtentAsync(
                connection,
                transaction,
                schema,
                table,
                geometryColumn,
                storageSrid,
                cancellationToken)
            .ConfigureAwait(false);

        var layerId = await InsertLayerAsync(
            connection,
            transaction,
            request.LayerName.Trim(),
            request.Description,
            schema,
            table,
            primaryKeyColumn.Name,
            geometryColumn,
            geometryType,
            srid,
            storageSrid,
            extent,
            request.Enabled,
            cancellationToken);

        await InsertFieldsAsync(connection, transaction, layerId, fields, cancellationToken);

        var materializedCount = await MaterializeLayerFeaturesAsync(
            connection,
            transaction,
            layerId,
            schema,
            table,
            geometryColumn,
            srid,
            selectedColumns,
            cancellationToken);
        Log.LayerMaterialized(_logger, layerId, materializedCount);

        await RefreshLayerExtentAsync(connection, transaction, layerId, cancellationToken);

        await EnsureServiceLayerAsync(connection, transaction, serviceName, layerId, cancellationToken);
        await UpdateServiceExtentAsync(connection, transaction, serviceName, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        await UpsertPublishedLayerMetadataV2Async(
                serviceName,
                request,
                layerId,
                schema,
                table,
                primaryKeyColumn.Name,
                geometryColumn,
                geometryType,
                srid,
                storageSrid,
                fields,
                extent,
                cancellationToken)
            .ConfigureAwait(false);

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

    public async Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
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

        var layer = await GetLayerSummaryByIdAsync(connection, transaction, layerId, cancellationToken)
            .ConfigureAwait(false);
        if (layer == null)
        {
            return null;
        }

        await EnsureServiceAsync(connection, transaction, normalizedService, layer.Srid, null, cancellationToken)
            .ConfigureAwait(false);
        await EnsureServiceLayerAsync(connection, transaction, normalizedService, layerId, cancellationToken)
            .ConfigureAwait(false);
        await SetLayerEnabledCoreAsync(connection, transaction, layerId, enabled, cancellationToken)
            .ConfigureAwait(false);
        await UpdateServiceExtentAsync(connection, transaction, normalizedService, cancellationToken)
            .ConfigureAwait(false);

        var linkedLayer = await GetLayerSummaryAsync(connection, transaction, layerId, normalizedService, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken);
        return linkedLayer;
    }

    public async Task<TablePublishValidationResult> ValidateTableForPublishAsync(
        string connectionString,
        TablePublishValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        var schema = request.Schema?.Trim() ?? string.Empty;
        var table = request.Table?.Trim() ?? string.Empty;
        var serviceName = NormalizeServiceName(request.ServiceName);
        var checks = new List<TablePublishValidationCheck>();

        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            checks.Add(Error("source-table", "Schema and table are required."));
            return BuildValidationResult(request, serviceName, null, null, null, checks);
        }

        if (!IsSafeIdentifier(schema) || !IsSafeIdentifier(table))
        {
            checks.Add(Error("source-table", "Schema or table contains invalid characters.", null, $"{schema}.{table}"));
            return BuildValidationResult(request, serviceName, null, null, null, checks);
        }

        checks.Add(Pass("source-table", $"Source table identifier '{schema}.{table}' is syntactically valid."));

        var tableInfo = await ResolveTableInfoForValidationAsync(connectionString, schema, table, cancellationToken)
            .ConfigureAwait(false);
        if (tableInfo == null)
        {
            checks.Add(Error(
                "source-table",
                $"Table '{schema}.{table}' was not found or does not expose a discoverable geometry column.",
                $"{schema}.{table}",
                null));
            return BuildValidationResult(request, serviceName, null, null, null, checks);
        }

        checks.Add(Pass("source-table", $"Table '{schema}.{table}' is discoverable."));

        var selectedColumns = ResolveSelectedColumnsForValidation(tableInfo.Columns, request.Fields, checks);
        if (selectedColumns.Count == 0)
        {
            checks.Add(Error("selected-fields", "No publishable fields were selected."));
        }
        else
        {
            checks.Add(Pass("selected-fields", $"{selectedColumns.Count} field(s) selected for publishing."));
        }

        var geometryColumn = ResolveGeometryColumnForValidation(tableInfo, request.GeometryColumn, checks);
        var geometryType = ResolveGeometryTypeForValidation(tableInfo, checks);
        var primaryKeyName = ResolvePrimaryKeyForValidation(tableInfo.Columns, selectedColumns, request.PrimaryKey, checks);
        var serviceSrid = await ResolveExistingServiceSridAsync(connectionString, serviceName, cancellationToken)
            .ConfigureAwait(false);
        var targetSrid = ResolveTargetSridForValidation(tableInfo.Srid, serviceSrid, request.TargetSrid, checks);

        GeometryHealth? geometryHealth = null;
        if (!string.IsNullOrWhiteSpace(geometryColumn) &&
            geometryColumn!.Equals(tableInfo.GeometryColumn, StringComparison.OrdinalIgnoreCase))
        {
            geometryHealth = await InspectGeometryHealthAsync(
                    connectionString,
                    schema,
                    table,
                    geometryColumn!,
                    cancellationToken)
                .ConfigureAwait(false);
            AddGeometryHealthChecks(geometryHealth, checks);
        }

        await AddExistingLayerCheckAsync(connectionString, schema, table, checks, cancellationToken)
            .ConfigureAwait(false);

        return BuildValidationResult(
            request,
            serviceName,
            tableInfo,
            new ResolvedPublishValidation(
                geometryColumn,
                geometryType,
                primaryKeyName,
                serviceSrid,
                targetSrid),
            geometryHealth,
            checks);
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

        await SetLayerEnabledCoreAsync(connection, transaction, layerId, enabled, cancellationToken)
            .ConfigureAwait(false);
        layer = CloneWithEnabled(layer, enabled);

        await UpdateServiceExtentAsync(connection, transaction, normalizedService, cancellationToken);
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

        await UpdateServiceExtentAsync(connection, transaction, normalizedService, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ListPublishedLayersAsync(connectionString, normalizedService, cancellationToken);
    }

    public async Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
        string connectionString,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var normalizedService = NormalizeServiceName(serviceName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await ServiceExistsAsync(connection, transaction, normalizedService, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        var layerIds = await ListServiceLayerIdsAsync(
                connection,
                transaction,
                normalizedService,
                cancellationToken)
            .ConfigureAwait(false);

        var layers = new List<LayerExtentRefreshLayerResult>(layerIds.Count);
        var refreshedExtents = new Dictionary<int, LayerExtentInsert?>(layerIds.Count);
        foreach (var layerId in layerIds)
        {
            var layerResult = await RefreshLayerExtentAsync(
                    connection,
                    transaction,
                    layerId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (layerResult != null)
            {
                layers.Add(layerResult.Value.Public);
                refreshedExtents[layerId] = layerResult.Value.Extent;
            }
        }

        await UpdateServiceExtentAsync(connection, transaction, normalizedService, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Mirror the recomputed extents into the canonical Metadata v2 graph so the
        // FeatureServer / OGC API Features / OData metadata endpoints (which read
        // resource.Spatial.Bbox from the V2 snapshot) reflect the same extent the v1
        // honua.layers cache just got.
        await SyncRefreshedExtentsIntoV2GraphAsync(refreshedExtents, cancellationToken).ConfigureAwait(false);

        var layersWithExtent = layers.Count(layer => layer.HasExtent);
        return new LayerExtentRefreshResult
        {
            ServiceName = normalizedService,
            RefreshedLayerCount = layers.Count,
            LayersWithExtent = layersWithExtent,
            LayersWithoutExtent = layers.Count - layersWithExtent,
            ServiceExtentUpdated = true,
            Layers = layers
        };
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 8201,
            Level = LogLevel.Error,
            Message = "Failed to discover tables for layer publishing")]
        public static partial void TableDiscoveryFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 8202,
            Level = LogLevel.Information,
            Message = "Materialized {FeatureCount} features for published layer {LayerId}")]
        public static partial void LayerMaterialized(ILogger logger, int layerId, int featureCount);
    }

    private sealed record LayerFieldInsert(
        string Name,
        MetadataV2FieldType Type,
        int? MaxLength,
        bool Nullable,
        string? Description,
        object? DefaultValue = null,
        MetadataV2FieldDomain? Domain = null);

    private sealed record LayerExtentInsert(
        double MinX,
        double MinY,
        double MaxX,
        double MaxY,
        int Srid);

    private sealed record LayerExtentRefreshMetadata(
        int LayerId,
        string LayerName,
        string Schema,
        string Table,
        string GeometryColumn,
        int SourceSrid);

    private readonly record struct GeometryHealth(
        long FeatureCount,
        long NullGeometryCount,
        long InvalidGeometryCount,
        int DistinctGeometryTypeCount,
        int? MinSrid,
        int? MaxSrid);

    private readonly record struct ResolvedPublishValidation(
        string? GeometryColumn,
        string? GeometryType,
        string? PrimaryKey,
        int? ServiceSrid,
        int? TargetSrid);
}
