// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a feature with geometry encoded as a KML fragment.
/// </summary>
public readonly record struct KmlFeature
{
    /// <summary>
    /// Unique identifier for the feature.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Geometry encoded as a KML fragment, or null when geometry is absent.
    /// </summary>
    public required string? GeometryKml { get; init; }

    /// <summary>
    /// Feature attributes as key-value pairs.
    /// </summary>
    public required ImmutableDictionary<string, object?> Attributes { get; init; }

    /// <summary>
    /// Creates a new KML feature with the specified properties.
    /// </summary>
    /// <param name="id">Feature identifier.</param>
    /// <param name="geometryKml">Geometry encoded as KML.</param>
    /// <param name="attributes">Feature attributes.</param>
    /// <returns>New KML feature instance.</returns>
    public static KmlFeature Create(long id, string? geometryKml, ImmutableDictionary<string, object?> attributes)
        => new() { Id = id, GeometryKml = geometryKml, Attributes = attributes };
}
