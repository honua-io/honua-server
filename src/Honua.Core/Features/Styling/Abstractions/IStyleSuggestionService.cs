// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Generates advisory style suggestions for a layer based on data analysis.
/// </summary>
public interface IStyleSuggestionService
{
    /// <summary>
    /// Analyzes a layer's data and suggests an appropriate style.
    /// </summary>
    /// <param name="layer">The layer definition to analyze.</param>
    /// <param name="options">Optional caller overrides.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An advisory style suggestion.</returns>
    Task<StyleSuggestion> SuggestAsync(
        LayerDefinition layer,
        StyleSuggestionOptions? options = null,
        CancellationToken cancellationToken = default);
}
