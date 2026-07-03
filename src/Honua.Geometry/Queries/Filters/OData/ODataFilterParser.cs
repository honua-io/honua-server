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
                // Use decimal for dot-only literals (no exponent) to preserve
                // full precision for Edm.Decimal comparisons (e.g. monetary
                // or high-precision IDs).  Fall back to double only for
                // scientific-notation forms where Edm.Double semantics apply.
                if (!value.Contains('e') && !value.Contains('E'))
                {
                    if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var dec))
                    {
                        throw new ODataFilterParseException($"Invalid numeric literal '{value}'", Previous().Position);
                    }

                    return new Literal(dec, LiteralType.Number);
                }

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
            // OData ABNF requires an explicit offset or 'Z' on dateTimeOffsetValue.
            // DateTimeStyles.RoundtripKind silently accepts offset-less strings and
            // interprets them in the server's local time zone, yielding TZ-dependent
            // query results.  AssumeUniversal treats offset-less inputs as UTC so
            // behaviour is at least deterministic, and the resulting DateTimeOffset
            // always carries a zero offset that round-trips consistently.
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
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
            "geo.length" => BuildGeoLengthExpression(args, identifier),
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

    private static FunctionCall BuildGeoLengthExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        // OData v4 geo.length over Edm.Geography returns geodesic length in meters,
        // mirroring geo.distance. Translators map GEOLENGTH to a geography-based
        // ST_Length, distinct from the planar CQL2/OGC ST_LENGTH function.
        EnsureArgumentCount(identifier, args, 1);
        return new FunctionCall("GEOLENGTH", args);
    }

    private static SpatialPredicate BuildGeoIntersectsExpression(IReadOnlyList<FilterExpression> args, string identifier)
    {
        EnsureArgumentCount(identifier, args, 2);

        // OData geo.intersects over Edm.Geography values has geodesic (ellipsoidal)
        // semantics, unlike the planar-in-CRS semantics of CQL2 S_INTERSECTS and
        // FES Intersects. The Geodesic flag is the protocol marker translators use
        // to route the predicate through geography evaluation on geographic layers
        // without changing any other protocol's behavior. Literals that a geography
        // type cannot represent faithfully (pole vertices, 180°/360° longitude edges
        // — e.g. the common whole-world envelope) stay planar.
        return new SpatialPredicate(SpatialOperator.Intersects, args[0], args[1])
        {
            Geodesic = args.All(IsGeographyCompatible)
        };
    }

    // PostGIS geography cannot represent edges whose endpoints are 180° (antipodal
    // ambiguity) or 360° (degenerate same-point edge) of longitude apart, and rings
    // with pole vertices collapse: the ubiquitous whole-world envelope
    // POLYGON((-180 -90, 180 -90, 180 90, -180 90, -180 -90)) becomes a zero-area
    // ring that matches nothing. Such literals keep planar evaluation. Everything
    // else — including antimeridian-crossing polygons, which geography evaluates
    // correctly via shortest-path edges — is geodesic-eligible.
    private static bool IsGeographyCompatible(FilterExpression expression)
    {
        if (expression is not GeometryLiteral literal)
        {
            // Property references resolve against the layer geometry column; the
            // translator gates on the layer's geographic context instead.
            return true;
        }

        try
        {
            var geometry = new WKBReader().Read(literal.Wkb);
            return IsGeographyCompatible(geometry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable literal: fall back to the planar path rather than reject.
            return false;
        }
    }

    private static bool IsGeographyCompatible(Geometry geometry)
    {
        switch (geometry)
        {
            case Point point:
                return IsPoleFree(point.Coordinate);
            case LineString line:
                return AreEdgesGeographyCompatible(line.Coordinates);
            case Polygon polygon:
                if (!AreEdgesGeographyCompatible(polygon.ExteriorRing.Coordinates))
                {
                    return false;
                }

                for (var i = 0; i < polygon.NumInteriorRings; i++)
                {
                    if (!AreEdgesGeographyCompatible(polygon.GetInteriorRingN(i).Coordinates))
                    {
                        return false;
                    }
                }

                return true;
            case GeometryCollection collection:
                foreach (var component in collection.Geometries)
                {
                    if (!IsGeographyCompatible(component))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    private static bool AreEdgesGeographyCompatible(Coordinate[] coordinates)
    {
        const double epsilon = 1e-9;
        for (var i = 0; i < coordinates.Length; i++)
        {
            if (!IsPoleFree(coordinates[i]))
            {
                return false;
            }

            if (i == 0)
            {
                continue;
            }

            var longitudeSpan = Math.Abs(coordinates[i].X - coordinates[i - 1].X);
            if (Math.Abs(longitudeSpan - 180d) < epsilon || Math.Abs(longitudeSpan - 360d) < epsilon)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPoleFree(Coordinate coordinate)
        => Math.Abs(coordinate.Y) < 90d - 1e-9;

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
            if (!DateTimeOffset.TryParse(token.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
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
            if (op is BinaryOperator.Equal or BinaryOperator.NotEqual)
            {
                var operand = left is Literal { Type: LiteralType.Null } ? right : left;
                return op == BinaryOperator.Equal
                    ? new UnaryExpression(UnaryOperator.IsNull, operand)
                    : new UnaryExpression(UnaryOperator.IsNotNull, operand);
            }

            // OData v4.01 §5.1.1.1.3–5.1.1.1.6: if any operand of a relational
            // operator (gt/ge/lt/le) is null, the operator returns false.
            return new Literal(false, LiteralType.Boolean);
        }

        // OData v4.01 eq/ne use two-valued logic where "the null value is equal to
        // itself and not equal to any other value", but SQL '='/'<>' use three-valued
        // logic where any comparison with NULL is UNKNOWN and the row is filtered out
        // (e.g. `Status ne 'closed'` must return rows whose Status is null). Rewrite
        // the comparison here — at the protocol boundary — into null-safe AST shapes
        // (IS [NOT] DISTINCT FROM semantics built from shared nodes) so the shared SQL
        // translators keep standard SQL 3VL for CQL2/FES/GeoServices SQL filters.
        if (op == BinaryOperator.NotEqual)
        {
            return BuildNullSafeNotEqual(left, right);
        }

        if (op == BinaryOperator.Equal && !IsNonNullValue(left) && !IsNonNullValue(right))
        {
            // Both operands may evaluate to null (e.g. two nullable properties):
            // OData requires `null eq null` to be true, SQL '=' yields UNKNOWN.
            return new BinaryExpression(
                new BinaryExpression(left, BinaryOperator.Equal, right),
                BinaryOperator.Or,
                new BinaryExpression(
                    new UnaryExpression(UnaryOperator.IsNull, left),
                    BinaryOperator.And,
                    new UnaryExpression(UnaryOperator.IsNull, right)));
        }

        return new BinaryExpression(left, op, right);
    }

    private static BinaryExpression BuildNullSafeNotEqual(FilterExpression left, FilterExpression right)
    {
        var notEqual = new BinaryExpression(left, BinaryOperator.NotEqual, right);
        var leftNullable = !IsNonNullValue(left);
        var rightNullable = !IsNonNullValue(right);

        if (!leftNullable && !rightNullable)
        {
            return notEqual;
        }

        if (leftNullable && rightNullable)
        {
            // Full IS DISTINCT FROM expansion: true when exactly one side is null,
            // false when both are null, plain '<>' when both are non-null.
            return new BinaryExpression(
                new BinaryExpression(
                    notEqual,
                    BinaryOperator.Or,
                    new BinaryExpression(
                        new UnaryExpression(UnaryOperator.IsNull, left),
                        BinaryOperator.And,
                        new UnaryExpression(UnaryOperator.IsNotNull, right))),
                BinaryOperator.Or,
                new BinaryExpression(
                    new UnaryExpression(UnaryOperator.IsNotNull, left),
                    BinaryOperator.And,
                    new UnaryExpression(UnaryOperator.IsNull, right)));
        }

        // One side is a non-null literal: a null on the other side must satisfy 'ne'.
        var nullableOperand = leftNullable ? left : right;
        return new BinaryExpression(
            notEqual,
            BinaryOperator.Or,
            new UnaryExpression(UnaryOperator.IsNull, nullableOperand));
    }

    // Conservative non-null detection: only literals with a concrete value are known
    // to never evaluate to null. Properties, functions and arithmetic over them can
    // all produce SQL NULL at evaluation time.
    private static bool IsNonNullValue(FilterExpression expression)
        => expression is Literal { Type: not LiteralType.Null } or GeometryLiteral;

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
