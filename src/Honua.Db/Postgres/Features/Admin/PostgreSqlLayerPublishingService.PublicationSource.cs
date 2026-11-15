// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private void ValidatePublicationRequest(LayerPublishRequest request)
    {
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

        if (!LayerPublishSourceMetadataBounds.TryValidate(request, out var sourceMetadataError))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, sourceMetadataError!);
        }

        if (!IsSafeIdentifier(schema) || !IsSafeIdentifier(table))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Schema or table contains invalid characters.");
        }

        if (!CommittedSinkSchemaGate.Allows(_schemaConfiguration, _schemaContext?.CurrentSchema, schema))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.NotFound,
                $"Table '{schema}.{table}' was not found.");
        }

    }

    private sealed record PublicationSourceRequest
    {
        public required string ConnectionString { get; init; }
        public required LayerPublishRequest Request { get; init; }
        public required string ServiceName { get; init; }
        public ManagedCopyTargetIdentity? ManagedCopyTarget { get; init; }
    }

    private sealed record PreparedPublicationSource
    {
        public required TableInfo Table { get; init; }
        public string? GeometryColumn { get; init; }
        public required string GeometryType { get; init; }
        public required int Srid { get; init; }
        public required int StorageSrid { get; init; }
        public required List<ColumnInfo> SelectedColumns { get; init; }
        public ColumnInfo? PrimaryKeyColumn { get; init; }
        public required List<LayerFieldInsert> Fields { get; init; }
        public required List<ColumnInfo> AttributeColumns { get; init; }
        public string? ManagedSchema { get; init; }
    }

    private async Task<PreparedPublicationSource> PreparePublicationSourceAsync(
        NpgsqlConnection connection, PublicationSourceRequest source, CancellationToken cancellationToken)
    {
        var request = source.Request;
        var table = await ValidatePublicationSourceAsync(connection, source, cancellationToken).ConfigureAwait(false);
        var (geometryColumn, geometryType) = ResolvePublicationGeometry(table, request);
        var serviceSrid = await ResolveExistingServiceSridAsync(connection, source.ServiceName, cancellationToken)
            .ConfigureAwait(false);
        var srid = serviceSrid ?? request.Srid ?? table.Srid ?? 4326;
        srid = srid > 0 ? srid : 4326;
        var columns = SelectPublicationColumns(table, request);
        var primaryKey = ResolvePublicationPrimaryKey(table, columns, request);
        var prepared = new PreparedPublicationSource
        {
            Table = table,
            GeometryColumn = geometryColumn,
            GeometryType = geometryType,
            Srid = srid,
            StorageSrid = table.Srid is > 0 ? table.Srid.Value : srid,
            SelectedColumns = columns,
            PrimaryKeyColumn = primaryKey,
            Fields = [],
            AttributeColumns = []
        };
        return await CompletePublicationSourceAsync(connection, new PublicationSourceCompletion(source, prepared),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TableInfo> ValidatePublicationSourceAsync(
        NpgsqlConnection connection, PublicationSourceRequest source, CancellationToken cancellationToken)
    {
        var request = source.Request;
        var isManagedStore = request.StorageMode == LayerStorageMode.Managed || request.CreateEditableCopy;
        var validation = await ValidateTableForPublishCoreAsync(
                source.ConnectionString,
                new TablePublishValidationRequest
                {
                    AllowEmptyTable = request.AllowEmptyTable,
                    ManagedStore = isManagedStore,
                    Schema = request.Schema.Trim(),
                    Table = request.Table.Trim(),
                    LayerName = request.LayerName,
                    ServiceName = source.ServiceName,
                    TargetSrid = request.Srid,
                    GeometryColumn = request.GeometryColumn,
                    PrimaryKey = request.PrimaryKey,
                    Fields = request.Fields,
                    ConnectionEndpointFingerprint = request.ConnectionEndpointFingerprint,
                    CommittedSinkPublication = request.CommittedSinkPublication
                },
                cancellationToken,
                connection)
            .ConfigureAwait(false);
        ThrowIfPublishValidationFailed(validation, request.CreateEditableCopy);

        return await ResolveTableInfoForValidationAsync(connection, request.Schema.Trim(), request.Table.Trim(), cancellationToken)
            ?? throw new LayerPublishingException(
                LayerPublishingErrorKind.NotFound,
                $"Table '{request.Schema.Trim()}.{request.Table.Trim()}' was not found.");

    }

    private static (string? Column, string Type) ResolvePublicationGeometry(TableInfo tableInfo, LayerPublishRequest request)
    {
        var geometryColumn = string.IsNullOrWhiteSpace(request.GeometryColumn)
            ? tableInfo.GeometryColumn
            : request.GeometryColumn;

        if (string.IsNullOrWhiteSpace(geometryColumn) &&
            (!string.IsNullOrWhiteSpace(request.GeometryType) || request.HasZ || request.HasM))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "An attribute-only table cannot declare geometry type or dimensions.");
        }

        var geometryTypeRaw = string.IsNullOrWhiteSpace(request.GeometryType)
            ? tableInfo.GeometryType
            : request.GeometryType;

        if (!string.IsNullOrWhiteSpace(geometryColumn) && string.IsNullOrWhiteSpace(geometryTypeRaw))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Geometry type is required.");
        }

        var geometryType = string.IsNullOrWhiteSpace(geometryColumn) ? "None" : NormalizeGeometryType(geometryTypeRaw!);

        // A legacy client may request the single-part spelling for a source that
        // is physically constrained to its multi family. Publish the stored type.
        var sourceType = tableInfo.GeometryType?.ToUpperInvariant();
        geometryType = (geometryType, sourceType) switch
        {
            ("Point", "MULTIPOINT") => "MultiPoint",
            ("LineString", "MULTILINESTRING") => "MultiLineString",
            ("Polygon", "MULTIPOLYGON") => "MultiPolygon",
            _ => geometryType
        };

        return (geometryColumn, geometryType);
    }

    private static List<ColumnInfo> SelectPublicationColumns(TableInfo tableInfo, LayerPublishRequest request)
    {
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

        return selectedColumns;
    }

    private static ColumnInfo? ResolvePublicationPrimaryKey(TableInfo table, List<ColumnInfo> selectedColumns,
        LayerPublishRequest request)
    {
        var isManagedStore = request.StorageMode == LayerStorageMode.Managed || request.CreateEditableCopy;
        var primaryKeyName = ResolvePrimaryKeyName(selectedColumns, request.PrimaryKey);
        if (primaryKeyName is null && (!isManagedStore || request.CreateEditableCopy))
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                "Primary key is required.");
        }

        var primaryKeyColumn = primaryKeyName is null
            ? null
            : selectedColumns.FirstOrDefault(col =>
                string.Equals(col.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
        if (primaryKeyColumn == null && (!isManagedStore || request.CreateEditableCopy))
        {
            var existsInTable = table.Columns.Any(col =>
                string.Equals(col.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
            var message = existsInTable
                ? $"Primary key field '{primaryKeyName}' must be included in selected fields."
                : $"Primary key field '{primaryKeyName}' was not found on the source table.";
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, message);
        }

        // A managed-store layer takes its identity from the managed store, so its source key
        // may be of any type, unselected or absent.
        if (!isManagedStore || request.CreateEditableCopy)
        {
            var primaryKeyType = MapPostgresType(primaryKeyColumn!.DataType);
            if (primaryKeyType is not MetadataV2FieldType.Integer and not MetadataV2FieldType.BigInteger)
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Validation,
                    "Primary key must be an integer column.");
            }
        }

        return primaryKeyColumn;
    }

    private sealed record PublicationSourceCompletion(PublicationSourceRequest Source, PreparedPublicationSource Prepared);

    private async Task<PreparedPublicationSource> CompletePublicationSourceAsync(NpgsqlConnection connection,
        PublicationSourceCompletion completion, CancellationToken cancellationToken)
    {
        var source = completion.Source;
        var prepared = completion.Prepared;
        var request = source.Request;
        var isManagedStore = request.StorageMode == LayerStorageMode.Managed || request.CreateEditableCopy;
        var fields = isManagedStore && !request.CreateEditableCopy
            ? BuildManagedLayerFields(prepared.SelectedColumns, prepared.PrimaryKeyColumn, prepared.GeometryColumn, request.FieldDomains)
            : BuildLayerFields(prepared.SelectedColumns, prepared.PrimaryKeyColumn!, prepared.GeometryColumn, request.FieldDomains);
        var attributeColumns = isManagedStore && !request.CreateEditableCopy
            ? SelectManagedAttributeColumns(prepared.SelectedColumns, prepared.PrimaryKeyColumn, prepared.GeometryColumn)
            : prepared.SelectedColumns;

        var managedSchema = request.CreateEditableCopy
            ? await ValidateManagedCopyTargetAsync(connection, fields, source.ManagedCopyTarget!, cancellationToken).ConfigureAwait(false)
            : null;
        if (request.CreateEditableCopy)
        {
            var primaryIndex = fields.FindIndex(field => field.Name.Equals(prepared.PrimaryKeyColumn!.Name, StringComparison.OrdinalIgnoreCase));
            fields[primaryIndex] = fields[primaryIndex] with { Type = MetadataV2FieldType.BigInteger };
            fields.Add(new LayerFieldInsert(ManagedSourceIdField, MetadataV2FieldType.BigInteger, null, true,
                "Original source object ID for migration identity mapping; null for newly created features."));
        }

        // Reject a bad temporal column before any rows are copied; the graph projection resolves it again.
        _ = ResolveTemporalColumnForPublish(request.TemporalColumn, fields);

        return prepared with { Fields = fields, AttributeColumns = attributeColumns, ManagedSchema = managedSchema };
    }
}
