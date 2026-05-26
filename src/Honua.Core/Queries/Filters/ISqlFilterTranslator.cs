// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
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
    /// <param name="layer">Layer definition for field validation.</param>
    /// <returns>SQL fragment with parameters.</returns>
    SqlFragment Translate(FilterExpression filter, LayerDefinition layer);

    /// <summary>
    /// V2 overload that resolves field metadata, the geometry column, the
    /// primary-key field, and the SRID from a Metadata v2 resource. Used by
    /// handlers ported to V2 metadata that no longer carry a
    /// <see cref="LayerDefinition"/>.
    /// </summary>
    /// <param name="filter">Filter expression to translate.</param>
    /// <param name="resource">Metadata v2 resource for field/spatial resolution.</param>
    /// <returns>SQL fragment with parameters.</returns>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/>; providers
    /// opt in by overriding. The Postgres and MySql backends implement the
    /// overload; backends without a V2-shaped translator yet stay on the v1
    /// <see cref="Translate(FilterExpression, LayerDefinition)"/> path.
    /// </remarks>
    SqlFragment Translate(FilterExpression filter, MetadataV2Resource resource)
        => throw new NotSupportedException(
            $"The SQL filter translator '{GetType().Name}' does not yet implement the Metadata v2 overload.");
}
