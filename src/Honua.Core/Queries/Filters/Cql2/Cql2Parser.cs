// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters.Cql2;

/// <summary>
/// Recursive descent parser for CQL2-Text expressions
/// </summary>
public sealed class Cql2Parser
{
    private IReadOnlyList<Cql2Token> _tokens = [];
    private int _position;
    private string _input = string.Empty;

    /// <summary>
    /// Parses a CQL2-Text expression into a FilterExpression AST
    /// </summary>
    /// <param name="cql2Text">CQL2-Text expression</param>
    /// <returns>Filter expression AST</returns>
    /// <exception cref="ArgumentException">Thrown when parsing fails</exception>
    public FilterExpression Parse(string cql2Text)
    {
        if (string.IsNullOrWhiteSpace(cql2Text))
            throw new ArgumentException("CQL2 expression cannot be null or empty", nameof(cql2Text));

        _input = cql2Text;
        var lexer = new Cql2Lexer(cql2Text);
        _tokens = lexer.Tokenize();
        _position = 0;

        try
        {
            var result = ParseBooleanExpression();

            if (!IsAtEnd())
                throw new ArgumentException($"Unexpected token '{Current().Value}' at position {Current().Position}");

            return result;
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException($"Failed to parse CQL2 expression: {ex.Message}", nameof(cql2Text), ex);
        }
    }

    private FilterExpression ParseBooleanExpression(bool allowBareScalar = false)
    {
        var expr = ParseBooleanTerm(allowBareScalar);

        while (Match(Cql2TokenType.Or))
        {
            var right = ParseBooleanTerm(allowBareScalar);
            expr = new BinaryExpression(expr, BinaryOperator.Or, right);
        }

        return expr;
    }

    private FilterExpression ParseBooleanTerm(bool allowBareScalar)
    {
        var expr = ParseBooleanFactor(allowBareScalar);

        while (Match(Cql2TokenType.And))
        {
            var right = ParseBooleanFactor(allowBareScalar);
            expr = new BinaryExpression(expr, BinaryOperator.And, right);
        }

        return expr;
    }

    private FilterExpression ParseBooleanFactor(bool allowBareScalar)
    {
        if (Match(Cql2TokenType.Not))
        {
            var operand = ParseBooleanFactor(allowBareScalar);
            return new UnaryExpression(UnaryOperator.Not, operand);
        }

        return ParseBooleanPrimary(allowBareScalar);
    }

