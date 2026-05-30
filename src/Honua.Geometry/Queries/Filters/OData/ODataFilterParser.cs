// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters.OData;

/// <summary>
/// Parses OData $filter expressions into a shared filter expression AST.
/// </summary>
public sealed class ODataFilterParser
{
    private IReadOnlyList<ODataFilterToken> _tokens = [];
    private int _position;
    private int _expressionDepth;

    /// <summary>
    /// Parses an OData $filter expression into a <see cref="FilterExpression"/>.
    /// </summary>
    /// <param name="filter">The OData $filter string.</param>
    /// <returns>Parsed filter expression AST.</returns>
    /// <exception cref="ArgumentException">Thrown when parsing fails.</exception>
    public FilterExpression Parse(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new ArgumentException("OData filter cannot be null or empty.", nameof(filter));
        }

        var lexer = new ODataFilterLexer(filter);
        _tokens = lexer.Tokenize();
        _position = 0;
        _expressionDepth = 0;

        var expression = ParseExpression();
        if (!IsAtEnd())
        {
            throw new ODataFilterParseException("Unexpected token after end of expression", Current().Position);
        }

        // The OData spec requires $filter to be a boolean expression. A bare literal
        // or a bare property reference (e.g. "$filter=invalid_syntax") is not a valid
        // filter — the translator would otherwise silently coerce it into an unbounded
        // attribute lookup and return 200 with all features instead of rejecting the
        // request as malformed. Reject up-front for a clearer 400.
        if (expression is PropertyReference || expression is Literal)
        {
            throw new ODataFilterParseException(
                "$filter must be a boolean expression (e.g. 'field op value'); a bare property reference or literal is not valid.",
                position: 0);
        }

