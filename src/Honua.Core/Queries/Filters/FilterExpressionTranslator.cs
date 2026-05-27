// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

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

    /// <summary>
    /// V2 overload of <c>Normalize</c>. Resolves field types from
    /// <c>MetadataV2Resource.SchemaFields</c>.
    /// </summary>
    FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource);

    /// <summary>
    /// V2 overload that normalises and translates a filter expression to a
    /// parameterized SQL fragment, using a Metadata v2 resource for field
    /// validation and spatial-reference resolution.
    /// </summary>
    /// <param name="expression">Filter expression to translate.</param>
    /// <param name="resource">Metadata v2 resource.</param>
    /// <returns>SQL fragment with parameters.</returns>
    SqlFragment Translate(FilterExpression expression, MetadataV2Resource resource);
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

    /// <inheritdoc />
    public FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource)
        => FilterExpressionNormalizer.Normalize(expression, resource);

    /// <inheritdoc />
    public SqlFragment Translate(FilterExpression expression, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var normalized = FilterExpressionNormalizer.Normalize(expression, resource);
        return _sqlFilterTranslator.Translate(normalized, resource);
    }
}
