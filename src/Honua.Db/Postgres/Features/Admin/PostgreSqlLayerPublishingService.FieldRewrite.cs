// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    /// <inheritdoc />
    public async Task<PublishedLayerSummary?> RewritePublishedLayerFieldsAsync(
        string connectionString,
        int layerId,
        LayerPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsSafeIdentifier(request.Schema) || !IsSafeIdentifier(request.Table))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Schema or table contains invalid characters.");
        }

        var layer = await ReadLayerFieldRewriteTargetAsync(connectionString, layerId, cancellationToken)
            .ConfigureAwait(false);
        if (layer is null
            || !string.Equals(layer.Schema, request.Schema, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(layer.Table, request.Table, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await ValidateTenantAccessAsync(
                null,
                new HashSet<int> { layerId },
                new LayerStorageScope(request.ConnectionId),
                cancellationToken)
            .ConfigureAwait(false);

        var tableInfo = await ResolveTableInfoForValidationAsync(
                connectionString,
                request.Schema,
                request.Table,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new LayerPublishingException(
                LayerPublishingErrorKind.NotFound,
                $"Table '{request.Schema}.{request.Table}' was not found.");

        var geometryColumn = string.IsNullOrWhiteSpace(request.GeometryColumn)
            ? tableInfo.GeometryColumn
            : request.GeometryColumn;
        var selectedColumns = ResolveSelectedColumns(tableInfo.Columns, request.Fields);
        if (selectedColumns.Count == 0)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "No fields selected for publishing.");
        }

        var primaryKeyName = ResolvePrimaryKeyName(selectedColumns, request.PrimaryKey ?? layer.PrimaryKey);
        var primaryKeyColumn = primaryKeyName is null
            ? null
            : selectedColumns.FirstOrDefault(column =>
                string.Equals(column.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
        if (primaryKeyColumn is null)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Primary key is required.");
        }

        var primaryKeyType = MapPostgresType(primaryKeyColumn.DataType);
        if (primaryKeyType is not MetadataV2FieldType.Integer and not MetadataV2FieldType.BigInteger)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Primary key must be an integer column.");
        }

        var fields = BuildLayerFields(selectedColumns, primaryKeyColumn, geometryColumn, request.FieldDomains);
        var schemaFields = fields
            .Select(field => MapLayerFieldToMetadataV2(field, primaryKeyColumn.Name, geometryColumn))
            .ToArray();
        var geometryType = string.IsNullOrWhiteSpace(geometryColumn)
            ? null
            : string.IsNullOrWhiteSpace(request.GeometryType)
                ? tableInfo.GeometryType
                : request.GeometryType;

        MetadataV2GraphMutation? mutation = null;
        try
        {
            mutation = await RetryMetadataGraphConflictAsync(() => RewritePublishedLayerGraphAsync(
                    layerId,
                    request,
                    schemaFields,
                    primaryKeyColumn.Name,
                    geometryColumn,
                    geometryType,
                    cancellationToken))
                .ConfigureAwait(false);
            await ReplaceLayerFieldRowsAsync(connectionString, layerId, fields, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (mutation is not null)
        {
            await CompensateMetadataV2MutationAsync(mutation, exception).ConfigureAwait(false);
            throw;
        }

        return new PublishedLayerSummary
        {
            LayerId = layer.LayerId,
            LayerName = layer.LayerName,
            Description = layer.Description,
            Schema = layer.Schema,
            Table = layer.Table,
            GeometryType = layer.GeometryType,
            Srid = layer.Srid,
            PrimaryKey = primaryKeyColumn.Name,
            FieldCount = fields.Count,
            Enabled = layer.Enabled,
            ServiceName = NormalizeServiceName(request.ServiceName)
        };
    }

    /// <summary>
    /// Projects a replaced source schema onto an existing published resource without
    /// changing its identity, storage binding, or unrelated display settings.
    /// </summary>
    internal static MetadataV2Resource ApplyReplacementFieldMetadata(
        MetadataV2Resource resource,
        IReadOnlyList<MetadataV2Field> schemaFields,
        LayerPublishRequest request,
        string primaryKey,
        string? geometryColumn,
        string? geometryType,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(schemaFields);
        ArgumentNullException.ThrowIfNull(request);
        var declared = new HashSet<string>(
            schemaFields.Select(static field => field.Name),
            StringComparer.OrdinalIgnoreCase);
        var inserts = schemaFields
            .Select(static field => new LayerFieldInsert(
                field.Name,
                field.Type,
                field.Length,
                field.Nullable,
                field.Description,
                Domain: field.Domain))
            .ToList();

        var display = resource.Display;
        if (display is not null)
        {
            var displayField = declared.Contains(display.DisplayField ?? string.Empty)
                ? display.DisplayField
                : schemaFields.FirstOrDefault(field =>
                    field.Type is not MetadataV2FieldType.Geometry and not MetadataV2FieldType.Geography
                    && !string.Equals(field.Name, primaryKey, StringComparison.OrdinalIgnoreCase))?.Name;
            display = display with
            {
                DisplayField = displayField,
                HasZ = request.HasZ,
                HasM = request.HasM
            };
        }

        var spatial = resource.Spatial;
        var resourceType = resource.Type;
        if (string.IsNullOrWhiteSpace(geometryColumn))
        {
            spatial = null;
            if (resourceType == MetadataV2ResourceType.FeatureDataset)
            {
                resourceType = MetadataV2ResourceType.Table;
            }
        }
        else if (spatial is not null)
        {
            var mappedGeometry = string.IsNullOrWhiteSpace(geometryType)
                ? spatial.GeometryType
                : MapMetadataV2GeometryType(NormalizeGeometryType(geometryType));
            spatial = spatial with
            {
                PrimaryGeometryField = geometryColumn,
                GeometryType = mappedGeometry
            };
        }

        var temporal = resource.Temporal;
        if (temporal is not null)
        {
            var start = KeepTemporalField(schemaFields, temporal.StartTimeField);
            var end = KeepTemporalField(schemaFields, temporal.EndTimeField);
            var track = KeepDeclaredField(declared, temporal.TrackIdField);
            temporal = start is null && end is null && track is null
                ? null
                : temporal with { StartTimeField = start, EndTimeField = end, TrackIdField = track };
        }

        var previousEditing = resource.Editing;
        var globalId = ResolveReplacementGlobalId(schemaFields, request.GlobalIdField, previousEditing?.GlobalIdField);
        MetadataV2ResourceEditing? editing = previousEditing is null && globalId is null && !request.SupportsAttachments
            ? null
            : (previousEditing ?? new MetadataV2ResourceEditing { CanModify = false, SupportsRelatedRecords = false }) with
            {
                GlobalIdField = globalId,
                SupportsAttachments = request.SupportsAttachments || previousEditing?.SupportsAttachments == true,
                CreatorField = KeepDeclaredField(declared, previousEditing?.CreatorField),
                CreatedAtField = KeepDeclaredField(declared, previousEditing?.CreatedAtField),
                EditorField = KeepDeclaredField(declared, previousEditing?.EditorField),
                UpdatedAtField = KeepDeclaredField(declared, previousEditing?.UpdatedAtField)
            };

        var symbology = resource.Symbology3D;
        if (symbology is not null)
        {
            var rules = symbology.Rules
                .Where(rule => declared.Contains(rule.Attribute))
                .ToArray();
            if (rules.Length != symbology.Rules.Count)
            {
                symbology = symbology with { Rules = rules };
            }
        }

        var extrusion = resource.Extrusion;
        if (extrusion is not null && MetadataV2ExtrusionValidator.Validate(extrusion, schemaFields).Count > 0)
        {
            extrusion = null;
        }

        return resource with
        {
            Metadata = resource.Metadata with { UpdatedAt = now },
            Type = resourceType,
            SchemaFields = schemaFields,
            Spatial = spatial,
            Temporal = temporal,
            Display = display,
            Editing = editing,
            Subtypes = ResolveSubtypesForPublish(request.Subtypes, inserts),
            AttributeRules = ResolveAttributeRulesForPublish(request.AttributeRules, inserts),
            Symbology3D = symbology,
            Extrusion = extrusion
        };
    }

    private async Task<MetadataV2GraphMutation?> RewritePublishedLayerGraphAsync(
        int layerId,
        LayerPublishRequest request,
        IReadOnlyList<MetadataV2Field> schemaFields,
        string primaryKey,
        string? geometryColumn,
        string? geometryType,
        CancellationToken cancellationToken)
    {
        var (graph, expectedEtag) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        var resource = FindPublishedResource(graph, layerId);
        if (resource is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var updatedResource = ApplyReplacementFieldMetadata(
            resource,
            schemaFields,
            request,
            primaryKey,
            geometryColumn,
            geometryType,
            now);
        var updatedGraph = graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = now,
            Resources = UpsertById(graph.Resources, updatedResource, static item => item.Metadata.Id)
        };
        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Replaced layer field metadata is invalid: " + string.Join("; ", validation.Errors));
        }

        return await PersistMetadataV2MutationAsync(graph, updatedGraph, expectedEtag, cancellationToken)
            .ConfigureAwait(false);
    }

    private static MetadataV2Resource? FindPublishedResource(MetadataV2Graph graph, int layerId)
    {
        var binding = graph.StorageBindings.FirstOrDefault(candidate => candidate.StorageLayerId == layerId);
        if (binding is not null)
        {
            var bound = graph.Resources.FirstOrDefault(resource =>
                string.Equals(resource.Metadata.Id, binding.ResourceId, StringComparison.Ordinal));
            if (bound is not null)
            {
                return bound;
            }
        }

        var resourceId = BuildResourceId(layerId);
        return graph.Resources.FirstOrDefault(resource =>
            string.Equals(resource.Metadata.Id, resourceId, StringComparison.Ordinal));
    }

    private static async Task ReplaceLayerFieldRowsAsync(
        string connectionString,
        int layerId,
        List<LayerFieldInsert> fields,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM honua.layer_fields WHERE layer_id = @layerId",
            connection,
            transaction))
        {
            delete.Parameters.AddWithValue("@layerId", layerId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertFieldsAsync(connection, transaction, layerId, fields, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LayerFieldRewriteTarget?> ReadLayerFieldRewriteTargetAsync(
        string connectionString,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                layer_id,
                layer_name,
                description,
                table_schema,
                table_name,
                primary_key_column,
                geometry_type,
                srid,
                enabled,
                COALESCE(storage_options ->> 'managedStore', 'false') = 'true' AS managed_store
            FROM honua.layers
            WHERE layer_id = @layerId;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@layerId", layerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetBoolean(9))
        {
            return null;
        }

        return new LayerFieldRewriteTarget(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetBoolean(8));
    }

    private static string? ResolveReplacementGlobalId(
        IReadOnlyList<MetadataV2Field> fields,
        string? requested,
        string? previous)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = fields.FirstOrDefault(field =>
                field.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)
                && field.Type == MetadataV2FieldType.Uuid);
            return match?.Name;
        }

        if (string.IsNullOrWhiteSpace(previous))
        {
            return null;
        }

        var existing = fields.FirstOrDefault(field =>
            field.Name.Equals(previous, StringComparison.OrdinalIgnoreCase)
            && field.Type == MetadataV2FieldType.Uuid);
        return existing?.Name;
    }

    private static string? KeepDeclaredField(HashSet<string> declared, string? fieldName)
        => fieldName is not null && declared.Contains(fieldName) ? fieldName : null;

    private static string? KeepTemporalField(IReadOnlyList<MetadataV2Field> fields, string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        var field = fields.FirstOrDefault(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        return field?.Type is MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time
            ? field.Name
            : null;
    }

    private sealed record LayerFieldRewriteTarget(
        int LayerId,
        string LayerName,
        string? Description,
        string Schema,
        string Table,
        string? PrimaryKey,
        string GeometryType,
        int Srid,
        bool Enabled);
}
