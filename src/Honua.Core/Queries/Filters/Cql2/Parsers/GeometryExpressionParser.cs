// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Queries.Filters.Cql2.Parsers;

/// <summary>
/// Specialized parser for CQL2 geometry expressions (POINT, POLYGON, BBOX, etc.).
/// Extracted from Cql2Parser to improve maintainability and focus.
/// </summary>
internal sealed class GeometryExpressionParser
{
    /// <summary>
    /// Parses geometry literal expressions from CQL2 tokens
    /// </summary>
    public static GeometryLiteral ParseGeometryLiteral(IReadOnlyList<Cql2Token> tokens, ref int position, string input)
    {
        var geometryToken = tokens[position++];

        if (geometryToken.Type == Cql2TokenType.Bbox)
        {
            return ParseBboxLiteral(tokens, ref position, geometryToken);
        }

        ConsumeToken(tokens, ref position, Cql2TokenType.LeftParen, $"Expected '(' after {geometryToken.Value}");
        var startPosition = geometryToken.Position;

        var parenDepth = 1;
        var lastToken = tokens[position - 1];

        while (parenDepth > 0 && position < tokens.Count)
        {
            var token = tokens[position++];
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
        var wktText = input[startPosition..endPosition];

        return CreateGeometryFromWkt(wktText);
    }

    /// <summary>
    /// Determines if the current token represents a geometry type
    /// </summary>
    public static bool IsGeometryType(Cql2TokenType type)
    {
        return type is Cql2TokenType.Point or Cql2TokenType.LineString or
               Cql2TokenType.Polygon or Cql2TokenType.MultiPoint or
               Cql2TokenType.MultiLineString or Cql2TokenType.MultiPolygon or
               Cql2TokenType.GeometryCollection or Cql2TokenType.Bbox;
    }

    private static GeometryLiteral ParseBboxLiteral(IReadOnlyList<Cql2Token> tokens, ref int position, Cql2Token bboxToken)
    {
        ConsumeToken(tokens, ref position, Cql2TokenType.LeftParen, "Expected '(' after BBOX");

        var minX = ParseSignedNumber(tokens, ref position);
        ConsumeToken(tokens, ref position, Cql2TokenType.Comma, "Expected ',' after minX");
        var minY = ParseSignedNumber(tokens, ref position);
        ConsumeToken(tokens, ref position, Cql2TokenType.Comma, "Expected ',' after minY");
        var maxX = ParseSignedNumber(tokens, ref position);
        ConsumeToken(tokens, ref position, Cql2TokenType.Comma, "Expected ',' after maxX");
        var maxY = ParseSignedNumber(tokens, ref position);

        // Optional Z coordinates
        if (position < tokens.Count && tokens[position].Type == Cql2TokenType.Comma)
        {
            position++; // consume comma
            ParseSignedNumber(tokens, ref position); // minZ
            ConsumeToken(tokens, ref position, Cql2TokenType.Comma, "Expected ',' after minZ");
            ParseSignedNumber(tokens, ref position); // maxZ
        }

        ConsumeToken(tokens, ref position, Cql2TokenType.RightParen, "Expected ')' after BBOX");

        return CreateBboxGeometry(minX, minY, maxX, maxY, bboxToken, tokens[position - 1]);
    }

    private static GeometryLiteral CreateBboxGeometry(double minX, double minY, double maxX, double maxY,
        Cql2Token startToken, Cql2Token endToken)
    {
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

        var originalText = $"BBOX({minX}, {minY}, {maxX}, {maxY})";
        return new GeometryLiteral(wkb, 4326, originalText);
    }

    private static GeometryLiteral CreateGeometryFromWkt(string wktText)
    {
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

    private static double ParseSignedNumber(IReadOnlyList<Cql2Token> tokens, ref int position)
    {
        var sign = 1.0;

        if (position < tokens.Count && tokens[position].Type == Cql2TokenType.Minus)
        {
            sign = -1.0;
            position++;
        }
        else if (position < tokens.Count && tokens[position].Type == Cql2TokenType.Plus)
        {
            position++;
        }

        var token = ConsumeToken(tokens, ref position, Cql2TokenType.Number, "Expected numeric literal");
        if (!double.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException($"Invalid numeric literal: {token.Value}");
        }

        return sign * number;
    }

    private static Cql2Token ConsumeToken(IReadOnlyList<Cql2Token> tokens, ref int position, Cql2TokenType expectedType, string message)
    {
        if (position >= tokens.Count || tokens[position].Type != expectedType)
        {
            var current = position < tokens.Count ? tokens[position] : new Cql2Token(Cql2TokenType.EndOfInput, "", 0, 0);
            throw new ArgumentException($"{message}. Found '{current.Value}' at position {current.Position}");
        }

        return tokens[position++];
    }
}
