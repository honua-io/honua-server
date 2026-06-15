// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.OData;

namespace Honua.Core.Tests.Queries.Filters.OData;

public class ODataFilterParserTests
{
    private readonly ODataFilterParser _parser = new();

    #region Not Operator Precedence

    [Fact]
    public void Parse_NotPrecedence_BindsTighterThanOr()
    {
        // "not A eq 1 or B eq 2" should parse as "((not A) eq 1) or (B eq 2)"
        var result = _parser.Parse("not A eq 1 or B eq 2");

        result.Should().BeOfType<BinaryExpression>();
        var orExpr = (BinaryExpression)result;
        orExpr.Operator.Should().Be(BinaryOperator.Or);

        orExpr.Left.Should().BeOfType<BinaryExpression>();
        var comparison = (BinaryExpression)orExpr.Left;
        comparison.Operator.Should().Be(BinaryOperator.Equal);

        comparison.Left.Should().BeOfType<UnaryExpression>();
        var notExpr = (UnaryExpression)comparison.Left;
        notExpr.Operator.Should().Be(UnaryOperator.Not);
        notExpr.Operand.Should().BeOfType<PropertyReference>();
        ((PropertyReference)notExpr.Operand).PropertyName.Should().Be("A");

        ((Literal)comparison.Right).Value.Should().Be(1);
    }

    [Fact]
    public void Parse_NotPrecedence_BindsTighterThanAnd()
    {
        // "not A eq 1 and B eq 2" should parse as "((not A) eq 1) and (B eq 2)"
        var result = _parser.Parse("not A eq 1 and B eq 2");

        result.Should().BeOfType<BinaryExpression>();
        var andExpr = (BinaryExpression)result;
        andExpr.Operator.Should().Be(BinaryOperator.And);

        andExpr.Left.Should().BeOfType<BinaryExpression>();
        var comparison = (BinaryExpression)andExpr.Left;
        comparison.Operator.Should().Be(BinaryOperator.Equal);

        comparison.Left.Should().BeOfType<UnaryExpression>();
        var notExpr = (UnaryExpression)comparison.Left;
        notExpr.Operator.Should().Be(UnaryOperator.Not);
        notExpr.Operand.Should().BeOfType<PropertyReference>();
    }

    [Fact]
    public void Parse_DoubleNot_ParsesCorrectly()
    {
        var result = _parser.Parse("not not active eq true");

        result.Should().BeOfType<BinaryExpression>();
        var comparison = (BinaryExpression)result;
        comparison.Operator.Should().Be(BinaryOperator.Equal);

        comparison.Left.Should().BeOfType<UnaryExpression>();
        var outer = (UnaryExpression)comparison.Left;
        outer.Operator.Should().Be(UnaryOperator.Not);

        outer.Operand.Should().BeOfType<UnaryExpression>();
        var inner = (UnaryExpression)outer.Operand;
        inner.Operator.Should().Be(UnaryOperator.Not);
        inner.Operand.Should().BeOfType<PropertyReference>();

        comparison.Right.Should().BeOfType<Literal>();
        ((Literal)comparison.Right).Value.Should().Be(true);
    }

    [Fact]
    public void Parse_NotWithParentheses_AppliesToComparisonExpression()
    {
        var result = _parser.Parse("not (A eq 1)");

        result.Should().BeOfType<UnaryExpression>();
        var notExpr = (UnaryExpression)result;
        notExpr.Operator.Should().Be(UnaryOperator.Not);
        notExpr.Operand.Should().BeOfType<BinaryExpression>();
    }

    #endregion

    #region In Operator

    [Fact]
    public void Parse_InOperator_WithStrings_ProducesBinaryExpressionWithValueList()
    {
        var result = _parser.Parse("status in ('active','pending')");

        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.In);

        binary.Left.Should().BeOfType<PropertyReference>();
        ((PropertyReference)binary.Left).PropertyName.Should().Be("status");

