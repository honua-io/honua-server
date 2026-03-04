// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Models;

/// <summary>
/// Represents a single geospatial feature with attributes and geometry.
/// </summary>
public sealed record Feature
{
    /// <summary>
    /// The unique identifier for this feature.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Feature attributes as key-value pairs.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// The geometry of this feature (optional).
    /// </summary>
    public Geometry? Geometry { get; init; }

    /// <summary>
    /// Creates a new feature with the specified ID and attributes.
    /// </summary>
    public static Feature Create(long id, IReadOnlyDictionary<string, object?> attributes, Geometry? geometry = null)
    {
        return new Feature
        {
            Id = id,
            Attributes = attributes,
            Geometry = geometry
        };
    }

    /// <summary>
    /// Creates a new feature with the specified attributes (for creating new features without IDs).
    /// </summary>
    public static Feature Create(IReadOnlyDictionary<string, object?> attributes, Geometry? geometry = null)
    {
        return new Feature
        {
            Id = 0, // Will be assigned by server
            Attributes = attributes,
            Geometry = geometry
        };
    }
}