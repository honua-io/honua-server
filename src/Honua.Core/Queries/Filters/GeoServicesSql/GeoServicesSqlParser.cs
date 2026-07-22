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

    // ANSI date/time fields the shared SQL translator already exposes as single-argument
    // functions (EXTRACT(YEAR FROM d) -> YEAR(d), etc.). Restricting to this allowlist keeps
    // unknown EXTRACT fields from ever reaching the SQL layer.
    //
    // The first group are the canonical ANSI fields mapped 1:1 to existing single-argument
    // functions (YEAR(d), MONTH(d), ...). The second group are additional Postgres-supported
    // fields (#1865) that have no equivalent single-arg function, so they are emitted as a
    // distinct EXTRACT_<FIELD> FunctionCall the translator re-emits as EXTRACT(<FIELD> FROM x).
    // Every entry here is a fixed SQL keyword chosen by the parser, never user text, so the
    // field token can be interpolated by the translator while operands stay parameterized.
    private static readonly HashSet<string> _extractFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "YEAR",
        "MONTH",
        "DAY",
        "HOUR",
        "MINUTE",
        "SECOND"
    };

    // Additional EXTRACT fields (beyond YEAR..SECOND) that Postgres supports and that we accept
    // as of #1865. Each is emitted as FunctionCall("EXTRACT_<FIELD>", [source]); the translator
    // owns the (allowlisted) field -> SQL mapping.
    private static readonly HashSet<string> _extendedExtractFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOW",
        "DOY",
        "QUARTER",
        "WEEK",
        "EPOCH"
    };

    // Allowlist of function names accepted by ParseIdentifierOrFunction for arbitrary
    // call-syntax (i.e. not already handled as a dedicated keyword: CAST, EXTRACT,
    // SUBSTRING, POSITION). Only names that every active ISqlFilterTranslator understands
    // are listed here so that an unknown function name is rejected at parse time rather than
    // silently producing an AST node that a future provider might execute without vetting.
    // CURRENT_DATE / CURRENT_TIMESTAMP / CURRENT_TIME are handled as dedicated keywords
    // (TokenType) and do not reach this path.
    private static readonly HashSet<string> _allowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // String functions
        "UPPER", "LOWER", "TRIM", "LTRIM", "RTRIM", "LENGTH", "LEN",
        "CONCAT", "REPLACE", "LEFT", "RIGHT",
        "CONTAINS", "STARTSWITH", "ENDSWITH",
        // SUBSTRING and POSITION are handled via dedicated SQL-standard keyword forms above,
        // but ArcGIS clients also emit the comma-delimited form SUBSTRING(field, start, len)
        // and POSITION(needle IN haystack) — the comma form doesn't match the keyword-path
        // guard so it falls through here.  Both are safe, well-understood string functions.
        "SUBSTRING", "POSITION",
        // CQL2 case-insensitive / accent-insensitive comparison functions. The FeatureServer
        // where parameter accepts CQL2-Text, so CASEI(...) / ACCENTI(...) reach this parser as
        // generic function calls; every ISqlFilterTranslator understands them (e.g. Postgres
        // maps CASEI -> LOWER and ACCENTI -> UNACCENT(LOWER(...))).
        "CASEI", "ACCENTI",
        // Null / conditional
        "COALESCE", "NULLIF", "IIF",
        // Numeric / math
        "ABS", "CEILING", "FLOOR", "ROUND", "SIGN",
        "POWER", "SQRT", "LOG", "EXP", "MOD",
        // Date / time helpers (single-arg forms; multi-arg EXTRACT handled separately)
        "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND",
        // Spatial predicates
        "ST_CONTAINS", "ST_WITHIN", "ST_INTERSECTS", "ST_DISJOINT",
        "ST_TOUCHES", "ST_CROSSES", "ST_OVERLAPS", "ST_EQUALS",
        "ST_DISTANCE", "ST_LENGTH", "ST_AREA", "ST_X", "ST_Y",
    };

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
        var left = ParseConcat();

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
                return ParseLikeRemainder(left, negate: true);
            }

            if (Match(TokenType.In))
            {
                return new BinaryExpression(left, BinaryOperator.NotIn, ParseInList());
            }

            if (Match(TokenType.Between))
            {
                var lower = ParseConcat();
                Consume(TokenType.And, "Expected AND in BETWEEN expression.");
                var upper = ParseConcat();
                return FilterExpressionHelpers.BuildBetweenExpression(left, lower, upper, negate: true);
            }

            throw Error("Expected LIKE, IN, or BETWEEN after NOT.");
        }

        if (Match(TokenType.Like))
        {
            return ParseLikeRemainder(left, negate: false);
        }

        if (Match(TokenType.In))
        {
            return new BinaryExpression(left, BinaryOperator.In, ParseInList());
        }

        if (Match(TokenType.Between))
        {
            var lower = ParseConcat();
            Consume(TokenType.And, "Expected AND in BETWEEN expression.");
            var upper = ParseConcat();
            return FilterExpressionHelpers.BuildBetweenExpression(left, lower, upper, negate: false);
        }

        if (Match(TokenType.Equal))
        {
            return new BinaryExpression(left, BinaryOperator.Equal, ParseConcat());
        }

        if (Match(TokenType.NotEqual))
        {
            return new BinaryExpression(left, BinaryOperator.NotEqual, ParseConcat());
        }

        if (Match(TokenType.GreaterEqual))
        {
            return new BinaryExpression(left, BinaryOperator.GreaterThanOrEqual, ParseConcat());
        }

        if (Match(TokenType.Greater))
        {
            return new BinaryExpression(left, BinaryOperator.GreaterThan, ParseConcat());
        }

        if (Match(TokenType.LessEqual))
        {
            return new BinaryExpression(left, BinaryOperator.LessThanOrEqual, ParseConcat());
        }

        if (Match(TokenType.Less))
        {
            return new BinaryExpression(left, BinaryOperator.LessThan, ParseConcat());
        }

        return left;
    }

    // Parses the right-hand side of a `[NOT] LIKE pattern [ESCAPE 'c']` predicate.
    // Without an ESCAPE clause this is the existing BinaryExpression(Like/NotLike). With one,
    // the escape character is captured as a single-character string Literal and the whole
    // predicate is emitted as FunctionCall("LIKE_ESCAPE"/"NOT_LIKE_ESCAPE", [left, pattern,
    // escape]). The escape character stays a bound parameter at translation time, so no user
    // text is interpolated into SQL.
    private FilterExpression ParseLikeRemainder(FilterExpression left, bool negate)
    {
        var pattern = ParseConcat();

        if (!TryMatchKeyword("ESCAPE"))
        {
            return new BinaryExpression(left, negate ? BinaryOperator.NotLike : BinaryOperator.Like, pattern);
        }

        if (!Match(TokenType.String))
        {
            throw Error("Expected a single-character string literal after ESCAPE.");
        }

        var escapeValue = Previous().Literal as string;
        if (escapeValue is not { Length: 1 })
        {
            throw Error("ESCAPE requires a single-character string literal.");
        }

        var escapeLiteral = new Literal(escapeValue, LiteralType.Text);
        return new FunctionCall(
            negate ? "NOT_LIKE_ESCAPE" : "LIKE_ESCAPE",
            new[] { left, pattern, escapeLiteral });
    }

    // CASE WHEN <cond> THEN <result> [WHEN ...] [ELSE <result>] END  (searched CASE), and
    // CASE <operand> WHEN <value> THEN <result> ... [ELSE <result>] END (simple CASE).
    //
    // Both are desugared to a single searched-CASE shape encoded as
    // FunctionCall("CASE", [cond1, result1, cond2, result2, ..., (else)]): an even argument
    // count carries no ELSE branch, an odd count's trailing argument is the ELSE result. A
    // simple CASE rewrites each `WHEN value` into the equality predicate `<operand> = value`
    // at parse time, so the translator only ever handles the searched form and every operand
    // (including the simple-CASE selector and branch values) is parameterized as usual.
    private FilterExpression ParseCaseExpression()
        => ParseWithDepth<FilterExpression>(() =>
        {
            // A simple CASE has an operand expression between CASE and the first WHEN.
            FilterExpression? selector = null;
            if (!Check(TokenType.When))
            {
                selector = ParseExpression();
            }

            var args = new List<FilterExpression>();
            var branchCount = 0;
            while (Match(TokenType.When))
            {
                var condition = ParseExpression();
                if (selector is not null)
                {
                    // Simple CASE: `WHEN value` means `selector = value`.
                    condition = new BinaryExpression(selector, BinaryOperator.Equal, condition);
                }

                Consume(TokenType.Then, "Expected 'THEN' in CASE expression.");
                var result = ParseExpression();

                args.Add(condition);
                args.Add(result);

                branchCount++;
                FilterParserGuard.EnsureInListSize(branchCount, "GeoServicesSQL CASE branch");
            }

            if (branchCount == 0)
            {
                throw Error("CASE expression requires at least one WHEN ... THEN branch.");
            }

            if (Match(TokenType.Else))
            {
                args.Add(ParseExpression());
            }

            Consume(TokenType.End, "Expected 'END' to close CASE expression.");
            return new FunctionCall("CASE", args);
        });

    // SQL string concatenation (a || b || c). Chained operands are flattened into a single
    // CONCAT(...) FunctionCall, which the shared SQL translator already emits with each literal
    // operand bound as a parameter. Concatenation binds tighter than comparison but looser than
    // additive arithmetic, matching ANSI SQL precedence.
    private FilterExpression ParseConcat()
    {
        var expression = ParseAdditive();
        if (!Check(TokenType.Concat))
        {
            return expression;
        }

        var operands = new List<FilterExpression> { expression };
        while (Match(TokenType.Concat))
        {
            operands.Add(ParseAdditive());
            FilterParserGuard.EnsureInListSize(operands.Count, "GeoServicesSQL concatenation");
        }

        return new FunctionCall("CONCAT", operands);
    }

    private FilterExpression ParseAdditive()
    {
        var expression = ParseMultiplicative();
        while (Match(TokenType.Plus, TokenType.Minus))
        {
            var op = Previous().Type == TokenType.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            var right = ParseMultiplicative();
            expression = new BinaryExpression(expression, op, right);
        }

        return expression;
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

        if (Match(TokenType.Case))
        {
            return ParseCaseExpression();
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
                return CreateDateLiteral(value, dateOnly: false, explicitTimestampKeyword: true);
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

        if (Match(TokenType.Identifier))
        {
            return ParseIdentifierOrFunction(Previous().Lexeme);
        }

        throw Error("Expected expression.");
    }

    private FilterExpression ParseIdentifierOrFunction(string identifier)
    {
        if (Match(TokenType.LeftParen))
        {
            // ArcGIS / SQL-92 standard functions accept keyword-delimited argument
            // syntax (e.g. CAST(x AS INTEGER), EXTRACT(YEAR FROM d)) rather than the
            // comma-delimited form. These are translated into the canonical FunctionCall
            // shape the shared SQL translator already understands, so no client SQL is
            // interpolated into a query string.
            if (IsKeyword(identifier, "CAST"))
            {
                return ParseCastFunction();
            }

            if (IsKeyword(identifier, "EXTRACT"))
            {
                return ParseExtractFunction();
            }

            if (IsKeyword(identifier, "SUBSTRING") && LooksLikeSqlStandardSubstring())
            {
                return ParseSqlStandardSubstring();
            }

            if (IsKeyword(identifier, "POSITION") && LooksLikeSqlStandardPosition())
            {
                return ParseSqlStandardPosition();
            }

            // Enforce a function-name allowlist so unknown identifiers are rejected at parse
            // time before any AST node is created. This provides defense-in-depth: even if
            // a future provider's ISqlFilterTranslator omits its own allowlist check, the
            // unknown function will never reach it.
            if (!_allowedFunctions.Contains(identifier))
            {
                throw new ArgumentException($"Unknown or unsupported function: '{identifier}'.");
            }

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

    // CAST(value AS type) -> FunctionCall("CAST", [value, Literal(type)]) which the shared
    // SQL translator maps to a parameter-safe `(value)::type` cast over an allowlisted type set.
    private FunctionCall ParseCastFunction()
    {
        var value = ParseExpression();
        ConsumeKeyword("AS", "Expected 'AS' in CAST expression.");
        var typeName = ParseTypeName();
        Consume(TokenType.RightParen, "Expected ')' after CAST type.");
        return new FunctionCall("CAST", new FilterExpression[] { value, new Literal(typeName, LiteralType.Text) });
    }

    // EXTRACT(field FROM source) -> FunctionCall("<FIELD>", [source]). Only the ANSI date/time
    // fields the translator already supports as EXTRACT(... FROM ...) are accepted; anything
    // else is rejected so unknown fields never reach the SQL layer.
    private FunctionCall ParseExtractFunction()
    {
        var field = ConsumeIdentifier("Expected a date/time field in EXTRACT expression.")
            .ToUpperInvariant();

        var isCanonical = _extractFields.Contains(field);
        var isExtended = _extendedExtractFields.Contains(field);
        if (!isCanonical && !isExtended)
        {
            throw Error(
                $"Unsupported EXTRACT field '{field}'. Supported fields: "
                + $"{string.Join(", ", _extractFields.Concat(_extendedExtractFields))}.");
        }

        ConsumeKeyword("FROM", "Expected 'FROM' in EXTRACT expression.");
        var source = ParseExpression();
        Consume(TokenType.RightParen, "Expected ')' after EXTRACT source.");

        // Canonical fields reuse the existing single-arg date/time functions (YEAR(x), ...);
        // extended fields carry an EXTRACT_ prefix the translator maps to EXTRACT(field FROM x).
        var functionName = isCanonical ? field : $"EXTRACT_{field}";
        return new FunctionCall(functionName, new[] { source });
    }

    // SUBSTRING(value FROM start [FOR length]) -> FunctionCall("SUBSTRING", [value, start (, length)])
    // matching the comma-delimited form the translator already supports.
    private FunctionCall ParseSqlStandardSubstring()
    {
        var value = ParseExpression();
        ConsumeKeyword("FROM", "Expected 'FROM' in SUBSTRING expression.");
        var start = ParseExpression();

        var args = new List<FilterExpression> { value, start };
        if (TryMatchKeyword("FOR"))
        {
            args.Add(ParseExpression());
        }

        Consume(TokenType.RightParen, "Expected ')' after SUBSTRING arguments.");
        return new FunctionCall("SUBSTRING", args);
    }

    // POSITION(substring IN value) -> FunctionCall("POSITION", [substring, value]) which the
    // translator already emits as POSITION(a IN b).
    private FunctionCall ParseSqlStandardPosition()
    {
        var needle = ParseAdditive();
        Consume(TokenType.In, "Expected 'IN' in POSITION expression.");
        var haystack = ParseExpression();
        Consume(TokenType.RightParen, "Expected ')' after POSITION arguments.");
        return new FunctionCall("POSITION", new[] { needle, haystack });
    }

    // SUBSTRING uses the SQL-standard form only when the keyword `FROM` appears before any comma.
    private bool LooksLikeSqlStandardSubstring()
        => ContainsKeywordBeforeComma("FROM");

    // POSITION uses the SQL-standard form only when the `IN` keyword appears before any comma.
    private bool LooksLikeSqlStandardPosition()
    {
        var depth = 0;
        for (var index = _current; index < _tokens.Count; index++)
        {
            var token = _tokens[index];
            switch (token.Type)
            {
                case TokenType.LeftParen:
                    depth++;
                    break;
                case TokenType.RightParen:
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                    break;
                case TokenType.Comma when depth == 0:
                    return false;
                case TokenType.In when depth == 0:
                    return true;
                case TokenType.EndOfFile:
                    return false;
            }
        }

        return false;
    }

    private bool ContainsKeywordBeforeComma(string keyword)
    {
        var depth = 0;
        for (var index = _current; index < _tokens.Count; index++)
        {
            var token = _tokens[index];
            switch (token.Type)
            {
                case TokenType.LeftParen:
                    depth++;
                    break;
                case TokenType.RightParen:
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                    break;
                case TokenType.Comma when depth == 0:
                    return false;
                case TokenType.Identifier when depth == 0
                    && string.Equals(token.Lexeme, keyword, StringComparison.OrdinalIgnoreCase):
                    return true;
                case TokenType.EndOfFile:
                    return false;
            }
        }

        return false;
    }

    private string ParseTypeName()
    {
        var name = ConsumeIdentifier("Expected a type name.");

        // Support two-word ANSI types (e.g. DOUBLE PRECISION) that the translator accepts.
        if (Check(TokenType.Identifier))
        {
            name = $"{name} {Advance().Lexeme}";
        }

        // Accept an optional length/precision argument list, e.g. VARCHAR(20),
        // CHAR(8), DECIMAL(10, 2). The arguments must be plain non-negative
        // integer literals (Number tokens that lexed to a long), so nothing other
        // than digits can ever reach the SQL layer. The arguments are appended to
        // the type-name string so the translator can re-emit a parameter-safe
        // cast such as `(value)::VARCHAR(20)`.
        if (Match(TokenType.LeftParen))
        {
            var first = ConsumeTypeArgument();
            if (Match(TokenType.Comma))
            {
                var second = ConsumeTypeArgument();
                Consume(TokenType.RightParen, "Expected ')' after CAST type arguments.");
                name = $"{name}({first},{second})";
            }
            else
            {
                Consume(TokenType.RightParen, "Expected ')' after CAST type argument.");
                name = $"{name}({first})";
            }
        }

        FilterParserGuard.EnsureIdentifierLength(name.Length, "GeoServicesSQL CAST type");
        return name;
    }

    // A CAST type length/precision argument must be a non-negative integer literal.
    private long ConsumeTypeArgument()
    {
        var token = Consume(TokenType.Number, "Expected an integer length/precision in CAST type.");
        if (token.Literal is not long value || value < 0)
        {
            throw Error("CAST type length/precision must be a non-negative integer.");
        }

        return value;
    }

    private static bool IsKeyword(string identifier, string keyword)
        => string.Equals(identifier, keyword, StringComparison.OrdinalIgnoreCase);

    private void ConsumeKeyword(string keyword, string message)
    {
        if (!TryMatchKeyword(keyword))
        {
            throw Error(message);
        }
    }

    private bool TryMatchKeyword(string keyword)
    {
        if (Check(TokenType.Identifier)
            && string.Equals(Current().Lexeme, keyword, StringComparison.OrdinalIgnoreCase))
        {
            Advance();
            return true;
        }

        return false;
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

    private static Literal CreateDateLiteral(string? value, bool? dateOnly = null, bool explicitTimestampKeyword = false)
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

        // A bare date (e.g. `2024-01-01`) is unambiguous; a date+time without an offset
        // (e.g. `2024-01-01T12:00:00`) is not, so AssumeUniversal folds it to UTC below
        // (see PA-026 note) instead of requiring callers to supply an explicit offset.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            // Mirror ArcGIS Server behaviour: bare datetime literals without a timezone
            // offset (whether in #...# or quoted form, with or without the TIMESTAMP keyword)
            // are accepted and treated as UTC. AssumeUniversal above already applied this.
            // Rejecting them broke legacy ArcGIS client queries (honua-server#PA-026).
            // The `explicitTimestampKeyword` branch was already accepted; now the bare form
            // is too (a `hasOffsetIndicator` check that used to gate a reject-if-missing
            // branch was removed by PA-026; it is intentionally not restored here). Callers
            // that need strict timezone enforcement should apply their own validation before
            // reaching the parser.

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
        // Kept as an explicit loop rather than `types.Any(Check)`: this is a hot path
        // called once per token during every filter parse, and a LINQ predicate here
        // would add a per-call delegate allocation on top of the existing `params`
        // array allocation for no readability gain.
        foreach (var type in (types).Where(type => Check(type)))
        {
            Advance();
            return true;
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
        Case,
        When,
        Then,
        Else,
        End,
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
            ["CASE"] = TokenType.Case,
            ["WHEN"] = TokenType.When,
            ["THEN"] = TokenType.Then,
            ["ELSE"] = TokenType.Else,
            ["END"] = TokenType.End
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
            if (!text.Contains('.') && !text.Contains('e', StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            {
                AddToken(TokenType.Number, longValue);
                return;
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
