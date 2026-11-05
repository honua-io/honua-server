// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Infrastructure.Validation;

internal static class OrderByParsing
{
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
        foreach (var trimmed in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(rawField => rawField.Trim()))
        {
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

            if (fieldDefinition == null && !allowUnknownFields &&
                (allowedCoreFields == null || !allowedCoreFields.Contains(field)))
            {
                throw unknownFieldException(field);
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

    // A colon is allowed because an extension field such as eo:cloud_cover is advertised as sortable; quotes, semicolons, and whitespace stay excluded.
    private static bool IsValidFeatureServerFieldName(string fieldName)
        => FeatureFieldNameSyntax.IsValid(fieldName);
}
