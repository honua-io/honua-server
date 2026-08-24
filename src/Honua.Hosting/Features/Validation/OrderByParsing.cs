// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

    /// <summary>
    /// Syntactic shape guard for an order-by field token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a shape check, not the authorization: <see cref="ParseOrderByV2"/> resolves
    /// every accepted token against <c>resource.SchemaFields</c> and throws
    /// <c>unknownFieldException</c> when the field is not declared, and the provider then
    /// binds the resolved name as a query parameter
    /// (<c>FeatureQueryBuilder.MapOrderByField</c> gates on
    /// <c>IsValidJsonAttributeKey</c> and orders by a parameter-bound jsonb accessor).
    /// </para>
    /// <para>
    /// The class therefore admits the prefixed, dotted and hyphenated shapes real schemas
    /// use — the STAC EO extension's <c>eo:cloud_cover</c> is the concrete case. Rejecting
    /// the colon here made <c>sortby=eo:cloud_cover</c> (OGC API Features, whose adapter
    /// rewrites this message) and <c>orderByFields=eo:cloud_cover</c> (GeoServices) fail
    /// with 400 on a field the service advertises as queryable and sortable
    /// (honua-server#3392). Quotes, semicolons, whitespace and control characters remain
    /// excluded, so a malformed token is still rejected as 400 rather than reaching the
    /// provider.
    /// </para>
    /// </remarks>
    private static bool IsValidFeatureServerFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        for (var i = 0; i < fieldName.Length; i++)
        {
            var ch = fieldName[i];
            var allowed = char.IsLetterOrDigit(ch)
                || ch == '_'
                || (i > 0 && (ch == ':' || ch == '.' || ch == '-'));
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
