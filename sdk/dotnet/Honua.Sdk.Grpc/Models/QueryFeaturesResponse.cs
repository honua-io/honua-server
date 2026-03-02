// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc.Models;

/// <summary>
/// Response from a feature query.
/// </summary>
public sealed class QueryFeaturesResponse
{
    /// <summary>Name of the object ID field.</summary>
    public string ObjectIdFieldName { get; init; } = string.Empty;

    /// <summary>Geometry type of the layer.</summary>
    public GeometryType GeometryType { get; init; }

    /// <summary>Spatial reference of the response.</summary>
    public SpatialReference? SpatialReference { get; init; }

    /// <summary>Field definitions.</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; init; } = [];

    /// <summary>Features in the response.</summary>
    public IReadOnlyList<Feature> Features { get; init; } = [];

    /// <summary>Whether the transfer limit was exceeded.</summary>
    public bool ExceededTransferLimit { get; init; }

    /// <summary>Count (for count-only queries).</summary>
    public long Count { get; init; }

    /// <summary>Object IDs (for IDs-only queries).</summary>
    public IReadOnlyList<long> ObjectIds { get; init; } = [];

    /// <summary>Extent (for extent-only queries).</summary>
    public Extent? Extent { get; init; }
}
