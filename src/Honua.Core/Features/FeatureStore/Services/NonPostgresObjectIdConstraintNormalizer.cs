// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Shared constraint recovery for providers that cannot execute a Postgres-translated
/// <see cref="FeatureQuery.SqlFilter"/>. SQL Server and Oracle both call this so an object-id
/// lift cannot accept a different attribute's <c>IN</c> list on only one of them.
/// </summary>
internal static partial class NonPostgresObjectIdConstraintNormalizer
{
    /// <summary>
    /// Proves <see cref="FeatureQuery.Where"/> against the shared translator when that service
    /// is available, then recovers object ids or fails closed.
    /// </summary>
    public static FeatureQuery Prepare(
        FeatureQuery query,
        string providerLabel,
        string? primaryKeyColumn,
        IFilterExpressionService? filterExpressions,
        MetadataV2Resource? resource)
    {
        if (query.SqlFilter is null)
        {
            return query;
        }

        SqlFragment? translation = null;
        var proved = false;
        if (!string.IsNullOrWhiteSpace(query.Where)
            && filterExpressions is not null
            && resource is not null)
        {
            var parsed = filterExpressions.Parse(FilterLanguage.ArcGisSql, query.Where);
            if (parsed.IsSuccess)
            {
                var translated = filterExpressions.Translate(parsed.Expression, resource);
                if (translated.IsSuccess)
                {
                    translation = translated.SqlFilter;
                    proved = true;
                }
            }
        }

        return Normalize(query, translation, proved, providerLabel, primaryKeyColumn);
    }

    /// <summary>
    /// Drops a filter that is exactly the proved WHERE translation. Lifts a pure integral
    /// <c>IN</c> list only when its target is <c>objectid</c> or <paramref name="primaryKeyColumn"/>.
    /// <c>NOT IN</c> and any other attribute stay in <see cref="FeatureQuery.SqlFilter"/> so the
    /// provider can reject them instead of querying the wrong rows.
    /// </summary>
    public static FeatureQuery Normalize(
        FeatureQuery query,
        SqlFragment? translationOfWhere,
        bool translationProved,
        string providerLabel,
        string? primaryKeyColumn)
    {
        if (query.SqlFilter is not { } sqlFilter)
        {
            return query;
        }

        if (translationProved && SqlEquals(sqlFilter, translationOfWhere))
        {
            return query with { SqlFilter = null };
        }

        var whereIsBlank = string.IsNullOrWhiteSpace(query.Where);
        var whereIsNoOp = !whereIsBlank && NoOpWhereRegex().IsMatch(query.Where!);
        if ((whereIsBlank || whereIsNoOp)
            && TryReadObjectIdInList(sqlFilter.Sql, sqlFilter.Parameters, startIndex: 0, primaryKeyColumn, out var objectIds))
        {
            var lifted = ApplyObjectIds(query, objectIds, providerLabel);
            return whereIsNoOp ? lifted with { Where = null } : lifted;
        }

        if (translationProved
            && translationOfWhere is not null
            && !whereIsBlank
            && TrySplitTranslatedWhereAndObjectIds(
                sqlFilter.Sql, translationOfWhere, sqlFilter.Parameters, primaryKeyColumn, out var andIds))
        {
            return ApplyObjectIds(query, andIds, providerLabel);
        }

        if (!whereIsBlank)
        {
            throw CannotCombine(providerLabel);
        }

        return query;
    }

    private static FeatureQuery ApplyObjectIds(FeatureQuery query, ImmutableArray<long> objectIds, string providerLabel)
    {
        if (query.ObjectIds is { IsDefaultOrEmpty: false } existing && !existing.SequenceEqual(objectIds))
        {
            throw CannotCombine(providerLabel);
        }

        return query with { SqlFilter = null, ObjectIds = objectIds };
    }

    private static NotSupportedException CannotCombine(string providerLabel)
        => new(
            $"{providerLabel} cannot safely combine canonical Where text with a translated SqlFilter. " +
            "Submit the additional constraints through provider-neutral FeatureQuery properties.");

    private static bool SqlEquals(SqlFragment left, SqlFragment? right)
    {
        if (right is null || !string.Equals(left.Sql, right.Sql, StringComparison.Ordinal))
        {
            return false;
        }

        return left.Parameters.SequenceEqual(right.Parameters);
    }

