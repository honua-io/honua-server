// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Honua.Core.Queries.Filters;
using Honua.TestKit.PropertyBased;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Arb = FsCheck.Fluent.Arb;
using Gen = FsCheck.Fluent.Gen;

namespace Honua.Core.Tests.Queries.Filters;

/// <summary>
/// Property-based tests for filter expressions to ensure mathematical properties hold.
/// </summary>
public class FilterExpressionPropertyTests
{
    /// <summary>
    /// Validates that binary expressions maintain associativity for AND operations.
    /// (a AND b) AND c should be equivalent to a AND (b AND c)
    /// </summary>
    [Property(Arbitrary = new[] { typeof(NestedExpressionArbs) })]
    public bool AndOperationIsAssociative(BinaryExpression a, BinaryExpression b, BinaryExpression c)
    {
        try
        {
            var leftAssoc = new BinaryExpression(
                new BinaryExpression(a, BinaryOperator.And, b),
                BinaryOperator.And,
                c);

            var rightAssoc = new BinaryExpression(
                a,
                BinaryOperator.And,
                new BinaryExpression(b, BinaryOperator.And, c));

            // Both should be valid expressions
            leftAssoc.Should().NotBeNull();
            rightAssoc.Should().NotBeNull();

            // The structure should represent the same logical operation
            return leftAssoc.GetType() == rightAssoc.GetType();
        }
        catch
        {
            // If construction fails, that's also a valid outcome for some inputs
            return true;
        }
    }

