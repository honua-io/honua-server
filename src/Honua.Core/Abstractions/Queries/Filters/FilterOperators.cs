// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

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

public enum UnaryOperator
{
    Not,

    IsNull,

    IsNotNull,

    Negate
}

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
    DWithin,

    Beyond
}

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

public enum ArrayOperator
{
    Equals,

    Contains,

    ContainedBy,

    Overlaps
}

public enum LiteralType
{
    Text,

    Number,

    Boolean,

    Null,

    Date,

    DateTime
}
