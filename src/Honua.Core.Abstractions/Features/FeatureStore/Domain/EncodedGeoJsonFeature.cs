// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a feature with geometry encoded as a GeoJSON geometry fragment.
/// </summary>
public readonly record struct EncodedGeoJsonFeature
{
    /// <summary>
    /// Unique identifier for the feature.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Geometry encoded as a GeoJSON geometry object, or null when geometry is absent.
    /// </summary>
    public required string? GeometryGeoJson { get; init; }

    /// <summary>
    /// Feature attributes as key-value pairs.
    /// </summary>
    public required ImmutableDictionary<string, object?> Attributes { get; init; }

    /// <summary>
    /// Creates a new encoded GeoJSON feature with the specified properties.
    /// </summary>
    /// <param name="id">Feature identifier.</param>
    /// <param name="geometryGeoJson">Geometry encoded as GeoJSON.</param>
    /// <param name="attributes">Feature attributes.</param>
    /// <returns>New encoded GeoJSON feature instance.</returns>
    public static EncodedGeoJsonFeature Create(
        long id,
        string? geometryGeoJson,
        ImmutableDictionary<string, object?> attributes)
        => new() { Id = id, GeometryGeoJson = geometryGeoJson, Attributes = attributes };
}
