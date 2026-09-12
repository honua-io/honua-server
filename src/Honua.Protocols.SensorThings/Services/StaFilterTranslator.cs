// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.SensorThings.Services;

/// <summary>
/// Outcome of translating a STA <c>$filter</c> expression into a parameterized
/// SQL WHERE fragment over one entity set's columns.
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
/// Translates a STA <c>$filter</c> string into a SQL WHERE fragment by reusing the shared
/// OData filter parser (<see cref="IFilterExpressionService"/> with
/// <see cref="FilterLanguage.OData"/>) rather than hand-rolling a parser. The parsed
/// <see cref="FilterExpression"/> AST is then walked into parameterized SQL against the
/// requested entity set's <see cref="StaEntitySchema"/>. Also translates <c>$orderby</c>,
/// which shares the same whitelist: only a property the schema declares can reach the SQL.
/// </summary>
/// <remarks>
/// Every literal is checked against its column's <see cref="StaPropertyType"/> before it is
/// bound. PostgreSQL has no implicit text -> double precision cast, so a filter such as
/// <c>result eq 'abc'</c> used to reach the reader as an untyped text parameter and raise
/// 42883 from inside the query — an unhandled exception and a 500 for what is a client
/// input error. It is now a translation failure the handler renders as 400 (#4203).
/// </remarks>
internal sealed class StaFilterTranslator
{
    private readonly IFilterExpressionService _filterExpressionService;

    public StaFilterTranslator(IFilterExpressionService filterExpressionService)
    {
        _filterExpressionService = filterExpressionService
            ?? throw new ArgumentNullException(nameof(filterExpressionService));
    }

    /// <summary>
    /// Translates <paramref name="filter"/> against <paramref name="schema"/>. A null/empty
    /// filter yields <see cref="StaFilterTranslation.Empty"/> (no WHERE fragment).
    /// </summary>
    public StaFilterTranslation Translate(StaEntitySchema schema, string? filter)
    {
        ArgumentNullException.ThrowIfNull(schema);

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
            Visit(schema, parseResult.Expression, sql, parameters);
        }
        catch (StaFilterTranslationException ex)
        {
            return StaFilterTranslation.Failure(ex.Message);
        }

