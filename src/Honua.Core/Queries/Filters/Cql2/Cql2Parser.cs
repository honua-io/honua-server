// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters.Cql2;

/// <summary>
/// Recursive descent parser for CQL2-Text expressions
/// </summary>
public sealed class Cql2Parser
{
    private IReadOnlyList<Cql2Token> _tokens = [];
    private int _position;

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

        var lexer = new Cql2Lexer(cql2Text);
        _tokens = lexer.Tokenize();
        _position = 0;

        try
        {
            var result = ParseOrExpression();

            if (!IsAtEnd())
                throw new ArgumentException($"Unexpected token '{Current().Value}' at position {Current().Position}");

            return result;
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException($"Failed to parse CQL2 expression: {ex.Message}", nameof(cql2Text), ex);
        }
    }

    private FilterExpression ParseOrExpression()
    {
        var expr = ParseAndExpression();

        while (Match(Cql2TokenType.Or))
        {
            var op = BinaryOperator.Or;
            var right = ParseAndExpression();
            expr = new BinaryExpression(expr, op, right);
        }

        return expr;
    }

    private FilterExpression ParseAndExpression()
    {
        var expr = ParseNotExpression();

        while (Match(Cql2TokenType.And))
        {
            var op = BinaryOperator.And;
            var right = ParseNotExpression();
            expr = new BinaryExpression(expr, op, right);
        }

        return expr;
    }

    private FilterExpression ParseNotExpression()
    {
        if (Match(Cql2TokenType.Not))
        {
            var operand = ParseNotExpression();
            return new UnaryExpression(UnaryOperator.Not, operand);
        }

        return ParseComparisonExpression();
    }

    private FilterExpression ParseComparisonExpression()
    {
        var expr = ParsePrimaryExpression();

        // Handle binary comparison operators
        if (IsComparisonOperator(Current().Type))
        {
            var op = GetBinaryOperator(Advance().Type);
            var right = ParsePrimaryExpression();
            return new BinaryExpression(expr, op, right);
        }

        // Handle NOT LIKE
        if (Check(Cql2TokenType.Not) && CheckNext(Cql2TokenType.Like))
        {
            Advance(); // Consume NOT
            Advance(); // Consume LIKE
            var right = ParsePrimaryExpression();
            return new BinaryExpression(expr, BinaryOperator.NotLike, right);
        }

        // Handle LIKE
        if (Match(Cql2TokenType.Like))
        {
            var right = ParsePrimaryExpression();
            return new BinaryExpression(expr, BinaryOperator.Like, right);
        }

        // Handle NOT IN
        if (Check(Cql2TokenType.Not) && CheckNext(Cql2TokenType.In))
        {
            Advance(); // Consume NOT
            Advance(); // Consume IN
            Consume(Cql2TokenType.LeftParen, "Expected '(' after NOT IN");
            var values = ParseValueList();
            Consume(Cql2TokenType.RightParen, "Expected ')' after value list");
            return new BinaryExpression(expr, BinaryOperator.NotIn, values);
        }

        // Handle IN
        if (Match(Cql2TokenType.In))
        {
            Consume(Cql2TokenType.LeftParen, "Expected '(' after IN");
            var values = ParseValueList();
            Consume(Cql2TokenType.RightParen, "Expected ')' after value list");
            return new BinaryExpression(expr, BinaryOperator.In, values);
        }

        // Handle IS NULL / IS NOT NULL
        if (Match(Cql2TokenType.IsNull))
        {
            if (Match(Cql2TokenType.Not))
            {
                Consume(Cql2TokenType.Null, "Expected NULL after IS NOT");
                return new UnaryExpression(UnaryOperator.IsNotNull, expr);
            }
            else
            {
                Consume(Cql2TokenType.Null, "Expected NULL after IS");
                return new UnaryExpression(UnaryOperator.IsNull, expr);
            }
        }

        return expr;
    }

    private FilterExpression ParsePrimaryExpression()
    {
        // Handle parenthesized expressions
        if (Match(Cql2TokenType.LeftParen))
        {
            var expr = ParseOrExpression();
            Consume(Cql2TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        // Handle spatial predicates
        if (IsSpatialPredicate(Current().Type))
        {
            return ParseSpatialPredicate();
        }

        // Handle literals
        if (Current().Type == Cql2TokenType.Text)
        {
            var value = Advance().Value;
            return new Literal(value, LiteralType.Text);
        }

        if (Current().Type == Cql2TokenType.Number)
        {
            var value = Advance().Value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return new Literal(number, LiteralType.Number);
            }
            throw new ArgumentException($"Invalid number format: {value}");
        }

        if (Current().Type == Cql2TokenType.Boolean)
        {
            var value = Advance().Value;
            var boolValue = string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase);
            return new Literal(boolValue, LiteralType.Boolean);
        }

        if (Current().Type == Cql2TokenType.Null)
        {
            Advance();
            return new Literal(null, LiteralType.Null);
        }

        // Handle identifiers (property references)
        if (Current().Type == Cql2TokenType.Identifier)
        {
            var propertyName = Advance().Value;
            return new PropertyReference(propertyName);
        }

        // Handle geometry literals
        if (IsGeometryType(Current().Type))
        {
            return ParseGeometryLiteral();
        }

        throw new ArgumentException($"Unexpected token '{Current().Value}' at position {Current().Position}");
    }

    private SpatialPredicate ParseSpatialPredicate()
    {
        var predicateToken = Advance();
        var spatialOperator = GetSpatialOperator(predicateToken.Type);

        Consume(Cql2TokenType.LeftParen, "Expected '(' after spatial predicate");

        // First argument should be a property reference
        var property = ParsePrimaryExpression();
        if (property is not PropertyReference propRef)
            throw new ArgumentException("First argument to spatial predicate must be a property reference");

        Consume(Cql2TokenType.Comma, "Expected ',' after property in spatial predicate");

        // Second argument should be a geometry
        var geometry = ParsePrimaryExpression();
        if (geometry is not GeometryLiteral geomLiteral)
            throw new ArgumentException("Second argument to spatial predicate must be a geometry literal");

        Consume(Cql2TokenType.RightParen, "Expected ')' after spatial predicate arguments");

        return new SpatialPredicate(spatialOperator, propRef, geomLiteral);
    }

    private GeometryLiteral ParseGeometryLiteral()
    {
        var geometryType = Advance().Value;
        Consume(Cql2TokenType.LeftParen, $"Expected '(' after {geometryType}");

        // For now, we'll parse the WKT representation
        // In a full implementation, this would handle the coordinate list parsing
        var wktBuilder = new System.Text.StringBuilder();
        wktBuilder.Append(geometryType);
        wktBuilder.Append('(');

        var parenCount = 1;
        while (parenCount > 0 && !IsAtEnd())
        {
            var token = Advance();
            if (token.Type == Cql2TokenType.LeftParen)
                parenCount++;
            else if (token.Type == Cql2TokenType.RightParen)
                parenCount--;

            if (parenCount > 0 || token.Type == Cql2TokenType.RightParen)
                wktBuilder.Append(token.Value);

            if (!IsAtEnd() && parenCount > 0)
                wktBuilder.Append(' ');
        }

        var wktText = wktBuilder.ToString();

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

    private ValueList ParseValueList()
    {
        var values = new List<Literal>();

        do
        {
            var expr = ParsePrimaryExpression();
            if (expr is not Literal literal)
                throw new ArgumentException("Value list must contain only literal values");

            values.Add(literal);

        } while (Match(Cql2TokenType.Comma));

        return new ValueList(values);
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
               Cql2TokenType.S_Disjoint or Cql2TokenType.S_Equals;
    }

    private bool IsGeometryType(Cql2TokenType type)
    {
        return type is Cql2TokenType.Point or Cql2TokenType.LineString or
               Cql2TokenType.Polygon or Cql2TokenType.MultiPoint or
               Cql2TokenType.MultiLineString or Cql2TokenType.MultiPolygon or
               Cql2TokenType.GeometryCollection;
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
            _ => throw new ArgumentException($"Unknown spatial operator: {type}")
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
