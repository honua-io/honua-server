// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Evaluates whether a streaming event matches a subscription filter.
/// Implementations must be synchronous and allocation-free on the comparison path
/// to avoid blocking the broadcast hot path.
/// </summary>
internal interface IStreamSubscriptionFilter
{
    /// <summary>
    /// Returns true if the event matches this subscription's filter criteria.
    /// </summary>
    /// <param name="envelope">Event metadata envelope.</param>
    /// <param name="geometryEnvelope">Geometry bounding box (MinX, MinY, MaxX, MaxY), null for deletes.</param>
    /// <param name="propertiesJson">Pre-serialized JSON of feature attributes, null for deletes.</param>
    bool Matches(FeatureStreamEnvelope envelope, double[]? geometryEnvelope, string? propertiesJson);

    /// <summary>
    /// Human-readable summary for admin visibility.
    /// </summary>
    string Summary { get; }
}
