// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Queries.Filters;

public sealed class FilterExpressionTranslator : IFilterExpressionTranslator
{
    private readonly ISqlFilterTranslator _sqlFilterTranslator;

    public FilterExpressionTranslator(ISqlFilterTranslator sqlFilterTranslator)
    {
        _sqlFilterTranslator = sqlFilterTranslator ?? throw new ArgumentNullException(nameof(sqlFilterTranslator));
    }

    public FilterExpression Normalize(FilterExpression expression, MetadataV2Object resource)
        => FilterExpressionNormalizer.Normalize(expression, resource);

    public SqlFragment Translate(FilterExpression expression, MetadataV2Object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var normalized = FilterExpressionNormalizer.Normalize(expression, resource);
        return _sqlFilterTranslator.Translate(normalized, resource);
    }
}
