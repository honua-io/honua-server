// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Shared recursion + bookkeeping for per-provider SQL filter translators.
/// </summary>
/// <remarks>
/// <para>
/// The four data providers historically each hand-wrote a recursive translator with its
/// own copy of the depth-limit check, parameter accumulator, dispatch switch, and
/// literal / value-list / binary / unary translation. The base centralises the parts
/// that are genuinely provider-independent:
/// </para>
/// <list type="bullet">
///   <item>parameter accumulation (<see cref="AddParameter(object?)"/>) using the dialect's
///         <see cref="ISqlDialect.ParameterPrefix"/>;</item>
///   <item>nesting-depth enforcement via <see cref="MaxExpressionDepth"/>;</item>
///   <item>literal and value-list translation;</item>
///   <item>the top-level dispatch switch, which calls protected <c>Translate*</c> hooks
///         that subclasses override.</item>
/// </list>
/// <para>
/// Each subclass overrides the hooks for the AST kinds it supports (and may throw
/// <see cref="NotSupportedException"/> for the rest). Provider-specific concerns
/// (geometry SRID handling, function-name maps, temporal-interval ranges, JSON
/// attribute access) live in the subclass; the base never makes a dialect-specific
/// choice on its own.
/// </para>
/// <para>
/// The base is single-use per <see cref="Translate(FilterExpression, FilterTranslationContext)"/>
/// call. Translators are scoped DI services that allocate one instance per request, so
/// the mutable bookkeeping is safe; concurrent use across requests is unsupported.
/// </para>
/// </remarks>
public abstract class SqlFilterExpressionVisitorBase
{
    /// <summary>
    /// Maximum allowed nesting depth for filter expressions. Mirrors the constant
    /// defined by <c>FilterExpressionNormalizer.MaxExpressionDepth</c> in
    /// <c>Honua.Core</c>; duplicated here because the base lives in the abstractions
    /// assembly which cannot reference Core. The depth check prevents stack overflow
    /// from maliciously deep filters.
    /// </summary>
    public const int MaxExpressionDepth = 50;

    private readonly List<object?> _parameters = [];
    private int _paramIndex;
    private int _depth;

    /// <summary>
    /// Initialises the visitor with the provider's SQL dialect.
    /// </summary>
    /// <param name="dialect">Provider-specific SQL dialect. Must not be <c>null</c>.</param>
    protected SqlFilterExpressionVisitorBase(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        Dialect = dialect;
    }

    /// <summary>SQL dialect injected at construction.</summary>
    protected ISqlDialect Dialect { get; }

    /// <summary>
    /// Translates <paramref name="filter"/> into a <see cref="SqlFragment"/> against
    /// the given <paramref name="context"/>. Resets all internal state.
    /// </summary>
    public SqlFragment Translate(FilterExpression filter, FilterTranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _paramIndex = 0;
        _depth = 0;
        _parameters.Clear();

        var sql = TranslateExpression(filter, context);
        return new SqlFragment(sql, _parameters);
    }

    /// <summary>
    /// Recursive entry point invoked by hooks. Enforces the depth cap and dispatches
    /// to the appropriate provider-overridable hook.
    /// </summary>
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

    /// <summary>
    /// Adds a parameter and returns its placeholder ( <c>@p0</c>, <c>$1</c>, …) using
    /// <see cref="ISqlDialect.ParameterPrefix"/>. Postgres / MySQL / SQL Server emit
    /// <c>@p{index}</c> (the named-parameter form their drivers accept); DuckDB emits
    /// <c>${index+1}</c> (positional, 1-based). The base computes the right form from
    /// the dialect so subclasses do not need to know.
    /// </summary>
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

    /// <summary>Translates a <see cref="Literal"/> into a single parameter placeholder.</summary>
    protected virtual string TranslateLiteral(Literal literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return AddParameter(literal.Value);
    }

    /// <summary>Translates a <see cref="ValueList"/> into a parenthesised comma-joined list.</summary>
    protected virtual string TranslateValueList(ValueList valueList, FilterTranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(valueList);
        var values = valueList.Values.Select(v => TranslateExpression(v, context));
        return $"({string.Join(", ", values)})";
    }

    /// <summary>Translates a <see cref="BinaryExpression"/>. Subclasses MUST implement.</summary>
    protected abstract string TranslateBinary(BinaryExpression binary, FilterTranslationContext context);

    /// <summary>Translates a <see cref="UnaryExpression"/>. Subclasses MUST implement.</summary>
    protected abstract string TranslateUnary(UnaryExpression unary, FilterTranslationContext context);

    /// <summary>Translates a <see cref="PropertyReference"/>. Subclasses MUST implement.</summary>
    protected abstract string TranslateProperty(PropertyReference property, FilterTranslationContext context);

    /// <summary>Translates a <see cref="SpatialPredicate"/>. Subclasses MUST implement (may throw <see cref="NotSupportedException"/>).</summary>
    protected abstract string TranslateSpatial(SpatialPredicate spatial, FilterTranslationContext context);

    /// <summary>Translates a <see cref="SpatialDistancePredicate"/>. Subclasses MUST implement (may throw <see cref="NotSupportedException"/>).</summary>
    protected abstract string TranslateSpatialDistance(SpatialDistancePredicate spatial, FilterTranslationContext context);

    /// <summary>
    /// Translates a <see cref="TemporalPredicate"/>. Default implementation throws
    /// <see cref="NotSupportedException"/>; providers that support temporal predicates override.
    /// </summary>
    protected virtual string TranslateTemporal(TemporalPredicate temporal, FilterTranslationContext context)
        => throw UnsupportedExpression(temporal);

    /// <summary>
    /// Translates an <see cref="ArrayPredicate"/>. Default implementation throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual string TranslateArrayPredicate(ArrayPredicate array, FilterTranslationContext context)
        => throw UnsupportedExpression(array);

    /// <summary>
    /// Translates a <see cref="FunctionCall"/>. Default implementation throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual string TranslateFunction(FunctionCall function, FilterTranslationContext context)
        => throw UnsupportedExpression(function);

    /// <summary>
    /// Translates an <see cref="IntervalLiteral"/>. Default implementation throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual string TranslateIntervalLiteral(IntervalLiteral interval)
        => throw UnsupportedExpression(interval);

    /// <summary>
    /// Translates an <see cref="ArrayLiteral"/>. Default implementation throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual string TranslateArrayLiteral(ArrayLiteral arrayLiteral)
        => throw UnsupportedExpression(arrayLiteral);

    /// <summary>
    /// Invoked when the dispatcher encounters an AST node the base does not recognise.
    /// Default throws <see cref="NotSupportedException"/>.
    /// </summary>
    protected virtual string TranslateUnknown(FilterExpression filter)
        => throw UnsupportedExpression(filter);

    /// <summary>
    /// Builds the canonical "this AST kind is not supported by this dialect" exception.
    /// Centralising the message means the dispatcher catch-all and every per-kind default
    /// stub produce the same diagnostic — and existing per-provider tests that match on
    /// the AST type name keep matching.
    /// </summary>
    private NotSupportedException UnsupportedExpression(FilterExpression filter)
        => new($"Filter expression '{filter.GetType().Name}' is not supported by the {Dialect.Name} provider.");
}
