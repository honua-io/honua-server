// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Generates advisory style suggestions for a metadata resource based on data analysis.
/// </summary>
public interface IStyleSuggestionService
{
    /// <summary>
    /// Analyzes a metadata resource's data and suggests an appropriate style.
    /// </summary>
    /// <param name="resource">The metadata v2 resource to analyze.</param>
    /// <param name="storageLayerId">Integer storage-layer handle used by feature profiling.</param>
    /// <param name="options">Optional caller overrides.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An advisory style suggestion.</returns>
    Task<StyleSuggestion> SuggestAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        StyleSuggestionOptions? options = null,
        CancellationToken cancellationToken = default);
}
