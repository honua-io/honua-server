// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Base class for all filter expressions in the shared AST
/// </summary>
public abstract record FilterExpression;

/// <summary>
/// Binary operation: AND, OR, =, &lt;&gt;, &lt;, &gt;, &lt;=, &gt;=, LIKE, IN
/// </summary>
public sealed record BinaryExpression(
    FilterExpression Left,
    BinaryOperator Operator,
    FilterExpression Right) : FilterExpression;

/// <summary>
/// Unary operation: NOT, IS NULL, IS NOT NULL
/// </summary>
public sealed record UnaryExpression(
    UnaryOperator Operator,
    FilterExpression Operand) : FilterExpression;

/// <summary>
/// Reference to a feature property/field
/// </summary>
public sealed record PropertyReference(string PropertyName) : FilterExpression;

/// <summary>
/// Literal value: string, number, boolean, null
/// </summary>
public sealed record Literal(object? Value, LiteralType Type) : FilterExpression;

/// <summary>
/// Spatial predicate: INTERSECTS, CONTAINS, WITHIN, CROSSES, etc.
/// </summary>
public sealed record SpatialPredicate(
    SpatialOperator Operator,
    FilterExpression Left,
    FilterExpression Right) : FilterExpression;

/// <summary>
/// Spatial predicate with a distance operand: DWithin, Beyond
/// </summary>
public sealed record SpatialDistancePredicate(
    SpatialOperator Operator,
    FilterExpression Left,
    FilterExpression Right,
    FilterExpression Distance) : FilterExpression;

/// <summary>
/// Temporal predicate: AFTER, BEFORE, INTERSECTS, etc.
/// </summary>
public sealed record TemporalPredicate(
    TemporalOperator Operator,
    FilterExpression Left,
    FilterExpression Right) : FilterExpression;

/// <summary>
/// Array predicate: A_CONTAINS, A_EQUALS, etc.
/// </summary>
public sealed record ArrayPredicate(
    ArrayOperator Operator,
    FilterExpression Left,
    FilterExpression Right) : FilterExpression;

/// <summary>
/// Geometry literal in WKB format (normalized from WKT, GeoJSON, GeoServices JSON)
/// </summary>
public sealed record GeometryLiteral(
    byte[] Wkb,
    int Srid,
    string OriginalFormat) : FilterExpression;

/// <summary>
/// Interval literal represented by start and end instants
/// </summary>
public sealed record IntervalLiteral(
    Literal? Start,
    Literal? End) : FilterExpression;

/// <summary>
/// Function call: UPPER, LOWER, LENGTH, etc.
/// </summary>
public sealed record FunctionCall(
    string FunctionName,
    IReadOnlyList<FilterExpression> Arguments) : FilterExpression;

/// <summary>
/// Array literal value
/// </summary>
public sealed record ArrayLiteral(
    IReadOnlyList<FilterExpression> Elements) : FilterExpression;

/// <summary>
/// List of values for IN operator
/// </summary>
public sealed record ValueList(IReadOnlyList<FilterExpression> Values) : FilterExpression;
