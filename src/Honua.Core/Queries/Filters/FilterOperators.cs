// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Binary operators for filter expressions
/// </summary>
public enum BinaryOperator
{
    // Logical operators
    /// <summary>
    /// Logical AND operator
    /// </summary>
    And,

    /// <summary>
    /// Logical OR operator
    /// </summary>
    Or,

    // Comparison operators
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

    // String operators
    /// <summary>
    /// String pattern matching operator (LIKE)
    /// </summary>
    Like,

    /// <summary>
    /// Negated string pattern matching operator (NOT LIKE)
    /// </summary>
    NotLike,

    // Collection operators
    /// <summary>
    /// Set membership operator (IN)
    /// </summary>
    In,

    /// <summary>
    /// Negated set membership operator (NOT IN)
    /// </summary>
    NotIn,

    // Arithmetic operators
    /// <summary>
    /// Addition operator (+)
    /// </summary>
    Add,

    /// <summary>
    /// Subtraction operator (-)
    /// </summary>
    Subtract,

    /// <summary>
    /// Multiplication operator (*)
    /// </summary>
    Multiply,

    /// <summary>
    /// Division operator (/)
    /// </summary>
    Divide,

    /// <summary>
    /// Modulo operator (%)
    /// </summary>
    Modulo,

    /// <summary>
    /// Integer division operator (DIV)
    /// </summary>
    Div,

    /// <summary>
    /// Exponentiation operator (^)
    /// </summary>
    Power
}

/// <summary>
/// Unary operators for filter expressions
/// </summary>
public enum UnaryOperator
{
    /// <summary>
    /// Logical negation operator (NOT)
    /// </summary>
    Not,

    /// <summary>
    /// Null check operator (IS NULL)
    /// </summary>
    IsNull,

    /// <summary>
    /// Not null check operator (IS NOT NULL)
    /// </summary>
    IsNotNull,

    /// <summary>
    /// Arithmetic negation operator (-)
    /// </summary>
    Negate
}

/// <summary>
/// Spatial operators for spatial predicates
/// </summary>
public enum SpatialOperator
{
    /// <summary>
    /// Tests if geometries spatially intersect
    /// </summary>
    Intersects,

    /// <summary>
    /// Tests if the first geometry spatially contains the second
    /// </summary>
    Contains,

    /// <summary>
    /// Tests if the first geometry is spatially within the second
    /// </summary>
    Within,

    /// <summary>
    /// Tests if geometries spatially cross
    /// </summary>
    Crosses,

    /// <summary>
    /// Tests if geometries spatially touch
    /// </summary>
    Touches,

    /// <summary>
    /// Tests if geometries spatially overlap
    /// </summary>
    Overlaps,

    /// <summary>
    /// Tests if geometries are spatially disjoint (do not intersect)
    /// </summary>
    Disjoint,

    /// <summary>
    /// Tests if geometries are spatially equal
    /// </summary>
    Equals,

    // Distance-based operators
    /// <summary>
    /// Tests if the first geometry is within the specified distance of the second
    /// </summary>
    DWithin,

    /// <summary>
    /// Tests if the first geometry is beyond the specified distance from the second
    /// </summary>
    Beyond
}

/// <summary>
/// Temporal operators for temporal predicates
/// </summary>
public enum TemporalOperator
{
    /// <summary>
    /// Tests if the first temporal entity occurs after the second
    /// </summary>
    After,

    /// <summary>
    /// Tests if the first temporal entity occurs before the second
    /// </summary>
    Before,

    /// <summary>
    /// Tests if the first temporal entity temporally contains the second
    /// </summary>
    Contains,

    /// <summary>
    /// Tests if temporal entities are disjoint (do not overlap in time)
    /// </summary>
    Disjoint,

    /// <summary>
    /// Tests if the first temporal entity occurs during the second
    /// </summary>
    During,

    /// <summary>
    /// Tests if temporal entities are temporally equal
    /// </summary>
    Equals,

    /// <summary>
    /// Tests if the first temporal entity is finished by the second
    /// </summary>
    FinishedBy,

    /// <summary>
    /// Tests if the first temporal entity finishes the second
    /// </summary>
    Finishes,

    /// <summary>
    /// Tests if temporal entities intersect (overlap) in time
    /// </summary>
    Intersects,

    /// <summary>
    /// Tests if the first temporal entity meets the second (end touches start)
    /// </summary>
    Meets,

    /// <summary>
    /// Tests if the first temporal entity is met by the second
    /// </summary>
    MetBy,

    /// <summary>
    /// Tests if the first temporal entity is overlapped by the second
    /// </summary>
    OverlappedBy,

    /// <summary>
    /// Tests if the first temporal entity overlaps the second
    /// </summary>
    Overlaps,

    /// <summary>
    /// Tests if the first temporal entity is started by the second
    /// </summary>
    StartedBy,

    /// <summary>
    /// Tests if the first temporal entity starts the second
    /// </summary>
    Starts
}

/// <summary>
/// Array operators for array predicates
/// </summary>
public enum ArrayOperator
{
    /// <summary>
    /// Tests if arrays are equal
    /// </summary>
    Equals,

    /// <summary>
    /// Tests if the first array contains the second
    /// </summary>
    Contains,

    /// <summary>
    /// Tests if the first array is contained by the second
    /// </summary>
    ContainedBy,

    /// <summary>
    /// Tests if arrays overlap (have common elements)
    /// </summary>
    Overlaps
}

/// <summary>
/// Types of literal values
/// </summary>
public enum LiteralType
{
    /// <summary>
    /// String/text literal value
    /// </summary>
    Text,

    /// <summary>
    /// Numeric literal value (integer or floating-point)
    /// </summary>
    Number,

    /// <summary>
    /// Boolean literal value (true or false)
    /// </summary>
    Boolean,

    /// <summary>
    /// Null literal value
    /// </summary>
    Null,

    /// <summary>
    /// Date literal value (date without time)
    /// </summary>
    Date,

    /// <summary>
    /// DateTime literal value (date with time and optional timezone)
    /// </summary>
    DateTime
}
