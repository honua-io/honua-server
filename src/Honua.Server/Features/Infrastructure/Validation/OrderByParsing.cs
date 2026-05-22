// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Infrastructure.Validation;

internal static class OrderByParsing
{
    public static ImmutableArray<OrderByClause>? ParseFeatureServerOrderBy(
        string? orderByFields,
        LayerDefinition layer,
        IReadOnlySet<string> allowedCoreFields)
    {
        return ParseOrderBy(
            orderByFields,
            layer,
            IsValidFeatureServerFieldName,
            allowedCoreFields,
            allowUnknownFields: false,
            allowExtraTokens: false,
            invalidFieldException: field => new InvalidOperationException($"Invalid orderByFields value: {field}"),
            invalidDirectionException: direction => new InvalidOperationException($"Invalid orderByFields direction: {direction}"),
            invalidExpressionException: expression => new InvalidOperationException($"Invalid orderByFields value: {expression}"),
            unknownFieldException: field => new InvalidOperationException($"Unknown orderByFields value: {field}"));
    }

    /// <summary>
    /// V2 overload of <see cref="ParseFeatureServerOrderBy(string?, LayerDefinition, IReadOnlySet{string})"/>.
    /// Validates orderBy field references against the canonical schema declared on the V2 resource.
    /// </summary>
    public static ImmutableArray<OrderByClause>? ParseFeatureServerOrderBy(
        string? orderByFields,
        MetadataV2Resource resource,
        IReadOnlySet<string> allowedCoreFields)
    {
        return ParseOrderByV2(
            orderByFields,
            resource,
            IsValidFeatureServerFieldName,
            allowedCoreFields,
            allowUnknownFields: false,
            allowExtraTokens: false,
            invalidFieldException: field => new InvalidOperationException($"Invalid orderByFields value: {field}"),
            invalidDirectionException: direction => new InvalidOperationException($"Invalid orderByFields direction: {direction}"),
            invalidExpressionException: expression => new InvalidOperationException($"Invalid orderByFields value: {expression}"),
            unknownFieldException: field => new InvalidOperationException($"Unknown orderByFields value: {field}"));
    }

    public static ImmutableArray<OrderByClause>? ParseODataOrderBy(
        string? orderby,
        LayerDefinition layer)
    {
        return ParseOrderBy(
            orderby,
            layer,
            IsValidODataFieldName,
            allowedCoreFields: null,
            allowUnknownFields: true,
            allowExtraTokens: false,
            invalidFieldException: field => new ArgumentException($"Invalid field name in $orderby: {field}"),
            invalidDirectionException: direction => new ArgumentException(
                $"Invalid sort direction in $orderby: {direction}. Use 'asc' or 'desc'."),
            invalidExpressionException: _ => new ArgumentException("Invalid $orderby expression."),
            unknownFieldException: _ => new ArgumentException("Unknown field in $orderby."));
    }

