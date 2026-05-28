// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a feature with geometry encoded as a GML fragment.
/// </summary>
public readonly record struct GmlFeature
{
    /// <summary>
    /// Unique identifier for the feature.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Geometry encoded as a GML fragment (or null if no geometry).
    /// </summary>
    public required string? GeometryGml { get; init; }

    /// <summary>
    /// Feature attributes as key-value pairs.
    /// </summary>
    public required ImmutableDictionary<string, object?> Attributes { get; init; }

    /// <summary>
    /// Creates a new GML feature with the specified properties.
    /// </summary>
    /// <param name="id">Feature identifier.</param>
    /// <param name="geometryGml">Geometry encoded as GML.</param>
    /// <param name="attributes">Feature attributes.</param>
    /// <returns>New GML feature instance.</returns>
    public static GmlFeature Create(long id, string? geometryGml, ImmutableDictionary<string, object?> attributes)
        => new() { Id = id, GeometryGml = geometryGml, Attributes = attributes };
}
