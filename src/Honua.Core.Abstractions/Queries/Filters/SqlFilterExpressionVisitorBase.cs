// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

// Concurrent translations must not share this visitor because its parameter state is mutable.
public abstract class SqlFilterExpressionVisitorBase
{
    // The depth cap is duplicated because this assembly cannot reference Core, and it stops a deep filter from overflowing the stack.
    public const int MaxExpressionDepth = 50;

    private readonly List<object?> _parameters = [];
    private int _paramIndex;
    private int _depth;

    protected SqlFilterExpressionVisitorBase(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        Dialect = dialect;
    }

    protected ISqlDialect Dialect { get; }

    public SqlFragment Translate(FilterExpression filter, FilterTranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _paramIndex = 0;
        _depth = 0;
        _parameters.Clear();

        var sql = TranslateExpression(filter, context);
        // The scoped translator can translate more than one independent fragment in a
        // request (for example enforced and caller filters). Snapshot the parameters so
        // resetting this visitor for the next translation cannot mutate an earlier result.
        return new SqlFragment(sql, _parameters.ToArray());
    }

    protected string TranslateExpression(FilterExpression filter, FilterTranslationContext context)
    {
        try
        {
            if (++_depth > MaxExpressionDepth)
            {
                throw new ArgumentException(
                    $"Filter expression exceeds the maximum nesting depth of {MaxExpressionDepth}.");
            }

            return filter switch
            {
                BinaryExpression bin => TranslateBinary(bin, context),
                UnaryExpression un => TranslateUnary(un, context),
                PropertyReference prop => TranslateProperty(prop, context),
                Literal lit => TranslateLiteral(lit),
                SpatialPredicate spatial => TranslateSpatial(spatial, context),
                SpatialDistancePredicate spatialDistance => TranslateSpatialDistance(spatialDistance, context),
                TemporalPredicate temporal => TranslateTemporal(temporal, context),
                ArrayPredicate array => TranslateArrayPredicate(array, context),
                FunctionCall func => TranslateFunction(func, context),
                IntervalLiteral interval => TranslateIntervalLiteral(interval),
                ArrayLiteral arrayLiteral => TranslateArrayLiteral(arrayLiteral),
                ValueList list => TranslateValueList(list, context),
                _ => TranslateUnknown(filter)
            };
        }
        finally
        {
            _depth--;
        }
    }

    protected string AddParameter(object? value)
    {
        _parameters.Add(value);
        var prefix = Dialect.ParameterPrefix;
        var placeholder = prefix == "$"
            // DuckDB positional parameters are 1-based.
            ? $"${_paramIndex + 1}"
            : $"{prefix}p{_paramIndex}";

        _paramIndex++;
        return placeholder;
    }

    protected virtual string TranslateLiteral(Literal literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return literal.Type == LiteralType.Null ? "NULL" : AddParameter(literal.Value);
    }

    protected virtual string TranslateValueList(ValueList valueList, FilterTranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(valueList);
        var values = valueList.Values.Select(v => TranslateExpression(v, context));
        return $"({string.Join(", ", values)})";
    }

    protected abstract string TranslateBinary(BinaryExpression binary, FilterTranslationContext context);

    protected abstract string TranslateUnary(UnaryExpression unary, FilterTranslationContext context);

    protected abstract string TranslateProperty(PropertyReference property, FilterTranslationContext context);

    protected abstract string TranslateSpatial(SpatialPredicate spatial, FilterTranslationContext context);

    protected abstract string TranslateSpatialDistance(SpatialDistancePredicate spatial, FilterTranslationContext context);

    protected virtual string TranslateTemporal(TemporalPredicate temporal, FilterTranslationContext context)
        => throw UnsupportedExpression(temporal);

    protected virtual string TranslateArrayPredicate(ArrayPredicate array, FilterTranslationContext context)
        => throw UnsupportedExpression(array);

    protected virtual string TranslateFunction(FunctionCall function, FilterTranslationContext context)
        => throw UnsupportedExpression(function);

    protected virtual string TranslateIntervalLiteral(IntervalLiteral interval)
        => throw UnsupportedExpression(interval);

    protected virtual string TranslateArrayLiteral(ArrayLiteral arrayLiteral)
        => throw UnsupportedExpression(arrayLiteral);

    protected virtual string TranslateUnknown(FilterExpression filter)
        => throw UnsupportedExpression(filter);

    private NotSupportedException UnsupportedExpression(FilterExpression filter)
        => new($"Filter expression '{filter.GetType().Name}' is not supported by the {Dialect.Name} provider.");
}
