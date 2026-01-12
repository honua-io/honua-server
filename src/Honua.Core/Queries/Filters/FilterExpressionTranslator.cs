// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Translates normalized filter expressions into SQL fragments.
/// </summary>
public interface IFilterExpressionTranslator
{
    /// <summary>
    /// Normalizes a filter expression based on layer schema.
    /// </summary>
    /// <param name="expression">Filter expression to normalize.</param>
    /// <param name="layer">Layer definition used for type coercion.</param>
    /// <returns>Normalized filter expression.</returns>
    FilterExpression Normalize(FilterExpression expression, LayerDefinition layer);

    /// <summary>
    /// Translates a filter expression to a parameterized SQL fragment.
    /// </summary>
    /// <param name="expression">Filter expression to translate.</param>
    /// <param name="layer">Layer definition used for field validation.</param>
    /// <returns>SQL fragment with parameters.</returns>
    SqlFragment Translate(FilterExpression expression, LayerDefinition layer);
}

/// <summary>
/// Default implementation for translating filter expressions to SQL.
/// </summary>
public sealed class FilterExpressionTranslator : IFilterExpressionTranslator
{
    private readonly ISqlFilterTranslator _sqlFilterTranslator;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterExpressionTranslator"/> class.
    /// </summary>
    /// <param name="sqlFilterTranslator">SQL translator implementation.</param>
    public FilterExpressionTranslator(ISqlFilterTranslator sqlFilterTranslator)
    {
        _sqlFilterTranslator = sqlFilterTranslator ?? throw new ArgumentNullException(nameof(sqlFilterTranslator));
    }

    /// <inheritdoc />
    public FilterExpression Normalize(FilterExpression expression, LayerDefinition layer)
        => FilterExpressionNormalizer.Normalize(expression, layer);

    /// <inheritdoc />
    public SqlFragment Translate(FilterExpression expression, LayerDefinition layer)
    {
        var normalized = FilterExpressionNormalizer.Normalize(expression, layer);
        return _sqlFilterTranslator.Translate(normalized, layer);
    }
}
