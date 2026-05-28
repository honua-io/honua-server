// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: Publish validation pipeline.
//
// Holds the validation helpers invoked by ValidateTableForPublishAsync / PublishLayerAsync —
// per-axis resolvers for selected columns, geometry column/type, primary key and SRID;
// geometry-health inspection; existing-layer conflict detection; the
// TablePublishValidationCheck factory (Pass/Warning/Error); and the assembly/throw helpers
// that translate a validation result into either a public report or a typed
// LayerPublishingException. Isolated here because validation is read-only against the source
// table and easier to reason about apart from the SQL-mutating publish path.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
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
}
