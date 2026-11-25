// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Services;

internal sealed class DeclaredFieldScope
{
    public required int LayerId { get; init; }
    public required string PrimaryKeyColumn { get; init; }
    public string? GeometryColumn { get; init; }
    public IReadOnlyList<string>? AttributeColumns { get; init; }
}

internal sealed class StatisticsHavingRequest
{
    public required StringBuilder Sql { get; init; }
    public required DeclaredFieldScope FieldScope { get; init; }
    public required FeatureQuery Query { get; init; }
    public required Func<string, string> QuoteAttribute { get; init; }
    public required Func<object, string> AddParameter { get; init; }
    public required Func<StatisticType, string, string> FormatStatistic { get; init; }
    public required Func<HavingComparisonOperator, string> FormatOperator { get; init; }
}

internal sealed class DeclaredOrderByRequest
{
    public required StringBuilder Sql { get; init; }
    public required DeclaredFieldScope FieldScope { get; init; }
    public required FeatureQuery Query { get; init; }
    public required Func<string, string> QuoteAttribute { get; init; }
}

internal sealed class StatisticsBodyRequest
{
    public required StringBuilder Sql { get; init; }
    public required string QuotedTableReference { get; init; }
    public required IReadOnlyList<string> QuotedGroupByColumns { get; init; }
    public required string StatisticColumnsSql { get; init; }
    public required Action AppendFilters { get; init; }
}

internal sealed class WarehouseWhereClauseRequest
{
    public required DeclaredFieldScope FieldScope { get; init; }
    public required Func<string, string> QuoteAttribute { get; init; }
    public required Func<object, string> AddParameter { get; init; }
}

internal static partial class RelationalFeatureStoreHelpers
{
    public static string ResolveDeclaredField(DeclaredFieldScope scope, string field, string clause)
    {
        if (scope.PrimaryKeyColumn.Equals(field, StringComparison.OrdinalIgnoreCase))
        {
            return scope.PrimaryKeyColumn;
        }

        if (scope.GeometryColumn is { } geometryColumn &&
            geometryColumn.Equals(field, StringComparison.OrdinalIgnoreCase))
        {
            return geometryColumn;
        }

        if (scope.AttributeColumns is null)
        {
            return field;
        }

        var declared = scope.AttributeColumns.FirstOrDefault(
            column => column.Equals(field, StringComparison.OrdinalIgnoreCase));
        return declared ?? throw new ArgumentException(
            $"{clause} field '{field}' is not declared on layer {scope.LayerId}.");
    }

