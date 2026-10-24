// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Translates filter expressions into parameterized SQL fragments.
/// </summary>
public interface ISqlFilterTranslator
{
    /// <summary>
    /// Translates a filter expression to a parameterized SQL fragment.
    /// </summary>
    /// <param name="filter">Filter expression to translate.</param>
    /// <param name="resource">Metadata v2 resource for field/spatial resolution.</param>
    /// <returns>SQL fragment with parameters.</returns>
    SqlFragment Translate(FilterExpression filter, MetadataV2Resource resource);
}
