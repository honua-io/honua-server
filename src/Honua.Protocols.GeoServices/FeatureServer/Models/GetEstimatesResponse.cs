// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response for the getEstimates operation containing approximate count and spatial extent
/// </summary>
public sealed class GetEstimatesResponse
{
    /// <summary>
    /// Estimated feature count for the layer
    /// </summary>
    public long Count { get; init; }

    /// <summary>
    /// Estimated spatial extent of the layer
    /// </summary>
    public ExtentInfo? Extent { get; init; }
}
