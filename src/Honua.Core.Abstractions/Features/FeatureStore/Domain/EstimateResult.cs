// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Result of a getEstimates operation containing approximate count and extent
/// </summary>
public readonly record struct EstimateResult
{
    /// <summary>
    /// Estimated feature count for the layer
    /// </summary>
    public long EstimatedCount { get; init; }

    /// <summary>
    /// Estimated spatial extent, null if the layer has no geometry
    /// </summary>
    public FeatureExtent? Extent { get; init; }
}
