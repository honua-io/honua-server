// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents a geographic feature with attributes and geometry
/// </summary>
public readonly record struct Feature
{
    /// <summary>
    /// Unique identifier for the feature
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Feature geometry in Well-Known Binary (WKB) format
    /// </summary>
    public required byte[]? Geometry { get; init; }

    /// <summary>
    /// Feature attributes as key-value pairs
    /// </summary>
    public required ImmutableDictionary<string, object?> Attributes { get; init; }

    /// <summary>
    /// Creates a new feature with the specified properties
    /// </summary>
    /// <param name="id">Feature identifier</param>
    /// <param name="geometry">Feature geometry in WKB format</param>
    /// <param name="attributes">Feature attributes</param>
    /// <returns>New feature instance</returns>
    public static Feature Create(long id, byte[]? geometry, ImmutableDictionary<string, object?> attributes)
        => new() { Id = id, Geometry = geometry, Attributes = attributes };

    /// <summary>
    /// Creates a new feature with empty attributes
    /// </summary>
    /// <param name="id">Feature identifier</param>
    /// <param name="geometry">Feature geometry in WKB format</param>
    /// <returns>New feature instance</returns>
    public static Feature Create(long id, byte[]? geometry)
        => new() { Id = id, Geometry = geometry, Attributes = ImmutableDictionary<string, object?>.Empty };
}
