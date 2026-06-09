// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Raw point feature payload used by GeoServices REST JSON fast paths.
/// </summary>
public readonly record struct RawGeoServicesFeature
{
    /// <summary>
    /// Internal object id.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Public id encoded as a raw JSON scalar, or null when the object id should be used.
    /// </summary>
    public string? PublicIdJson { get; init; }

    /// <summary>
    /// Attributes encoded as a raw JSON object, or null when no attributes are present.
    /// </summary>
    public string? AttributesJson { get; init; }

    /// <summary>
    /// Projected point x coordinate.
    /// </summary>
    public double? X { get; init; }

    /// <summary>
    /// Projected point y coordinate.
    /// </summary>
    public double? Y { get; init; }

    /// <summary>
    /// Creates a feature using the object id as the public identifier.
    /// </summary>
    /// <param name="id">The internal object id.</param>
    /// <param name="attributesJson">Attributes encoded as a raw JSON object, or null.</param>
    /// <param name="x">Projected point x coordinate.</param>
    /// <param name="y">Projected point y coordinate.</param>
    /// <returns>The constructed feature payload.</returns>
    public static RawGeoServicesFeature Create(long id, string? attributesJson, double? x, double? y)
        => new() { Id = id, AttributesJson = attributesJson, X = x, Y = y };

    /// <summary>
    /// Creates a feature with an explicit public identifier JSON scalar.
    /// </summary>
    /// <param name="id">The internal object id.</param>
    /// <param name="publicIdJson">Public id encoded as a raw JSON scalar, or null.</param>
    /// <param name="attributesJson">Attributes encoded as a raw JSON object, or null.</param>
    /// <param name="x">Projected point x coordinate.</param>
    /// <param name="y">Projected point y coordinate.</param>
    /// <returns>The constructed feature payload.</returns>
    public static RawGeoServicesFeature Create(long id, string? publicIdJson, string? attributesJson, double? x, double? y)
        => new() { Id = id, PublicIdJson = publicIdJson, AttributesJson = attributesJson, X = x, Y = y };
}
