// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: Low-level SQL/string helpers shared across the publishing partials.
//
// Includes service-name and identifier normalization, identifier/literal quoting, the
// canonical geometry expression builder used during materialization, the chunked
// jsonb_build_object expression used to project source attributes into features.attributes,
// the nullable-double parameter helper, and the column-lookup convenience. Lives in its own
// file because these primitives are referenced by Validation, TableIntrospection,
// LayerPersistence, and ExtentRefresh — keeping them adjacent to any one of those would
// imply a false ownership.

using Honua.Core.Features.Admin.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private static string NormalizeServiceName(string? serviceName)
    {
        return string.IsNullOrWhiteSpace(serviceName) ? DefaultServiceName : serviceName.Trim();
    }

    private static bool IsSafeIdentifier(string value) => SchemaSearchPath.IsValidIdentifier(value);

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
}
