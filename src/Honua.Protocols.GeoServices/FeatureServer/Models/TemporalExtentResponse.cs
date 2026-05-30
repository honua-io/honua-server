// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response for the temporalExtent endpoint exposing the resolved temporal field
/// metadata and the layer's observed time bounds. Min/max values may be null when
/// the layer has temporal field configuration but no rows have been ingested.
/// </summary>
public sealed class TemporalExtentResponse
{
    /// <summary>
    /// Layer identifier the response describes.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Layer display name.
    /// </summary>
    public string? LayerName { get; init; }

    /// <summary>
    /// Resolved start-time field name. Always present for time-aware layers.
    /// </summary>
    public string? StartTimeField { get; init; }

    /// <summary>
    /// Resolved end-time field name. Null when the layer represents instants only.
    /// </summary>
    public string? EndTimeField { get; init; }

    /// <summary>
    /// Lower bound of the temporal extent as ISO 8601 UTC. Null if the layer is empty.
    /// </summary>
    public string? Min { get; init; }

    /// <summary>
    /// Upper bound of the temporal extent as ISO 8601 UTC. Null if the layer is empty.
    /// </summary>
    public string? Max { get; init; }

    /// <summary>
    /// Lower bound expressed as Unix epoch milliseconds. Mirrors GeoServices REST timeExtent.
    /// </summary>
    public long? MinEpochMs { get; init; }

    /// <summary>
    /// Upper bound expressed as Unix epoch milliseconds. Mirrors GeoServices REST timeExtent.
    /// </summary>
    public long? MaxEpochMs { get; init; }
}