        return expression;
    }

    private FilterExpression ParseExpression()
        => ParseOrExpression();

    private FilterExpression ParseOrExpression()
    {
        var expression = ParseAndExpression();

        while (Match(ODataFilterTokenType.Or))
        {
            var right = ParseAndExpression();
            expression = new BinaryExpression(expression, BinaryOperator.Or, right);
        }

        return expression;
    }

    private FilterExpression ParseAndExpression()
    {
        var expression = ParseComparisonExpression();

        while (Match(ODataFilterTokenType.And))
        {
            var right = ParseComparisonExpression();
            expression = new BinaryExpression(expression, BinaryOperator.And, right);
        }

        return expression;
    }

    private FilterExpression ParseComparisonExpression()
    {
        var left = ParseAdditiveExpression();

        if (Match(ODataFilterTokenType.Eq) ||
            Match(ODataFilterTokenType.Ne) ||
            Match(ODataFilterTokenType.Gt) ||
            Match(ODataFilterTokenType.Ge) ||
            Match(ODataFilterTokenType.Lt) ||
            Match(ODataFilterTokenType.Le))
        {
            var opToken = Previous();
            var right = ParseAdditiveExpression();
            var op = MapComparisonOperator(opToken.Type);
            return NormalizeComparison(left, op, right, opToken.Position);
        }

        if (Match(ODataFilterTokenType.In))
        {
            Consume(ODataFilterTokenType.LeftParen, "Expected '(' after 'in'");
            var values = new List<FilterExpression>();
            if (!Check(ODataFilterTokenType.RightParen))
            {
                do
                {
                    values.Add(ParseAdditiveExpression());
                    // Cap the number of IN operands so a single malicious request
                    // cannot force the parser to allocate an arbitrarily large list.
                    FilterParserGuard.EnsureInListSize(values.Count, "OData");
                } while (Match(ODataFilterTokenType.Comma));
            }

            Consume(ODataFilterTokenType.RightParen, "Expected ')' after value list");
            return new BinaryExpression(left, BinaryOperator.In, new ValueList(values));
        }

        return left;
    }

    private FilterExpression ParseAdditiveExpression()
    {
        var expression = ParseMultiplicativeExpression();

        while (Match(ODataFilterTokenType.Add) || Match(ODataFilterTokenType.Sub))
        {
            var opToken = Previous();
            var right = ParseMultiplicativeExpression();
            var op = opToken.Type == ODataFilterTokenType.Add ? BinaryOperator.Add : BinaryOperator.Subtract;
            expression = new BinaryExpression(expression, op, right);
        }

        return expression;
    }

    private FilterExpression ParseMultiplicativeExpression()
    {
        var expression = ParseUnaryExpression();

        while (Match(ODataFilterTokenType.Mul) || Match(ODataFilterTokenType.Div) || Match(ODataFilterTokenType.Mod))
        {
            var opToken = Previous();
            var right = ParseUnaryExpression();
            var op = opToken.Type switch
            {
                ODataFilterTokenType.Mul => BinaryOperator.Multiply,
                ODataFilterTokenType.Div => BinaryOperator.Divide,
                ODataFilterTokenType.Mod => BinaryOperator.Modulo,
                _ => throw new ODataFilterParseException("Unsupported arithmetic operator", opToken.Position)
            };
            expression = new BinaryExpression(expression, op, right);
        }

        return expression;
    }

    private FilterExpression ParseUnaryExpression()
        => ParseWithDepth(() =>
        {
            if (Match(ODataFilterTokenType.Not))
            {
                var operand = ParseUnaryExpression();
                return new UnaryExpression(UnaryOperator.Not, operand);
            }

            if (Match(ODataFilterTokenType.Minus))
            {
                var operand = ParseUnaryExpression();
                return new UnaryExpression(UnaryOperator.Negate, operand);
            }

            return ParsePrimaryExpression();
        });

    private FilterExpression ParsePrimaryExpression()
    {
        if (Match(ODataFilterTokenType.LeftParen))
        {
            var expression = ParseExpression();
            Consume(ODataFilterTokenType.RightParen, "Expected ')'");
            return expression;
        }

        if (Match(ODataFilterTokenType.BooleanLiteral))
        {
            var value = string.Equals(Previous().Value, "true", StringComparison.OrdinalIgnoreCase);
            return new Literal(value, LiteralType.Boolean);
        }

        if (Match(ODataFilterTokenType.NullLiteral))
        {
            return new Literal(null, LiteralType.Null);
        }

        if (Match(ODataFilterTokenType.NumberLiteral))
        {
            var value = Previous().Value;
            if (value.Contains('.') || value.Contains('e') || value.Contains('E'))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl))
                {
                    throw new ODataFilterParseException($"Invalid numeric literal '{value}'", Previous().Position);
                }

                return new Literal(dbl, LiteralType.Number);
            }

            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                throw new ODataFilterParseException($"Invalid numeric literal '{value}'", Previous().Position);
            }

            if (integer is >= int.MinValue and <= int.MaxValue)
            {
                return new Literal((int)integer, LiteralType.Number);
            }

            return new Literal(integer, LiteralType.Number);
        }

        if (Match(ODataFilterTokenType.DateLiteral))
        {
            var value = Previous().Value;
            if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                throw new ODataFilterParseException($"Invalid date literal '{value}'", Previous().Position);
            }

            return new Literal(date, LiteralType.Date);
        }

        if (Match(ODataFilterTokenType.DateTimeLiteral))
        {
            var value = Previous().Value;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            {
                throw new ODataFilterParseException($"Invalid datetime literal '{value}'", Previous().Position);
            }

            return new Literal(timestamp, LiteralType.DateTime);
        }

        if (Match(ODataFilterTokenType.StringLiteral))
        {
            return new Literal(Previous().Value, LiteralType.Text);
        }

        if (Match(ODataFilterTokenType.Identifier))
        {
            var identifier = Previous().Value;

            if (Match(ODataFilterTokenType.LeftParen))
            {
                return ParseFunctionCall(identifier);
            }

            if (TryParseTypedLiteral(identifier, out var typedLiteral))
            {
                return typedLiteral;
            }

            return new PropertyReference(identifier);
        }

        throw new ODataFilterParseException("Unexpected token in expression", Current().Position);
    }

    private T ParseWithDepth<T>(Func<T> parse)
    {
        _expressionDepth++;
        try
        {
            if (_expressionDepth > FilterParserGuard.MaxExpressionDepth)
            {
                throw new ODataFilterParseException(
                    $"Filter expression exceeds the maximum nesting depth of {FilterParserGuard.MaxExpressionDepth}.",
                    Current().Position);
            }

            return parse();
        }
        finally
        {
            _expressionDepth--;
        }
    }

    private FilterExpression ParseFunctionCall(string identifier)
    {
        var args = new List<FilterExpression>();

        if (!Check(ODataFilterTokenType.RightParen))
        {
            do
            {
                args.Add(ParseExpression());
            } while (Match(ODataFilterTokenType.Comma));
        }

        Consume(ODataFilterTokenType.RightParen, "Expected ')' after function arguments");

        return BuildFunctionExpression(identifier, args);
    }

    private FilterExpression BuildFunctionExpression(string identifier, IReadOnlyList<FilterExpression> args)
    {
        var name = identifier.ToLowerInvariant();

        return name switch
        {
            "contains" => BuildContainsExpression(args, identifier),
            "startswith" => BuildStartsWithExpression(args, identifier),
            "endswith" => BuildEndsWithExpression(args, identifier),
            "substring" => BuildSubstringExpression(args, identifier),
            "tolower" => BuildUnaryFunction("LOWER", args, identifier),
            "toupper" => BuildUnaryFunction("UPPER", args, identifier),
            "length" => BuildUnaryFunction("LENGTH", args, identifier),
            "trim" => BuildUnaryFunction("TRIM", args, identifier),
            "indexof" => BuildIndexOfExpression(args, identifier),
            "replace" => BuildReplaceExpression(args, identifier),
            "round" => BuildUnaryFunction("ROUND", args, identifier),
            "floor" => BuildUnaryFunction("FLOOR", args, identifier),
            "ceiling" => BuildUnaryFunction("CEILING", args, identifier),
            "abs" => BuildUnaryFunction("ABS", args, identifier),
            "now" => BuildZeroArgFunction("NOW", args, identifier),
            "concat" => BuildConcatExpression(args, identifier),
            "year" => BuildUnaryFunction("YEAR", args, identifier),
            "month" => BuildUnaryFunction("MONTH", args, identifier),
            "day" => BuildUnaryFunction("DAY", args, identifier),
            "hour" => BuildUnaryFunction("HOUR", args, identifier),
            "minute" => BuildUnaryFunction("MINUTE", args, identifier),
            "second" => BuildUnaryFunction("SECOND", args, identifier),
            "geo.distance" => BuildGeoDistanceExpression(args, identifier),
            "geo.intersects" => BuildGeoIntersectsExpression(args, identifier),
            _ => throw new ODataFilterParseException($"Unsupported function '{identifier}'", Previous().Position)
        };
    }

    private static BinaryExpression BuildContainsExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 2);
        var position = new FunctionCall("POSITION", new[] { args[1], args[0] });
        return new BinaryExpression(position, BinaryOperator.GreaterThan, new Literal(0, LiteralType.Number));
    }

    private static BinaryExpression BuildStartsWithExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 2);
        var position = new FunctionCall("POSITION", new[] { args[1], args[0] });
        return new BinaryExpression(position, BinaryOperator.Equal, new Literal(1, LiteralType.Number));
    }

    private static BinaryExpression BuildEndsWithExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 2);
        var lengthTarget = new FunctionCall("LENGTH", new[] { args[0] });
        var lengthSuffix = new FunctionCall("LENGTH", new[] { args[1] });
        var start = new BinaryExpression(
            new BinaryExpression(lengthTarget, BinaryOperator.Subtract, lengthSuffix),
            BinaryOperator.Add,
            new Literal(1, LiteralType.Number));
        var substring = new FunctionCall("SUBSTRING", new[] { args[0], start });
        return new BinaryExpression(substring, BinaryOperator.Equal, args[1]);
    }

    private static FunctionCall BuildSubstringExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        if (args.Count is < 2 or > 3)
        {
            throw new ArgumentException($"{identifier} requires 2 or 3 arguments.");
        }

        var start = AdjustSubstringStart(args[1]);
        if (args.Count == 2)
        {
            return new FunctionCall("SUBSTRING", new[] { args[0], start });
        }

        return new FunctionCall("SUBSTRING", new[] { args[0], start, args[2] });
    }

    private static FunctionCall BuildConcatExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        if (args.Count < 2)
        {
            throw new ArgumentException($"{identifier} requires at least 2 arguments.");
        }

        return new FunctionCall("CONCAT", args);
    }

    private static BinaryExpression BuildIndexOfExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 2);
        var position = new FunctionCall("POSITION", new[] { args[1], args[0] });
        return new BinaryExpression(position, BinaryOperator.Subtract, new Literal(1, LiteralType.Number));
    }

    private static FunctionCall BuildReplaceExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 3);
        return new FunctionCall("REPLACE", args);
    }

    private static FunctionCall BuildZeroArgFunction(string name, IReadOnlyList<FilterExpression> args, string identifier)
    {
        if (args.Count != 0)
        {
            throw new ArgumentException($"{identifier} does not accept arguments.");
        }

        return new FunctionCall(name, Array.Empty<FilterExpression>());
    }

    private static FunctionCall BuildUnaryFunction(string name, IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 1);
        return new FunctionCall(name, args);
    }

    private static FunctionCall BuildGeoDistanceExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 2);
        return new FunctionCall("GEODISTANCE", args);
    }

    private static SpatialPredicate BuildGeoIntersectsExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 2);
        return new SpatialPredicate(SpatialOperator.Intersects, args[0], args[1]);
    }

    private static void EnsureArgumentCount(string identifier, IReadOnlyList<FilterExpression> args, int expected)
    {
        if (args.Count != expected)
        {
            throw new ArgumentException($"{identifier} requires {expected} arguments.");
        }
    }

    private bool TryParseTypedLiteral(string identifier, out FilterExpression literal)
    {
        literal = null!;

        if (!Check(ODataFilterTokenType.StringLiteral))
        {
            return false;
        }

        if (identifier.Equals("datetime", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase))
        {
            var token = Advance();
            if (!DateTimeOffset.TryParse(token.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            {
                throw new ODataFilterParseException($"Invalid datetime literal '{token.Value}'", token.Position);
            }

            literal = new Literal(timestamp, LiteralType.DateTime);
            return true;
        }

        if (identifier.Equals("date", StringComparison.OrdinalIgnoreCase))
        {
            var token = Advance();
            if (!DateOnly.TryParse(token.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                throw new ODataFilterParseException($"Invalid date literal '{token.Value}'", token.Position);
            }

            literal = new Literal(date, LiteralType.Date);
            return true;
        }

        if (identifier.Equals("geography", StringComparison.OrdinalIgnoreCase) ||
            identifier.Equals("geometry", StringComparison.OrdinalIgnoreCase))
        {
            var token = Advance();
            literal = ParseGeographyLiteral(token.Value, token.Position);
            return true;
        }

        return false;
    }

    private static GeometryLiteral ParseGeographyLiteral(string value, int position)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ODataFilterParseException("Geography literal cannot be empty", position);
        }

        var trimmed = value.Trim();
        var srid = 4326;
        var wkt = trimmed;

        if (trimmed.StartsWith("SRID=", StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = trimmed.IndexOf(';');
            if (separatorIndex < 0 || separatorIndex <= 5)
            {
                throw new ODataFilterParseException($"Invalid SRID literal '{value}'", position);
            }

            var sridToken = trimmed[5..separatorIndex];
            if (!int.TryParse(sridToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid))
            {
                throw new ODataFilterParseException($"Invalid SRID value '{sridToken}'", position);
            }

            wkt = trimmed[(separatorIndex + 1)..].Trim();
        }

        try
        {
            var geometry = FilterParserGeometryGuard.ParseWktGeometry(wkt, "OData geometry literal", srid);
            if (!IsGeometryValid(geometry) || !geometry.IsValid)
            {
                throw new ODataFilterParseException($"Invalid geometry literal '{value}'", position);
            }

            var writer = new WKBWriter();
            var wkb = writer.Write(geometry);
            return new GeometryLiteral(wkb, srid, value);
        }
        catch (ODataFilterParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ODataFilterParseException($"Invalid geometry literal '{value}': {ex.Message}", position);
        }
    }

    private static bool IsGeometryValid(Geometry geometry)
    {
        if (geometry is Polygon polygon)
        {
            return IsLinearRingValid(polygon.ExteriorRing);
        }

        if (geometry is MultiPolygon multiPolygon)
        {
            foreach (var item in multiPolygon.Geometries)
            {
                if (item is Polygon poly && !IsLinearRingValid(poly.ExteriorRing))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsLinearRingValid(LineString? ring)
    {
        if (ring == null)
        {
            return false;
        }

        return ring.IsClosed && ring.NumPoints >= 4;
    }

    private static FilterExpression AdjustSubstringStart(FilterExpression start)
    {
        if (start is Literal { Type: LiteralType.Number } literal)
        {
            return literal.Value switch
            {
                int i => new Literal(i + 1, LiteralType.Number),
                long l => new Literal(l + 1, LiteralType.Number),
                double d => new Literal(d + 1, LiteralType.Number),
                _ => new BinaryExpression(start, BinaryOperator.Add, new Literal(1, LiteralType.Number))
            };
        }

        return new BinaryExpression(start, BinaryOperator.Add, new Literal(1, LiteralType.Number));
    }

    private static FilterExpression NormalizeComparison(FilterExpression left, BinaryOperator op, FilterExpression right, int position)
    {
        if (left is Literal { Type: LiteralType.Null } || right is Literal { Type: LiteralType.Null })
        {
            if (op is not (BinaryOperator.Equal or BinaryOperator.NotEqual))
            {
                throw new ODataFilterParseException("Null comparisons require eq or ne", position);
            }

            var operand = left is Literal { Type: LiteralType.Null } ? right : left;
            return op == BinaryOperator.Equal
                ? new UnaryExpression(UnaryOperator.IsNull, operand)
                : new UnaryExpression(UnaryOperator.IsNotNull, operand);
        }

        return new BinaryExpression(left, op, right);
    }

    private static BinaryOperator MapComparisonOperator(ODataFilterTokenType tokenType)
    {
        return tokenType switch
        {
            ODataFilterTokenType.Eq => BinaryOperator.Equal,
            ODataFilterTokenType.Ne => BinaryOperator.NotEqual,
            ODataFilterTokenType.Gt => BinaryOperator.GreaterThan,
            ODataFilterTokenType.Ge => BinaryOperator.GreaterThanOrEqual,
            ODataFilterTokenType.Lt => BinaryOperator.LessThan,
            ODataFilterTokenType.Le => BinaryOperator.LessThanOrEqual,
            _ => throw new ArgumentOutOfRangeException(nameof(tokenType), tokenType, "Unsupported comparison operator.")
        };
    }

    private bool Match(ODataFilterTokenType type)
    {
        if (Check(type))
        {
            _position++;
            return true;
        }

        return false;
    }

    private ODataFilterToken Consume(ODataFilterTokenType type, string message)
    {
        if (Check(type))
        {
            return Advance();
        }

        throw new ODataFilterParseException(message, Current().Position);
    }

    private bool Check(ODataFilterTokenType type)
        => Current().Type == type;

    private bool IsAtEnd()
        => Current().Type == ODataFilterTokenType.EndOfInput;

    private ODataFilterToken Advance()
    {
        if (!IsAtEnd())
        {
            _position++;
        }

        return Previous();
    }

    private ODataFilterToken Current() => _tokens[_position];

    private ODataFilterToken Previous() => _tokens[_position - 1];
}
