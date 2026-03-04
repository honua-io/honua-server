// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Models;

/// <summary>
/// Represents the result of a feature query.
/// </summary>
public sealed record QueryResult<T>
{
    /// <summary>
    /// The features returned by the query.
    /// </summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>
    /// The name of the object ID field.
    /// </summary>
    public string ObjectIdFieldName { get; init; } = "objectid";

    /// <summary>
    /// The geometry type of the layer.
    /// </summary>
    public GeometryType GeometryType { get; init; }

    /// <summary>
    /// The spatial reference of the results.
    /// </summary>
    public SpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Field definitions for the layer.
    /// </summary>
    public IReadOnlyList<FieldDefinition> Fields { get; init; } = Array.Empty<FieldDefinition>();

    /// <summary>
    /// Whether the query exceeded the transfer limit (more results available).
    /// </summary>
    public bool HasMoreResults { get; init; }

    /// <summary>
    /// Total count for count-only queries.
    /// </summary>
    public long? Count { get; init; }

    /// <summary>
    /// Object IDs for IDs-only queries.
    /// </summary>
    public IReadOnlyList<long>? ObjectIds { get; init; }

    /// <summary>
    /// Extent for extent-only queries.
    /// </summary>
    public Extent? Extent { get; init; }
}

/// <summary>
/// Geometry types supported by the system.
/// </summary>
public enum GeometryType
{
    None,
    Point,
    MultiPoint,
    LineString,
    MultiLineString,
    Polygon,
    MultiPolygon,
    GeometryCollection
}

/// <summary>
/// Represents a field definition in a layer schema.
/// </summary>
public sealed record FieldDefinition
{
    public string Name { get; init; } = string.Empty;
    public FieldType Type { get; init; }
    public int? Length { get; init; }
    public bool Nullable { get; init; } = true;
}

/// <summary>
/// Field types supported by the system.
/// </summary>
public enum FieldType
{
    String,
    Integer,
    BigInteger,
    Double,
    Float,
    Boolean,
    DateTime,
    Date,
    Time,
    Geometry,
    Json,
    Binary,
    Uuid
}

/// <summary>
/// Represents a bounding box extent.
/// </summary>
public sealed record Extent
{
    public double XMin { get; init; }
    public double YMin { get; init; }
    public double XMax { get; init; }
    public double YMax { get; init; }
    public SpatialReference? SpatialReference { get; init; }

    public static Extent Create(double xMin, double yMin, double xMax, double yMax, SpatialReference? spatialReference = null)
    {
        return new Extent
        {
            XMin = xMin,
            YMin = yMin,
            XMax = xMax,
            YMax = yMax,
            SpatialReference = spatialReference
        };
    }
}