// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters.Cql2;

/// <summary>
/// Represents a token in CQL2-Text expressions
/// </summary>
/// <param name="Type">The type of token</param>
/// <param name="Value">The string value of the token</param>
/// <param name="Position">The position in the input where this token starts</param>
/// <param name="Length">The length of the token in characters</param>
public sealed record Cql2Token(
    Cql2TokenType Type,
    string Value,
    int Position,
    int Length)
{
    /// <summary>
    /// Creates a new CQL2 token with the specified properties.
    /// </summary>
    /// <param name="type">The type of token being created.</param>
    /// <param name="value">The string value of the token.</param>
    /// <param name="position">The position of the token in the input string.</param>
    /// <param name="length">The length of the token. If -1, uses the value's length.</param>
    /// <returns>A new CQL2 token instance.</returns>
    public static Cql2Token Create(Cql2TokenType type, string value, int position, int length = -1)
        => new(type, value, position, length == -1 ? value.Length : length);
}

/// <summary>
/// Types of CQL2 tokens
/// </summary>
public enum Cql2TokenType
{
    // Literals
    /// <summary>
    /// Text/string literal
    /// </summary>
    Text,

    /// <summary>
    /// Numeric literal (integer or floating-point)
    /// </summary>
    Number,

    /// <summary>
    /// Boolean literal (true or false)
    /// </summary>
    Boolean,

    /// <summary>
    /// Null literal
    /// </summary>
    Null,

    // Identifiers
    /// <summary>
    /// Property/field identifier
    /// </summary>
    Identifier,

    // Operators
    /// <summary>
    /// Equality operator (=)
    /// </summary>
    Equal,

    /// <summary>
    /// Inequality operator (&lt;&gt;)
    /// </summary>
    NotEqual,

    /// <summary>
    /// Less than operator (&lt;)
    /// </summary>
    LessThan,

    /// <summary>
    /// Less than or equal operator (&lt;=)
    /// </summary>
    LessThanOrEqual,

    /// <summary>
    /// Greater than operator (&gt;)
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Greater than or equal operator (&gt;=)
    /// </summary>
    GreaterThanOrEqual,

    /// <summary>
    /// Like operator (LIKE)
    /// </summary>
    Like,

    /// <summary>
    /// Between operator (BETWEEN)
    /// </summary>
    Between,

    // Logical
    /// <summary>
    /// Logical AND operator
    /// </summary>
    And,

    /// <summary>
    /// Logical OR operator
    /// </summary>
    Or,

    /// <summary>
    /// Logical NOT operator
    /// </summary>
    Not,

    // Spatial predicates
    /// <inheritdoc/>
    S_Intersects,      // S_INTERSECTS
    /// <inheritdoc/>
    S_Contains,        // S_CONTAINS
    /// <inheritdoc/>
    S_Within,          // S_WITHIN
    /// <inheritdoc/>
    S_Crosses,         // S_CROSSES
    /// <inheritdoc/>
    S_Touches,         // S_TOUCHES
    /// <inheritdoc/>
    S_Overlaps,        // S_OVERLAPS
    /// <inheritdoc/>
    S_Disjoint,        // S_DISJOINT
    /// <inheritdoc/>
    S_Equals,          // S_EQUALS
    /// <inheritdoc/>
    S_DWithin,         // S_DWITHIN
    /// <inheritdoc/>
    S_Beyond,          // S_BEYOND

    // Temporal predicates
    /// <inheritdoc/>
    T_After,           // T_AFTER
    /// <inheritdoc/>
    T_Before,          // T_BEFORE
    /// <inheritdoc/>
    T_Contains,        // T_CONTAINS
    /// <inheritdoc/>
    T_Disjoint,        // T_DISJOINT
    /// <inheritdoc/>
    T_During,          // T_DURING
    /// <inheritdoc/>
    T_Equals,          // T_EQUALS
    /// <inheritdoc/>
    T_FinishedBy,      // T_FINISHEDBY
    /// <inheritdoc/>
    T_Finishes,        // T_FINISHES
    /// <inheritdoc/>
    T_Intersects,      // T_INTERSECTS
    /// <inheritdoc/>
    T_Meets,           // T_MEETS
    /// <inheritdoc/>
    T_MetBy,           // T_METBY
    /// <inheritdoc/>
    T_OverlappedBy,    // T_OVERLAPPEDBY
    /// <inheritdoc/>
    T_Overlaps,        // T_OVERLAPS
    /// <inheritdoc/>
    T_StartedBy,       // T_STARTEDBY
    /// <inheritdoc/>
    T_Starts,          // T_STARTS

    // Array predicates
    /// <inheritdoc/>
    A_Equals,          // A_EQUALS
    /// <inheritdoc/>
    A_Contains,        // A_CONTAINS
    /// <inheritdoc/>
    A_ContainedBy,     // A_CONTAINEDBY
    /// <inheritdoc/>
    A_Overlaps,        // A_OVERLAPS

    // Predicates
    /// <inheritdoc/>
    In,                // IN
    /// <inheritdoc/>
    IsNull,            // IS NULL

    // Arithmetic operators
    /// <inheritdoc/>
    Plus,              // +
    /// <inheritdoc/>
    Minus,             // -
    /// <inheritdoc/>
    Star,              // *
    /// <inheritdoc/>
    Slash,             // /
    /// <inheritdoc/>
    Percent,           // %
    /// <inheritdoc/>
    Caret,             // ^
    /// <inheritdoc/>
    Div,               // DIV

    // Functions
    /// <inheritdoc/>
    Function,          // UPPER, LOWER, etc.
    /// <inheritdoc/>
    Casei,             // CASEI
    /// <inheritdoc/>
    Accenti,           // ACCENTI
    /// <inheritdoc/>
    Date,              // DATE
    /// <inheritdoc/>
    Timestamp,         // TIMESTAMP
    /// <inheritdoc/>
    Interval,          // INTERVAL

    // Geometry literals
    /// <inheritdoc/>
    Point,             // POINT
    /// <inheritdoc/>
    LineString,        // LINESTRING
    /// <inheritdoc/>
    Polygon,           // POLYGON
    /// <inheritdoc/>
    MultiPoint,        // MULTIPOINT
    /// <inheritdoc/>
    MultiLineString,   // MULTILINESTRING
    /// <inheritdoc/>
    MultiPolygon,      // MULTIPOLYGON
    /// <inheritdoc/>
    GeometryCollection, // GEOMETRYCOLLECTION
    /// <inheritdoc/>
    Bbox,              // BBOX

    // Punctuation
    /// <inheritdoc/>
    LeftParen,         // (
    /// <inheritdoc/>
    RightParen,        // )
    /// <inheritdoc/>
    Comma,             // ,

    // Special
    /// <inheritdoc/>
    EndOfInput,
    /// <inheritdoc/>
    Unknown
}
