// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.Cql2;

/// <summary>
/// Represents a token in CQL2-Text expressions
/// </summary>
public sealed record Cql2Token(
    Cql2TokenType Type,
    string Value,
    int Position,
    int Length)
{
    /// <summary>
    /// Creates a token
    /// </summary>
    public static Cql2Token Create(Cql2TokenType type, string value, int position, int length = -1)
        => new(type, value, position, length == -1 ? value.Length : length);
}

/// <summary>
/// Types of CQL2 tokens
/// </summary>
public enum Cql2TokenType
{
    // Literals
    Text,
    Number,
    Boolean,
    Null,

    // Identifiers
    Identifier,

    // Operators
    Equal,              // =
    NotEqual,           // <>
    LessThan,          // <
    LessThanOrEqual,   // <=
    GreaterThan,       // >
    GreaterThanOrEqual, // >=
    Like,              // LIKE

    // Logical
    And,               // AND
    Or,                // OR
    Not,               // NOT

    // Spatial predicates
    S_Intersects,      // S_INTERSECTS
    S_Contains,        // S_CONTAINS
    S_Within,          // S_WITHIN
    S_Crosses,         // S_CROSSES
    S_Touches,         // S_TOUCHES
    S_Overlaps,        // S_OVERLAPS
    S_Disjoint,        // S_DISJOINT
    S_Equals,          // S_EQUALS

    // Predicates
    In,                // IN
    IsNull,            // IS NULL

    // Functions
    Function,          // UPPER, LOWER, etc.

    // Geometry literals
    Point,             // POINT
    LineString,        // LINESTRING
    Polygon,           // POLYGON
    MultiPoint,        // MULTIPOINT
    MultiLineString,   // MULTILINESTRING
    MultiPolygon,      // MULTIPOLYGON
    GeometryCollection, // GEOMETRYCOLLECTION

    // Punctuation
    LeftParen,         // (
    RightParen,        // )
    Comma,             // ,

    // Special
    EndOfInput,
    Unknown
}
