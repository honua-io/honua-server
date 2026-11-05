// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#pragma warning disable CA1707 // CQL2 token names intentionally preserve spec predicate families.
namespace Honua.Core.Queries.Filters.Cql2;

public sealed record Cql2Token(
    Cql2TokenType Type,
    string Value,
    int Position,
    int Length)
{
    public static Cql2Token Create(Cql2TokenType type, string value, int position, int length = -1)
        => new(type, value, position, length == -1 ? value.Length : length);
}

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
    Equal,

    NotEqual,

    LessThan,

    LessThanOrEqual,

    GreaterThan,

    GreaterThanOrEqual,

    Like,

    Between,

    // Logical
    And,

    Or,

    Not,

    // Spatial predicates
    S_Intersects,      // S_INTERSECTS
    S_Contains,        // S_CONTAINS
    S_Within,          // S_WITHIN
    S_Crosses,         // S_CROSSES
    S_Touches,         // S_TOUCHES
    S_Overlaps,        // S_OVERLAPS
    S_Disjoint,        // S_DISJOINT
    S_Equals,          // S_EQUALS
    S_DWithin,         // S_DWITHIN
    S_Beyond,          // S_BEYOND

    // Temporal predicates
    T_After,           // T_AFTER
    T_Before,          // T_BEFORE
    T_Contains,        // T_CONTAINS
    T_Disjoint,        // T_DISJOINT
    T_During,          // T_DURING
    T_Equals,          // T_EQUALS
    T_FinishedBy,      // T_FINISHEDBY
    T_Finishes,        // T_FINISHES
    T_Intersects,      // T_INTERSECTS
    T_Meets,           // T_MEETS
    T_MetBy,           // T_METBY
    T_OverlappedBy,    // T_OVERLAPPEDBY
    T_Overlaps,        // T_OVERLAPS
    T_StartedBy,       // T_STARTEDBY
    T_Starts,          // T_STARTS

    // Array predicates
    A_Equals,          // A_EQUALS
    A_Contains,        // A_CONTAINS
    A_ContainedBy,     // A_CONTAINEDBY
    A_Overlaps,        // A_OVERLAPS

    // Predicates
    In,                // IN
    IsNull,            // IS NULL

    // Arithmetic operators
    Plus,              // +
    Minus,             // -
    Star,              // *
    Slash,             // /
    Percent,           // %
    Caret,             // ^
    Div,               // DIV

    // Functions
    Function,          // UPPER, LOWER, etc.
    Casei,             // CASEI
    Accenti,           // ACCENTI
    Date,              // DATE
    Timestamp,         // TIMESTAMP
    Interval,          // INTERVAL

    // Geometry literals
    Point,             // POINT
    LineString,        // LINESTRING
    Polygon,           // POLYGON
    MultiPoint,        // MULTIPOINT
    MultiLineString,   // MULTILINESTRING
    MultiPolygon,      // MULTIPOLYGON
    GeometryCollection, // GEOMETRYCOLLECTION
    Bbox,              // BBOX

    // Punctuation
    LeftParen,         // (
    RightParen,        // )
    Comma,             // ,

    // Special
    EndOfInput,
    Unknown
}
#pragma warning restore CA1707