    private static bool TrySplitTranslatedWhereAndObjectIds(
        string sql,
        SqlFragment translation,
        IReadOnlyList<object?> parameters,
        string? primaryKeyColumn,
        out ImmutableArray<long> ids)
    {
        ids = default;
        var trimmed = sql.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '(' || trimmed[^1] != ')')
        {
            return false;
        }

        var inner = trimmed[1..^1];
        const string marker = " AND ";
        var splitAt = inner.LastIndexOf(marker, StringComparison.Ordinal);
        if (splitAt <= 0)
        {
            return false;
        }

        var wherePart = inner[..splitAt];
        if (!string.Equals(wherePart, translation.Sql, StringComparison.Ordinal))
        {
            return false;
        }

        var whereCount = translation.Parameters.Count;
        if (whereCount > parameters.Count)
        {
            return false;
        }

        for (var i = 0; i < whereCount; i++)
        {
            if (!Equals(parameters[i], translation.Parameters[i]))
            {
                return false;
            }
        }

        return TryReadObjectIdInList(inner[(splitAt + marker.Length)..], parameters, whereCount, primaryKeyColumn, out ids);
    }

    private static bool TryReadObjectIdInList(
        string sql,
        IReadOnlyList<object?> parameters,
        int startIndex,
        string? primaryKeyColumn,
        out ImmutableArray<long> ids)
    {
        ids = default;
        var match = ObjectIdInRegex().Match(sql.Trim());
        if (!match.Success || match.Groups["negated"].Success)
        {
            return false;
        }

        var left = match.Groups["left"].Value;
        if (left.Length == 0 || AndTokenRegex().IsMatch(left) || !IsObjectIdTarget(left, primaryKeyColumn))
        {
            return false;
        }

        var placeholders = match.Groups["ph"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (placeholders.Length == 0 || startIndex + placeholders.Length != parameters.Count)
        {
            return false;
        }

        var parsed = new long[placeholders.Length];
        for (var i = 0; i < placeholders.Length; i++)
        {
            var expected = "@p" + (startIndex + i).ToString(CultureInfo.InvariantCulture);
            if (!placeholders[i].Equals(expected, StringComparison.OrdinalIgnoreCase)
                || !TryReadLong(parameters[startIndex + i], out parsed[i]))
            {
                return false;
            }
        }

        ids = ImmutableArray.Create(parsed);
        return true;
    }

    private static bool IsObjectIdTarget(string left, string? primaryKeyColumn)
    {
        var trimmed = left.Trim();
        if (IsIdentifier(trimmed, "objectid")
            || (!string.IsNullOrWhiteSpace(primaryKeyColumn) && IsIdentifier(trimmed, primaryKeyColumn)))
        {
            return true;
        }

        var extracted = JsonAttributeRegex().Match(trimmed);
        if (!extracted.Success)
        {
            return false;
        }

        var field = extracted.Groups["field"].Value;
        return field.Equals("objectid", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(primaryKeyColumn)
                && field.Equals(primaryKeyColumn, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsIdentifier(string text, string name)
    {
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        return text.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadLong(object? value, out long id)
    {
        switch (value)
        {
            case long integral:
                id = integral;
                return true;
            case int integral:
                id = integral;
                return true;
            case short integral:
                id = integral;
                return true;
            case byte integral:
                id = integral;
                return true;
            default:
                id = 0;
                return false;
        }
    }

    [GeneratedRegex(
        @"^\s*(?:\(*\s*)*(?:1\s*=\s*1|TRUE)(?:\s*\))*\s*(?:AND\s*(?:\(*\s*)*(?:1\s*=\s*1|TRUE)(?:\s*\))*\s*)*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoOpWhereRegex();

    [GeneratedRegex(
        @"^(?<left>.+?)\s+(?<negated>NOT\s+)?IN\s*\(\s*(?<ph>@p\d+(?:\s*,\s*@p\d+)*)\s*\)\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ObjectIdInRegex();

    [GeneratedRegex(@"\bAND\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AndTokenRegex();

    [GeneratedRegex(
        @"^\(?\s*(?:""attributes""|attributes)\s*->>\s*'(?<field>[^']+)'\s*\)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonAttributeRegex();
}
