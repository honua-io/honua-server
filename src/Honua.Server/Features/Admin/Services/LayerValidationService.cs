// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Admin.Models;
using PermanentFilterLanguages = Honua.Core.Features.Metadata.Domain.V2.MetadataV2PermanentFilterLanguages;

namespace Honua.Server.Features.Admin.Services;

internal sealed class LayerValidationService(
    ITableDiscoveryService tableDiscoveryService,
    IAdoNetDatabaseConnectionProvider connectionProvider,
    IFilterExpressionService filterExpressionService,
    IMetadataV2GraphProvider metadataV2GraphProvider)
{
    private const string SeverityPass = "pass";
    private const string SeverityWarning = "warning";
    private const string SeverityError = "error";

    public async Task<ResourceValidationResult<LayerValidationResponse>> ValidateLayerAsync(
        int storageLayerId,
        CancellationToken cancellationToken = default)
    {
        if (storageLayerId < 0)
        {
            return ResourceValidationResult.NotFound<LayerValidationResponse>($"Layer {storageLayerId} not found.");
        }

        var snapshot = await metadataV2GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resolution = snapshot.ResolveStorageResource(storageLayerId);
        if (resolution.IsAmbiguous)
        {
            return ResourceValidationResult.AmbiguousIdentifier<LayerValidationResponse>(resolution.ConflictDetail!);
        }
        var resource = resolution.Resource;
        if (resource is null)
        {
            return ResourceValidationResult.NotFound<LayerValidationResponse>($"Layer {storageLayerId} not found.");
        }

        var checks = new List<LayerValidationCheck>
        {
            Pass("catalog-layer", $"Layer {storageLayerId} exists in the metadata v2 graph.")
        };
        ValidatePermanentFilter(resource, checks);

        if (!snapshot.Index.StorageBindingsByStorageLayerId.TryGetValue(storageLayerId, out var storageBinding))
        {
            checks.Add(Error(
                "storage-mapping",
                "Layer does not define a metadata v2 storage binding."));

            return ResourceValidationResult.Success(BuildResponse(storageLayerId, resource, null, checks));
        }

        var storageMapping = ResolveStorageMapping(resource, storageBinding, checks);
        if (storageMapping == null)
        {
            return ResourceValidationResult.Success(BuildResponse(storageLayerId, resource, null, checks));
        }

        checks.Add(Pass(
            "storage-mapping",
            $"Layer is mapped to '{storageMapping.QualifiedName}'."));

        await using var connection = await connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var tables = await tableDiscoveryService
            .DiscoverPostGisTablesAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        var table = FindTable(tables, storageMapping);
        if (table is null && !string.IsNullOrWhiteSpace(storageMapping.SchemaName))
        {
            table = await tableDiscoveryService.DiscoverNonSpatialTableAsync(
                connection, storageMapping.SchemaName, storageMapping.TableName, cancellationToken).ConfigureAwait(false);
        }
        if (table == null)
        {
            checks.Add(Error(
                "storage-table",
                "Mapped storage table was not found or is excluded from discovery.",
                storageMapping.QualifiedName,
                null));

            return ResourceValidationResult.Success(BuildResponse(storageLayerId, resource, storageMapping, checks));
        }

        checks.Add(Pass(
            "storage-table",
            $"Mapped storage table '{table.Schema}.{table.Table}' is discoverable."));

        ValidatePrimaryKey(storageMapping, table, checks);
        ValidateGeometry(resource, storageMapping, table, checks);
        ValidateSrid(storageMapping, table, checks);
        ValidateDeclaredFields(resource, table, checks);

        return ResourceValidationResult.Success(BuildResponse(storageLayerId, resource, storageMapping, checks));
    }

    private static TableInfo? FindTable(
        IReadOnlyCollection<TableInfo> tables,
        StorageValidationMapping storageMapping)
    {
        if (!string.IsNullOrWhiteSpace(storageMapping.SchemaName))
        {
            return tables.FirstOrDefault(table =>
                table.Schema.Equals(storageMapping.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                table.Table.Equals(storageMapping.TableName, StringComparison.OrdinalIgnoreCase));
        }

        return tables.FirstOrDefault(table =>
            table.Table.Equals(storageMapping.TableName, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidatePrimaryKey(
        StorageValidationMapping storageMapping,
        TableInfo table,
        List<LayerValidationCheck> checks)
    {
        var primaryKeyColumn = FindColumn(table, storageMapping.PrimaryKeyColumn);
        if (primaryKeyColumn == null)
        {
            checks.Add(Error(
                "primary-key-column",
                "Mapped primary key column is missing from the storage table.",
                storageMapping.PrimaryKeyColumn,
                null));
            return;
        }

        checks.Add(Pass(
            "primary-key-column",
            $"Primary key column '{primaryKeyColumn.Name}' exists."));
    }

    private static void ValidateGeometry(
        MetadataV2Object resource,
        StorageValidationMapping storageMapping,
        TableInfo table,
        List<LayerValidationCheck> checks)
    {
        var hasGeometry = resource.ReadGeometryType() != MetadataV2GeometryType.None ||
            resource.FindPrimaryGeometryField() is not null;
        if (!hasGeometry)
        {
            checks.Add(Pass("geometry-column", "Layer is non-spatial."));
            return;
        }

        var expectedGeometryColumn = storageMapping.GeometryColumn;
        if (string.IsNullOrWhiteSpace(expectedGeometryColumn))
        {
            checks.Add(Error(
                "geometry-column",
                "Layer is spatial but does not define a mapped geometry column."));
            return;
        }

        if (string.IsNullOrWhiteSpace(table.GeometryColumn))
        {
            checks.Add(Error(
                "geometry-column",
                "Storage table no longer exposes a geometry column.",
                expectedGeometryColumn,
                null));
            return;
        }

        if (!table.GeometryColumn.Equals(expectedGeometryColumn, StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Error(
                "geometry-column",
                "Storage table geometry column does not match layer metadata.",
                expectedGeometryColumn,
                table.GeometryColumn));
            return;
        }

        checks.Add(Pass(
            "geometry-column",
            $"Geometry column '{table.GeometryColumn}' exists."));
    }

    private static void ValidateSrid(
        StorageValidationMapping storageMapping,
        TableInfo table,
        List<LayerValidationCheck> checks)
    {
        var expectedSrid = storageMapping.StorageSrid;
        var actualSrid = table.Srid;

        if (expectedSrid is null or <= 0)
        {
            checks.Add(Warning(
                "storage-srid",
                "Layer metadata does not define an expected storage SRID.",
                null,
                actualSrid?.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        if (actualSrid is null or <= 0)
        {
            checks.Add(Error(
                "storage-srid",
                "Storage table does not report a valid SRID.",
                expectedSrid.Value.ToString(CultureInfo.InvariantCulture),
                actualSrid?.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        if (actualSrid.Value != expectedSrid.Value)
        {
            checks.Add(Error(
                "storage-srid",
                "Storage table SRID does not match layer storage metadata.",
                expectedSrid.Value.ToString(CultureInfo.InvariantCulture),
                actualSrid.Value.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        checks.Add(Pass(
            "storage-srid",
            $"Storage SRID {actualSrid.Value} matches layer metadata."));
    }

    private static void ValidateDeclaredFields(
        MetadataV2Object resource,
        TableInfo table,
        List<LayerValidationCheck> checks)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) ||
                field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
            {
                continue;
            }

            if (FindColumn(table, field.Name) != null)
            {
                checks.Add(Pass(
                    "declared-field",
                    $"Declared field '{field.Name}' exists.",
                    field.Name,
                    field.Name));
                continue;
            }

            checks.Add(Error(
                "declared-field",
                $"Declared field '{field.Name}' is missing from the storage table.",
                field.Name,
                null));
        }
    }

    private void ValidatePermanentFilter(
        MetadataV2Object resource,
        List<LayerValidationCheck> checks)
    {
        var permanentFilter = resource.PermanentFilter;
        if (permanentFilter == null || string.IsNullOrWhiteSpace(permanentFilter.Expression))
        {
            checks.Add(Pass(
                "permanent-filter",
                "No permanent layer filter is configured."));
            return;
        }

        if (PermanentFilterLanguages.Resolve(permanentFilter.Language) is not { } filterLanguage)
        {
            checks.Add(Error(
                "permanent-filter",
                "Saved permanent filter uses an unsupported language.",
                "arcgis-sql, cql2-text, cql2-json",
                permanentFilter.Language));
            return;
        }

        var parse = filterExpressionService.ParseAndNormalize(filterLanguage, permanentFilter.Expression, resource);
        if (!parse.IsSuccess || parse.Expression is null)
        {
            checks.Add(Error(
                "permanent-filter",
                "Saved permanent filter is invalid.",
                permanentFilter.Expression,
                parse.ErrorMessage ?? "Invalid filter."));
            return;
        }

        var translation = filterExpressionService.Translate(parse.Expression, resource);
        if (!translation.IsSuccess)
        {
            checks.Add(Error(
                "permanent-filter",
                "Saved permanent filter is invalid.",
                permanentFilter.Expression,
                translation.ErrorMessage ?? "Invalid filter."));
            return;
        }

        checks.Add(Pass(
            "permanent-filter",
            "Saved permanent layer filter is valid.",
            permanentFilter.Expression,
            permanentFilter.Language));
    }

    private static StorageValidationMapping? ResolveStorageMapping(
        MetadataV2Object resource,
        MetadataV2StorageBinding storageBinding,
        List<LayerValidationCheck> checks)
    {
        if (storageBinding.StorageType != MetadataV2StorageType.RelationalTable)
        {
            checks.Add(Error(
                "storage-mapping",
                "Storage binding is not a relational table.",
                MetadataV2StorageType.RelationalTable.ToString(),
                storageBinding.StorageType.ToString()));
            return null;
        }

        var (schemaName, tableName) = storageBinding.ResolveRelationalTable();

        var primaryKeyColumn = storageBinding.ReadStringOption("primaryKeyColumn")
            ?? resource.FindPrimaryIdField()?.Name
            ?? FieldNames.ObjectId;
        var geometryColumn = storageBinding.ReadStringOption("geometryColumn")
            ?? resource.FindPrimaryGeometryField()?.Name;
        var storageSrid = storageBinding.ReadIntOption("storageSrid")
            ?? storageBinding.ReadIntOption("srid")
            ?? resource.Spatial?.StorageCrs?.ResolveSrid()
            ?? resource.ReadSrid();

        var errors = ValidateStorageMapping(tableName, primaryKeyColumn, storageSrid);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                checks.Add(Error("storage-mapping", error));
            }
            return null;
        }

        return new StorageValidationMapping(
            tableName!,
            schemaName,
            primaryKeyColumn,
            geometryColumn,
            storageSrid);
    }

    private static List<string> ValidateStorageMapping(
        string? tableName,
        string? primaryKeyColumn,
        int? storageSrid)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(tableName))
        {
            errors.Add("Storage table or view name cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(primaryKeyColumn))
        {
            errors.Add("Storage primary key column cannot be empty");
        }

        if (storageSrid is <= 0)
        {
            errors.Add("Storage SRID must be positive when provided");
        }

        return errors;
    }

    private static ColumnInfo? FindColumn(TableInfo table, string columnName)
        => table.Columns.FirstOrDefault(column =>
            column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

    private static LayerValidationResponse BuildResponse(
        int storageLayerId,
        MetadataV2Object resource,
        StorageValidationMapping? storageMapping,
        IReadOnlyCollection<LayerValidationCheck> checks)
    {
        var hasErrors = checks.Any(check => check.Severity == SeverityError);
        var hasWarnings = checks.Any(check => check.Severity == SeverityWarning);

        return new LayerValidationResponse
        {
            StorageLayerId = storageLayerId,
            LayerName = resource.Metadata.Name,
            IsValid = !hasErrors,
            Status = hasErrors ? "invalid" : hasWarnings ? "warning" : "valid",
            StorageSchema = storageMapping?.SchemaName,
            StorageTable = storageMapping?.TableName,
            CheckedAt = DateTimeOffset.UtcNow,
            Checks = checks.ToArray()
        };
    }

    private static LayerValidationCheck Pass(
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

    private static LayerValidationCheck Warning(
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

    private static LayerValidationCheck Error(
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

    private sealed record StorageValidationMapping(
        string TableName,
        string? SchemaName,
        string PrimaryKeyColumn,
        string? GeometryColumn,
        int? StorageSrid)
    {
        public string QualifiedName =>
            string.IsNullOrWhiteSpace(SchemaName) ? TableName : $"{SchemaName}.{TableName}";
    }
}