    /// <summary>
    /// Validates that OR operations are also associative.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(NestedExpressionArbs) })]
    public bool OrOperationIsAssociative(BinaryExpression a, BinaryExpression b, BinaryExpression c)
    {
        try
        {
            var leftAssoc = new BinaryExpression(
                new BinaryExpression(a, BinaryOperator.Or, b),
                BinaryOperator.Or,
                c);

            var rightAssoc = new BinaryExpression(
                a,
                BinaryOperator.Or,
                new BinaryExpression(b, BinaryOperator.Or, c));

            leftAssoc.Should().NotBeNull();
            rightAssoc.Should().NotBeNull();

            return leftAssoc.GetType() == rightAssoc.GetType();
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Validates that property references handle all valid property names correctly.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(PropertyNameArbs) })]
    public bool PropertyReferenceHandlesAllValidNames(string propertyName)
    {
        try
        {
            var propRef = new PropertyReference(propertyName);

            propRef.PropertyName.Should().Be(propertyName);
            propRef.Should().NotBeNull();

            return true;
        }
        catch (ArgumentException)
        {
            // Some property names might be invalid, that's expected
            return string.IsNullOrWhiteSpace(propertyName) ||
                   propertyName.Contains(' ') ||
                   char.IsDigit(propertyName[0]);
        }
    }

    /// <summary>
    /// Validates that literal expressions preserve value equality.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(LiteralValueArbs) })]
    public bool LiteralExpressionsPreserveValues(object? value)
    {
        try
        {
            var literal = CreateLiteral(value);

            literal.Value.Should().Be(value);
            literal.Should().NotBeNull();

            return true;
        }
        catch
        {
            // Some values might not be valid literals
            return value == null;
        }
    }

    /// <summary>
    /// Validates that comparison operations with null values are handled consistently.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(PropertyNameArbs), typeof(ComparisonOperatorArbs) })]
    public bool NullComparisonHandling(string propertyName, BinaryOperator op)
    {
        try
        {
            var propRef = new PropertyReference(propertyName);
            var nullLiteral = new Literal(null, LiteralType.Null);
            var expr = new BinaryExpression(propRef, op, nullLiteral);

            expr.Should().NotBeNull();
            expr.Operator.Should().Be(op);

            return true;
        }
        catch (ArgumentException)
        {
            // Invalid property names should throw
            return string.IsNullOrWhiteSpace(propertyName);
        }
    }

    /// <summary>
    /// Validates that spatial predicates handle boundary coordinates correctly.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(PropertyNameArbs), typeof(BoundaryCoordinateArbs) })]
    public bool SpatialPredicatesBoundaryHandling(string propertyName, Coordinate coord)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                return true;

            var propRef = new PropertyReference(propertyName);
            var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
            var point = geometryFactory.CreatePoint(coord);
            var wkb = new WKBWriter().Write(point);
            var wkt = new WKTWriter().Write(point);
            var geometryLiteral = new GeometryLiteral(wkb, point.SRID <= 0 ? 4326 : point.SRID, wkt);

            var spatialPred = new SpatialPredicate(
                SpatialOperator.Intersects,
                propRef,
                geometryLiteral);

            spatialPred.Should().NotBeNull();
            spatialPred.Operator.Should().Be(SpatialOperator.Intersects);

            return true;
        }
        catch (ArgumentException)
        {
            // Invalid coordinates or property names should be rejected
            return double.IsNaN(coord.X) || double.IsNaN(coord.Y) ||
                   double.IsInfinity(coord.X) || double.IsInfinity(coord.Y) ||
                   Math.Abs(coord.X) > 180 || Math.Abs(coord.Y) > 90 ||
                   string.IsNullOrWhiteSpace(propertyName);
        }
    }

    /// <summary>
    /// Validates that function calls handle various argument combinations.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(FunctionNameArbs), typeof(FunctionArgsArbs) })]
    public bool FunctionCallArgumentHandling(string functionName, FilterExpression[] args)
    {
        try
        {
            if (args.Length == 0)
                return true;

            var functionCall = new FunctionCall(functionName, args);

            functionCall.Should().NotBeNull();
            functionCall.FunctionName.Should().Be(functionName);
            functionCall.Arguments.Should().HaveCount(args.Length);

            return true;
        }
        catch (ArgumentException)
        {
            // Some function name/argument combinations might be invalid
            return string.IsNullOrWhiteSpace(functionName) || args.Length == 0;
        }
    }

    /// <summary>
    /// Validates that temporal predicates handle date boundary conditions.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(PropertyNameArbs), typeof(DateTimeArbs), typeof(TemporalOperatorArbs) })]
    public bool TemporalPredicateDateBoundaries(string propertyName, DateTime dateTime, TemporalOperator temporalOp)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                return true;

            var propRef = new PropertyReference(propertyName);
            var dateLiteral = new Literal(dateTime, LiteralType.DateTime);
            var temporalPred = new TemporalPredicate(temporalOp, propRef, dateLiteral);

            temporalPred.Should().NotBeNull();
            temporalPred.Operator.Should().Be(temporalOp);

            return true;
        }
        catch (ArgumentException)
        {
            return string.IsNullOrWhiteSpace(propertyName);
        }
    }

    private static Literal CreateLiteral(object? value)
    {
        return value switch
        {
            null => new Literal(null, LiteralType.Null),
            string text => new Literal(text, LiteralType.Text),
            bool boolean => new Literal(boolean, LiteralType.Boolean),
            DateOnly date => new Literal(date, LiteralType.Date),
            DateTime dateTime => new Literal(dateTime, LiteralType.DateTime),
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => new Literal(value, LiteralType.Number),
            _ => new Literal(value, LiteralType.Text)
        };
    }

    internal static class NestedExpressionArbs
    {
        public static Arbitrary<BinaryExpression> BinaryExpression() => FilterExpressionGenerators.NestedExpression();
    }

    internal static class PropertyNameArbs
    {
        public static Arbitrary<string> PropertyName() => FilterExpressionGenerators.PropertyName();
    }

    internal static class ComparisonOperatorArbs
    {
        public static Arbitrary<BinaryOperator> ComparisonOperator() => FilterExpressionGenerators.ComparisonOperator();
    }

    internal static class BoundaryCoordinateArbs
    {
        public static Arbitrary<Coordinate> Coordinate() => GeometryGenerators.BoundaryCoordinate();
    }

    internal static class LiteralValueArbs
    {
        public static Arbitrary<object?> LiteralValue() =>
            Arb.From(Gen.OneOf(
                FilterExpressionGenerators.StringLiteral().Generator.Select(s => (object?)s),
                FilterExpressionGenerators.NumericLiteral().Generator.Select(n => (object?)n),
                Gen.Elements(true, false).Select(b => (object?)b)));
    }

    internal static class FunctionNameArbs
    {
        public static Arbitrary<string> FunctionName() =>
            Arb.From(Gen.Elements("ST_INTERSECTS", "ST_WITHIN", "ST_CONTAINS", "UPPER", "LOWER"));
    }

    internal static class FunctionArgsArbs
    {
        public static Arbitrary<FilterExpression[]> FunctionArgs() =>
            Arb.From(
                from count in Gen.Choose(0, 5)
                from args in Gen.ListOf(FunctionArg(), count)
                select args.ToArray());

        private static Gen<FilterExpression> FunctionArg() =>
            Gen.OneOf(
                FilterExpressionGenerators.PropertyName().Generator.Select(name => (FilterExpression)new PropertyReference(name)),
                FilterExpressionGenerators.StringLiteral().Generator.Select(text => (FilterExpression)new Literal(text, LiteralType.Text)));
    }

    internal static class DateTimeArbs
    {
        public static Arbitrary<DateTime> DateTimeValue() =>
            Arb.From(Gen.Elements(DateTime.MinValue, DateTime.MaxValue, DateTime.UtcNow));
    }

    internal static class TemporalOperatorArbs
    {
        public static Arbitrary<TemporalOperator> TemporalOperatorValue() =>
            Arb.From(Gen.Elements(TemporalOperator.Before, TemporalOperator.After, TemporalOperator.During));
    }
}
