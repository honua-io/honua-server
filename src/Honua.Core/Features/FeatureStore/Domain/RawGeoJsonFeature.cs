// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a feature with geometry and properties preserved as raw GeoJSON fragments.
/// </summary>
public readonly record struct RawGeoJsonFeature
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
    /// Properties encoded as a raw JSON object, or null when no properties are present.
    /// </summary>
    public required string? PropertiesJson { get; init; }

    /// <summary>
    /// Creates a new raw GeoJSON feature with the specified properties.
    /// </summary>
    public static RawGeoJsonFeature Create(
        long id,
        string? geometryGeoJson,
        string? propertiesJson)
        => new() { Id = id, GeometryGeoJson = geometryGeoJson, PropertiesJson = propertiesJson };
}
