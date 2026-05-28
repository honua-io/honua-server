// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Translates normalized filter expressions into SQL fragments.
/// </summary>
public interface IFilterExpressionTranslator
{
    /// <summary>
    /// Normalizes a filter expression. Resolves field types from
    /// <c>MetadataV2Resource.SchemaFields</c>.
    /// </summary>
    FilterExpression Normalize(FilterExpression expression, MetadataV2Resource resource);

    /// <summary>
    /// Normalises and translates a filter expression to a
    /// parameterized SQL fragment, using a Metadata v2 resource for field
    /// validation and spatial-reference resolution.
    /// </summary>
    /// <param name="expression">Filter expression to translate.</param>
    /// <param name="resource">Metadata v2 resource.</param>
    /// <returns>SQL fragment with parameters.</returns>
    SqlFragment Translate(FilterExpression expression, MetadataV2Resource resource);
}
