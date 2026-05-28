// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Postgres.Features.Admin;

/// <summary>
/// PostgreSQL implementation of layer publishing operations.
/// </summary>
internal sealed partial class PostgreSqlLayerPublishingService(
    ITableDiscoveryService tableDiscoveryService,
    IMetadataV2GraphStore metadataGraphStore,
    ILogger<PostgreSqlLayerPublishingService> logger) : ILayerPublishingService
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

    private async Task UpsertPublishedLayerMetadataV2Async(
        string serviceName,
        LayerPublishRequest request,
        int layerId,
        string schema,
        string table,
        string primaryKeyColumn,
        string geometryColumn,
        string geometryType,
        int srid,
        int storageSrid,
        IReadOnlyList<LayerFieldInsert> fields,
        LayerExtentInsert? extent,
        CancellationToken cancellationToken)
    {
        var snapshot = await _metadataGraphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var graph = snapshot.Graph;
        var now = DateTimeOffset.UtcNow;
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);
        var service = BuildPublishedService(graph, serviceName, srid, now);
        var resource = BuildPublishedResource(
            request,
            layerId,
            primaryKeyColumn,
            geometryColumn,
            geometryType,
            srid,
            storageSrid,
            fields,
            extent,
            now);
        var binding = BuildPublishedStorageBinding(
            request,
            layerId,
            resource.Metadata.Id,
            schema,
            table,
            primaryKeyColumn,
            geometryColumn,
            storageSrid,
            now);
        var publication = BuildPublishedPublication(
            service,
            resource,
            binding,
            layerIdText,
            request.LayerName.Trim(),
            now);
        var connection = BuildPublishedConnection(request.ConnectionId, now);
        service = service with
        {
            PublicationIds = service.PublicationIds
                .Append(publication.Metadata.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        var updatedGraph = graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = now,
            Services = UpsertById(graph.Services, service, static item => item.Metadata.Id),
            Resources = UpsertById(graph.Resources, resource, static item => item.Metadata.Id),
            StorageBindings = UpsertById(graph.StorageBindings, binding, static item => item.Metadata.Id),
            Publications = UpsertPublication(graph.Publications, publication),
            Connections = connection is null
                ? graph.Connections
                : UpsertById(graph.Connections, connection, static item => item.Metadata.Id)
        };

        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Published layer metadata v2 graph is invalid: {string.Join("; ", validation.Errors)}");
        }

        await _metadataGraphStore.SaveAsync(updatedGraph, snapshot.Etag, cancellationToken).ConfigureAwait(false);
    }

    private static MetadataV2Service BuildPublishedService(
        MetadataV2Graph graph,
        string serviceName,
        int srid,
        DateTimeOffset now)
    {
        var existing = graph.Services.FirstOrDefault(service =>
            string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.Metadata.Id, serviceName, StringComparison.Ordinal));

        var serviceId = existing?.Metadata.Id ?? $"svc-publish-{SanitizeMetadataId(serviceName)}";
        var metadata = (existing?.Metadata ?? new MetadataV2ObjectMetadata()) with
        {
            Id = serviceId,
            Name = string.IsNullOrWhiteSpace(existing?.Metadata.Name) ? serviceName : existing!.Metadata.Name,
            Title = existing?.Metadata.Title ?? serviceName,
            Description = existing?.Metadata.Description ?? $"Honua service '{serviceName}'",
            CreatedAt = existing?.Metadata.CreatedAt ?? now,
            UpdatedAt = now
        };

        return (existing ?? new MetadataV2Service()) with
        {
            Metadata = metadata,
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = $"/rest/services/{serviceName}/FeatureServer",
            Protocols = MetadataV2ServiceProtocols.All,
            SpatialReference = CreateSpatialReference(srid),
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Resource BuildPublishedResource(
        LayerPublishRequest request,
        int layerId,
        string primaryKeyColumn,
        string geometryColumn,
        string geometryType,
        int srid,
        int storageSrid,
        IReadOnlyList<LayerFieldInsert> fields,
        LayerExtentInsert? extent,
        DateTimeOffset now)
    {
        var bindingId = BuildStorageBindingId(layerId);
        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = BuildResourceId(layerId),
                Name = request.LayerName.Trim(),
                Title = request.LayerName.Trim(),
                Description = request.Description,
                CreatedAt = now,
                UpdatedAt = now
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = [bindingId],
            PrimaryStorageBindingId = bindingId,
            SchemaFields = fields
                .Select(field => MapLayerFieldToMetadataV2(field, primaryKeyColumn, geometryColumn))
                .ToArray(),
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = CreateSpatialReference(srid),
                GeometryType = MapMetadataV2GeometryType(geometryType),
                Bbox = extent is not null && extent.Srid == srid
                    ? new MetadataV2Bbox
                    {
                        West = extent.MinX,
                        South = extent.MinY,
                        East = extent.MaxX,
                        North = extent.MaxY
                    }
                    : null,
                PrimaryGeometryField = geometryColumn,
                StorageCrs = CreateSpatialReference(storageSrid)
            },
            Display = new MetadataV2ResourceDisplay
            {
                DisplayField = fields
                    .FirstOrDefault(field =>
                        field.Type != MetadataV2FieldType.Geometry &&
                        !string.Equals(field.Name, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                    ?.Name,
                Queryable = true,
                DefaultVisibility = request.Enabled
            },
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2StorageBinding BuildPublishedStorageBinding(
        LayerPublishRequest request,
        int layerId,
        string resourceId,
        string schema,
        string table,
        string primaryKeyColumn,
        string geometryColumn,
        int storageSrid,
        DateTimeOffset now)
    {
        var connectionId = request.ConnectionId?.ToString("D");
        return new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = BuildStorageBindingId(layerId),
                Name = BuildStorageBindingId(layerId),
                Title = $"{schema}.{table}",
                CreatedAt = now,
                UpdatedAt = now
            },
            ResourceId = resourceId,
            ConnectionId = connectionId,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"{schema}.{table}",
            StorageLayerId = layerId,
            Capabilities =
            [
                MetadataV2StorageBindingCapability.Query,
                MetadataV2StorageBindingCapability.Filter,
                MetadataV2StorageBindingCapability.Sort,
                MetadataV2StorageBindingCapability.Aggregate
            ],
            Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [FeatureStorageMapping.SourceBackedOption] = JsonSerializer.SerializeToElement(true),
                ["schemaName"] = JsonSerializer.SerializeToElement(schema),
                ["tableName"] = JsonSerializer.SerializeToElement(table),
                ["primaryKeyColumn"] = JsonSerializer.SerializeToElement(primaryKeyColumn),
                ["geometryColumn"] = JsonSerializer.SerializeToElement(geometryColumn),
                ["storageSrid"] = JsonSerializer.SerializeToElement(storageSrid)
            },
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Publication BuildPublishedPublication(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2StorageBinding binding,
        string layerIdText,
        string layerTitle,
        DateTimeOffset now)
    {
        return new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"pub-{service.Metadata.Id}-{layerIdText}",
                Name = layerIdText,
                Title = layerTitle,
                CreatedAt = now,
                UpdatedAt = now
            },
            ResourceId = resource.Metadata.Id,
            ServiceId = service.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerIdText,
                IsNumeric = true
            },
            IsPrimary = true,
            SupportedFormats = _defaultFormats,
            Capabilities = _defaultCapabilities,
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Connection? BuildPublishedConnection(Guid? connectionId, DateTimeOffset now)
    {
        if (!connectionId.HasValue)
        {
            return null;
        }

        var id = connectionId.Value.ToString("D");
        return new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = id,
                Name = id,
                Title = "PostGIS secure connection",
                CreatedAt = now,
                UpdatedAt = now
            },
            Type = MetadataV2ConnectionType.Database,
            Provider = DataProviderNames.Postgis,
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Field MapLayerFieldToMetadataV2(
        LayerFieldInsert field,
        string primaryKeyColumn,
        string geometryColumn)
    {
        var semanticRoles = new List<string>(capacity: 2);
        if (string.Equals(field.Name, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
        {
            semanticRoles.Add("id.primary");
        }
        if (string.Equals(field.Name, geometryColumn, StringComparison.OrdinalIgnoreCase))
        {
            semanticRoles.Add("geometry.primary");
        }

        return new MetadataV2Field
        {
            Name = field.Name,
            Type = MapMetadataV2FieldType(field.Type),
            Title = field.Name,
            Description = field.Description,
            Nullable = field.Nullable,
            SemanticRoles = semanticRoles.ToArray(),
            Alias = field.Name,
            Editable = field.Type != MetadataV2FieldType.Geometry,
            Length = field.MaxLength
        };
    }

    private static MetadataV2FieldType MapMetadataV2FieldType(MetadataV2FieldType fieldType)
        => fieldType switch
        {
            MetadataV2FieldType.String => MetadataV2FieldType.String,
            MetadataV2FieldType.Integer => MetadataV2FieldType.Integer,
            MetadataV2FieldType.BigInteger => MetadataV2FieldType.BigInteger,
            MetadataV2FieldType.Double => MetadataV2FieldType.Double,
            MetadataV2FieldType.Float => MetadataV2FieldType.Float,
            MetadataV2FieldType.Boolean => MetadataV2FieldType.Boolean,
            MetadataV2FieldType.DateTime => MetadataV2FieldType.DateTime,
            MetadataV2FieldType.Date => MetadataV2FieldType.Date,
            MetadataV2FieldType.Time => MetadataV2FieldType.Time,
            MetadataV2FieldType.Geometry => MetadataV2FieldType.Geometry,
            MetadataV2FieldType.Json => MetadataV2FieldType.Json,
            MetadataV2FieldType.Binary => MetadataV2FieldType.Binary,
            MetadataV2FieldType.Uuid => MetadataV2FieldType.Uuid,
            _ => MetadataV2FieldType.Unknown
        };

    private static MetadataV2GeometryType MapMetadataV2GeometryType(string geometryType)
        => geometryType.Trim().ToLowerInvariant() switch
        {
            "point" => MetadataV2GeometryType.Point,
            "multipoint" => MetadataV2GeometryType.MultiPoint,
            "linestring" => MetadataV2GeometryType.LineString,
            "multilinestring" => MetadataV2GeometryType.MultiLineString,
            "polygon" => MetadataV2GeometryType.Polygon,
            "multipolygon" => MetadataV2GeometryType.MultiPolygon,
            "geometrycollection" => MetadataV2GeometryType.GeometryCollection,
            _ => MetadataV2GeometryType.Mixed
        };

    private static MetadataV2SpatialReference CreateSpatialReference(int srid)
        => new()
        {
            Srid = srid,
            Crs = $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}",
            IsGeographic = srid == 4326
        };

    private static MetadataV2Status ActiveReadyStatus(DateTimeOffset now)
        => new()
        {
            Lifecycle = MetadataV2LifecycleStatus.Active,
            State = MetadataV2OperationalState.Ready,
            ObservedAt = now
        };

    private static List<T> UpsertById<T>(
        IReadOnlyList<T> items,
        T item,
        Func<T, string> idSelector)
    {
        var itemId = idSelector(item);
        var result = new List<T>(items.Count + 1);
        var replaced = false;

        foreach (var existing in items)
        {
            if (string.Equals(idSelector(existing), itemId, StringComparison.Ordinal))
            {
                if (!replaced)
                {
                    result.Add(item);
                    replaced = true;
                }
                continue;
            }

            result.Add(existing);
        }

        if (!replaced)
        {
            result.Add(item);
        }

        return result;
    }

    private static List<MetadataV2Publication> UpsertPublication(
        IReadOnlyList<MetadataV2Publication> publications,
        MetadataV2Publication publication)
    {
        var result = new List<MetadataV2Publication>(publications.Count + 1);
        var replaced = false;

        foreach (var existing in publications)
        {
            var sameIdentity = string.Equals(
                existing.Metadata.Id,
                publication.Metadata.Id,
                StringComparison.Ordinal);
            var sameServiceLayer = string.Equals(
                existing.ServiceId,
                publication.ServiceId,
                StringComparison.Ordinal) &&
                existing.LayerIndex == publication.LayerIndex;

            if (sameIdentity || sameServiceLayer)
            {
                if (!replaced)
                {
                    result.Add(publication);
                    replaced = true;
                }
                continue;
            }

            result.Add(existing);
        }

        if (!replaced)
        {
            result.Add(publication);
        }

        return result;
    }

    private static string BuildResourceId(int layerId)
        => $"res-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildStorageBindingId(int layerId)
        => $"binding-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static string SanitizeMetadataId(string value)
    {
        var trimmed = value.Trim();
        var chars = trimmed.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'
                ? ch
                : '-');
        var sanitized = new string(chars.ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
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
                layers.Add(layerResult);
            }
        }

        await UpdateServiceExtentAsync(connection, transaction, normalizedService, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

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

    private async Task<TableInfo?> ResolveTableInfoForValidationAsync(
        string connectionString,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var discovered = await ResolveTableInfoAsync(connectionString, schema, table, cancellationToken)
            .ConfigureAwait(false);
        if (discovered != null)
        {
            return discovered;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, schema, table, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TableInfo
        {
            Schema = schema,
            Table = table,
            GeometryColumn = null,
            GeometryType = null,
            Srid = null,
            EstimatedRows = await GetEstimatedRowCountAsync(connection, schema, table, cancellationToken)
                .ConfigureAwait(false),
            Columns = await GetTableColumnsAsync(connection, schema, table, cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = @schema
                  AND table_name = @table
                  AND table_type IN ('BASE TABLE', 'FOREIGN TABLE')
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool exists && exists;
    }

    private static async Task<long?> GetEstimatedRowCountAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT reltuples::bigint
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result == null || result == DBNull.Value
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<List<ColumnInfo>> GetTableColumnsAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                c.column_name,
                c.data_type,
                c.is_nullable,
                c.character_maximum_length,
                CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END as is_primary_key
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT a.attname as column_name
                FROM pg_index i
                JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                JOIN pg_class cl ON cl.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = cl.relnamespace
                WHERE i.indisprimary
                  AND n.nspname = @schema
                  AND cl.relname = @table
            ) pk ON pk.column_name = c.column_name
            WHERE c.table_schema = @schema
              AND c.table_name = @table
              AND c.data_type NOT IN ('geometry', 'geography')
              AND c.udt_name NOT IN ('geometry', 'geography')
            ORDER BY c.ordinal_position;
            """;

        var columns = new List<ColumnInfo>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new ColumnInfo
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = reader.GetString(2) == "YES",
                MaxLength = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                IsPrimaryKey = reader.GetBoolean(4)
            });
        }

        return columns;
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

        if (!added.Contains(geometryColumn))
        {
            fields.Add(new LayerFieldInsert(
                geometryColumn,
                MetadataV2FieldType.Geometry,
                null,
                true,
                "Geometry"));
        }

        return fields;
    }

    private static MetadataV2FieldType MapPostgresType(string dataType)
    {
        var normalized = dataType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "smallint" => MetadataV2FieldType.Integer,
            "integer" => MetadataV2FieldType.Integer,
            "bigint" => MetadataV2FieldType.BigInteger,
            "real" => MetadataV2FieldType.Float,
            "double precision" => MetadataV2FieldType.Double,
            "numeric" => MetadataV2FieldType.Double,
            "decimal" => MetadataV2FieldType.Double,
            "boolean" => MetadataV2FieldType.Boolean,
            "date" => MetadataV2FieldType.Date,
            "timestamp without time zone" => MetadataV2FieldType.DateTime,
            "timestamp with time zone" => MetadataV2FieldType.DateTime,
            "time without time zone" => MetadataV2FieldType.Time,
            "time with time zone" => MetadataV2FieldType.Time,
            "uuid" => MetadataV2FieldType.Uuid,
            "json" => MetadataV2FieldType.Json,
            "jsonb" => MetadataV2FieldType.Json,
            "bytea" => MetadataV2FieldType.Binary,
            "character varying" => MetadataV2FieldType.String,
            "character" => MetadataV2FieldType.String,
            "text" => MetadataV2FieldType.String,
            _ => MetadataV2FieldType.String
        };
    }

    private static List<ColumnInfo> ResolveSelectedColumnsForValidation(
        List<ColumnInfo> columns,
        IReadOnlyList<string> selected,
        List<TablePublishValidationCheck> checks)
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

            if (!lookup.TryGetValue(field.Trim(), out var column))
            {
                checks.Add(Error(
                    "selected-field",
                    $"Field '{field}' was not found on the source table.",
                    field,
                    null));
                continue;
            }

            if (seen.Add(column.Name))
            {
                result.Add(column);
            }
        }

        return result;
    }

    private static string? ResolveGeometryColumnForValidation(
        TableInfo tableInfo,
        string? requestedGeometryColumn,
        List<TablePublishValidationCheck> checks)
    {
        var discoveredGeometryColumn = tableInfo.GeometryColumn;
        var geometryColumn = string.IsNullOrWhiteSpace(requestedGeometryColumn)
            ? discoveredGeometryColumn
            : requestedGeometryColumn.Trim();

        if (string.IsNullOrWhiteSpace(discoveredGeometryColumn))
        {
            checks.Add(Error("geometry-column", "Source table does not expose a geometry column."));
            return geometryColumn;
        }

        if (string.IsNullOrWhiteSpace(geometryColumn))
        {
            checks.Add(Error("geometry-column", "Geometry column is required.", discoveredGeometryColumn, null));
            return geometryColumn;
        }

        if (!geometryColumn.Equals(discoveredGeometryColumn, StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Error(
                "geometry-column",
                "Requested geometry column does not match the discovered PostGIS geometry column.",
                discoveredGeometryColumn,
                geometryColumn));
            return geometryColumn;
        }

        checks.Add(Pass("geometry-column", $"Geometry column '{geometryColumn}' exists."));
        return geometryColumn;
    }

    private static string? ResolveGeometryTypeForValidation(
        TableInfo tableInfo,
        List<TablePublishValidationCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(tableInfo.GeometryType))
        {
            checks.Add(Error("geometry-type", "Source table does not report a geometry type."));
            return null;
        }

        try
        {
            var normalized = NormalizeGeometryType(tableInfo.GeometryType);
            checks.Add(Pass("geometry-type", $"Geometry type '{normalized}' is supported."));
            return normalized;
        }
        catch (LayerPublishingException)
        {
            checks.Add(Error(
                "geometry-type",
                $"Geometry type '{tableInfo.GeometryType}' is not supported.",
                null,
                tableInfo.GeometryType));
            return tableInfo.GeometryType;
        }
    }

    private static string? ResolvePrimaryKeyForValidation(
        List<ColumnInfo> columns,
        List<ColumnInfo> selectedColumns,
        string? requestedPrimaryKey,
        List<TablePublishValidationCheck> checks)
    {
        var primaryKeyName = ResolvePrimaryKeyName(selectedColumns, requestedPrimaryKey);
        if (string.IsNullOrWhiteSpace(primaryKeyName))
        {
            checks.Add(Error(
                "primary-key",
                "Primary key is required. Select an integer source column or choose an object id strategy."));
            return null;
        }

        var primaryKeyColumn = selectedColumns.FirstOrDefault(col =>
            string.Equals(col.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
        if (primaryKeyColumn == null)
        {
            var existsInTable = columns.Any(col =>
                string.Equals(col.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
            checks.Add(Error(
                "primary-key",
                existsInTable
                    ? $"Primary key field '{primaryKeyName}' must be included in selected fields."
                    : $"Primary key field '{primaryKeyName}' was not found on the source table.",
                primaryKeyName,
                existsInTable ? "not selected" : null));
            return primaryKeyName;
        }

        var primaryKeyType = MapPostgresType(primaryKeyColumn.DataType);
        if (primaryKeyType is not MetadataV2FieldType.Integer and not MetadataV2FieldType.BigInteger)
        {
            checks.Add(Error(
                "primary-key-type",
                "Primary key must be an integer column.",
                "Integer or BigInteger",
                primaryKeyColumn.DataType));
            return primaryKeyColumn.Name;
        }

        checks.Add(Pass("primary-key", $"Primary key column '{primaryKeyColumn.Name}' is publishable."));
        return primaryKeyColumn.Name;
    }

    private static int? ResolveTargetSridForValidation(
        int? tableSrid,
        int? serviceSrid,
        int? requestedTargetSrid,
        List<TablePublishValidationCheck> checks)
    {
        if (tableSrid is null or <= 0)
        {
            checks.Add(Error(
                "source-srid",
                "Source table does not report a valid SRID.",
                null,
                tableSrid?.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            checks.Add(Pass("source-srid", $"Source table SRID is {tableSrid.Value}."));
        }

        var targetSrid = serviceSrid ?? requestedTargetSrid ?? tableSrid;
        if (targetSrid is null or <= 0)
        {
            checks.Add(Error(
                "target-srid",
                "Target SRID could not be resolved from the request, service, or source table."));
            return targetSrid;
        }

        checks.Add(Pass("target-srid", $"Target SRID is {targetSrid.Value}."));

        if (serviceSrid.HasValue && requestedTargetSrid.HasValue && requestedTargetSrid.Value != serviceSrid.Value)
        {
            checks.Add(Warning(
                "service-srid-authoritative",
                "Existing service projection overrides the requested target SRID.",
                serviceSrid.Value.ToString(CultureInfo.InvariantCulture),
                requestedTargetSrid.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (tableSrid is > 0 && tableSrid.Value != targetSrid.Value)
        {
            checks.Add(Warning(
                "source-srid-transform",
                "Source geometries will be transformed to the target service projection during publish.",
                targetSrid.Value.ToString(CultureInfo.InvariantCulture),
                tableSrid.Value.ToString(CultureInfo.InvariantCulture)));
        }

        return targetSrid;
    }

    private static void AddGeometryHealthChecks(
        GeometryHealth? health,
        List<TablePublishValidationCheck> checks)
    {
        if (health is not { } value)
        {
            checks.Add(Error("geometry-scan", "Geometry validation could not inspect the source table."));
            return;
        }

        if (value.FeatureCount == 0)
        {
            checks.Add(Error("feature-count", "Source table is empty."));
        }
        else
        {
            checks.Add(Pass(
                "feature-count",
                $"Source table contains {value.FeatureCount.ToString(CultureInfo.InvariantCulture)} row(s)."));
        }

        if (value.NullGeometryCount > 0)
        {
            checks.Add(Warning(
                "null-geometries",
                "Some source rows have NULL geometry and will publish without geometry.",
                "0",
                value.NullGeometryCount.ToString(CultureInfo.InvariantCulture)));
        }

        if (value.InvalidGeometryCount > 0)
        {
            checks.Add(Error(
                "invalid-geometries",
                "Source table contains invalid geometries.",
                "0",
                value.InvalidGeometryCount.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            checks.Add(Pass("invalid-geometries", "No invalid geometries were found."));
        }

        if (value.DistinctGeometryTypeCount > 1)
        {
            checks.Add(Error(
                "mixed-geometry",
                "Source table contains mixed geometry types.",
                "1",
                value.DistinctGeometryTypeCount.ToString(CultureInfo.InvariantCulture)));
        }
        else if (value.DistinctGeometryTypeCount == 1)
        {
            checks.Add(Pass("mixed-geometry", "Source table uses a single geometry type."));
        }

        if (value.MinSrid.HasValue && value.MaxSrid.HasValue && value.MinSrid.Value != value.MaxSrid.Value)
        {
            checks.Add(Error(
                "mixed-srid",
                "Source table contains mixed geometry SRIDs.",
                value.MinSrid.Value.ToString(CultureInfo.InvariantCulture),
                value.MaxSrid.Value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static TablePublishValidationResult BuildValidationResult(
        TablePublishValidationRequest request,
        string serviceName,
        TableInfo? tableInfo,
        ResolvedPublishValidation? resolved,
        GeometryHealth? geometryHealth,
        IReadOnlyCollection<TablePublishValidationCheck> checks)
    {
        var hasErrors = checks.Any(check => check.Severity == SeverityError);
        var hasWarnings = checks.Any(check => check.Severity == SeverityWarning);

        return new TablePublishValidationResult
        {
            IsValid = !hasErrors,
            Status = hasErrors ? "invalid" : hasWarnings ? "warning" : "valid",
            Schema = request.Schema?.Trim() ?? string.Empty,
            Table = request.Table?.Trim() ?? string.Empty,
            ServiceName = serviceName,
            LayerName = request.LayerName,
            GeometryColumn = resolved?.GeometryColumn ?? request.GeometryColumn ?? tableInfo?.GeometryColumn,
            GeometryType = resolved?.GeometryType ?? tableInfo?.GeometryType,
            PrimaryKey = resolved?.PrimaryKey ?? request.PrimaryKey,
            ObjectIdStrategy = ResolveObjectIdStrategy(resolved?.PrimaryKey, checks),
            SourceSrid = tableInfo?.Srid,
            ServiceSrid = resolved?.ServiceSrid,
            TargetSrid = resolved?.TargetSrid,
            EstimatedRows = tableInfo?.EstimatedRows,
            FeatureCount = geometryHealth?.FeatureCount,
            NullGeometryCount = geometryHealth?.NullGeometryCount,
            InvalidGeometryCount = geometryHealth?.InvalidGeometryCount,
            Fields = BuildValidationFields(tableInfo?.Columns ?? [], request.Fields),
            Checks = checks.ToArray()
        };
    }

    private static void ThrowIfPublishValidationFailed(TablePublishValidationResult validation)
    {
        var errors = validation.Checks
            .Where(check => check.Severity == SeverityError)
            .ToArray();
        if (errors.Length == 0)
        {
            return;
        }

        var blockingError = errors.FirstOrDefault(check => check.Code != LayerConflictCheckCode);
        if (blockingError != null)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                BuildPublishValidationFailureMessage(blockingError));
        }

        var conflictLayerId = errors
            .Where(static check => check.Code == LayerConflictCheckCode)
            .Select(static check =>
                int.TryParse(check.Actual, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : (int?)null)
            .FirstOrDefault(static layerId => layerId.HasValue);

        throw new LayerPublishingException(
            LayerPublishingErrorKind.Conflict,
            $"Layer already exists for table '{validation.Schema}.{validation.Table}'.",
            conflictLayerId);
    }

    private static string BuildPublishValidationFailureMessage(TablePublishValidationCheck check)
        => $"Table validation failed ({check.Code}): {check.Message}";

    private static string ResolveObjectIdStrategy(
        string? primaryKey,
        IReadOnlyCollection<TablePublishValidationCheck> checks)
    {
        var hasPrimaryKeyError = checks.Any(check =>
            check.Severity == SeverityError &&
            (check.Code == "primary-key" || check.Code == "primary-key-type"));
        if (hasPrimaryKeyError)
        {
            return UnsupportedSourcePrimaryKeyObjectIdStrategy;
        }

        var hasPrimaryKeyPass = checks.Any(check =>
            check.Severity == SeverityPass && check.Code == "primary-key");
        if (!string.IsNullOrWhiteSpace(primaryKey) && hasPrimaryKeyPass)
        {
            return SourceIntegerPrimaryKeyObjectIdStrategy;
        }

        return UnresolvedObjectIdStrategy;
    }

    private static TablePublishValidationField[] BuildValidationFields(
        List<ColumnInfo> columns,
        IReadOnlyList<string> selected)
    {
        var selectedSet = selected == null || selected.Count == 0
            ? null
            : new HashSet<string>(
                selected.Where(field => !string.IsNullOrWhiteSpace(field)).Select(field => field.Trim()),
                StringComparer.OrdinalIgnoreCase);

        return columns
            .Select(column => new TablePublishValidationField
            {
                Name = column.Name,
                DataType = column.DataType,
                FieldType = MapPostgresType(column.DataType).ToString(),
                IsNullable = column.IsNullable,
                IsPrimaryKey = column.IsPrimaryKey,
                IsSelected = selectedSet == null || selectedSet.Contains(column.Name),
                MaxLength = column.MaxLength
            })
            .ToArray();
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
            "GEOMETRY" => GeometryType.GeometryCollection,
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

    private static bool IsSafeIdentifier(string value) => SchemaSearchPath.IsValidIdentifier(value);

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

    private static async Task<LayerExtentInsert?> ReadLayerExtentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        string geometryColumn,
        int sourceSrid,
        CancellationToken cancellationToken)
    {
        var qualifiedTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var quotedGeometryColumn = QuoteIdentifier(geometryColumn);
        var normalizedSourceSrid = sourceSrid > 0 ? sourceSrid : CatalogExtentSrid;
        var sql = $"""
            WITH source_geometries AS (
                SELECT {quotedGeometryColumn}::geometry AS geom
                FROM {qualifiedTable}
                WHERE {quotedGeometryColumn} IS NOT NULL
            ),
            catalog_geometries AS (
                SELECT
                    CASE
                        WHEN geom IS NULL OR ST_IsEmpty(geom) THEN NULL
                        WHEN COALESCE(NULLIF(ST_SRID(geom), 0), @sourceSrid) = @catalogSrid
                            THEN ST_SetSRID(geom, @catalogSrid)
                        ELSE ST_Transform(
                            ST_SetSRID(geom, COALESCE(NULLIF(ST_SRID(geom), 0), @sourceSrid)),
                            @catalogSrid)
                    END AS geom
                FROM source_geometries
            )
            SELECT ST_XMin(extent), ST_YMin(extent), ST_XMax(extent), ST_YMax(extent)
            FROM (
                SELECT ST_Extent(geom) AS extent
                FROM catalog_geometries
                WHERE geom IS NOT NULL
            ) AS extent_query;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@sourceSrid", normalizedSourceSrid);
        command.Parameters.AddWithValue("@catalogSrid", CatalogExtentSrid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return new LayerExtentInsert(
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            CatalogExtentSrid);
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

    private static async Task<LayerExtentRefreshLayerResult?> RefreshLayerExtentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        CancellationToken cancellationToken)
    {
        var metadata = await GetLayerExtentRefreshMetadataAsync(
                connection,
                transaction,
                layerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (metadata == null)
        {
            return null;
        }

        var extent = await ReadLayerExtentAsync(
                connection,
                transaction,
                metadata.Schema,
                metadata.Table,
                metadata.GeometryColumn,
                metadata.SourceSrid,
                cancellationToken)
            .ConfigureAwait(false);

        const string sql = """
            UPDATE honua.layers
            SET extent = CASE
                WHEN @extentMinX IS NULL THEN NULL
                ELSE ST_MakeEnvelope(@extentMinX, @extentMinY, @extentMaxX, @extentMaxY, @extentSrid)
            END
            WHERE layer_id = @layerId;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);
        AddNullableDouble(command, "@extentMinX", extent?.MinX);
        AddNullableDouble(command, "@extentMinY", extent?.MinY);
        AddNullableDouble(command, "@extentMaxX", extent?.MaxX);
        AddNullableDouble(command, "@extentMaxY", extent?.MaxY);
        command.Parameters.AddWithValue("@extentSrid", extent?.Srid ?? CatalogExtentSrid);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new LayerExtentRefreshLayerResult
        {
            LayerId = metadata.LayerId,
            LayerName = metadata.LayerName,
            HasExtent = extent != null,
            ExtentSrid = extent?.Srid
        };
    }

    private static async Task UpdateServiceExtentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH extent_box AS (
                SELECT ST_Extent(l.extent) AS box
                FROM honua.service_layers sl
                INNER JOIN honua.layers l
                    ON l.layer_id = sl.layer_id
                WHERE sl.service_name = @serviceName
                  AND l.enabled = TRUE
                  AND l.extent IS NOT NULL
            ),
            computed_extent AS (
                SELECT
                    CASE
                        WHEN box IS NULL THEN NULL
                        ELSE ST_MakeEnvelope(
                            ST_XMin(box),
                            ST_YMin(box),
                            ST_XMax(box),
                            ST_YMax(box),
                            @catalogSrid)
                    END AS extent
                FROM extent_box
            )
            UPDATE honua.services AS service
            SET service_extent = computed_extent.extent,
                updated_at = NOW()
            FROM computed_extent
            WHERE service.service_name = @serviceName;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        command.Parameters.AddWithValue("@catalogSrid", CatalogExtentSrid);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<LayerExtentRefreshMetadata?> GetLayerExtentRefreshMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                layer_id,
                layer_name,
                table_schema,
                table_name,
                geometry_column,
                COALESCE(NULLIF(storage_srid, 0), NULLIF(srid, 0), @catalogSrid) AS source_srid
            FROM honua.layers
            WHERE layer_id = @layerId;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);
        command.Parameters.AddWithValue("@catalogSrid", CatalogExtentSrid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(4))
        {
            return null;
        }

        return new LayerExtentRefreshMetadata(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5));
    }

    private static string BuildCanonicalGeometryExpression(string sourceGeometry)
    {
        return $"""
            CASE
                WHEN {sourceGeometry} IS NULL THEN NULL
                WHEN ST_SRID({sourceGeometry}::geometry) = 0
                    THEN ST_SetSRID({sourceGeometry}::geometry, @srid)
                WHEN ST_SRID({sourceGeometry}::geometry) = @srid
                    THEN {sourceGeometry}::geometry
                ELSE ST_Transform({sourceGeometry}::geometry, @srid)
            END
            """;
    }

    private static string BuildAttributesExpression(IReadOnlyList<ColumnInfo> attributeColumns)
    {
        if (attributeColumns.Count == 0)
        {
            return "'{}'::jsonb";
        }

        if (attributeColumns.Count <= MaxJsonbBuildObjectPairs)
        {
            return BuildAttributesExpressionChunk(attributeColumns);
        }

        var chunks = attributeColumns
            .Chunk(MaxJsonbBuildObjectPairs)
            .Select(BuildAttributesExpressionChunk);
        return string.Join(" || ", chunks);
    }

    private static string BuildAttributesExpressionChunk(IReadOnlyList<ColumnInfo> attributeColumns)
    {
        var parts = new List<string>(attributeColumns.Count * 2);
        foreach (var column in attributeColumns)
        {
            parts.Add(QuoteLiteral(column.Name));
            parts.Add($"src.{QuoteIdentifier(column.Name)}");
        }

        return $"jsonb_build_object({string.Join(", ", parts)})";
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteLiteral(string literal)
        => "'" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void AddNullableDouble(NpgsqlCommand command, string name, double? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Double);
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static ColumnInfo? FindColumn(TableInfo tableInfo, string columnName)
        => tableInfo.Columns.FirstOrDefault(column =>
            column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

    private static async Task<GeometryHealth?> InspectGeometryHealthAsync(
        string connectionString,
        string schema,
        string table,
        string geometryColumn,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sourceTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var sourceGeometry = QuoteIdentifier(geometryColumn);
        var sql = $"""
            WITH source AS (
                SELECT {sourceGeometry}::geometry AS geom
                FROM {sourceTable}
            )
            SELECT
                COUNT(*)::bigint,
                COUNT(*) FILTER (WHERE geom IS NULL)::bigint,
                COUNT(*) FILTER (WHERE geom IS NOT NULL AND NOT ST_IsValid(geom))::bigint,
                COUNT(DISTINCT GeometryType(geom)) FILTER (WHERE geom IS NOT NULL)::int,
                MIN(NULLIF(ST_SRID(geom), 0)) FILTER (WHERE geom IS NOT NULL)::int,
                MAX(NULLIF(ST_SRID(geom), 0)) FILTER (WHERE geom IS NOT NULL)::int
            FROM source;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new GeometryHealth(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5));
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

    private static async Task AddExistingLayerCheckAsync(
        string connectionString,
        string schema,
        string table,
        List<TablePublishValidationCheck> checks,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HonuaTableExistsAsync(connection, "layers", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string sql = """
            SELECT layer_id
            FROM honua.layers
            WHERE table_schema = @schema
              AND table_name = @table
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result == null || result == DBNull.Value)
        {
            checks.Add(Pass("layer-conflict", "No existing layer is mapped to this source table."));
            return;
        }

        checks.Add(Error(
            "layer-conflict",
            "A layer already exists for this source table.",
            null,
            Convert.ToString(result, CultureInfo.InvariantCulture)));
    }

    private static async Task<bool> HonuaTableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'honua'
                  AND table_name = @tableName
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool exists && exists;
    }

    private static TablePublishValidationCheck Pass(
        string code,
        string message,
        string? expected = null,
        string? actual = null)
        => new()
        {
            Code = code,
            Severity = SeverityPass,
            Message = message,
            Expected = expected,
            Actual = actual
        };

    private static TablePublishValidationCheck Warning(
        string code,
        string message,
        string? expected = null,
        string? actual = null)
        => new()
        {
            Code = code,
            Severity = SeverityWarning,
            Message = message,
            Expected = expected,
            Actual = actual
        };

    private static TablePublishValidationCheck Error(
        string code,
        string message,
        string? expected = null,
        string? actual = null)
        => new()
        {
            Code = code,
            Severity = SeverityError,
            Message = message,
            Expected = expected,
            Actual = actual
        };

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
        object? DefaultValue = null);

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
