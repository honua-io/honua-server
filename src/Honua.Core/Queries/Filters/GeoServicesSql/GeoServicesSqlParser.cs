// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Core.Queries.Filters.GeoServicesSql;

/// <summary>
/// Parses GeoServices SQL where clauses into the shared filter AST.
/// </summary>
public sealed class GeoServicesSqlParser
{
    private readonly List<Token> _tokens = [];
    private int _current;
    private int _expressionDepth;

    /// <summary>
    /// Parses a GeoServices SQL WHERE expression into a filter AST.
    /// </summary>
    public FilterExpression Parse(string whereClause)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
        {
            throw new ArgumentException("WHERE clause is empty.");
        }

        _tokens.Clear();
        _tokens.AddRange(new Lexer(whereClause).Tokenize());
        _current = 0;
        _expressionDepth = 0;

        var expression = ParseExpression();
        Consume(TokenType.EndOfFile, "Unexpected trailing tokens.");
        return expression;
    }

    private FilterExpression ParseExpression() => ParseOr();

    private FilterExpression ParseOr()
    {
        var expression = ParseAnd();
        while (Match(TokenType.Or))
        {
            var right = ParseAnd();
            expression = new BinaryExpression(expression, BinaryOperator.Or, right);
        }

        return expression;
    }

    private FilterExpression ParseAnd()
    {
        var expression = ParseNot();
        while (Match(TokenType.And))
        {
            var right = ParseNot();
            expression = new BinaryExpression(expression, BinaryOperator.And, right);
        }

        return expression;
    }

    private FilterExpression ParseNot()
        => ParseWithDepth(() =>
        {
            if (Match(TokenType.Not))
            {
                var operand = ParseNot();
                return new UnaryExpression(UnaryOperator.Not, operand);
            }

            return ParseComparison();
        });

    private FilterExpression ParseComparison()
    {
        var left = ParseAdditive();

        if (Match(TokenType.Is))
        {
            var isNot = Match(TokenType.Not);
            Consume(TokenType.Null, "Expected NULL after IS.");
            return new UnaryExpression(isNot ? UnaryOperator.IsNotNull : UnaryOperator.IsNull, left);
        }

        if (Match(TokenType.Not))
        {
            if (Match(TokenType.Like))
            {
                return new BinaryExpression(left, BinaryOperator.NotLike, ParseAdditive());
            }

            if (Match(TokenType.In))
            {
                return new BinaryExpression(left, BinaryOperator.NotIn, ParseInList());
            }

            if (Match(TokenType.Between))
            {
                var lower = ParseAdditive();
                Consume(TokenType.And, "Expected AND in BETWEEN expression.");
                var upper = ParseAdditive();
                return FilterExpressionHelpers.BuildBetweenExpression(left, lower, upper, negate: true);
            }

            throw Error("Expected LIKE, IN, or BETWEEN after NOT.");
        }

        if (Match(TokenType.Like))
        {
            return new BinaryExpression(left, BinaryOperator.Like, ParseAdditive());
        }

        if (Match(TokenType.In))
        {
            return new BinaryExpression(left, BinaryOperator.In, ParseInList());
        }

        if (Match(TokenType.Between))
        {
            var lower = ParseAdditive();
            Consume(TokenType.And, "Expected AND in BETWEEN expression.");
            var upper = ParseAdditive();
            return FilterExpressionHelpers.BuildBetweenExpression(left, lower, upper, negate: false);
        }

        if (Match(TokenType.Equal))
        {
            return new BinaryExpression(left, BinaryOperator.Equal, ParseAdditive());
        }

        if (Match(TokenType.NotEqual))
        {
            return new BinaryExpression(left, BinaryOperator.NotEqual, ParseAdditive());
        }

        if (Match(TokenType.GreaterEqual))
        {
            return new BinaryExpression(left, BinaryOperator.GreaterThanOrEqual, ParseAdditive());
        }

        if (Match(TokenType.Greater))
        {
            return new BinaryExpression(left, BinaryOperator.GreaterThan, ParseAdditive());
        }

        if (Match(TokenType.LessEqual))
        {
            return new BinaryExpression(left, BinaryOperator.LessThanOrEqual, ParseAdditive());
        }

        if (Match(TokenType.Less))
        {
            return new BinaryExpression(left, BinaryOperator.LessThan, ParseAdditive());
        }

        return left;
    }

    private FilterExpression ParseAdditive()
    {
        var expression = ParseConcatenation();
        while (Match(TokenType.Plus, TokenType.Minus))
        {
            var op = Previous().Type == TokenType.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            var right = ParseConcatenation();
            expression = new BinaryExpression(expression, op, right);
        }

        return expression;
    }

    private FilterExpression ParseConcatenation()
    {
        var expression = ParseMultiplicative();

        // ANSI/ArcGIS SQL string concatenation operator `||`. ArcGIS and the
        // backing geodatabases accept `a || b` as string concatenation; map it
        // onto the canonical CONCAT function so it flows through the existing,
        // already-parameterized function allowlist (no raw SQL reaches the DB).
        if (!Check(TokenType.Concat))
        {
            return expression;
        }

        var operands = new List<FilterExpression> { expression };
        while (Match(TokenType.Concat))
        {
            operands.Add(ParseMultiplicative());
        }

        return new FunctionCall("CONCAT", operands);
    }

    private FilterExpression ParseMultiplicative()
    {
        var expression = ParseUnary();
        while (Match(TokenType.Star, TokenType.Slash, TokenType.Percent))
        {
            var op = Previous().Type switch
            {
                TokenType.Star => BinaryOperator.Multiply,
                TokenType.Slash => BinaryOperator.Divide,
                _ => BinaryOperator.Modulo
            };
            var right = ParseUnary();
            expression = new BinaryExpression(expression, op, right);
        }

        return expression;
    }

    private FilterExpression ParseUnary()
        => ParseWithDepth(() =>
        {
            if (Match(TokenType.Minus))
            {
                return new UnaryExpression(UnaryOperator.Negate, ParseUnary());
            }

            if (Match(TokenType.Plus))
            {
                return ParseUnary();
            }

            return ParsePrimary();
        });

    private FilterExpression ParsePrimary()
    {
        if (Match(TokenType.LeftParen))
        {
            var expression = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression.");
            return expression;
        }

        if (Match(TokenType.Number))
        {
            return new Literal(Previous().Literal, LiteralType.Number);
        }

        if (Match(TokenType.String))
        {
            return new Literal(Previous().Literal, LiteralType.Text);
        }

        if (Match(TokenType.Null))
        {
            return new Literal(null, LiteralType.Null);
        }

        if (Match(TokenType.True))
        {
            return new Literal(true, LiteralType.Boolean);
        }

        if (Match(TokenType.False))
        {
            return new Literal(false, LiteralType.Boolean);
        }

        if (Match(TokenType.DateLiteral))
        {
            return CreateDateLiteral(Previous().Literal?.ToString());
        }

        if (Match(TokenType.Date))
        {
            if (Check(TokenType.String))
            {
                var value = Consume(TokenType.String, "Expected date literal string after DATE.").Literal?.ToString();
                return CreateDateLiteral(value, dateOnly: true);
            }

            return ParseIdentifierOrFunction(Previous().Lexeme);
        }

        if (Match(TokenType.Timestamp))
        {
            if (Check(TokenType.String))
            {
                var value = Consume(TokenType.String, "Expected timestamp literal string after TIMESTAMP.").Literal?.ToString();
                return CreateDateLiteral(value, dateOnly: false);
            }

            return ParseIdentifierOrFunction(Previous().Lexeme);
        }

        if (Match(TokenType.CurrentDate))
        {
            return new FunctionCall("CURRENT_DATE", Array.Empty<FilterExpression>());
        }

        if (Match(TokenType.CurrentTimestamp))
        {
            return new FunctionCall("CURRENT_TIMESTAMP", Array.Empty<FilterExpression>());
        }

        if (Match(TokenType.CurrentTime))
        {
            return new FunctionCall("CURRENT_TIME", Array.Empty<FilterExpression>());
        }

        if (Match(TokenType.Extract))
        {
            return ParseExtract();
        }

        if (Match(TokenType.Cast))
        {
            return ParseCast();
        }

        if (Match(TokenType.Identifier))
        {
            return ParseIdentifierOrFunction(Previous().Lexeme);
        }

        throw Error("Expected expression.");
    }

    private FunctionCall ParseExtract()
    {
        // ANSI/ArcGIS SQL temporal extraction: EXTRACT(<field> FROM <source>).
        // Map the recognised fields onto the canonical single-argument date-part
        // functions (YEAR/MONTH/DAY/HOUR/MINUTE/SECOND) that the provider
        // translators already support and parameterize.
        Consume(TokenType.LeftParen, "Expected '(' after EXTRACT.");
        var field = ConsumeIdentifier("Expected a date/time field name in EXTRACT.").ToUpperInvariant();

        var functionName = field switch
        {
            "YEAR" => "YEAR",
            "MONTH" => "MONTH",
            "DAY" => "DAY",
            "HOUR" => "HOUR",
            "MINUTE" => "MINUTE",
            "SECOND" => "SECOND",
            _ => throw Error(
                $"Unsupported EXTRACT field '{field}'. Supported fields: YEAR, MONTH, DAY, HOUR, MINUTE, SECOND.")
        };

        if (!Match(TokenType.From))
        {
            throw Error("Expected FROM in EXTRACT expression.");
        }

        var source = ParseExpression();
        Consume(TokenType.RightParen, "Expected ')' after EXTRACT source.");
        return new FunctionCall(functionName, new[] { source });
    }

    private FunctionCall ParseCast()
    {
        // ANSI/ArcGIS SQL type conversion: CAST(<value> AS <type>). Emit the
        // target type as a text literal so the provider translator resolves it
        // against its allowlisted cast targets; the value flows through the
        // normal parameterized path.
        Consume(TokenType.LeftParen, "Expected '(' after CAST.");
        var value = ParseExpression();
        if (!Match(TokenType.As))
        {
            throw Error("Expected AS in CAST expression.");
        }

        var typeName = ConsumeIdentifier("Expected a target type in CAST.");

        // Allow multi-word types such as DOUBLE PRECISION.
        while (Check(TokenType.Identifier))
        {
            typeName = $"{typeName} {ConsumeIdentifier("Expected a target type in CAST.")}";
        }

        Consume(TokenType.RightParen, "Expected ')' after CAST type.");
        return new FunctionCall("CAST", new FilterExpression[]
        {
            value,
            new Literal(typeName, LiteralType.Text)
        });
    }

    private FilterExpression ParseIdentifierOrFunction(string identifier)
    {
        if (Match(TokenType.LeftParen))
        {
            var args = new List<FilterExpression>();
            if (!Check(TokenType.RightParen))
            {
                do
                {
                    args.Add(ParseExpression());
                    FilterParserGuard.EnsureInListSize(args.Count, "GeoServicesSQL function argument");
                }
                while (Match(TokenType.Comma));
            }

            Consume(TokenType.RightParen, "Expected ')' after function arguments.");
            return new FunctionCall(identifier, args);
        }

        while (Match(TokenType.Dot))
        {
            var next = ConsumeIdentifier("Expected identifier after '.'.");
            identifier = $"{identifier}.{next}";
        }

        return new PropertyReference(identifier);
    }

    private string ConsumeIdentifier(string message)
    {
        if (Match(TokenType.Identifier))
        {
            return Previous().Lexeme;
        }

        if (Match(TokenType.Date) || Match(TokenType.Timestamp))
        {
            return Previous().Lexeme;
        }

        throw Error(message);
    }

    private ValueList ParseInList()
    {
        Consume(TokenType.LeftParen, "Expected '(' after IN.");
        var values = new List<FilterExpression>();

        if (!Check(TokenType.RightParen))
        {
            do
            {
                values.Add(ParseAdditive());
                FilterParserGuard.EnsureInListSize(values.Count, "GeoServicesSQL");
            }
            while (Match(TokenType.Comma));
        }

        Consume(TokenType.RightParen, "Expected ')' after value list.");
        return new ValueList(values);
    }

    private static Literal CreateDateLiteral(string? value, bool? dateOnly = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Invalid temporal literal.");
        }

        if (dateOnly == true)
        {
            if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return new Literal(date, LiteralType.Date);
            }

            throw new ArgumentException($"Invalid DATE literal '{value}'.");
        }

        // Require an explicit offset only when the literal includes a time component.
        // A bare date (e.g. `2024-01-01`) is unambiguous; a date+time without an offset
        // (e.g. `2024-01-01T12:00:00`) is not, and AssumeUniversal would silently fold
        // it to UTC, producing off-by-TZ filters for non-UTC callers.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            var hasTimeComponent = value.Contains('T', StringComparison.OrdinalIgnoreCase)
                || value.Contains(':');
            if (hasTimeComponent)
            {
                var hasOffsetIndicator = value.Contains('Z', StringComparison.OrdinalIgnoreCase)
                    || value.LastIndexOf('+') > 0
                    || (value.LastIndexOf('-') > 8); // after the date portion
                if (!hasOffsetIndicator)
                {
                    throw new ArgumentException(
                        $"Datetime literal '{value}' is missing a timezone offset. "
                        + "Use an explicit 'Z' or '+hh:mm' suffix to disambiguate.");
                }
            }

            if (dateOnly == null && timestamp.TimeOfDay == TimeSpan.Zero)
            {
                return new Literal(DateOnly.FromDateTime(timestamp.UtcDateTime), LiteralType.Date);
            }

            return new Literal(timestamp, LiteralType.DateTime);
        }

        if (dateOnly == null &&
            DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var inferredDate))
        {
            return new Literal(inferredDate, LiteralType.Date);
        }

        throw new ArgumentException($"Invalid temporal literal '{value}'.");
    }

    private T ParseWithDepth<T>(Func<T> parse)
    {
        _expressionDepth++;
        try
        {
            FilterParserGuard.EnsureExpressionDepth(_expressionDepth);
            return parse();
        }
        finally
        {
            _expressionDepth--;
        }
    }

    private bool Match(params TokenType[] types)
    {
        foreach (var type in types)
        {
            if (Check(type))
            {
                Advance();
                return true;
            }
        }

        return false;
    }

    private bool Check(TokenType type)
        => Current().Type == type;

    private Token Consume(TokenType type, string message)
    {
        if (Check(type))
        {
            return Advance();
        }

        throw Error(message);
    }

    private Token Advance()
    {
        if (!IsAtEnd())
        {
            _current++;
        }

        return Previous();
    }

    private bool IsAtEnd() => Current().Type == TokenType.EndOfFile;

    private Token Current() => _tokens[_current];

    private Token Previous() => _tokens[_current - 1];

    private ArgumentException Error(string message)
        => new($"{message} (position {Current().Position}).");

    private enum TokenType
    {
        LeftParen,
        RightParen,
        Comma,
        Dot,
        Plus,
        Minus,
        Star,
        Slash,
        Percent,
        Concat,
        Equal,
        NotEqual,
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
        Identifier,
        Number,
        String,
        DateLiteral,
        And,
        Or,
        Not,
        Like,
        In,
        Between,
        Is,
        Null,
        True,
        False,
        Date,
        Timestamp,
        CurrentDate,
        CurrentTimestamp,
        CurrentTime,
        Extract,
        From,
        Cast,
        As,
        EndOfFile
    }

    private readonly record struct Token(TokenType Type, string Lexeme, object? Literal, int Position);

    private sealed class Lexer
    {
        private readonly string _source;
        private readonly List<Token> _tokens = [];
        private int _start;
        private int _current;

        private static readonly Dictionary<string, TokenType> _keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AND"] = TokenType.And,
            ["OR"] = TokenType.Or,
            ["NOT"] = TokenType.Not,
            ["LIKE"] = TokenType.Like,
            ["IN"] = TokenType.In,
            ["BETWEEN"] = TokenType.Between,
            ["IS"] = TokenType.Is,
            ["NULL"] = TokenType.Null,
            ["TRUE"] = TokenType.True,
            ["FALSE"] = TokenType.False,
            ["DATE"] = TokenType.Date,
            ["TIMESTAMP"] = TokenType.Timestamp,
            ["CURRENT_DATE"] = TokenType.CurrentDate,
            ["CURRENT_TIMESTAMP"] = TokenType.CurrentTimestamp,
            ["CURRENT_TIME"] = TokenType.CurrentTime,
            ["EXTRACT"] = TokenType.Extract,
            ["FROM"] = TokenType.From,
            ["CAST"] = TokenType.Cast,
            ["AS"] = TokenType.As
        };

        public Lexer(string source)
        {
            _source = source ?? string.Empty;
        }

        public List<Token> Tokenize()
        {
            while (!IsAtEnd())
            {
                _start = _current;
                ScanToken();
            }

            _tokens.Add(new Token(TokenType.EndOfFile, string.Empty, null, _current));
            return _tokens;
        }

        private void ScanToken()
        {
            var c = Advance();
            switch (c)
            {
                case '(':
                    AddToken(TokenType.LeftParen);
                    return;
                case ')':
                    AddToken(TokenType.RightParen);
                    return;
                case ',':
                    AddToken(TokenType.Comma);
                    return;
                case '.':
                    if (IsDigit(Peek()))
                    {
                        ScanNumber(startedWithDot: true);
                        return;
                    }
                    AddToken(TokenType.Dot);
                    return;
                case '+':
                    AddToken(TokenType.Plus);
                    return;
                case '-':
                    AddToken(TokenType.Minus);
                    return;
                case '*':
                    AddToken(TokenType.Star);
                    return;
                case '/':
                    AddToken(TokenType.Slash);
                    return;
                case '%':
                    AddToken(TokenType.Percent);
                    return;
                case '|':
                    if (Match('|'))
                    {
                        AddToken(TokenType.Concat);
                        return;
                    }
                    throw new ArgumentException("Unexpected '|'.");
                case '=':
                    AddToken(TokenType.Equal);
                    return;
                case '<':
                    AddToken(Match('=') ? TokenType.LessEqual : Match('>') ? TokenType.NotEqual : TokenType.Less);
                    return;
                case '>':
                    AddToken(Match('=') ? TokenType.GreaterEqual : TokenType.Greater);
                    return;
                case '!':
                    if (Match('='))
                    {
                        AddToken(TokenType.NotEqual);
                        return;
                    }
                    throw new ArgumentException("Unexpected '!'.");
                case '\'':
                    ScanString();
                    return;
                case '"':
                    ScanQuotedIdentifier();
                    return;
                case '[':
                    ScanBracketIdentifier();
                    return;
                case '#':
                    ScanDateLiteral();
                    return;
                default:
                    if (IsWhitespace(c))
                    {
                        return;
                    }

                    if (IsDigit(c))
                    {
                        ScanNumber(startedWithDot: false);
                        return;
                    }

                    if (IsAlpha(c))
                    {
                        ScanIdentifier();
                        return;
                    }

                    throw new ArgumentException($"Unexpected character '{c}'.");
            }
        }

        private void ScanIdentifier()
        {
            while (IsAlphaNumeric(Peek()))
            {
                Advance();
            }

            var text = _source[_start.._current];
            if (_keywords.TryGetValue(text, out var type))
            {
                AddToken(type, text);
                return;
            }

            FilterParserGuard.EnsureIdentifierLength(text.Length, "GeoServicesSQL identifier");
            AddToken(TokenType.Identifier, text);
        }

        private void ScanQuotedIdentifier()
        {
            while (!IsAtEnd() && Peek() != '"')
            {
                Advance();
            }

            if (IsAtEnd())
            {
                throw new ArgumentException("Unterminated quoted identifier.");
            }

            Advance(); // closing quote
            var value = _source[(_start + 1)..(_current - 1)];
            FilterParserGuard.EnsureIdentifierLength(value.Length, "GeoServicesSQL quoted identifier");
            AddToken(TokenType.Identifier, value);
        }

        private void ScanBracketIdentifier()
        {
            while (!IsAtEnd() && Peek() != ']')
            {
                Advance();
            }

            if (IsAtEnd())
            {
                throw new ArgumentException("Unterminated bracket identifier.");
            }

            Advance(); // closing bracket
            var value = _source[(_start + 1)..(_current - 1)];
            FilterParserGuard.EnsureIdentifierLength(value.Length, "GeoServicesSQL bracket identifier");
            AddToken(TokenType.Identifier, value);
        }

        private void ScanString()
        {
            var builder = new StringBuilder();

            while (!IsAtEnd())
            {
                if (Peek() == '\'')
                {
                    Advance();
                    if (Peek() == '\'')
                    {
                        Advance();
                        builder.Append('\'');
                        continue;
                    }

                    AddToken(TokenType.String, builder.ToString());
                    return;
                }

                // Bound the literal size so an unbounded single-quoted payload cannot
                // pin a large StringBuilder in memory before the closing quote arrives.
                if (builder.Length >= FilterParserGuard.MaxStringLiteralLength)
                {
                    FilterParserGuard.EnsureStringLiteralLength(builder.Length + 1, "GeoServicesSQL string literal");
                }

                builder.Append(Advance());
            }

            throw new ArgumentException("Unterminated string literal.");
        }

        private void ScanDateLiteral()
        {
            while (!IsAtEnd() && Peek() != '#')
            {
                Advance();
            }

            if (IsAtEnd())
            {
                throw new ArgumentException("Unterminated date literal.");
            }

            Advance(); // closing #
            var value = _source[(_start + 1)..(_current - 1)];
            AddToken(TokenType.DateLiteral, value);
        }

        private void ScanNumber(bool startedWithDot)
        {
            while (IsDigit(Peek()))
            {
                Advance();
            }

            if (!startedWithDot && Peek() == '.' && IsDigit(PeekNext()))
            {
                Advance();
                while (IsDigit(Peek()))
                {
                    Advance();
                }
            }

            if (Peek() is 'e' or 'E')
            {
                var next = PeekNext();
                if (IsDigit(next) || next is '+' or '-')
                {
                    Advance();
                    if (next is '+' or '-')
                    {
                        Advance();
                    }

                    while (IsDigit(Peek()))
                    {
                        Advance();
                    }
                }
            }

            var text = _source[_start.._current];
            if (!text.Contains('.') && !text.Contains('e', StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    AddToken(TokenType.Number, longValue);
                    return;
                }
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
            {
                // Reject exponent overflow (parses into ±Infinity) and "NaN"/"Infinity"
                // literals that NumberStyles.Float accepts by default.
                if (!double.IsFinite(doubleValue))
                {
                    throw new ArgumentException($"Numeric literal '{text}' is not a finite number.");
                }
                AddToken(TokenType.Number, doubleValue);
                return;
            }

            throw new ArgumentException($"Invalid numeric literal '{text}'.");
        }

        private char Advance() => _source[_current++];

        private bool Match(char expected)
        {
            if (IsAtEnd() || _source[_current] != expected)
            {
                return false;
            }

            _current++;
            return true;
        }

        private char Peek() => IsAtEnd() ? '\0' : _source[_current];

        private char PeekNext() => _current + 1 >= _source.Length ? '\0' : _source[_current + 1];

        private bool IsAtEnd() => _current >= _source.Length;

        private static bool IsWhitespace(char c) => c is ' ' or '\r' or '\t' or '\n';

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        private static bool IsAlpha(char c)
            => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

        private static bool IsAlphaNumeric(char c) => IsAlpha(c) || IsDigit(c);

        private void AddToken(TokenType type) => AddToken(type, null);

        private void AddToken(TokenType type, object? literal)
        {
            var text = _source[_start.._current];
            _tokens.Add(new Token(type, text, literal, _start));
        }
    }
}