    private FilterExpression ParseBooleanPrimary(bool allowBareScalar)
    {
        if (Match(Cql2TokenType.LeftParen))
        {
            var expr = ParseBooleanExpression(allowBareScalar);
            Consume(Cql2TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        if (Current().Type == Cql2TokenType.Boolean)
        {
            return ParseBooleanLiteral();
        }

        if (IsSpatialPredicate(Current().Type))
        {
            return ParseSpatialPredicate();
        }

        if (IsTemporalPredicate(Current().Type))
        {
            return ParseTemporalPredicate();
        }

        if (IsArrayPredicate(Current().Type))
        {
            return ParseArrayPredicate();
        }

        var exprResult = ParseComparisonPredicate();

        if (allowBareScalar)
        {
            return exprResult;
        }

        if (exprResult is BinaryExpression or UnaryExpression or SpatialPredicate or SpatialDistancePredicate or TemporalPredicate or ArrayPredicate)
        {
            return exprResult;
        }

        if (exprResult is FunctionCall)
        {
            return exprResult;
        }

        if (exprResult is Literal literal && literal.Type == LiteralType.Boolean)
        {
            return exprResult;
        }

        throw new ArgumentException("Expected predicate or boolean expression");
    }

    private FilterExpression ParseComparisonPredicate()
    {
        var left = ParseScalarExpression();

        // IS NULL / IS NOT NULL
        if (Match(Cql2TokenType.IsNull))
        {
            if (Match(Cql2TokenType.Not))
            {
                Consume(Cql2TokenType.Null, "Expected NULL after IS NOT");
                return new UnaryExpression(UnaryOperator.IsNotNull, left);
            }

            Consume(Cql2TokenType.Null, "Expected NULL after IS");
            return new UnaryExpression(UnaryOperator.IsNull, left);
        }

        // NOT LIKE
        if (Check(Cql2TokenType.Not) && CheckNext(Cql2TokenType.Like))
        {
            Advance(); // NOT
            Advance(); // LIKE
            var pattern = ParsePatternExpression();
            return new BinaryExpression(left, BinaryOperator.NotLike, pattern);
        }

        // LIKE
        if (Match(Cql2TokenType.Like))
        {
            var pattern = ParsePatternExpression();
            return new BinaryExpression(left, BinaryOperator.Like, pattern);
        }

        // NOT BETWEEN
        if (Check(Cql2TokenType.Not) && CheckNext(Cql2TokenType.Between))
        {
            Advance(); // NOT
            Advance(); // BETWEEN
            var lower = ParseScalarExpression();
            Consume(Cql2TokenType.And, "Expected AND in BETWEEN predicate");
            var upper = ParseScalarExpression();
            return BuildBetweenExpression(left, lower, upper, negate: true);
        }

        // BETWEEN
        if (Match(Cql2TokenType.Between))
        {
            var lower = ParseScalarExpression();
            Consume(Cql2TokenType.And, "Expected AND in BETWEEN predicate");
            var upper = ParseScalarExpression();
            return BuildBetweenExpression(left, lower, upper, negate: false);
        }

        // NOT IN
        if (Check(Cql2TokenType.Not) && CheckNext(Cql2TokenType.In))
        {
            Advance(); // NOT
            Advance(); // IN
            var values = ParseInList();
            return new BinaryExpression(left, BinaryOperator.NotIn, values);
        }

        // IN
        if (Match(Cql2TokenType.In))
        {
            var values = ParseInList();
            return new BinaryExpression(left, BinaryOperator.In, values);
        }

        // Binary comparison
        if (IsComparisonOperator(Current().Type))
        {
            var op = GetBinaryOperator(Advance().Type);
            var right = ParseScalarExpression();
            return new BinaryExpression(left, op, right);
        }

        return left;
    }

    private ValueList ParseInList()
    {
        Consume(Cql2TokenType.LeftParen, "Expected '(' after IN");

        var values = new List<FilterExpression>();
        if (!Check(Cql2TokenType.RightParen))
        {
            do
            {
                values.Add(ParseScalarExpression());
            }
            while (Match(Cql2TokenType.Comma));
        }

        Consume(Cql2TokenType.RightParen, "Expected ')' after value list");
        return new ValueList(values);
    }

    private BinaryExpression BuildBetweenExpression(FilterExpression target, FilterExpression lower, FilterExpression upper, bool negate)
    {
        var lowerExpr = new BinaryExpression(target, BinaryOperator.GreaterThanOrEqual, lower);
        var upperExpr = new BinaryExpression(target, BinaryOperator.LessThanOrEqual, upper);
        var combined = new BinaryExpression(lowerExpr, BinaryOperator.And, upperExpr);

        if (!negate)
        {
            return combined;
        }

        var lowerNot = new BinaryExpression(target, BinaryOperator.LessThan, lower);
        var upperNot = new BinaryExpression(target, BinaryOperator.GreaterThan, upper);
        return new BinaryExpression(lowerNot, BinaryOperator.Or, upperNot);
    }

    private FilterExpression ParseScalarExpression()
    {
        if (IsTemporalLiteralStart())
        {
            return ParseTemporalLiteral();
        }

        if (Current().Type == Cql2TokenType.Text)
        {
            var value = Advance().Value;
            return new Literal(value, LiteralType.Text);
        }

        if (Current().Type == Cql2TokenType.Boolean)
        {
            return ParseBooleanLiteral();
        }

        if (Current().Type == Cql2TokenType.Null)
        {
            Advance();
            return new Literal(null, LiteralType.Null);
        }

        return ParseArithmeticExpression();
    }

    private FilterExpression ParsePatternExpression()
    {
        if (Match(Cql2TokenType.Casei))
        {
            Consume(Cql2TokenType.LeftParen, "Expected '(' after CASEI");
            var inner = ParsePatternExpression();
            Consume(Cql2TokenType.RightParen, "Expected ')' after CASEI pattern");
            return new FunctionCall("CASEI", [inner]);
        }

        if (Match(Cql2TokenType.Accenti))
        {
            Consume(Cql2TokenType.LeftParen, "Expected '(' after ACCENTI");
            var inner = ParsePatternExpression();
            Consume(Cql2TokenType.RightParen, "Expected ')' after ACCENTI pattern");
            return new FunctionCall("ACCENTI", [inner]);
        }

        if (Current().Type == Cql2TokenType.Text)
        {
            var value = Advance().Value;
            return new Literal(value, LiteralType.Text);
        }

        throw new ArgumentException("Pattern expression must be a string literal or case/accent insensitive wrapper");
    }

    private FilterExpression ParseArithmeticExpression()
    {
        var expr = ParseArithmeticTerm();

        while (true)
        {
            if (Match(Cql2TokenType.Plus))
            {
                var right = ParseArithmeticTerm();
                expr = new BinaryExpression(expr, BinaryOperator.Add, right);
            }
            else if (Match(Cql2TokenType.Minus))
            {
                var right = ParseArithmeticTerm();
                expr = new BinaryExpression(expr, BinaryOperator.Subtract, right);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private FilterExpression ParseArithmeticTerm()
    {
        var expr = ParsePowerTerm();

        while (true)
        {
            if (Match(Cql2TokenType.Star))
            {
                var right = ParsePowerTerm();
                expr = new BinaryExpression(expr, BinaryOperator.Multiply, right);
            }
            else if (Match(Cql2TokenType.Slash))
            {
                var right = ParsePowerTerm();
                expr = new BinaryExpression(expr, BinaryOperator.Divide, right);
            }
            else if (Match(Cql2TokenType.Percent))
            {
                var right = ParsePowerTerm();
                expr = new BinaryExpression(expr, BinaryOperator.Modulo, right);
            }
            else if (Match(Cql2TokenType.Div))
            {
                var right = ParsePowerTerm();
                expr = new BinaryExpression(expr, BinaryOperator.Div, right);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private FilterExpression ParsePowerTerm()
    {
        var expr = ParseArithmeticFactor();

        if (Match(Cql2TokenType.Caret))
        {
            var right = ParsePowerTerm();
            expr = new BinaryExpression(expr, BinaryOperator.Power, right);
        }

        return expr;
    }

    private FilterExpression ParseArithmeticFactor()
    {
        if (Match(Cql2TokenType.Minus))
        {
            var operand = ParseArithmeticFactor();
            return new UnaryExpression(UnaryOperator.Negate, operand);
        }

        if (Match(Cql2TokenType.LeftParen))
        {
            var expr = ParseArithmeticExpression();
            Consume(Cql2TokenType.RightParen, "Expected ')' after arithmetic expression");
            return expr;
        }

        return ParseArithmeticOperand();
    }

    private FilterExpression ParseArithmeticOperand()
    {
        if (Current().Type == Cql2TokenType.Number)
        {
            return ParseNumericLiteral();
        }

        if (IsTemporalLiteralStart())
        {
            return ParseTemporalLiteral();
        }

        if (IsGeometryType(Current().Type))
        {
            return ParseGeometryLiteral();
        }

        if (IsFunctionStart(Current().Type))
        {
            return ParseFunctionCall();
        }

        if (Current().Type == Cql2TokenType.Identifier)
        {
            return ParsePropertyOrFunction();
        }

        if (IsKeywordIdentifier(Current().Type))
        {
            return ParseKeywordIdentifier();
        }

        if (Current().Type == Cql2TokenType.Boolean)
        {
            return ParseBooleanLiteral();
        }

        throw new ArgumentException($"Unexpected token '{Current().Value}' at position {Current().Position}");
    }

    private FilterExpression ParsePropertyOrFunction()
    {
        if (Check(Cql2TokenType.Identifier) && CheckNext(Cql2TokenType.LeftParen))
        {
            return ParseFunctionCall();
        }

        var propertyName = Consume(Cql2TokenType.Identifier, "Expected property name").Value;
        return new PropertyReference(propertyName);
    }

    private PropertyReference ParseKeywordIdentifier()
    {
        var token = Advance();
        return new PropertyReference(token.Value);
    }

    private FunctionCall ParseFunctionCall()
    {
        var nameToken = Advance();
        var functionName = nameToken.Value;

        Consume(Cql2TokenType.LeftParen, $"Expected '(' after function {functionName}");

        var args = new List<FilterExpression>();
        if (!Check(Cql2TokenType.RightParen))
        {
            do
            {
                args.Add(ParseFunctionArgument());
            }
            while (Match(Cql2TokenType.Comma));
        }

        Consume(Cql2TokenType.RightParen, $"Expected ')' after function arguments for {functionName}");
        return new FunctionCall(functionName, args);
    }

    private FilterExpression ParseFunctionArgument()
    {
        if (Check(Cql2TokenType.LeftParen) && IsArrayLiteralAhead())
        {
            Advance();
            return ParseArrayLiteral(afterLeftParen: true);
        }

        return ParseBooleanExpression(allowBareScalar: true);
    }

    private FilterExpression ParseSpatialPredicate()
    {
        var predicateToken = Advance();
        var spatialOperator = GetSpatialOperator(predicateToken.Type);

        Consume(Cql2TokenType.LeftParen, "Expected '(' after spatial predicate");

        var left = ParseGeometryExpression();

        Consume(Cql2TokenType.Comma, "Expected ',' after first spatial argument");

        var right = ParseGeometryExpression();

        if (spatialOperator is SpatialOperator.DWithin or SpatialOperator.Beyond)
        {
            Consume(Cql2TokenType.Comma, "Expected ',' after second spatial argument");
            var distance = ParseScalarExpression();
            Consume(Cql2TokenType.RightParen, "Expected ')' after spatial predicate arguments");
            return new SpatialDistancePredicate(spatialOperator, left, right, distance);
        }

        Consume(Cql2TokenType.RightParen, "Expected ')' after spatial predicate arguments");

        return new SpatialPredicate(spatialOperator, left, right);
    }

    private FilterExpression ParseGeometryExpression()
    {
        if (IsGeometryType(Current().Type))
        {
            return ParseGeometryLiteral();
        }

        if (IsFunctionStart(Current().Type))
        {
            return ParseFunctionCall();
        }

        if (Current().Type == Cql2TokenType.Identifier)
        {
            return ParsePropertyOrFunction();
        }

        if (IsKeywordIdentifier(Current().Type))
        {
            return ParseKeywordIdentifier();
        }

        throw new ArgumentException($"Unexpected token '{Current().Value}' at position {Current().Position}");
    }

    private TemporalPredicate ParseTemporalPredicate()
    {
        var predicateToken = Advance();
        var temporalOperator = GetTemporalOperator(predicateToken.Type);

        Consume(Cql2TokenType.LeftParen, "Expected '(' after temporal predicate");

        var left = ParseTemporalExpression();

        Consume(Cql2TokenType.Comma, "Expected ',' after first temporal argument");

        var right = ParseTemporalExpression();

        Consume(Cql2TokenType.RightParen, "Expected ')' after temporal predicate arguments");

        return new TemporalPredicate(temporalOperator, left, right);
    }

    private FilterExpression ParseTemporalExpression()
    {
        if (IsTemporalLiteralStart())
        {
            return ParseTemporalLiteral();
        }

        if (IsFunctionStart(Current().Type))
        {
            return ParseFunctionCall();
        }

        if (Current().Type == Cql2TokenType.Identifier)
        {
            return ParsePropertyOrFunction();
        }

        if (IsKeywordIdentifier(Current().Type))
        {
            return ParseKeywordIdentifier();
        }

        throw new ArgumentException($"Unexpected token '{Current().Value}' at position {Current().Position}");
    }

    private ArrayPredicate ParseArrayPredicate()
    {
        var predicateToken = Advance();
        var arrayOperator = GetArrayOperator(predicateToken.Type);

        Consume(Cql2TokenType.LeftParen, "Expected '(' after array predicate");

        var left = ParseArrayExpression();

        Consume(Cql2TokenType.Comma, "Expected ',' after first array argument");

        var right = ParseArrayExpression();

        Consume(Cql2TokenType.RightParen, "Expected ')' after array predicate arguments");

        return new ArrayPredicate(arrayOperator, left, right);
    }

    private FilterExpression ParseArrayExpression()
    {
        if (Check(Cql2TokenType.LeftParen))
        {
            Advance();
            return ParseArrayLiteral(afterLeftParen: true);
        }

        if (IsFunctionStart(Current().Type))
        {
            return ParseFunctionCall();
        }

        if (Current().Type == Cql2TokenType.Identifier)
        {
            return ParsePropertyOrFunction();
        }

        if (IsKeywordIdentifier(Current().Type))
        {
            return ParseKeywordIdentifier();
        }

        throw new ArgumentException($"Unexpected token '{Current().Value}' at position {Current().Position}");
    }

    private ArrayLiteral ParseArrayLiteral(bool afterLeftParen)
    {
        if (!afterLeftParen)
        {
            Consume(Cql2TokenType.LeftParen, "Expected '(' to start array literal");
        }

        if (Match(Cql2TokenType.RightParen))
        {
            return new ArrayLiteral([]);
        }

        var elements = new List<FilterExpression>();
        do
        {
            elements.Add(ParseArrayElement());
        }
        while (Match(Cql2TokenType.Comma));

        Consume(Cql2TokenType.RightParen, "Expected ')' after array literal");
        return new ArrayLiteral(elements);
    }

    private FilterExpression ParseArrayElement()
    {
        if (Check(Cql2TokenType.LeftParen))
        {
            if (IsArrayLiteralAhead())
            {
                Advance();
                return ParseArrayLiteral(afterLeftParen: true);
            }

            Match(Cql2TokenType.LeftParen);
            var expr = ParseBooleanExpression(allowBareScalar: true);
            Consume(Cql2TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        if (IsGeometryType(Current().Type))
        {
            return ParseGeometryLiteral();
        }

        if (IsTemporalLiteralStart())
        {
            return ParseTemporalLiteral();
        }

        if (Current().Type == Cql2TokenType.Text)
        {
            var value = Advance().Value;
            return new Literal(value, LiteralType.Text);
        }

        if (Current().Type == Cql2TokenType.Boolean)
        {
            return ParseBooleanLiteral();
        }

        if (Current().Type == Cql2TokenType.Null)
        {
            Advance();
            return new Literal(null, LiteralType.Null);
        }

        return ParseBooleanExpression(allowBareScalar: true);
    }

    private GeometryLiteral ParseGeometryLiteral()
    {
        var geometryToken = Advance();
        if (geometryToken.Type == Cql2TokenType.Bbox)
        {
            return ParseBboxLiteral(geometryToken);
        }

        Consume(Cql2TokenType.LeftParen, $"Expected '(' after {geometryToken.Value}");
        var startPosition = geometryToken.Position;

        var parenDepth = 1;
        var lastToken = Previous();
        while (parenDepth > 0 && !IsAtEnd())
        {
            var token = Advance();
            if (token.Type == Cql2TokenType.LeftParen)
                parenDepth++;
            else if (token.Type == Cql2TokenType.RightParen)
                parenDepth--;
            lastToken = token;
        }

        if (parenDepth != 0)
        {
            throw new ArgumentException("Unterminated geometry literal");
        }

        var endPosition = lastToken.Position + lastToken.Length;
        var wktText = _input[startPosition..endPosition];

        try
        {
            var reader = new WKTReader();
            var geometry = reader.Read(wktText);
            var writer = new WKBWriter();
            var wkb = writer.Write(geometry);

            return new GeometryLiteral(wkb, geometry.SRID <= 0 ? 4326 : geometry.SRID, wktText);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid geometry literal: {wktText}", ex);
        }
    }

    private GeometryLiteral ParseBboxLiteral(Cql2Token bboxToken)
    {
        Consume(Cql2TokenType.LeftParen, "Expected '(' after BBOX");

        var minX = ParseSignedNumber();
        Consume(Cql2TokenType.Comma, "Expected ',' after minX");
        var minY = ParseSignedNumber();
        Consume(Cql2TokenType.Comma, "Expected ',' after minY");
        var maxX = ParseSignedNumber();
        Consume(Cql2TokenType.Comma, "Expected ',' after maxX");
        var maxY = ParseSignedNumber();

        if (Match(Cql2TokenType.Comma))
        {
            ParseSignedNumber();
            Consume(Cql2TokenType.Comma, "Expected ',' after minZ");
            ParseSignedNumber();
        }

        Consume(Cql2TokenType.RightParen, "Expected ')' after BBOX");

        var coordinates = new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        };

        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var polygon = geometryFactory.CreatePolygon(coordinates);
        var writer = new WKBWriter();
        var wkb = writer.Write(polygon);

        var original = _input[bboxToken.Position..(Previous().Position + Previous().Length)];
        return new GeometryLiteral(wkb, 4326, original);
    }

    private Literal ParseNumericLiteral()
    {
        var value = Advance().Value;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return new Literal(number, LiteralType.Number);
        }
        throw new ArgumentException($"Invalid number format: {value}");
    }

    private Literal ParseBooleanLiteral()
    {
        var value = Advance().Value;
        var boolValue = string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase);
        return new Literal(boolValue, LiteralType.Boolean);
    }

    private FilterExpression ParseTemporalLiteral()
    {
        if (Match(Cql2TokenType.Date))
        {
            return ParseDateLiteral();
        }

        if (Match(Cql2TokenType.Timestamp))
        {
            return ParseTimestampLiteral();
        }

        if (Match(Cql2TokenType.Interval))
        {
            return ParseIntervalLiteral();
        }

        throw new ArgumentException($"Unexpected temporal literal '{Current().Value}'");
    }

    private Literal ParseDateLiteral()
    {
        Consume(Cql2TokenType.LeftParen, "Expected '(' after DATE");
        var textToken = Consume(Cql2TokenType.Text, "Expected date literal");
        Consume(Cql2TokenType.RightParen, "Expected ')' after DATE literal");

        if (!DateOnly.TryParseExact(textToken.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            throw new ArgumentException($"Invalid DATE literal: {textToken.Value}");
        }

        return new Literal(date, LiteralType.Date);
    }

    private Literal ParseTimestampLiteral()
    {
        Consume(Cql2TokenType.LeftParen, "Expected '(' after TIMESTAMP");
        var textToken = Consume(Cql2TokenType.Text, "Expected timestamp literal");
        Consume(Cql2TokenType.RightParen, "Expected ')' after TIMESTAMP literal");

        if (!DateTimeOffset.TryParse(textToken.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw new ArgumentException($"Invalid TIMESTAMP literal: {textToken.Value}");
        }

        return new Literal(timestamp, LiteralType.DateTime);
    }

    private IntervalLiteral ParseIntervalLiteral()
    {
        Consume(Cql2TokenType.LeftParen, "Expected '(' after INTERVAL");

        var start = ParseIntervalBound();
        Consume(Cql2TokenType.Comma, "Expected ',' after interval start");
        var end = ParseIntervalBound();

        Consume(Cql2TokenType.RightParen, "Expected ')' after INTERVAL literal");

        if (start != null && end != null && start.Type != end.Type)
        {
            throw new ArgumentException("INTERVAL bounds must share the same temporal granularity");
        }

        return new IntervalLiteral(start, end);
    }

    private Literal? ParseIntervalBound()
    {
        var token = Consume(Cql2TokenType.Text, "Expected interval bound");
        if (token.Value == "..")
        {
            return null;
        }

        if (DateOnly.TryParseExact(token.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new Literal(date, LiteralType.Date);
        }

        if (DateTimeOffset.TryParse(token.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return new Literal(timestamp, LiteralType.DateTime);
        }

        throw new ArgumentException($"Invalid interval bound: {token.Value}");
    }

    private double ParseSignedNumber()
    {
        var sign = 1.0;
        if (Match(Cql2TokenType.Minus))
        {
            sign = -1.0;
        }
        else
        {
            Match(Cql2TokenType.Plus);
        }

        var token = Consume(Cql2TokenType.Number, "Expected numeric literal");
        if (!double.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException($"Invalid numeric literal: {token.Value}");
        }

        return sign * number;
    }

    private bool IsArrayLiteralAhead()
    {
        var depth = 0;
        for (var i = _position; i < _tokens.Count; i++)
        {
            var type = _tokens[i].Type;
            if (type == Cql2TokenType.LeftParen)
                depth++;
            else if (type == Cql2TokenType.RightParen)
            {
                depth--;
                if (depth == 0)
                    return false;
            }
            else if (type == Cql2TokenType.Comma && depth == 1)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsComparisonOperator(Cql2TokenType type)
    {
        return type is Cql2TokenType.Equal or Cql2TokenType.NotEqual or
               Cql2TokenType.LessThan or Cql2TokenType.LessThanOrEqual or
               Cql2TokenType.GreaterThan or Cql2TokenType.GreaterThanOrEqual;
    }

    private bool IsSpatialPredicate(Cql2TokenType type)
    {
        return type is Cql2TokenType.S_Intersects or Cql2TokenType.S_Contains or
               Cql2TokenType.S_Within or Cql2TokenType.S_Crosses or
               Cql2TokenType.S_Touches or Cql2TokenType.S_Overlaps or
               Cql2TokenType.S_Disjoint or Cql2TokenType.S_Equals or
               Cql2TokenType.S_DWithin or Cql2TokenType.S_Beyond;
    }

    private bool IsTemporalPredicate(Cql2TokenType type)
    {
        return type is Cql2TokenType.T_After or Cql2TokenType.T_Before or
               Cql2TokenType.T_Contains or Cql2TokenType.T_Disjoint or
               Cql2TokenType.T_During or Cql2TokenType.T_Equals or
               Cql2TokenType.T_FinishedBy or Cql2TokenType.T_Finishes or
               Cql2TokenType.T_Intersects or Cql2TokenType.T_Meets or
               Cql2TokenType.T_MetBy or Cql2TokenType.T_OverlappedBy or
               Cql2TokenType.T_Overlaps or Cql2TokenType.T_StartedBy or
               Cql2TokenType.T_Starts;
    }

    private bool IsArrayPredicate(Cql2TokenType type)
    {
        return type is Cql2TokenType.A_Equals or Cql2TokenType.A_Contains or
               Cql2TokenType.A_ContainedBy or Cql2TokenType.A_Overlaps;
    }

    private bool IsFunctionStart(Cql2TokenType type)
    {
        return (type is Cql2TokenType.Casei or Cql2TokenType.Accenti) &&
               CheckNext(Cql2TokenType.LeftParen);
    }

    private bool IsTemporalLiteralStart()
    {
        return IsTemporalLiteral(Current().Type) && CheckNext(Cql2TokenType.LeftParen);
    }

    private static bool IsKeywordIdentifier(Cql2TokenType type)
    {
        return type is Cql2TokenType.Date or Cql2TokenType.Timestamp or Cql2TokenType.Interval or
            Cql2TokenType.Casei or Cql2TokenType.Accenti;
    }

    private bool IsTemporalLiteral(Cql2TokenType type)
    {
        return type is Cql2TokenType.Date or Cql2TokenType.Timestamp or Cql2TokenType.Interval;
    }

    private bool IsGeometryType(Cql2TokenType type)
    {
        return type is Cql2TokenType.Point or Cql2TokenType.LineString or
               Cql2TokenType.Polygon or Cql2TokenType.MultiPoint or
               Cql2TokenType.MultiLineString or Cql2TokenType.MultiPolygon or
               Cql2TokenType.GeometryCollection or Cql2TokenType.Bbox;
    }

    private BinaryOperator GetBinaryOperator(Cql2TokenType type)
    {
        return type switch
        {
            Cql2TokenType.Equal => BinaryOperator.Equal,
            Cql2TokenType.NotEqual => BinaryOperator.NotEqual,
            Cql2TokenType.LessThan => BinaryOperator.LessThan,
            Cql2TokenType.LessThanOrEqual => BinaryOperator.LessThanOrEqual,
            Cql2TokenType.GreaterThan => BinaryOperator.GreaterThan,
            Cql2TokenType.GreaterThanOrEqual => BinaryOperator.GreaterThanOrEqual,
            _ => throw new ArgumentException($"Unknown binary operator: {type}")
        };
    }

    private SpatialOperator GetSpatialOperator(Cql2TokenType type)
    {
        return type switch
        {
            Cql2TokenType.S_Intersects => SpatialOperator.Intersects,
            Cql2TokenType.S_Contains => SpatialOperator.Contains,
            Cql2TokenType.S_Within => SpatialOperator.Within,
            Cql2TokenType.S_Crosses => SpatialOperator.Crosses,
            Cql2TokenType.S_Touches => SpatialOperator.Touches,
            Cql2TokenType.S_Overlaps => SpatialOperator.Overlaps,
            Cql2TokenType.S_Disjoint => SpatialOperator.Disjoint,
            Cql2TokenType.S_Equals => SpatialOperator.Equals,
            Cql2TokenType.S_DWithin => SpatialOperator.DWithin,
            Cql2TokenType.S_Beyond => SpatialOperator.Beyond,
            _ => throw new ArgumentException($"Unknown spatial operator: {type}")
        };
    }

    private TemporalOperator GetTemporalOperator(Cql2TokenType type)
    {
        return type switch
        {
            Cql2TokenType.T_After => TemporalOperator.After,
            Cql2TokenType.T_Before => TemporalOperator.Before,
            Cql2TokenType.T_Contains => TemporalOperator.Contains,
            Cql2TokenType.T_Disjoint => TemporalOperator.Disjoint,
            Cql2TokenType.T_During => TemporalOperator.During,
            Cql2TokenType.T_Equals => TemporalOperator.Equals,
            Cql2TokenType.T_FinishedBy => TemporalOperator.FinishedBy,
            Cql2TokenType.T_Finishes => TemporalOperator.Finishes,
            Cql2TokenType.T_Intersects => TemporalOperator.Intersects,
            Cql2TokenType.T_Meets => TemporalOperator.Meets,
            Cql2TokenType.T_MetBy => TemporalOperator.MetBy,
            Cql2TokenType.T_OverlappedBy => TemporalOperator.OverlappedBy,
            Cql2TokenType.T_Overlaps => TemporalOperator.Overlaps,
            Cql2TokenType.T_StartedBy => TemporalOperator.StartedBy,
            Cql2TokenType.T_Starts => TemporalOperator.Starts,
            _ => throw new ArgumentException($"Unknown temporal operator: {type}")
        };
    }

    private ArrayOperator GetArrayOperator(Cql2TokenType type)
    {
        return type switch
        {
            Cql2TokenType.A_Equals => ArrayOperator.Equals,
            Cql2TokenType.A_Contains => ArrayOperator.Contains,
            Cql2TokenType.A_ContainedBy => ArrayOperator.ContainedBy,
            Cql2TokenType.A_Overlaps => ArrayOperator.Overlaps,
            _ => throw new ArgumentException($"Unknown array operator: {type}")
        };
    }

    private bool Match(Cql2TokenType type)
    {
        if (Check(type))
        {
            Advance();
            return true;
        }
        return false;
    }

    private bool Check(Cql2TokenType type)
    {
        if (IsAtEnd())
            return false;
        return Current().Type == type;
    }

    private bool CheckNext(Cql2TokenType type)
    {
        if (_position + 1 >= _tokens.Count)
            return false;
        return _tokens[_position + 1].Type == type;
    }

    private Cql2Token Advance()
    {
        if (!IsAtEnd())
            _position++;
        return Previous();
    }

    private bool IsAtEnd()
    {
        return Current().Type == Cql2TokenType.EndOfInput;
    }

    private Cql2Token Current()
    {
        return _tokens[_position];
    }

    private Cql2Token Previous()
    {
        return _tokens[_position - 1];
    }

    private Cql2Token Consume(Cql2TokenType type, string message)
    {
        if (Check(type))
            return Advance();
        throw new ArgumentException($"{message}. Found '{Current().Value}' at position {Current().Position}");
    }
}
