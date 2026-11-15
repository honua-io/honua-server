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
        var connectionString = source.ConnectionString;
        var schema = request.Schema.Trim();
        var table = request.Table.Trim();
        var serviceName = source.ServiceName;
        var isManagedStore = request.StorageMode == LayerStorageMode.Managed || request.CreateEditableCopy;
        var managedCopyTarget = source.ManagedCopyTarget;
        var validation = await ValidateTableForPublishCoreAsync(
                connectionString,
                new TablePublishValidationRequest
                {
                    AllowEmptyTable = request.AllowEmptyTable,
                    ManagedStore = isManagedStore,
                    Schema = schema,
                    Table = table,
                    LayerName = request.LayerName,
                    ServiceName = serviceName,
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

        var tableInfo = await ResolveTableInfoForValidationAsync(connection, schema, table, cancellationToken)
            ?? throw new LayerPublishingException(
                LayerPublishingErrorKind.NotFound,
                $"Table '{schema}.{table}' was not found.");

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

        var serviceSrid = await ResolveExistingServiceSridAsync(connection, serviceName, cancellationToken)
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
            var existsInTable = columns.Any(col =>
                string.Equals(col.Name, primaryKeyName, StringComparison.OrdinalIgnoreCase));
            var message = existsInTable
                ? $"Primary key field '{primaryKeyName}' must be included in selected fields."
                : $"Primary key field '{primaryKeyName}' was not found on the source table.";
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, message);
        }

        // A managed-store layer takes its identity from the managed store, so its source key
        // may be of any type, unselected or absent (honua-server#4859).
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

        var fields = isManagedStore && !request.CreateEditableCopy
            ? BuildManagedLayerFields(selectedColumns, primaryKeyColumn, geometryColumn, request.FieldDomains)
            : BuildLayerFields(selectedColumns, primaryKeyColumn!, geometryColumn, request.FieldDomains);
        var attributeColumns = isManagedStore && !request.CreateEditableCopy
            ? SelectManagedAttributeColumns(selectedColumns, primaryKeyColumn, geometryColumn)
            : selectedColumns;

        var managedSchema = request.CreateEditableCopy
            ? await ValidateManagedCopyTargetAsync(connection, fields, managedCopyTarget!, cancellationToken).ConfigureAwait(false)
            : null;
        if (request.CreateEditableCopy)
        {
            var primaryIndex = fields.FindIndex(field => field.Name.Equals(primaryKeyColumn!.Name, StringComparison.OrdinalIgnoreCase));
            fields[primaryIndex] = fields[primaryIndex] with { Type = MetadataV2FieldType.BigInteger };
            fields.Add(new LayerFieldInsert(ManagedSourceIdField, MetadataV2FieldType.BigInteger, null, true,
                "Original source object ID for migration identity mapping; null for newly created features."));
        }

        // Reject a bad temporal column before any rows are copied; the graph projection resolves it again.
        _ = ResolveTemporalColumnForPublish(request.TemporalColumn, fields);

        return new PreparedPublicationSource
        {
            Table = tableInfo,
            GeometryColumn = geometryColumn,
            GeometryType = geometryType,
            Srid = srid,
            StorageSrid = storageSrid,
            SelectedColumns = selectedColumns,
            PrimaryKeyColumn = primaryKeyColumn,
            Fields = fields,
            AttributeColumns = attributeColumns,
            ManagedSchema = managedSchema
        };
    }
}
