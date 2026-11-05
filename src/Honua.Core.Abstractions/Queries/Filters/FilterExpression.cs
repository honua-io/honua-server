// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

public abstract record FilterExpression;

public sealed record BinaryExpression(
    FilterExpression Left,
    BinaryOperator Operator,
    FilterExpression Right) : FilterExpression;

public sealed record UnaryExpression(
    UnaryOperator Operator,
    FilterExpression Operand) : FilterExpression;

public sealed record PropertyReference(string PropertyName) : FilterExpression;

public sealed record Literal(object? Value, LiteralType Type) : FilterExpression;

public sealed record SpatialPredicate(
    SpatialOperator Operator,
    FilterExpression Left,
    FilterExpression Right) : FilterExpression
{
    public bool Geodesic { get; init; }
}

public sealed record SpatialDistancePredicate(
    SpatialOperator Operator,
    FilterExpression Left,
    FilterExpression Right,
    FilterExpression Distance) : FilterExpression
{
    // An explicit unit must be converted into the projected CRS unit; a unitless value stays in the layer unit.
    public bool DistanceInMeters { get; init; }
}

public sealed record TemporalPredicate(
    TemporalOperator Operator,
    FilterExpression Left,
    FilterExpression Right) : FilterExpression;

public sealed record ArrayPredicate(
    ArrayOperator Operator,
    FilterExpression Left,
    FilterExpression Right) : FilterExpression;

public sealed record GeometryLiteral(
    byte[] Wkb,
    int Srid,
    string OriginalFormat) : FilterExpression;

public sealed record IntervalLiteral(
    Literal? Start,
    Literal? End) : FilterExpression;

public sealed record FunctionCall(
    string FunctionName,
    IReadOnlyList<FilterExpression> Arguments) : FilterExpression;

public sealed record ArrayLiteral(
    IReadOnlyList<FilterExpression> Elements) : FilterExpression;

public sealed record ValueList(IReadOnlyList<FilterExpression> Values) : FilterExpression;
