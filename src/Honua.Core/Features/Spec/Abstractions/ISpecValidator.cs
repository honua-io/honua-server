// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Orchestrates the resolve → type-check → semantic-check passes over a
/// parsed <see cref="SpecDocument"/>, returning the accumulated diagnostics.
/// </summary>
public interface ISpecValidator
{
    /// <summary>
    /// Validates <paramref name="document"/> against <paramref name="catalog"/>.
    /// </summary>
    /// <param name="document">Parsed AST (typically from <see cref="ISpecParser"/>).</param>
    /// <param name="catalog">Catalog snapshot used to resolve external refs.</param>
    /// <returns>Validation result (diagnostics + summary).</returns>
    SpecValidationResult Validate(SpecDocument document, ISpecCatalogSnapshot? catalog = null);
}