        return StaFilterTranslation.Success(sql.ToString(), parameters);
    }

    /// <summary>
    /// Translates <c>$orderby</c> into an ORDER BY clause over <paramref name="schema"/>'s
    /// columns. The clause always ends in the entity's default ordering so paging over
    /// equal sort keys stays stable across pages.
    /// </summary>
    /// <returns>
    /// A translation whose <see cref="StaFilterTranslation.Sql"/> is the ORDER BY body
    /// (without the <c>ORDER BY</c> keyword), or a failure the handler renders as 400.
    /// </returns>
    public static StaFilterTranslation TranslateOrderBy(StaEntitySchema schema, string? orderBy)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (string.IsNullOrWhiteSpace(orderBy))
        {
            return StaFilterTranslation.Success(schema.DefaultOrderBySql, Array.Empty<object?>());
        }

        var terms = new List<string>();
        foreach (var rawTerm in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is 0 or > 2)
            {
                return StaFilterTranslation.Failure(
                    $"'{rawTerm}' is not a valid $orderby term. Expected '<property> [asc|desc]'.");
            }

            var direction = "ASC";
            if (parts.Length == 2)
            {
                if (string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase))
                {
                    direction = "DESC";
                }
                else if (!string.Equals(parts[1], "asc", StringComparison.OrdinalIgnoreCase))
                {
                    return StaFilterTranslation.Failure(
                        $"'{parts[1]}' is not a valid $orderby direction. Expected 'asc' or 'desc'.");
                }
            }

            if (!schema.TryGetProperty(parts[0], out var property))
            {
                return StaFilterTranslation.Failure(
                    $"Property '{parts[0]}' is not orderable on {schema.EntitySet}. Allowed: {schema.PropertyList}.");
            }

            terms.Add($"{property.Column} {direction}");
        }

        // The default clause is the tiebreaker, not a replacement: ordering by a non-unique
        // property (result, name) is otherwise not deterministic across pages.
        terms.Add(schema.DefaultOrderBySql);
        return StaFilterTranslation.Success(string.Join(", ", terms), Array.Empty<object?>());
    }

    private static void Visit(StaEntitySchema schema, FilterExpression expression, StringBuilder sql, List<object?> parameters)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                VisitBinary(schema, binary, sql, parameters);
                break;
            case UnaryExpression { Operator: UnaryOperator.Not } unary:
                sql.Append("NOT (");
                Visit(schema, unary.Operand, sql, parameters);
                sql.Append(')');
                break;
            // The shared OData parser normalises `x eq null` / `x ne null` into these
            // nodes, and expands `x ne <value>` into a null-safe shape built from them.
            // Emitting them as SQL null tests is what makes `resultTime eq null` return
            // the rows whose value is absent instead of `result_time = NULL`, which is
            // never true and silently matched nothing.
            case UnaryExpression { Operator: UnaryOperator.IsNull } isNull:
                VisitNullTest(schema, isNull.Operand, " IS NULL", sql);
                break;
            case UnaryExpression { Operator: UnaryOperator.IsNotNull } isNotNull:
                VisitNullTest(schema, isNotNull.Operand, " IS NOT NULL", sql);
                break;
            // A relational comparison against null is constant-false per OData 4.01
            // §5.1.1.1.3-6; the parser folds it to a boolean literal. It only reaches
            // here as a sub-expression - the shared parser rejects a bare literal as a
            // whole $filter - so the surrounding and/or still translates.
            case Literal { Type: LiteralType.Boolean, Value: bool constant }:
                sql.Append(constant ? "TRUE" : "FALSE");
                break;
            default:
                throw new StaFilterTranslationException(
                    $"Unsupported $filter expression for {schema.EntitySet}. Supported: comparisons between a property and a literal, combined with and/or/not.");
        }
    }

    private static void VisitBinary(StaEntitySchema schema, BinaryExpression binary, StringBuilder sql, List<object?> parameters)
    {
        switch (binary.Operator)
        {
            case BinaryOperator.And:
            case BinaryOperator.Or:
                sql.Append('(');
                Visit(schema, binary.Left, sql, parameters);
                sql.Append(binary.Operator == BinaryOperator.And ? " AND " : " OR ");
                Visit(schema, binary.Right, sql, parameters);
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
                $"Unsupported operator '{binary.Operator}' in $filter."),
        };

        var property = ResolveProperty(schema, binary.Left, binary.Right, out var literal);
        sql.Append(property.Column).Append(' ').Append(op).Append(" @p")
            .Append(parameters.Count.ToString(CultureInfo.InvariantCulture));
        parameters.Add(ConvertLiteral(property, literal));
    }

    /// <summary>
    /// Emits a SQL null test. The operand is a property on every shape the parser
    /// produces; a literal operand is decided statically because its nullness is known.
    /// </summary>
    private static void VisitNullTest(StaEntitySchema schema, FilterExpression operand, string test, StringBuilder sql)
    {
        switch (operand)
        {
            case PropertyReference property:
                sql.Append(MapProperty(schema, property.PropertyName).Column).Append(test);
                return;
            case Literal literal:
                var isNull = literal.Type == LiteralType.Null;
                sql.Append((test == " IS NULL") == isNull ? "TRUE" : "FALSE");
                return;
            default:
                throw new StaFilterTranslationException(
                    "A null test in $filter must name a property.");
        }
    }

    private static StaProperty ResolveProperty(
        StaEntitySchema schema,
        FilterExpression left,
        FilterExpression right,
        out Literal literal)
    {
        if (left is PropertyReference leftProperty && right is Literal rightLiteral)
        {
            literal = rightLiteral;
            return MapProperty(schema, leftProperty.PropertyName);
        }

        if (right is PropertyReference rightProperty && left is Literal leftLiteral)
        {
            literal = leftLiteral;
            return MapProperty(schema, rightProperty.PropertyName);
        }

        throw new StaFilterTranslationException(
            "Each $filter comparison must be between a known property and a literal value.");
    }

    private static StaProperty MapProperty(StaEntitySchema schema, string propertyName)
    {
        if (schema.TryGetProperty(propertyName, out var property))
        {
            return property;
        }

        throw new StaFilterTranslationException(
            $"Property '{propertyName}' is not filterable on {schema.EntitySet}. Allowed: {schema.PropertyList}.");
    }

    /// <summary>
    /// Converts a parsed literal to the CLR type the column binds as, or fails the
    /// translation. The provider binds by CLR type, so this is the only place that decides
    /// whether a comparison is well typed.
    /// </summary>
    private static object? ConvertLiteral(StaProperty property, Literal literal) => property.Type switch
    {
        StaPropertyType.Integer => ConvertInteger(property, literal),
        StaPropertyType.Number => ConvertNumber(property, literal),
        StaPropertyType.Text => ConvertText(property, literal),
        StaPropertyType.Timestamp => ConvertTimestamp(property, literal),
        _ => throw new StaFilterTranslationException($"Property '{property.Name}' is not filterable."),
    };

    private static long ConvertInteger(StaProperty property, Literal literal)
    {
        if (literal.Type == LiteralType.Number && literal.Value is { } value)
        {
            try
            {
                var asDecimal = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (decimal.Truncate(asDecimal) == asDecimal &&
                    asDecimal >= long.MinValue && asDecimal <= long.MaxValue)
                {
                    return (long)asDecimal;
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // Fall through to the typed failure below.
            }
        }

        throw TypeMismatch(property, literal, "an integer");
    }

    private static double ConvertNumber(StaProperty property, Literal literal)
    {
        if (literal.Type == LiteralType.Number && literal.Value is { } value)
        {
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // Fall through to the typed failure below.
            }
        }

        throw TypeMismatch(property, literal, "a number");
    }

    private static string ConvertText(StaProperty property, Literal literal) =>
        literal.Type == LiteralType.Text && literal.Value is string text
            ? text
            : throw TypeMismatch(property, literal, "a quoted string");

    private static DateTimeOffset ConvertTimestamp(StaProperty property, Literal literal)
    {
        switch (literal.Value)
        {
            case DateTimeOffset instant:
                return instant;
            case DateTime dateTime:
                return new DateTimeOffset(dateTime, TimeSpan.Zero);
            // STA temporal properties arrive as ISO-8601 text in many client forms, so a
            // parseable string is promoted rather than rejected.
            case string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed):
                return parsed;
        }

        throw TypeMismatch(property, literal, "an ISO-8601 instant");
    }

    private static StaFilterTranslationException TypeMismatch(StaProperty property, Literal literal, string expected)
    {
        var actual = literal.Value is null
            ? "null"
            : Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? literal.Type.ToString();
        return new StaFilterTranslationException(
            $"$filter value '{actual}' is not valid for property '{property.Name}', which requires {expected}.");
    }

    private sealed class StaFilterTranslationException : Exception
    {
        public StaFilterTranslationException(string message)
            : base(message)
        {
        }
    }
}
