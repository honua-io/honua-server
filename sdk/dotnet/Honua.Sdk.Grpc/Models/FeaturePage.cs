// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc.Models;

/// <summary>
/// A single page in a streaming feature query response.
/// </summary>
public sealed class FeaturePage
{
    /// <summary>Name of the object ID field (first page only).</summary>
    public string ObjectIdFieldName { get; init; } = string.Empty;

    /// <summary>Geometry type (first page only).</summary>
    public GeometryType GeometryType { get; init; }

    /// <summary>Spatial reference (first page only).</summary>
    public SpatialReference? SpatialReference { get; init; }

    /// <summary>Field definitions (first page only).</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; init; } = [];

    /// <summary>Features in this page.</summary>
    public IReadOnlyList<Feature> Features { get; init; } = [];

    /// <summary>Whether this is the final page.</summary>
    public bool IsLastPage { get; init; }
}
