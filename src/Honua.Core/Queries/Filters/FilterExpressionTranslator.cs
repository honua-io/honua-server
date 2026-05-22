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
    /// <remarks>
    /// A V2 <see cref="Translate(FilterExpression, LayerDefinition)"/> overload is
    /// intentionally not exposed yet — final SQL translation still goes through the
    /// provider-specific v1 path (<see cref="ISqlFilterTranslator"/>) which requires a
    /// <see cref="LayerDefinition"/>. Consumers that just need to validate / coerce a
    /// filter against a V2 schema use this overload; consumers that need the
    /// final SQL fragment still feed a <see cref="LayerDefinition"/> through the v1
    /// <see cref="Translate(FilterExpression, LayerDefinition)"/> path until the SQL
    /// backends gain V2 overloads.
    /// </remarks>
    FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource);
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
}
