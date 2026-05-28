// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Queries.Filters;

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