        binary.Right.Should().BeOfType<ValueList>();
        var values = (ValueList)binary.Right;
        values.Values.Should().HaveCount(2);
        ((Literal)values.Values[0]).Value.Should().Be("active");
        ((Literal)values.Values[1]).Value.Should().Be("pending");
    }

    [Fact]
    public void Parse_InOperator_WithIntegers_ProducesValueList()
    {
        var result = _parser.Parse("id in (1,2,3)");

        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.In);

        var values = (ValueList)binary.Right;
        values.Values.Should().HaveCount(3);
        ((Literal)values.Values[0]).Value.Should().Be(1);
        ((Literal)values.Values[1]).Value.Should().Be(2);
        ((Literal)values.Values[2]).Value.Should().Be(3);
    }

    [Fact]
    public void Parse_InOperator_EmptyList_ProducesEmptyValueList()
    {
        var result = _parser.Parse("status in ()");

        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.In);

        var values = (ValueList)binary.Right;
        values.Values.Should().BeEmpty();
    }

    [Fact]
    public void Parse_InOperator_CombinedWithAnd_ParsesCorrectly()
    {
        var result = _parser.Parse("status in ('active','pending') and priority eq 1");

        result.Should().BeOfType<BinaryExpression>();
        var andExpr = (BinaryExpression)result;
        andExpr.Operator.Should().Be(BinaryOperator.And);

        andExpr.Left.Should().BeOfType<BinaryExpression>();
        var inExpr = (BinaryExpression)andExpr.Left;
        inExpr.Operator.Should().Be(BinaryOperator.In);
    }

    #endregion

    #region Unary Negation

    [Fact]
    public void Parse_UnaryNegation_Property_ProducesNegateExpression()
    {
        var result = _parser.Parse("-population gt 0");

        result.Should().BeOfType<BinaryExpression>();
        var comparison = (BinaryExpression)result;
        comparison.Operator.Should().Be(BinaryOperator.GreaterThan);

        comparison.Left.Should().BeOfType<UnaryExpression>();
        var negate = (UnaryExpression)comparison.Left;
        negate.Operator.Should().Be(UnaryOperator.Negate);
        negate.Operand.Should().BeOfType<PropertyReference>();
        ((PropertyReference)negate.Operand).PropertyName.Should().Be("population");
    }

    [Fact]
    public void Parse_UnaryNegation_InArithmetic_ParsesCorrectly()
    {
        // "price add -discount gt 0" means "price + (-discount) > 0"
        var result = _parser.Parse("price add -discount gt 0");

        result.Should().BeOfType<BinaryExpression>();
        var comparison = (BinaryExpression)result;
        comparison.Operator.Should().Be(BinaryOperator.GreaterThan);

        var addExpr = (BinaryExpression)comparison.Left;
        addExpr.Operator.Should().Be(BinaryOperator.Add);

        addExpr.Right.Should().BeOfType<UnaryExpression>();
        var negate = (UnaryExpression)addExpr.Right;
        negate.Operator.Should().Be(UnaryOperator.Negate);
        ((PropertyReference)negate.Operand).PropertyName.Should().Be("discount");
    }

    [Fact]
    public void Parse_GeometryLiteral_WithTooManyVertices_ThrowsODataFilterParseException()
    {
        var points = string.Join(",", Enumerable.Range(0, 50_001).Select(i => $"{i} 0"));
        var filter = $"geo.intersects(geom, geography'LINESTRING({points})')";

        var act = () => _parser.Parse(filter);

        act.Should().Throw<ODataFilterParseException>()
            .WithMessage("*maximum geometry complexity*");
    }

    [Fact]
    public void Parse_WithNestedParenthesesBeyondLimit_ThrowsODataFilterParseException()
    {
        var filter = string.Concat(Enumerable.Repeat("(", FilterParserGuard.MaxExpressionDepth + 1)) +
            "Name eq 'deep'" +
            string.Concat(Enumerable.Repeat(")", FilterParserGuard.MaxExpressionDepth + 1));

        var act = () => _parser.Parse(filter);

        act.Should().Throw<ODataFilterParseException>()
            .WithMessage($"*maximum nesting depth of {FilterParserGuard.MaxExpressionDepth}*");
    }

    #endregion

    #region Integer vs Double Literals

    [Fact]
    public void Parse_IntegerLiteral_StoredAsInt()
    {
        var result = _parser.Parse("age eq 42");

        var binary = (BinaryExpression)result;
        var literal = (Literal)binary.Right;
        literal.Value.Should().BeOfType<int>();
        literal.Value.Should().Be(42);
        literal.Type.Should().Be(LiteralType.Number);
    }

    [Fact]
    public void Parse_DecimalLiteral_StoredAsDecimal()
    {
        // OData v4 ABNF: a dot-only numeric literal (no exponent) is Edm.Decimal;
        // only scientific-notation forms carry Edm.Double semantics.
        var result = _parser.Parse("rating eq 42.0");

        var binary = (BinaryExpression)result;
        var literal = (Literal)binary.Right;
        literal.Value.Should().BeOfType<decimal>();
        literal.Value.Should().Be(42.0m);
        literal.Type.Should().Be(LiteralType.Number);
    }

    [Fact]
    public void Parse_ScientificNotation_StoredAsDouble()
    {
        var result = _parser.Parse("value eq 1e5");

        var binary = (BinaryExpression)result;
        var literal = (Literal)binary.Right;
        literal.Value.Should().BeOfType<double>();
        literal.Value.Should().Be(100000.0);
    }

    [Fact]
    public void Parse_LargeInteger_StoredAsLong()
    {
        var result = _parser.Parse("id eq 3000000000");

        var binary = (BinaryExpression)result;
        var literal = (Literal)binary.Right;
        literal.Value.Should().BeOfType<long>();
        literal.Value.Should().Be(3000000000L);
    }

    [Fact]
    public void Parse_ZeroInteger_StoredAsInt()
    {
        var result = _parser.Parse("count eq 0");

        var binary = (BinaryExpression)result;
        var literal = (Literal)binary.Right;
        literal.Value.Should().BeOfType<int>();
        literal.Value.Should().Be(0);
    }

    [Fact]
    public void Parse_NegativeNumberLiteral_StoredAsInt()
    {
        // "-5" as a standalone negative literal (at start of expression before digit)
        var result = _parser.Parse("value eq -5");

        var binary = (BinaryExpression)result;
        var literal = (Literal)binary.Right;
        literal.Value.Should().BeOfType<int>();
        literal.Value.Should().Be(-5);
    }

    #endregion

    #region Null Comparisons

    [Fact]
    public void Parse_NullOnRight_ProducesIsNullExpression()
    {
        var result = _parser.Parse("name eq null");

        result.Should().BeOfType<UnaryExpression>();
        var unary = (UnaryExpression)result;
        unary.Operator.Should().Be(UnaryOperator.IsNull);
        unary.Operand.Should().BeOfType<PropertyReference>();
        ((PropertyReference)unary.Operand).PropertyName.Should().Be("name");
    }

    [Fact]
    public void Parse_NullOnLeft_ProducesIsNullOnProperty()
    {
        // "null eq name" should produce IsNull(name), not IsNull(null)
        var result = _parser.Parse("null eq name");

        result.Should().BeOfType<UnaryExpression>();
        var unary = (UnaryExpression)result;
        unary.Operator.Should().Be(UnaryOperator.IsNull);
        unary.Operand.Should().BeOfType<PropertyReference>();
        ((PropertyReference)unary.Operand).PropertyName.Should().Be("name");
    }

    [Fact]
    public void Parse_LiteralEqNull_ProducesIsNullOnLiteral()
    {
        // "'hello' eq null" — the non-null operand ('hello') should be the subject of IsNull
        var result = _parser.Parse("'hello' eq null");

        result.Should().BeOfType<UnaryExpression>();
        var unary = (UnaryExpression)result;
        unary.Operator.Should().Be(UnaryOperator.IsNull);
        unary.Operand.Should().BeOfType<Literal>();
        var literal = (Literal)unary.Operand;
        literal.Value.Should().Be("hello");
    }

    [Fact]
    public void Parse_NullNeProperty_ProducesIsNotNull()
    {
        var result = _parser.Parse("name ne null");

        result.Should().BeOfType<UnaryExpression>();
        var unary = (UnaryExpression)result;
        unary.Operator.Should().Be(UnaryOperator.IsNotNull);
        unary.Operand.Should().BeOfType<PropertyReference>();
    }

    #endregion

    #region OData 2VL eq/ne Rewrites

    [Fact]
    public void Parse_NotEqualStringLiteral_RewritesToNullSafeComparison()
    {
        // OData v4.01: null "is not equal to any value but itself", so
        // `state ne 'California'` must match rows whose state is null. SQL '<>'
        // is 3VL and would silently drop them — the parser rewrites at the
        // protocol boundary so the shared SQL translators stay 3VL for CQL2/FES.
        var result = _parser.Parse("state ne 'California'");

        result.Should().BeOfType<BinaryExpression>();
        var or = (BinaryExpression)result;
        or.Operator.Should().Be(BinaryOperator.Or);

        or.Left.Should().BeOfType<BinaryExpression>();
        var notEqual = (BinaryExpression)or.Left;
        notEqual.Operator.Should().Be(BinaryOperator.NotEqual);
        ((PropertyReference)notEqual.Left).PropertyName.Should().Be("state");
        ((Literal)notEqual.Right).Value.Should().Be("California");

        or.Right.Should().BeOfType<UnaryExpression>();
        var isNull = (UnaryExpression)or.Right;
        isNull.Operator.Should().Be(UnaryOperator.IsNull);
        ((PropertyReference)isNull.Operand).PropertyName.Should().Be("state");
    }

    [Fact]
    public void Parse_NotEqualLiteralOnLeft_RewritesIsNullOnProperty()
    {
        var result = _parser.Parse("'California' ne state");

        result.Should().BeOfType<BinaryExpression>();
        var or = (BinaryExpression)result;
        or.Operator.Should().Be(BinaryOperator.Or);
        ((BinaryExpression)or.Left).Operator.Should().Be(BinaryOperator.NotEqual);

        or.Right.Should().BeOfType<UnaryExpression>();
        var isNull = (UnaryExpression)or.Right;
        isNull.Operator.Should().Be(UnaryOperator.IsNull);
        ((PropertyReference)isNull.Operand).PropertyName.Should().Be("state");
    }

    [Fact]
    public void Parse_NotEqualTwoProperties_RewritesToIsDistinctFromExpansion()
    {
        // ((state <> country) OR (state IS NULL AND country IS NOT NULL))
        //   OR (state IS NOT NULL AND country IS NULL)
        var result = _parser.Parse("state ne country");

        result.Should().BeOfType<BinaryExpression>();
        var outerOr = (BinaryExpression)result;
        outerOr.Operator.Should().Be(BinaryOperator.Or);

        var innerOr = (BinaryExpression)outerOr.Left;
        innerOr.Operator.Should().Be(BinaryOperator.Or);
        ((BinaryExpression)innerOr.Left).Operator.Should().Be(BinaryOperator.NotEqual);

        var leftNullArm = (BinaryExpression)innerOr.Right;
        leftNullArm.Operator.Should().Be(BinaryOperator.And);
        ((UnaryExpression)leftNullArm.Left).Operator.Should().Be(UnaryOperator.IsNull);
        ((UnaryExpression)leftNullArm.Right).Operator.Should().Be(UnaryOperator.IsNotNull);

        var rightNullArm = (BinaryExpression)outerOr.Right;
        rightNullArm.Operator.Should().Be(BinaryOperator.And);
        ((UnaryExpression)rightNullArm.Left).Operator.Should().Be(UnaryOperator.IsNotNull);
        ((UnaryExpression)rightNullArm.Right).Operator.Should().Be(UnaryOperator.IsNull);
    }

    [Fact]
    public void Parse_EqualNonNullLiteral_KeepsPlainEquality()
    {
        // The common property-vs-literal equality stays a plain '=' — the literal can
        // never be null, so no null-safe arm is needed.
        var result = _parser.Parse("state eq 'California'");

        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.Equal);
        binary.Left.Should().BeOfType<PropertyReference>();
        binary.Right.Should().BeOfType<Literal>();
    }

    [Fact]
    public void Parse_EqualTwoProperties_RewritesToNullSafeEquality()
    {
        // OData requires `null eq null` to be true; SQL '=' yields UNKNOWN. Two
        // nullable operands therefore gain a both-null disjunct.
        var result = _parser.Parse("state eq country");

        result.Should().BeOfType<BinaryExpression>();
        var or = (BinaryExpression)result;
        or.Operator.Should().Be(BinaryOperator.Or);
        ((BinaryExpression)or.Left).Operator.Should().Be(BinaryOperator.Equal);

        var bothNull = (BinaryExpression)or.Right;
        bothNull.Operator.Should().Be(BinaryOperator.And);
        ((UnaryExpression)bothNull.Left).Operator.Should().Be(UnaryOperator.IsNull);
        ((UnaryExpression)bothNull.Right).Operator.Should().Be(UnaryOperator.IsNull);
    }

    #endregion

    #region Geo Functions

    [Fact]
    public void Parse_GeoLength_MapsToGeoLengthFunction()
    {
        var result = _parser.Parse("geo.length(Geometry) gt 100");

        result.Should().BeOfType<BinaryExpression>();
        var binary = (BinaryExpression)result;
        binary.Operator.Should().Be(BinaryOperator.GreaterThan);

        binary.Left.Should().BeOfType<FunctionCall>();
        var function = (FunctionCall)binary.Left;
        function.FunctionName.Should().Be("GEOLENGTH");
        function.Arguments.Should().HaveCount(1);
        function.Arguments[0].Should().BeOfType<PropertyReference>();
    }

    [Fact]
    public void Parse_GeoLength_WrongArgumentCount_Throws()
    {
        var act = () => _parser.Parse("geo.length(Geometry, Geometry) gt 100");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_GeoIntersects_MarksPredicateGeodesic()
    {
        // OData geo.intersects carries Edm.Geography geodesic semantics. The flag is
        // the protocol marker SQL translators key on; CQL2/FES parsers never set it,
        // keeping the divergence protocol-scoped.
        var result = _parser.Parse("geo.intersects(Geometry, geography'POINT(1 2)')");

        result.Should().BeOfType<SpatialPredicate>();
        var spatial = (SpatialPredicate)result;
        spatial.Operator.Should().Be(SpatialOperator.Intersects);
        spatial.Geodesic.Should().BeTrue();
        spatial.Right.Should().BeOfType<GeometryLiteral>();
    }

    [Fact]
    public void Parse_GeoIntersectsWholeWorldEnvelope_FallsBackToPlanar()
    {
        // PostGIS geography collapses the whole-world rectangle (pole vertices and
        // 360°-longitude edges) into a zero-area ring that matches nothing, so this
        // ubiquitous "no spatial constraint" envelope must stay on the planar path.
        var result = _parser.Parse(
            "geo.intersects(Geometry, geography'POLYGON((-180 -90, 180 -90, 180 90, -180 90, -180 -90))')");

        result.Should().BeOfType<SpatialPredicate>();
        ((SpatialPredicate)result).Geodesic.Should().BeFalse();
    }

    [Fact]
    public void Parse_GeoIntersectsAntimeridianPolygon_StaysGeodesic()
    {
        // Dateline-crossing polygons are exactly the case geography evaluates
        // correctly via shortest-path edges; they remain geodesic-eligible.
        var result = _parser.Parse(
            "geo.intersects(Geometry, geography'POLYGON((170 -10, -170 -10, -170 10, 170 10, 170 -10))')");

        result.Should().BeOfType<SpatialPredicate>();
        ((SpatialPredicate)result).Geodesic.Should().BeTrue();
    }

    #endregion
}
