// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin.Services;

internal sealed class LayerValidationService(
    ILayerCatalog catalog,
    ITableDiscoveryService tableDiscoveryService,
    IDatabaseConnectionProvider connectionProvider) : ILayerValidationService
{
    private const string SeverityPass = "pass";
    private const string SeverityWarning = "warning";
    private const string SeverityError = "error";

    public async Task<LayerValidationResponse?> ValidateLayerAsync(
        int layerId,
        CancellationToken cancellationToken = default)
    {
        if (layerId < 0)
        {
            return null;
        }

        var layer = await catalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (layer == null)
        {
            return null;
        }

        var checks = new List<LayerValidationCheck>
        {
            Pass("catalog-layer", $"Layer {layerId} exists in the catalog.")
        };

        var storageMapping = layer.StorageMapping;
        if (storageMapping == null)
        {
            checks.Add(Error(
                "storage-mapping",
                "Layer does not define a physical storage mapping."));

            return BuildResponse(layer, null, checks);
        }

        var storageErrors = storageMapping.Validate();
        if (storageErrors.Count > 0)
        {
            foreach (var error in storageErrors)
            {
                checks.Add(Error("storage-mapping", error));
            }

            return BuildResponse(layer, storageMapping, checks);
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
        if (table == null)
        {
            checks.Add(Error(
                "storage-table",
                "Mapped storage table was not found or no longer exposes a geometry column.",
                storageMapping.QualifiedName,
                null));

            return BuildResponse(layer, storageMapping, checks);
        }

        checks.Add(Pass(
            "storage-table",
            $"Mapped storage table '{table.Schema}.{table.Table}' is discoverable."));

        ValidatePrimaryKey(storageMapping, table, checks);
        ValidateGeometry(layer, storageMapping, table, checks);
        ValidateSrid(layer, storageMapping, table, checks);
        ValidateDeclaredFields(layer, table, checks);

        return BuildResponse(layer, storageMapping, checks);
    }

    private static TableInfo? FindTable(
        IReadOnlyCollection<TableInfo> tables,
        LayerStorageMapping storageMapping)
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
        LayerStorageMapping storageMapping,
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
        LayerDefinition layer,
        LayerStorageMapping storageMapping,
        TableInfo table,
        List<LayerValidationCheck> checks)
    {
        if (!layer.HasGeometry)
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
        LayerDefinition layer,
        LayerStorageMapping storageMapping,
        TableInfo table,
        List<LayerValidationCheck> checks)
    {
        var expectedSrid = storageMapping.StorageSrid ?? layer.SpatialReference.Wkid;
        var actualSrid = table.Srid;

        if (expectedSrid <= 0)
        {
            checks.Add(Warning(
                "storage-srid",
                "Layer metadata does not define an expected storage SRID.",
                null,
                actualSrid?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }

        if (actualSrid is null or <= 0)
        {
            checks.Add(Error(
                "storage-srid",
                "Storage table does not report a valid SRID.",
                expectedSrid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                actualSrid?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }

        if (actualSrid.Value != expectedSrid)
        {
            checks.Add(Error(
                "storage-srid",
                "Storage table SRID does not match layer storage metadata.",
                expectedSrid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                actualSrid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }

        checks.Add(Pass(
            "storage-srid",
            $"Storage SRID {actualSrid.Value} matches layer metadata."));
    }

    private static void ValidateDeclaredFields(
        LayerDefinition layer,
        TableInfo table,
        List<LayerValidationCheck> checks)
    {
        foreach (var field in layer.AttributeFields)
        {
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

    private static ColumnInfo? FindColumn(TableInfo table, string columnName)
        => table.Columns.FirstOrDefault(column =>
            column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

    private static LayerValidationResponse BuildResponse(
        LayerDefinition layer,
        LayerStorageMapping? storageMapping,
        IReadOnlyCollection<LayerValidationCheck> checks)
    {
        var hasErrors = checks.Any(check => check.Severity == SeverityError);
        var hasWarnings = checks.Any(check => check.Severity == SeverityWarning);

        return new LayerValidationResponse
        {
            LayerId = layer.Id,
            LayerName = layer.Name,
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
}
