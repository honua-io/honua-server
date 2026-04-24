// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// Response for a service-level getEstimates operation containing per-layer estimates.
/// </summary>
public sealed class ServiceGetEstimatesResponse
{
    /// <summary>
    /// Estimate results for each visible layer in the service.
    /// </summary>
    [JsonPropertyName("layers")]
    public ServiceLayerEstimateInfo[] Layers { get; init; } = [];
}

/// <summary>
/// Estimate metadata for a single layer in a service-level getEstimates response.
/// </summary>
public sealed class ServiceLayerEstimateInfo
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// Estimated feature count for the layer.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; init; }

    /// <summary>
    /// Estimated spatial extent of the layer.
    /// </summary>
    [JsonPropertyName("extent")]
    public ExtentInfo? Extent { get; init; }
}
