// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private const string NativeAttributeAliasPrefix = "__honua_native_attribute_";
    private const string EmptyNativeAttributeJson = "'{}'::jsonb";

    private string? BuildNativeAttributesProjection(FeatureQuery query, SqlBuilder sql)
    {
        // DISTINCT's existing text equality/order and JSONB storage keep their
        // canonical projection. Native values only come from physical columns.
        if (query.Distinct || query.ExcludeAttributes || !string.IsNullOrWhiteSpace(_mapping.AttributesColumn))
        {
            return null;
        }

        var fields = ResolveAttributeFields(query);
        // JSONB orders keys independently of schema order. Case-colliding keys
        // feed a case-insensitive canonical dictionary, so preserve that old
        // collision behavior rather than overwrite in native projection order.
        if (fields.Select(field => field.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
        {
            return null;
        }

        var nativeFields = fields.Where(field => field.Type is MetadataV2FieldType.String
            or MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger or MetadataV2FieldType.Boolean).ToArray();
        // PostgreSQL limits a result target list to 1664 columns; reserve the
        // feature header and optional distance column, then use JSON for wider
        // projections rather than introduce a new query failure.
        if (nativeFields.Length is 0 or > 1660)
        {
            return null;
        }

        sql.NativeAttributeNames = nativeFields.Select(field => field.Name).ToArray();
        var nativeNames = sql.NativeAttributeNames.ToHashSet(StringComparer.Ordinal);
        var jsonFields = fields.Where(field => !nativeNames.Contains(field.Name)).ToArray();
        sql.HasJsonAttributeFields = jsonFields.Length != 0;
        var parts = new List<string>(nativeFields.Length + 1)
        {
            jsonFields.Length == 0 ? EmptyNativeAttributeJson : BuildAttributesJsonbExpression(jsonFields, useMapping: true, sql.AddParameter)
        };

        foreach (var field in nativeFields)
        {
            var column = ValidateAndQuoteIdentifier(field.Name);
            var key = sql.AddParameter(field.Name);
            // Match actual database types, not stale publication hints. Unknown,
            // floating, numeric, temporal, domain and other types retain exactly
            // the PostgreSQL JSON representation and scalar conversion. There
            // is no catalogue round trip or retry of a successfully read query.
            parts.Add($"""
                CASE WHEN pg_catalog.pg_typeof({column}) OPERATOR(pg_catalog.=) ANY (ARRAY[
                    'pg_catalog.text'::pg_catalog.regtype, 'pg_catalog.varchar'::pg_catalog.regtype,
                    'pg_catalog.bool'::pg_catalog.regtype, 'pg_catalog.int2'::pg_catalog.regtype,
                    'pg_catalog.int4'::pg_catalog.regtype, 'pg_catalog.int8'::pg_catalog.regtype])
                THEN {EmptyNativeAttributeJson} ELSE jsonb_build_object({key}::text, {column}) END
                """);
        }

        return $"({string.Join(" || ", parts)})";
    }

    private static string BuildNativeAttributeSelect(SqlBuilder sql)
    {
        if (sql.NativeAttributeNames.Length == 0)
        {
            return string.Empty;
        }

        return ", " + string.Join(", ", sql.NativeAttributeNames.Select((name, index) =>
            ValidateAndQuoteIdentifier(name) + " AS " + NativeAttributeAliasPrefix + index.ToString(CultureInfo.InvariantCulture)));
    }

    private static NativeAttributeDecoder? BindNativeAttributeDecoder(SqlBuilder sql, NpgsqlDataReader reader)
    {
        if (sql.NativeAttributeNames.Length == 0)
        {
            return null;
        }

        var firstOrdinal = reader.FieldCount - sql.NativeAttributeNames.Length;
        if (firstOrdinal < 3)
        {
            throw new InvalidOperationException("Native attribute columns are missing from the feature result.");
        }

        var columns = new NativeAttributeColumn[sql.NativeAttributeNames.Length];
        var hasJsonFallback = sql.HasJsonAttributeFields;
        for (var i = 0; i < columns.Length; i++)
        {
            var ordinal = firstOrdinal + i;
            if (!reader.GetName(ordinal).Equals(NativeAttributeAliasPrefix + i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Native attribute result columns do not match the query projection.");
            }

            var type = reader.GetPostgresType(ordinal);
            var kind = type.Namespace == "pg_catalog" ? type.InternalName switch
            {
                "text" or "varchar" => NativeAttributeKind.Text,
                "bool" => NativeAttributeKind.Boolean,
                "int2" => NativeAttributeKind.Int16,
                "int4" => NativeAttributeKind.Int32,
                "int8" => NativeAttributeKind.Int64,
                _ => NativeAttributeKind.JsonFallback
            } : NativeAttributeKind.JsonFallback;
            hasJsonFallback |= kind == NativeAttributeKind.JsonFallback;
            columns[i] = new NativeAttributeColumn(sql.NativeAttributeNames[i], ordinal, kind);
        }

        return new NativeAttributeDecoder(columns, hasJsonFallback);
    }

    private static bool IsNativeCachedResultTypeChange(PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.FeatureNotSupported &&
           string.Equals(exception.Routine, "RevalidateCachedQuery", StringComparison.Ordinal) &&
           string.Equals(exception.File, "plancache.c", StringComparison.Ordinal) &&
           string.Equals(exception.MessageText, "cached plan must not change result type", StringComparison.Ordinal) &&
           string.IsNullOrEmpty(exception.Where) && string.IsNullOrEmpty(exception.InternalQuery);

    private enum NativeAttributeKind
    {
        JsonFallback,
        Text,
        Boolean,
        Int16,
        Int32,
        Int64
    }

    private readonly record struct NativeAttributeColumn(string Name, int Ordinal, NativeAttributeKind Kind);

    private sealed class NativeAttributeDecoder(NativeAttributeColumn[] columns, bool hasJsonFallback)
    {
        public bool HasJsonFallback { get; } = hasJsonFallback;

        public int FirstOrdinal { get; } = columns[0].Ordinal;

        public void ReadInto(NpgsqlDataReader reader, Dictionary<string, object?> destination)
        {
            foreach (var column in columns)
            {
                if (column.Kind == NativeAttributeKind.JsonFallback)
                {
                    // The shared JSON reader has already populated this field,
                    // including SQL NULL and independently owned nested values.
                    continue;
                }

                if (reader.IsDBNull(column.Ordinal))
                {
                    destination[column.Name] = null;
                    continue;
                }

                // JSON's existing scalar contract represents every in-range
                // integral value as Int64, including physical smallint/integer.
                destination[column.Name] = column.Kind switch
                {
                    NativeAttributeKind.Text => reader.GetString(column.Ordinal),
                    NativeAttributeKind.Boolean => reader.GetBoolean(column.Ordinal),
                    NativeAttributeKind.Int16 => (long)reader.GetInt16(column.Ordinal),
                    NativeAttributeKind.Int32 => (long)reader.GetInt32(column.Ordinal),
                    NativeAttributeKind.Int64 => reader.GetInt64(column.Ordinal),
                    _ => throw new InvalidOperationException("Unexpected native attribute decoder.")
                };
            }
        }
    }
}
