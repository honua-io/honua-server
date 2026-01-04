// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Queries.Filters.Cql2.Parsers;

/// <summary>
/// Specialized parser for CQL2 temporal expressions (DATE, TIMESTAMP, INTERVAL).
/// Extracted from Cql2Parser to improve maintainability and focus.
/// </summary>
internal sealed class TemporalExpressionParser
{
    /// <summary>
    /// Parses temporal literal expressions (DATE, TIMESTAMP, INTERVAL)
    /// </summary>
    public static FilterExpression ParseTemporalLiteral(IReadOnlyList<Cql2Token> tokens, ref int position)
    {
        var current = tokens[position];

        if (current.Type == Cql2TokenType.Date)
        {
            position++;
            return ParseDateLiteral(tokens, ref position);
        }

        if (current.Type == Cql2TokenType.Timestamp)
        {
            position++;
            return ParseTimestampLiteral(tokens, ref position);
        }

        if (current.Type == Cql2TokenType.Interval)
        {
            position++;
            return ParseIntervalLiteral(tokens, ref position);
        }

        throw new ArgumentException($"Unexpected temporal literal '{current.Value}' at position {current.Position}");
    }

    /// <summary>
    /// Determines if the current token starts a temporal literal
    /// </summary>
    public static bool IsTemporalLiteralStart(IReadOnlyList<Cql2Token> tokens, int position)
    {
        if (position >= tokens.Count)
            return false;

        var current = tokens[position];
        return IsTemporalLiteral(current.Type) &&
               position + 1 < tokens.Count &&
               tokens[position + 1].Type == Cql2TokenType.LeftParen;
    }

    private static bool IsTemporalLiteral(Cql2TokenType type)
    {
        return type is Cql2TokenType.Date or Cql2TokenType.Timestamp or Cql2TokenType.Interval;
    }

    private static Literal ParseDateLiteral(IReadOnlyList<Cql2Token> tokens, ref int position)
    {
        ConsumeToken(tokens, ref position, Cql2TokenType.LeftParen, "Expected '(' after DATE");
        var textToken = ConsumeToken(tokens, ref position, Cql2TokenType.Text, "Expected date literal");
        ConsumeToken(tokens, ref position, Cql2TokenType.RightParen, "Expected ')' after DATE literal");

        if (!DateOnly.TryParseExact(textToken.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            throw new ArgumentException($"Invalid DATE literal: {textToken.Value}");
        }

        return new Literal(date, LiteralType.Date);
    }

    private static Literal ParseTimestampLiteral(IReadOnlyList<Cql2Token> tokens, ref int position)
    {
        ConsumeToken(tokens, ref position, Cql2TokenType.LeftParen, "Expected '(' after TIMESTAMP");
        var textToken = ConsumeToken(tokens, ref position, Cql2TokenType.Text, "Expected timestamp literal");
        ConsumeToken(tokens, ref position, Cql2TokenType.RightParen, "Expected ')' after TIMESTAMP literal");

        if (!DateTimeOffset.TryParse(textToken.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw new ArgumentException($"Invalid TIMESTAMP literal: {textToken.Value}");
        }

        return new Literal(timestamp, LiteralType.DateTime);
    }

    private static IntervalLiteral ParseIntervalLiteral(IReadOnlyList<Cql2Token> tokens, ref int position)
    {
        ConsumeToken(tokens, ref position, Cql2TokenType.LeftParen, "Expected '(' after INTERVAL");

        var start = ParseIntervalBound(tokens, ref position);
        ConsumeToken(tokens, ref position, Cql2TokenType.Comma, "Expected ',' after interval start");
        var end = ParseIntervalBound(tokens, ref position);

        ConsumeToken(tokens, ref position, Cql2TokenType.RightParen, "Expected ')' after INTERVAL literal");

        if (start != null && end != null && start.Type != end.Type)
        {
            throw new ArgumentException("INTERVAL bounds must share the same temporal granularity");
        }

        return new IntervalLiteral(start, end);
    }

    private static Literal? ParseIntervalBound(IReadOnlyList<Cql2Token> tokens, ref int position)
    {
        var token = ConsumeToken(tokens, ref position, Cql2TokenType.Text, "Expected interval bound");
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