    private static ImmutableArray<OrderByClause>? ParseOrderBy(
        string? orderBy,
        LayerDefinition layer,
        Func<string, bool> fieldNameValidator,
        IReadOnlySet<string>? allowedCoreFields,
        bool allowUnknownFields,
        bool allowExtraTokens,
        Func<string, Exception> invalidFieldException,
        Func<string, Exception> invalidDirectionException,
        Func<string, Exception> invalidExpressionException,
        Func<string, Exception> unknownFieldException)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            return null;
        }

        var clauses = new List<OrderByClause>();
        foreach (var rawField in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawField.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (!allowExtraTokens && parts.Length > 2)
            {
                throw invalidExpressionException(trimmed);
            }

            var field = parts[0];
            if (!fieldNameValidator(field))
            {
                throw invalidFieldException(field);
            }

            var ascending = true;
            if (parts.Length > 1)
            {
                if (parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    ascending = false;
                }
                else if (!parts[1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                {
                    throw invalidDirectionException(parts[1]);
                }
            }

            var fieldDefinition = layer.Fields.FirstOrDefault(f =>
                f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));

            if (fieldDefinition == null && !allowUnknownFields)
            {
                if (allowedCoreFields == null || !allowedCoreFields.Contains(field))
                {
                    throw unknownFieldException(field);
                }
            }

            var resolvedField = fieldDefinition?.Name ?? field;
            if (!fieldNameValidator(resolvedField))
            {
                throw invalidFieldException(field);
            }

            clauses.Add(new OrderByClause(resolvedField, ascending, fieldDefinition?.Type));
        }

        return clauses.Count == 0 ? null : clauses.ToImmutableArray();
    }

    private static ImmutableArray<OrderByClause>? ParseOrderByV2(
        string? orderBy,
        MetadataV2Resource resource,
        Func<string, bool> fieldNameValidator,
        IReadOnlySet<string>? allowedCoreFields,
        bool allowUnknownFields,
        bool allowExtraTokens,
        Func<string, Exception> invalidFieldException,
        Func<string, Exception> invalidDirectionException,
        Func<string, Exception> invalidExpressionException,
        Func<string, Exception> unknownFieldException)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            return null;
        }

        var clauses = new List<OrderByClause>();
        foreach (var rawField in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawField.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (!allowExtraTokens && parts.Length > 2)
            {
                throw invalidExpressionException(trimmed);
            }

            var field = parts[0];
            if (!fieldNameValidator(field))
            {
                throw invalidFieldException(field);
            }

            var ascending = true;
            if (parts.Length > 1)
            {
                if (parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    ascending = false;
                }
                else if (!parts[1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                {
                    throw invalidDirectionException(parts[1]);
                }
            }

            var fieldDefinition = resource.SchemaFields.FirstOrDefault(f =>
                f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));

            if (fieldDefinition == null && !allowUnknownFields)
            {
                if (allowedCoreFields == null || !allowedCoreFields.Contains(field))
                {
                    throw unknownFieldException(field);
                }
            }

            var resolvedField = fieldDefinition?.Name ?? field;
            if (!fieldNameValidator(resolvedField))
            {
                throw invalidFieldException(field);
            }

            clauses.Add(new OrderByClause(resolvedField, ascending, MapV2FieldTypeToFieldType(fieldDefinition?.Type)));
        }

        return clauses.Count == 0 ? null : clauses.ToImmutableArray();
    }

    /// <summary>
    /// Maps a Metadata v2 field type label onto the v1 <see cref="FieldType"/> enum that
    /// the unified query model still consumes. Returns <c>null</c> when the type cannot
    /// be classified — callers tolerate a null type on <see cref="OrderByClause"/>.
    /// </summary>
    private static FieldType? MapV2FieldTypeToFieldType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }
        return type.Trim().ToLowerInvariant() switch
        {
            "string" or "text" or "varchar" or "char" => FieldType.String,
            "uuid" or "guid" => FieldType.Uuid,
            "integer" or "int" or "int32" or "int4" or "smallint" or "int16" or "int2"
                or "tinyint" or "byte" => FieldType.Integer,
            "biginteger" or "bigint" or "int64" or "int8" => FieldType.BigInteger,
            "double" or "float64" or "float8" or "numeric" or "decimal" => FieldType.Double,
            "real" or "float" or "float32" or "float4" => FieldType.Float,
            "boolean" or "bool" => FieldType.Boolean,
            "date" => FieldType.Date,
            "time" => FieldType.Time,
            "datetime" or "timestamp" or "timestamptz" => FieldType.DateTime,
            "geometry" => FieldType.Geometry,
            "json" or "jsonb" => FieldType.Json,
            "binary" or "bytea" or "blob" => FieldType.Binary,
            _ => null,
        };
    }

    private static bool IsValidFeatureServerFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        for (var i = 0; i < fieldName.Length; i++)
        {
            var ch = fieldName[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidODataFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        var first = fieldName[0];
        if (!(char.IsLetter(first) || first == '_'))
        {
            return false;
        }

        for (var i = 1; i < fieldName.Length; i++)
        {
            var ch = fieldName[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
