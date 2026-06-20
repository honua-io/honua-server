// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.SensorThings.Services;

/// <summary>
/// Outcome of translating a STA <c>$filter</c> expression into a parameterized
/// SQL WHERE fragment over the observations table columns.
/// </summary>
/// <param name="IsSuccess">Whether translation succeeded.</param>
/// <param name="Sql">The SQL WHERE fragment (placeholders <c>@p0..@pN</c>), or null.</param>
/// <param name="Parameters">Ordered parameter values for the placeholders.</param>
/// <param name="Error">Error message when translation fails.</param>
internal readonly record struct StaFilterTranslation(
    bool IsSuccess,
    string? Sql,
    IReadOnlyList<object?> Parameters,
    string? Error)
{
    public static StaFilterTranslation Empty { get; } =
        new(true, null, Array.Empty<object?>(), null);

    public static StaFilterTranslation Success(string sql, IReadOnlyList<object?> parameters) =>
        new(true, sql, parameters, null);

    public static StaFilterTranslation Failure(string error) =>
        new(false, null, Array.Empty<object?>(), error);
}

/// <summary>
/// Translates a STA Observations <c>$filter</c> string into a SQL WHERE fragment by
/// reusing the shared OData filter parser (<see cref="IFilterExpressionService"/> with
/// <see cref="FilterLanguage.OData"/>) rather than hand-rolling a parser. The parsed
/// <see cref="FilterExpression"/> AST is then walked into parameterized SQL against a
/// fixed whitelist of observation columns. This keeps the read API on the shared,
/// CITE/OData-validated grammar while targeting the dedicated observations store.
/// </summary>
internal sealed class StaObservationFilterTranslator
{
    private static readonly Dictionary<string, string> AllowedColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["phenomenonTime"] = "phenomenon_time",
            ["resultTime"] = "result_time",
            ["result"] = "result",
            ["@iot.id"] = "id",
            ["id"] = "id"
        };

    private readonly IFilterExpressionService _filterExpressionService;

    public StaObservationFilterTranslator(IFilterExpressionService filterExpressionService)
    {
        _filterExpressionService = filterExpressionService
            ?? throw new ArgumentNullException(nameof(filterExpressionService));
    }

    /// <summary>
    /// Translates <paramref name="filter"/>. A null/empty filter yields
    /// <see cref="StaFilterTranslation.Empty"/> (no WHERE fragment).
    /// </summary>
    public StaFilterTranslation Translate(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return StaFilterTranslation.Empty;
        }

        var parseResult = _filterExpressionService.Parse(FilterLanguage.OData, filter);
        if (!parseResult.IsSuccess || parseResult.Expression is null)
        {
            return StaFilterTranslation.Failure(parseResult.ErrorMessage ?? "Invalid $filter expression.");
        }

        var sql = new StringBuilder();
        var parameters = new List<object?>();
        try
        {
            Visit(parseResult.Expression, sql, parameters);
        }
        catch (StaFilterTranslationException ex)
        {
            return StaFilterTranslation.Failure(ex.Message);
        }

        return StaFilterTranslation.Success(sql.ToString(), parameters);
    }

    private static void Visit(FilterExpression expression, StringBuilder sql, List<object?> parameters)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                VisitBinary(binary, sql, parameters);
                break;
            case UnaryExpression { Operator: UnaryOperator.Not } unary:
                sql.Append("NOT (");
                Visit(unary.Operand, sql, parameters);
                sql.Append(')');
                break;
            default:
                throw new StaFilterTranslationException(
                    "Unsupported $filter expression. Phase 1 supports comparisons and logical and/or on phenomenonTime, resultTime, and result.");
        }
    }

    private static void VisitBinary(BinaryExpression binary, StringBuilder sql, List<object?> parameters)
    {
        switch (binary.Operator)
        {
            case BinaryOperator.And:
            case BinaryOperator.Or:
                sql.Append('(');
                Visit(binary.Left, sql, parameters);
                sql.Append(binary.Operator == BinaryOperator.And ? " AND " : " OR ");
                Visit(binary.Right, sql, parameters);
                sql.Append(')');
                return;
        }

        var op = binary.Operator switch
        {
            BinaryOperator.Equal => "=",
            BinaryOperator.NotEqual => "<>",
            BinaryOperator.LessThan => "<",
            BinaryOperator.LessThanOrEqual => "<=",
            BinaryOperator.GreaterThan => ">",
            BinaryOperator.GreaterThanOrEqual => ">=",
            _ => throw new StaFilterTranslationException(
                $"Unsupported operator '{binary.Operator}' in $filter.")
        };

        var column = ResolveColumn(binary.Left, binary.Right, out var literal);
        sql.Append(column).Append(' ').Append(op).Append(" @p")
            .Append(parameters.Count.ToString(CultureInfo.InvariantCulture));
        parameters.Add(ConvertLiteral(literal));
    }

    private static string ResolveColumn(FilterExpression left, FilterExpression right, out Literal literal)
    {
        if (left is PropertyReference leftProp && right is Literal rightLiteral)
        {
            literal = rightLiteral;
            return MapColumn(leftProp.PropertyName);
        }

        if (right is PropertyReference rightProp && left is Literal leftLiteral)
        {
            literal = leftLiteral;
            return MapColumn(rightProp.PropertyName);
        }

        throw new StaFilterTranslationException(
            "Each $filter comparison must be between a known property and a literal value.");
    }

    private static string MapColumn(string propertyName)
    {
        if (AllowedColumns.TryGetValue(propertyName, out var column))
        {
            return column;
        }

        throw new StaFilterTranslationException(
            $"Property '{propertyName}' is not filterable. Allowed: phenomenonTime, resultTime, result, id.");
    }

    private static object? ConvertLiteral(Literal literal)
    {
        return literal.Type switch
        {
            LiteralType.Null => null,
            LiteralType.Number => literal.Value,
            LiteralType.Boolean => literal.Value,
            LiteralType.Date or LiteralType.DateTime => CoerceTemporal(literal.Value),
            LiteralType.Text => CoerceTextual(literal.Value),
            _ => literal.Value
        };
    }

    private static object? CoerceTemporal(object? value)
    {
        if (value is DateTimeOffset dto)
        {
            return dto;
        }

        if (value is DateTime dt)
        {
            return new DateTimeOffset(dt, TimeSpan.Zero);
        }

        if (value is string s && DateTimeOffset.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return value;
    }

    private static object? CoerceTextual(object? value)
    {
        // STA temporal properties arrive as ISO-8601 text in many client forms;
        // promote parseable timestamps so the bound parameter matches a timestamptz column.
        if (value is string s && DateTimeOffset.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return value;
    }

    private sealed class StaFilterTranslationException : Exception
    {
        public StaFilterTranslationException(string message)
            : base(message)
        {
        }
    }
}
