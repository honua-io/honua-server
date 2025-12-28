// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Binary operators for filter expressions
/// </summary>
public enum BinaryOperator
{
    // Logical operators
    And,
    Or,

    // Comparison operators
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,

    // String operators
    Like,
    NotLike,

    // Collection operators
    In,
    NotIn,

    // Arithmetic operators
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Div,
    Power
}

/// <summary>
/// Unary operators for filter expressions
/// </summary>
public enum UnaryOperator
{
    Not,
    IsNull,
    IsNotNull,
    Negate
}

/// <summary>
/// Spatial operators for spatial predicates
/// </summary>
public enum SpatialOperator
{
    Intersects,
    Contains,
    Within,
    Crosses,
    Touches,
    Overlaps,
    Disjoint,
    Equals,

    // Distance-based operators
    DWithin,    // Within distance
    Beyond      // Beyond distance
}

/// <summary>
/// Temporal operators for temporal predicates
/// </summary>
public enum TemporalOperator
{
    After,
    Before,
    Contains,
    Disjoint,
    During,
    Equals,
    FinishedBy,
    Finishes,
    Intersects,
    Meets,
    MetBy,
    OverlappedBy,
    Overlaps,
    StartedBy,
    Starts
}

/// <summary>
/// Array operators for array predicates
/// </summary>
public enum ArrayOperator
{
    Equals,
    Contains,
    ContainedBy,
    Overlaps
}

/// <summary>
/// Types of literal values
/// </summary>
public enum LiteralType
{
    Text,
    Number,
    Boolean,
    Null,
    Date,
    DateTime
}