    public static void AppendStatisticsBody(StatisticsBodyRequest request)
    {
        var sb = request.Sql;
        sb.Append("SELECT ");
        if (request.QuotedGroupByColumns.Count > 0)
        {
            sb.Append(string.Join(", ", request.QuotedGroupByColumns)).Append(", ");
        }

        sb.Append(request.StatisticColumnsSql);
        sb.Append(" FROM ").Append(request.QuotedTableReference);
        sb.Append(" WHERE 1=1");

        request.AppendFilters();

        if (request.QuotedGroupByColumns.Count > 0)
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", request.QuotedGroupByColumns));
        }
    }

    public static void AppendStatisticsHaving(StatisticsHavingRequest request)
    {
        if (request.Query.Having is not { IsDefaultOrEmpty: false } having)
        {
            return;
        }

        request.Sql.Append(" HAVING ");
        for (var i = 0; i < having.Length; i++)
        {
            if (i > 0)
            {
                request.Sql.Append(" AND ");
            }

            var condition = having[i];
            _ = request.QuoteAttribute(condition.OnStatisticField);
            var resolved = ResolveDeclaredField(request.FieldScope, condition.OnStatisticField, "HAVING");
            var fieldExpr = request.QuoteAttribute(resolved);
            var marker = request.AddParameter(condition.BoundValue());
            request.Sql.Append(request.FormatStatistic(condition.StatisticType, fieldExpr))
                .Append(' ')
                .Append(request.FormatOperator(condition.Operator))
                .Append(' ')
                .Append(marker);
        }
    }

    public static void AppendDeclaredOrderBy(DeclaredOrderByRequest request)
    {
        if (!request.Query.OrderBy.HasValue || request.Query.OrderBy.Value.IsDefaultOrEmpty)
        {
            return;
        }

        var clauses = new List<string>(request.Query.OrderBy.Value.Length);
        foreach (var orderBy in request.Query.OrderBy.Value)
        {
            var direction = orderBy.Ascending ? "ASC" : "DESC";
            _ = request.QuoteAttribute(orderBy.Field);
            var resolved = ResolveDeclaredField(request.FieldScope, orderBy.Field, "ORDER BY");
            clauses.Add($"{request.QuoteAttribute(resolved)} {direction}");
        }

        request.Sql.Append(" ORDER BY ").Append(string.Join(", ", clauses));
    }

    public static string RenderWarehouseWhereClause(string whereClause, WarehouseWhereClauseRequest request)
    {
        var rendered = new List<string>();
        var parsed = LegacyWhereClauseParser.Parse(
            WhereClauseSplitter.SplitOnAnd(whereClause),
            LegacyWhereGrammar.Warehouse);
        foreach (var expression in parsed)
        {
            rendered.Add(RenderWarehouseWhereExpression(expression, request));
        }

        return string.Join(" AND ", rendered);
    }

    private static string RenderWarehouseWhereExpression(
        LegacyWhereExpression expression,
        WarehouseWhereClauseRequest request)
        => expression.Kind switch
        {
            LegacyWhereExpressionKind.True => "1=1",
            LegacyWhereExpressionKind.NullCheck => RenderWarehouseNullCheck(expression.Match, request),
            LegacyWhereExpressionKind.In => RenderWarehouseInExpression(expression.Match, request),
            LegacyWhereExpressionKind.Comparison => RenderWarehouseComparison(expression.Match, request),
            _ => throw new ArgumentException("WHERE clause format not supported."),
        };

    private static string RenderWarehouseNullCheck(Match match, WarehouseWhereClauseRequest request)
    {
        var quotedField = ResolveQuotedWhereField(match.Groups["field"].Value, request);
        var notClause = string.IsNullOrWhiteSpace(match.Groups["not"].Value) ? string.Empty : "NOT ";
        return $"{quotedField} IS {notClause}NULL";
    }

    private static string RenderWarehouseInExpression(Match match, WarehouseWhereClauseRequest request)
    {
        var quotedField = ResolveQuotedWhereField(match.Groups["field"].Value, request);
        var placeholders = new List<string>();
        foreach (Match valueMatch in LegacyWhereClauseParser.MatchInValues(match.Groups["values"].Value))
        {
            var parsedValue = LegacyWhereClauseParser.ParseValueToken(
                valueMatch.Value,
                LegacyWhereValuePolicy.NumericRequired);
            placeholders.Add(request.AddParameter(parsedValue));
        }

        return $"{quotedField} IN ({string.Join(", ", placeholders)})";
    }

    private static string RenderWarehouseComparison(Match match, WarehouseWhereClauseRequest request)
    {
        var quotedField = ResolveQuotedWhereField(match.Groups["field"].Value, request);
        var op = LegacyWhereClauseParser.NormalizeOperator(match.Groups["op"].Value, LegacyWhereGrammar.Warehouse);
        var value = LegacyWhereClauseParser.ParseValueToken(
            match.Groups["value"].Value,
            LegacyWhereValuePolicy.NumericRequired);
        var paramName = request.AddParameter(value);
        return $"{quotedField} {op} {paramName}";
    }

    private static string ResolveQuotedWhereField(string rawField, WarehouseWhereClauseRequest request)
    {
        _ = request.QuoteAttribute(rawField);
        var resolved = ResolveDeclaredField(request.FieldScope, rawField, "WHERE");
        return request.QuoteAttribute(resolved);
    }
}
