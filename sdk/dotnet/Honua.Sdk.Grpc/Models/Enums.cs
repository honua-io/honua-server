// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc.Models;

/// <summary>Supported attribute field types.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Enum values mirror proto FieldType names")]
public enum FieldType
{
    /// <summary>Unspecified field type.</summary>
    Unspecified = 0,
    /// <summary>String field.</summary>
    String = 1,
    /// <summary>32-bit integer field.</summary>
    Integer = 2,
    /// <summary>64-bit integer field.</summary>
    BigInteger = 3,
    /// <summary>Double-precision floating point field.</summary>
    Double = 4,
    /// <summary>Single-precision floating point field.</summary>
    Float = 5,
    /// <summary>Boolean field.</summary>
    Boolean = 6,
    /// <summary>Date and time field.</summary>
    DateTime = 7,
    /// <summary>Date-only field.</summary>
    Date = 8,
    /// <summary>Time-only field.</summary>
    Time = 9,
    /// <summary>Geometry field.</summary>
    Geometry = 10,
    /// <summary>JSON field.</summary>
    Json = 11,
    /// <summary>Binary field.</summary>
    Binary = 12,
    /// <summary>UUID field.</summary>
    Uuid = 13
}

/// <summary>Supported geometry types.</summary>
public enum GeometryType
{
    /// <summary>Unspecified geometry type.</summary>
    Unspecified = 0,
    /// <summary>Point geometry.</summary>
    Point = 1,
    /// <summary>Multi-point geometry.</summary>
    MultiPoint = 2,
    /// <summary>Line string geometry.</summary>
    LineString = 3,
    /// <summary>Multi-line string geometry.</summary>
    MultiLineString = 4,
    /// <summary>Polygon geometry.</summary>
    Polygon = 5,
    /// <summary>Multi-polygon geometry.</summary>
    MultiPolygon = 6,
    /// <summary>Geometry collection.</summary>
    GeometryCollection = 7,
    /// <summary>No geometry.</summary>
    None = 8
}

/// <summary>Spatial relationship types for filtering.</summary>
public enum SpatialRelationship
{
    /// <summary>Unspecified spatial relationship.</summary>
    Unspecified = 0,
    /// <summary>Geometries intersect.</summary>
    Intersects = 1,
    /// <summary>Geometry is within the filter geometry.</summary>
    Within = 2,
    /// <summary>Geometry contains the filter geometry.</summary>
    Contains = 3,
    /// <summary>Envelopes intersect.</summary>
    EnvelopeIntersects = 4,
    /// <summary>Geometries cross.</summary>
    Crosses = 5,
    /// <summary>Geometries touch.</summary>
    Touches = 6,
    /// <summary>Geometries overlap.</summary>
    Overlaps = 7,
    /// <summary>Geometries are disjoint.</summary>
    Disjoint = 8,
    /// <summary>Geometries are equal.</summary>
    Equals = 9,
    /// <summary>Geometry is within distance.</summary>
    WithinDistance = 10,
    /// <summary>Geometry is beyond distance.</summary>
    BeyondDistance = 11,
    /// <summary>Nearest neighbor query.</summary>
    NearestNeighbor = 12
}

/// <summary>Distance measurement units.</summary>
public enum DistanceUnit
{
    /// <summary>Unspecified distance unit.</summary>
    Unspecified = 0,
    /// <summary>Meters.</summary>
    Meters = 1,
    /// <summary>Feet.</summary>
    Feet = 2,
    /// <summary>Kilometers.</summary>
    Kilometers = 3,
    /// <summary>Miles.</summary>
    Miles = 4
}

/// <summary>Aggregate statistic functions.</summary>
public enum StatisticType
{
    /// <summary>Unspecified statistic type.</summary>
    Unspecified = 0,
    /// <summary>Count of values.</summary>
    Count = 1,
    /// <summary>Sum of values.</summary>
    Sum = 2,
    /// <summary>Minimum value.</summary>
    Min = 3,
    /// <summary>Maximum value.</summary>
    Max = 4,
    /// <summary>Average value.</summary>
    Avg = 5,
    /// <summary>Standard deviation.</summary>
    Stddev = 6,
    /// <summary>Variance.</summary>
    Var = 7
}
